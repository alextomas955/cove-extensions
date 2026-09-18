namespace Renamer.Execution;

// The cross-volume tier of the executor, used when a rename or move crosses volumes and the atomic
// same-volume File.Move that DiskMover uses is unavailable. A cross-volume File.Move is an
// unverified, non-atomic copy-then-delete that leaves a silent duplicate on a locked source. This
// class mirrors DiskMover's shape, so the executor consumes its result the same way, and it is the
// only tier that can report VerifyFailed or Cancelled, since only a copy can be read back or
// cancelled.
//
// The per-file sequence is fixed and is not reordered:
//
// 1. Copy the source into an in-flight copy in the destination directory, under a name minted for
//    this call alone, feeding each read buffer to both the destination and a running XxHash3 so the
//    source is read once. The destination is then flushed with flushToDisk, an fsync, so the copied
//    bytes are durable on media before the stream closes and are not left in the OS write-back
//    cache.
// 2. Verify by re-opening the in-flight copy fresh from disk and hashing it independently. The copy
//    is accepted only when the size and the content hash both match the source-pass values; a
//    size-only check would pass a same-length torn write. On a mismatch the suspect copy is deleted
//    and the source is untouched.
// 3. Promote: a two-argument File.Move renames the verified in-flight copy to the final name. Same
//    directory, so the rename is atomic, and it throws when the final name already exists.
// 4. Delete the source, and only after the promote succeeds. The source is the durable fallback
//    until this last step. Because the destination data was forced to media in (1) and confirmed in
//    (2), a crash at any point, from a process crash to a power loss, leaves either the intact
//    source or the verified, media-durable final. The residual window is the promote's
//    directory-entry durability, since the data extents are already durable. An in-flight copy
//    orphaned by a crash carries a name no later call produces, so it is never promoted, never
//    collided with and never deleted here; removing it is left to the user.
//
// The source delete is the one step outside the all-or-nothing try. Past the promote the
// destination is the verified, media-durable copy, so the move has happened whatever the delete
// does: a delete refused by a lock or a permission returns Moved and adds a warning naming the
// source it could not remove. Classifying it as a skip instead left RenamerExecutor taking the
// no-database-write path with Cove's row still naming the source, so the next run copied the file
// again and its collision loop suffixed past the survivor, and nothing bounded the pile. The same
// engine backs SafeCopyBackAsync, so a rollback whose source delete fails is reported as the
// completed rollback it is, with the stranded source named in the warnings.
//
// Failures are classified, not thrown: a locked source (IOException) is a Locked skip; an occupied
// destination, from the up-front check or from the same IOException resolved by testing the
// destination, is a TargetExists skip; a permission denial is PermissionDenied; a failed verify is
// VerifyFailed; a cancelled token is Cancelled, with the in-flight copy this call created removed
// first. No path throws out, deletes the source on failure, or leaves a corrupt file. A source that
// cannot be deleted after a successful promote is reported as a move with a warning. Because the
// in-flight name is minted per call, an orphan from an earlier crash is never collided with and
// never surfaces as a skip.
//
// System.IO and System.IO.Hashing only: no CoveContext, no EF, no static or global state, so
// concurrency is bounded by the caller per source and destination pair, and a test drives it against
// a real temp directory whatever the volume layout is.
//
// Both sides are self-hashed with XxHash3, a fast integrity check and not a security control.
// Reusing Cove's stored MD5 to skip the source read is deferred: that MD5 lives in a FileFingerprint
// row the renamer data port does not load, so it is invisible here.
public sealed class CrossVolumeMover
{
    // A 1 MiB copy and hash buffer matches File.Copy throughput on multi-GB sequential I/O, where the
    // 4 KiB default and CopyTo's 80 KiB are too small. FileOptions.SequentialScan is a no-op on modern
    // Windows and is left unset; only FileOptions.Asynchronous is worth setting.
    private const int BufferSize = 1 << 20;

