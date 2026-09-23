namespace Renamer.Execution;

// The executor's cross-volume tier, used where DiskMover's atomic same-volume File.Move is
// unavailable. A cross-volume File.Move is an unverified copy-then-delete that can leave a silent
// duplicate behind a locked source, so this tier copies, verifies, then deletes. It mirrors
// DiskMover's shape so the executor consumes either the same way, and it is the only tier that can
// report VerifyFailed or Cancelled.
public sealed class CrossVolumeMover
{
    // Matches File.Copy throughput on multi-GB sequential I/O; the 4 KiB default and CopyTo's 80 KiB
    // are both too small.
    private const int BufferSize = 1 << 20;

    // The fixed marker opening the minted segment, so an orphan is recognisable as this extension's.
    private const string InFlightMarker = ".rnm";

    private const int InFlightRandomChars = 8;

    // How many characters MintInFlightPath appends. internal so the planner's in-flight overflow
    // warning derives the length from here and cannot drift from the minter.
    internal static readonly int InFlightSuffixLength = InFlightMarker.Length + InFlightRandomChars;

    // Test-only seam, invoked on the closed in-flight copy between the copy and the verify so a test
    // can corrupt it. It is also the only way a test learns the minted name, which is unguessable by
    // design. Production leaves it null.
    private readonly Func<string, CancellationToken, Task>? _postCopyFaultForTests;

    public CrossVolumeMover()
        : this(null)
    {
    }

    public CrossVolumeMover(Func<string, CancellationToken, Task>? postCopyFaultForTests)
    {
        _postCopyFaultForTests = postCopyFaultForTests;
    }

    // Moves the primary, then each sidecar through the same sequence, skipping rather than clobbering
    // an occupied target. Nothing throws out, cancellation included. A source that could not be removed
    // after a successful promote still counts as Moved, with a warning.
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

    // Reverses a successful MoveAsync, for instance when a database save threw after the move.
    // Best-effort: a secondary failure goes into the returned warnings rather than throwing, so a
    // failed save's cleanup cannot crash the batch. An empty list means a clean restore.
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

    // The single-file engine, in an order that survives a crash at any point: the copy is flushed to
    // media, the verify re-reads it and compares size and hash (size alone would pass a torn write),
    // the promote is a same-directory rename and so atomic, and the source is deleted last. Every
    // failure is classified into MoveOutcome; none is thrown.
    private async Task<(bool Ok, MoveOutcome Outcome, string? Reason, string? Warning)> CopyVerifyPromoteDeleteAsync(
        string srcFull,
        string finalFull,
        CancellationToken ct)
    {
        if (System.IO.File.Exists(finalFull))
        {
            return (false, MoveOutcome.TargetExists, $"target exists, not overwritten: {finalFull}", null);
        }

        // Every delete below targets this one path, minted in this invocation, so "never removes a
        // file it did not create" holds by the shape of the code and not by a check.
        var inFlightFull = MintInFlightPath(finalFull);

        try
        {
            Movers.EnsureParentDir(inFlightFull);

            // Opened CreateNew, so it cannot clobber.
            var (srcSize, srcHash) = await CopyAndHashAsync(srcFull, inFlightFull, ct).ConfigureAwait(false);

            if (_postCopyFaultForTests is not null)
            {
                await _postCopyFaultForTests(inFlightFull, ct).ConfigureAwait(false);
            }

            var (dstSize, dstHash) = await HashFileAsync(inFlightFull, ct).ConfigureAwait(false);
            bool verified = dstSize == srcSize && dstHash.AsSpan().SequenceEqual(srcHash);
            if (!verified)
            {
                TryDelete(inFlightFull);
                return (false, MoveOutcome.VerifyFailed, "verify failed: destination size or hash mismatch", null);
            }

            try
            {
                System.IO.File.Move(inFlightFull, finalFull);
            }
            catch (IOException ex)
            {
                TryDelete(inFlightFull);
                // A racing writer that took the final name and a locked in-flight copy arrive as the
                // same IOException, so the destination is measured to tell them apart.
                return System.IO.File.Exists(finalFull)
                    ? (false, MoveOutcome.TargetExists, $"target exists at promote, not overwritten: {ex.Message}", null)
                    : (false, MoveOutcome.Locked, $"promote refused, in-flight copy locked: {ex.Message}", null);
            }

            // Past the promote the move has happened whatever the delete does. Reporting it as
            // not-done would leave the caller's row naming the source, and the next run would copy the
            // file again and suffix past the survivor, with nothing bounding the pile.
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
            // The source is untouched: its delete runs only after a verified promote, which a cancel
            // never reaches.
            TryDelete(inFlightFull);
            return (false, MoveOutcome.Cancelled, "cancelled", null);
        }
        catch (IOException ex)
        {
            TryDelete(inFlightFull);
            // A destination present here is a racing writer that took the final name, not a promoted
            // copy: a post-promote delete failure is caught beside the delete and never reaches here.
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

    // The in-flight path for one call: the final path plus a marker and cryptographic randomness, in
    // the destination directory so the promote stays a same-directory atomic rename. No two calls
    // mint the same name, which is what makes an orphan from an earlier crash impossible to collide
    // with, promote or delete. Hex only, so the segment carries no separator and cannot move the copy
    // out of the destination directory. internal so the test pinning InFlightSuffixLength measures
    // the real segment.
    internal static string MintInFlightPath(string finalFull) =>
        finalFull
        + InFlightMarker
        + System.Security.Cryptography.RandomNumberGenerator.GetHexString(InFlightRandomChars, lowercase: true);

    // Reads the source once, feeding each slice to both the destination stream and a running hash, so
    // the source is never read a second time. XxHash3 is an integrity check, not a security control.
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
}