    // The fixed marker opening the minted segment, so an orphan is recognisable as this extension's.
    private const string InFlightMarker = ".rnm";

    private const int InFlightRandomChars = 8;

    // How many characters MintInFlightPath appends to the final path.
    //
    // internal so the planner's in-flight overflow warning derives the length from this declaration and
    // does not restate it. A hand-mirrored copy that missed a narrowing of the minted segment would
    // leave the preview warning on a band that no longer overruns, warning on a correct plan.
    //
    // static readonly and not const, because string.Length is not a compile-time constant expression in
    // C# and writing the sum out as a literal is the mirroring this member removes.
    internal static readonly int InFlightSuffixLength = InFlightMarker.Length + InFlightRandomChars;

    // Test-only fault-injection seam, invoked on the closed in-flight copy after the copy and before
    // the verify, with that copy's absolute path, so a test can corrupt or truncate it and prove the
    // verify catches the damage and the source survives. Production leaves it null and never mutates
    // the live copy.
    //
    // It is also the only way a test learns the minted name: the name is unguessable by design, so a
    // test that composed its own expectation would assert on a value it supplied and would pass
    // however wrong the real one was.
    private readonly Func<string, CancellationToken, Task>? _postCopyFaultForTests;

    public CrossVolumeMover()
        : this(null)
    {
    }

    // Test-only constructor wiring the post-copy fault seam. Production uses the parameterless one.
    public CrossVolumeMover(Func<string, CancellationToken, Task>? postCopyFaultForTests)
    {
        _postCopyFaultForTests = postCopyFaultForTests;
    }

    // One planned sidecar move, absolute source to absolute destination; either slash convention.
    public readonly record struct SidecarMove(string From, string To);

    // A result that is not Moved is a skip and never a thrown error. Moved is true once the primary
    // was copied, verified and promoted; the source is deleted last, and one that could not be
    // removed leaves the move done and adds a warning. MovedSidecars carries the pairs that moved, in
    // move order, which is what a rollback reverses. Reason is null on success. The shape matches
    // DiskMover.MoveResult, so the executor's call site is the same for both tiers.
    public sealed record MoveResult(
        bool Moved,
        MoveOutcome Outcome,
        IReadOnlyList<SidecarMove> MovedSidecars,
        IReadOnlyList<string> Warnings,
        string? Reason);

    // Moves the primary across volumes through the fixed copy, verify on size and hash, atomic promote,
    // delete-source-last sequence, then each planned sidecar through the same sequence, skipping rather
    // than clobbering. Every failure comes back classified: a locked source as Locked, an occupied
    // destination as TargetExists, a permission failure as PermissionDenied, a destination that does not
    // match the source by size or hash as VerifyFailed, a cancelled token as Cancelled. On a failure the
    // source is not deleted and the in-flight copy this call created is removed; nothing is overwritten,
    // no corrupt file is left, and nothing throws out, cancellation included. A source that cannot be
    // deleted after a successful promote is reported as a move with a warning.
    public async Task<MoveResult> MoveAsync(
        string oldFull,
        string newFull,
        IReadOnlyList<SidecarMove>? sidecars,
        CancellationToken ct)
    {
        var primary = await CopyVerifyPromoteDeleteAsync(oldFull, newFull, ct).ConfigureAwait(false);
        if (!primary.Ok)
        {
            return new MoveResult(false, primary.Outcome, [], [], primary.Reason);
        }

        var moved = new List<SidecarMove>();
        var warnings = new List<string>();
        if (primary.Warning is not null)
        {
            warnings.Add(primary.Warning);
        }

        if (sidecars is not null)
        {
            foreach (var sc in sidecars)
            {
                if (System.IO.File.Exists(sc.To))
                {
                    // Skip-not-clobber: leave the pre-existing target untouched, warn.
                    warnings.Add($"sidecar target exists, skipped: {sc.To}");
                    continue;
                }

                var scResult = await CopyVerifyPromoteDeleteAsync(sc.From, sc.To, ct).ConfigureAwait(false);
                if (scResult.Ok)
                {
                    if (scResult.Warning is null)
                    {
                        moved.Add(sc);
                    }
                    else
                    {
                        // Its source is still in place, so there is nothing for a rollback to put back:
                        // a copy-back would find the slot occupied and leave the promoted copy standing,
                        // reporting an incomplete restore for one that needed no work.
                        warnings.Add(scResult.Warning);
                    }
                }
                else
                {
                    // A locked, racing or unverifiable sidecar is not fatal once the primary has moved.
                    warnings.Add($"sidecar move failed ({scResult.Outcome}), skipped: {sc.From} -> {sc.To}: {scResult.Reason}");
                }
            }
        }

        return new MoveResult(true, MoveOutcome.Moved, moved, warnings, null);
    }

    // Reverses a successful MoveAsync, for instance when a database save threw after a verified
    // cross-volume move. Each moved sidecar is copied back to its source first, then the primary, each
    // through the same copy, verify and delete sequence. Best-effort: a secondary failure, from a
    // re-occupied old slot, a failed verify or a locked target, goes into the returned warnings and is
    // not thrown, so a failed save's cleanup cannot crash the batch. An empty list means a clean
    // restore.
    public async Task<IReadOnlyList<string>> RollbackAsync(
        string oldFull,
        string newFull,
        IReadOnlyList<SidecarMove> movedSidecars,
        CancellationToken ct)
    {
        var warnings = new List<string>();

        // Sidecars come back first, the most recent move undone first, then the primary file.
        for (int i = movedSidecars.Count - 1; i >= 0; i--)
        {
            var sc = movedSidecars[i];
            await SafeCopyBackAsync(sc.To, sc.From, warnings, ct).ConfigureAwait(false);
        }

        await SafeCopyBackAsync(newFull, oldFull, warnings, ct).ConfigureAwait(false);
        return warnings;
    }

    // The single-file engine: copy, verify, atomic promote, delete the source last. Every failure comes
    // back classified and none is thrown.
    private async Task<(bool Ok, MoveOutcome Outcome, string? Reason, string? Warning)> CopyVerifyPromoteDeleteAsync(
        string srcFull,
        string finalFull,
        CancellationToken ct)
    {
        // An existing final destination is never overwritten.
        if (System.IO.File.Exists(finalFull))
        {
            return (false, MoveOutcome.TargetExists, $"target exists, not overwritten: {finalFull}", null);
        }

        // Every delete below targets this one path, minted here in this invocation, so "the mover never
        // removes a file it did not create" holds by the shape of the code and not by a check.
        var inFlightFull = MintInFlightPath(finalFull);

        try
        {
            EnsureParentDir(inFlightFull);

            // Single-pass copy and source hash into the in-flight copy, opened CreateNew so it cannot
            // clobber.
            var (srcSize, srcHash) = await CopyAndHashAsync(srcFull, inFlightFull, ct).ConfigureAwait(false);

            // The test-only fault seam corrupts the closed in-flight copy between copy and verify.
            if (_postCopyFaultForTests is not null)
            {
                await _postCopyFaultForTests(inFlightFull, ct).ConfigureAwait(false);
            }

            // Verify against a fresh destination read, on size and hash. On a mismatch the suspect copy
            // is deleted and the source is left untouched.
            var (dstSize, dstHash) = await HashFileAsync(inFlightFull, ct).ConfigureAwait(false);
            bool verified = dstSize == srcSize && dstHash.AsSpan().SequenceEqual(srcHash);
            if (!verified)
            {
                TryDelete(inFlightFull);
                return (false, MoveOutcome.VerifyFailed, "verify failed: destination size or hash mismatch", null);
            }

            // The same-directory promote is atomic, and the two-argument Move cannot clobber the final.
            try
            {
                System.IO.File.Move(inFlightFull, finalFull);
            }
            catch (IOException ex)
            {
                TryDelete(inFlightFull);
                // A racing writer that took the final name between the pre-check and here, and a locked
                // in-flight copy, arrive as the same IOException. The destination is measured to tell
                // them apart; MoveOutcome.TargetExists covers why the exception message is never read.
                return System.IO.File.Exists(finalFull)
                    ? (false, MoveOutcome.TargetExists, $"target exists at promote, not overwritten: {ex.Message}", null)
                    : (false, MoveOutcome.Locked, $"promote refused, in-flight copy locked: {ex.Message}", null);
            }

            // Past the promote the destination is the verified, media-durable copy, so the move has
            // happened whatever the delete does. Reporting it as not-done would leave the caller's row
            // naming the source, and the next run would copy the file again and suffix past the
            // survivor, with nothing bounding the pile.
            try
            {
                System.IO.File.Delete(srcFull);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return (true, MoveOutcome.Moved, null,
                    $"moved, but the source could not be removed and is still there: {srcFull}: {ex.Message}");
            }

            return (true, MoveOutcome.Moved, null, null);
        }
        catch (OperationCanceledException)
        {
            // A cancelled token throws out of the read and write loop. The in-flight copy is removed so
            // a cancel leaks no unverified copy. The source is untouched, because its delete runs only
            // after a verified promote, which a cancel never reaches.
            TryDelete(inFlightFull);
            return (false, MoveOutcome.Cancelled, "cancelled", null);
        }
        catch (IOException ex)
        {
            // A locked source and torn I/O arrive here. The attempt is reported as a skip, the lock is
            // not forced and the source is not deleted; the suspect in-flight copy is removed when the
            // CreateNew got far enough to make one.
            TryDelete(inFlightFull);
            // The destination is measured to classify this, never the exception message; see
            // MoveOutcome.TargetExists. A destination present here is a racing writer that took the
            // final name, not a promoted copy: a post-promote delete failure is caught beside the
            // delete and never reaches this arm.
            return System.IO.File.Exists(finalFull)
                ? (false, MoveOutcome.TargetExists, $"target exists, not overwritten: {ex.Message}", null)
                : (false, MoveOutcome.Locked, $"source locked/in-use: {ex.Message}", null);
        }
        catch (UnauthorizedAccessException ex)
        {
            TryDelete(inFlightFull);
            return (false, MoveOutcome.PermissionDenied, $"permission denied: {ex.Message}", null);
        }
    }

    // The path the in-flight copy occupies for one call: the final path plus a short marker and
    // cryptographic randomness, in the destination directory so the later promote stays a
    // same-directory atomic rename.
    //
    // The name is unguessable by construction, and that is what carries the safety property, not a
    // check: no two calls produce the same name, so this call's copy can only land on a path it just
    // created, and an orphan left by an earlier crash is never collided with, never promoted and never
    // deleted. A counter, a process id or a timestamp would each be guessable and would put back the
    // question of whether a given file belongs to this call.
    //
    // The segment is kept short: the planner budgets only the final path against
    // RenamerOptions.FullPathMax, so the in-flight path is unbudgeted.
    //
    // The alphabet is hexadecimal, so the minted segment carries no separator, no parent-directory
    // segment and no drive qualifier, and cannot move the copy out of the destination directory.
    //
    // internal for one reader: the test that pins InFlightSuffixLength measures the segment this method
    // appends, because a pin that recomposed the marker and the random count would agree with a
    // rewritten minter forever.
    internal static string MintInFlightPath(string finalFull) =>
        finalFull
        + InFlightMarker
        + System.Security.Cryptography.RandomNumberGenerator.GetHexString(InFlightRandomChars, lowercase: true);

    // Reads the source once into a reused buffer, feeding each slice to both the in-flight destination
    // stream and a running hash, and returns the source size and the digest from that same pass, so the
    // source is never read a second time.
    private static async Task<(long Size, byte[] Hash)> CopyAndHashAsync(
        string srcNative,
        string inFlightNative,
        CancellationToken ct)
    {
        var hash = new System.IO.Hashing.XxHash3();
        long total = 0;

        await using var src = new FileStream(
            srcNative, FileMode.Open, FileAccess.Read, FileShare.Read,
            BufferSize, FileOptions.Asynchronous);
        // CreateNew throws IOException when the in-flight file already exists, so it cannot clobber.
        await using var dst = new FileStream(
            inFlightNative, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            BufferSize, FileOptions.Asynchronous);

        var buffer = new byte[BufferSize];
        int read;
        while ((read = await src.ReadAsync(buffer.AsMemory(0, BufferSize), ct).ConfigureAwait(false)) > 0)
        {
            var slice = buffer.AsMemory(0, read);
            hash.Append(slice.Span);
            await dst.WriteAsync(slice, ct).ConfigureAwait(false);
            total += read;
        }

        await dst.FlushAsync(ct).ConfigureAwait(false);
        // The write-back cache is forced to physical media before the stream closes, before the verify
        // re-reads and before the source is deleted. FlushAsync alone only drains the managed buffer
        // into the OS file cache; flushToDisk issues the fsync that makes the bytes durable. Without it
        // the verify would re-read the same volatile cache, and a power loss after the source delete
        // could leave a destination that never reached media. This is what carries the guarantee that
        // an interrupted transfer never loses the original across a power loss or an OS crash, not only
        // across a process crash.
        dst.Flush(flushToDisk: true);
        return (total, hash.GetCurrentHash());
    }

    // Re-reads the file fresh from disk, after the copy stream is flushed and closed, and computes its
    // size and digest independently, so the verify confirms what landed on disk and not what the copy
    // buffer held.
    private static async Task<(long Size, byte[] Hash)> HashFileAsync(string native, CancellationToken ct)
    {
        var hash = new System.IO.Hashing.XxHash3();
        await using var s = new FileStream(
            native, FileMode.Open, FileAccess.Read, FileShare.Read,
            BufferSize, FileOptions.Asynchronous);

        var buffer = new byte[BufferSize];
        long total = 0;
        int read;
        while ((read = await s.ReadAsync(buffer.AsMemory(0, BufferSize), ct).ConfigureAwait(false)) > 0)
        {
            hash.Append(buffer.AsSpan(0, read));
            total += read;
        }

        return (total, hash.GetCurrentHash());
    }

    // Best-effort copy-back for rollback, through the verified cross-volume sequence. A failure is
    // recorded in the warnings and never thrown, matching DiskMover's SafeMoveBack.
    private async Task SafeCopyBackAsync(string from, string to, List<string> warnings, CancellationToken ct)
    {
        try
        {
            if (!System.IO.File.Exists(from))
            {
                warnings.Add($"rollback source missing, cannot restore: {from}");
                return;
            }
            if (System.IO.File.Exists(to))
            {
                warnings.Add($"rollback target re-occupied, leaving as-is: {to}");
                return;
            }

            var result = await CopyVerifyPromoteDeleteAsync(from, to, ct).ConfigureAwait(false);
            if (!result.Ok)
            {
                warnings.Add($"rollback move failed {from} -> {to}: {result.Outcome} {result.Reason}");
            }
            else if (result.Warning is not null)
            {
                warnings.Add($"rollback {from} -> {to}: {result.Warning}");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            warnings.Add($"rollback move failed {from} -> {to}: {ex.Message}");
        }
    }

    // Best-effort cleanup of the in-flight copy the calling invocation minted; it never throws. Every
    // call site passes a path minted inside the same invocation, which is the whole of the ownership
    // guarantee: this helper can only be pointed at a file the mover itself created a moment earlier.
    private static void TryDelete(string path)
    {
        try
        {
            if (System.IO.File.Exists(path))
            {
                System.IO.File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort: an in-flight copy we cannot delete is never promoted, so it is harmless.
        }
    }

    private static void EnsureParentDir(string fullPath)
    {
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
    }
}
