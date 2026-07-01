namespace Renamer.Execution;

/// <summary>
/// The cross-volume tier of the executor: the data-loss-critical copy → verify(size + hash) →
/// atomic-renamer → delete-source primitive used when a renamer/move crosses volumes (where the
/// atomic same-volume <see cref="System.IO.File.Move(string,string)"/> that <see cref="DiskMover"/>
/// uses is not available — a cross-volume <c>File.Move</c> is an opaque, unverified, non-atomic
/// copy-then-delete that leaves a silent duplicate on a locked source). It mirrors
/// <see cref="DiskMover"/>'s shape verbatim — the same <see cref="SidecarMove"/>,
/// <see cref="MoveResult"/> record and (extended) <see cref="MoveOutcome"/> enum, the same
/// classify-not-throw discipline, skip-not-clobber sidecars, and best-effort rollback — so the
/// <c>RenamerExecutor</c> consumes its result identically.
///
/// SAFETY CONTRACT (the strict, NEVER-REORDERED sequence per file):
/// <list type="number">
/// <item>Copy the source → a <c>.renamer-partial</c> on the destination volume via an async
/// <see cref="FileStream"/> loop (1 MiB buffer, <see cref="FileMode.CreateNew"/> on the partial →
/// no-clobber), feeding each read buffer to BOTH the destination and a running
/// <see cref="System.IO.Hashing.XxHash3"/> so the SOURCE is read EXACTLY ONCE (single-pass hash).
/// The destination is then flushed-to-disk (<see cref="FileStream.Flush(bool)"/> with
/// <c>flushToDisk:true</c> → an fsync/FlushFileBuffers) so the copied bytes are DURABLE on physical
/// media before the stream closes — not merely sitting in the OS write-back cache.</item>
/// <item>Verify: re-open the (now media-durable) <c>.partial</c> fresh from disk and hash it
/// independently; the copy is accepted only when BOTH the size AND the content hash match the
/// source-pass values. This is the destination's SINGLE re-read (the source is never read a second
/// time). A size-only check would false-pass a same-length torn write, so the hash is the authority.
/// On any mismatch the suspect <c>.partial</c> is deleted and the result is
/// <see cref="MoveOutcome.VerifyFailed"/> — the SOURCE IS UNTOUCHED.</item>
/// <item>Promote: a 2-arg <see cref="System.IO.File.Move(string,string)"/> renamers the verified
/// <c>.partial</c> → the final name (same volume → atomic; throws if the final already exists →
/// no-clobber).</item>
/// <item>Delete the source ONLY after the promote in (3) succeeds. The source is the durable
/// fallback until this last step. Because the destination data was forced to media in (1) and the
/// verify in (2) confirmed it, a crash at any point — process crash OR power loss / OS crash —
/// leaves EITHER the intact source (steps 1-3) OR the verified, media-durable final (after 3),
/// never a lost or duplicated file. (The one residual filesystem-dependent window is the
/// <see cref="System.IO.File.Move(string,string)"/> renamer's directory-entry durability in (3); the data extents
/// themselves are already durable. A leftover <c>.partial</c> from a crash is cleaned up and NEVER
/// promoted unverified.)</item>
/// </list>
/// classify-not-throw: a locked source / existing destination (<see cref="IOException"/>) → a
/// <see cref="MoveOutcome.LockedOrExists"/> skip; a permission denial
/// (<see cref="UnauthorizedAccessException"/>) → a <see cref="MoveOutcome.PermissionDenied"/> skip;
/// a failed verify → <see cref="MoveOutcome.VerifyFailed"/>; a cancelled token → a
/// <see cref="MoveOutcome.Cancelled"/> skip (the in-flight <c>.partial</c> cleaned up first). NEVER a
/// throw, NEVER a source delete on failure, NEVER a corrupt or duplicated file.
///
/// Pure <see cref="System.IO"/> + <see cref="System.IO.Hashing"/> — no <c>CoveContext</c>/EF
/// dependency, no static/global state (so it is concurrency-agnostic; concurrency is bounded by the
/// caller, per (src,dst) pair) — so it is testable purely against a real temp directory,
/// called DIRECTLY regardless of the real volume layout (a second physical drive is NOT required).
///
/// Hash algorithm: the hard default is to self-hash both sides with XxHash3 (a fast
/// non-crypto integrity check, not a security control). Reusing Cove's stored MD5 to skip the source
/// read is a deferred TODO — Cove's MD5 lives in a <c>FileFingerprint</c> row the renamer data port
/// does not load today, so the stored hash is invisible to the mover; the reuse path is NOT built.
/// </summary>
public sealed class CrossVolumeMover
{
    /// <summary>1 MiB copy/hash buffer — matches File.Copy throughput on multi-GB sequential I/O
    /// (the default 4 KiB / CopyTo's 80 KiB are too small). FileOptions.SequentialScan is a no-op on
    /// modern Windows and is deliberately NOT set; only FileOptions.Asynchronous is worth setting.</summary>
    private const int BufferSize = 1 << 20;

    /// <summary>The suffix appended to the final destination path for the in-flight, unverified copy.</summary>
    private const string PartialSuffix = ".renamer-partial";

    /// <summary>
    /// TEST-ONLY fault-injection seam. When non-null, it is invoked on the closed <c>.partial</c>
    /// AFTER the copy but BEFORE the verify, with the partial's absolute path — letting a test corrupt
    /// (bit-flip / truncate) the copy to prove the verify catches it and the source survives. The
    /// production path leaves this null (a no-op), so the live copy is never mutated. Tests construct
    /// the mover with this hook; nothing in the executor ever sets it.
    /// </summary>
    private readonly Func<string, CancellationToken, Task>? _postCopyFaultForTests;

    /// <summary>Production constructor — no fault hook; the live copy path is never mutated.</summary>
    public CrossVolumeMover()
        : this(null)
    {
    }

    /// <summary>
    /// TEST-ONLY constructor wiring the post-copy fault seam (see
    /// <see cref="_postCopyFaultForTests"/>). Production code uses the parameterless constructor.
    /// </summary>
    /// <param name="postCopyFaultForTests">Invoked on the closed <c>.partial</c> path between copy and
    /// verify to inject a fault; null in production (no-op).</param>
    public CrossVolumeMover(Func<string, CancellationToken, Task>? postCopyFaultForTests)
    {
        _postCopyFaultForTests = postCopyFaultForTests;
    }

    /// <summary>One planned sidecar move: absolute source → absolute destination (forward/native slashes ok).</summary>
    public readonly record struct SidecarMove(string From, string To);

    /// <summary>
    /// The outcome of a <see cref="MoveAsync"/>: whether the primary file moved, the sidecars that were
    /// actually moved (for rollback), any skip warnings, and a classification + reason when the primary
    /// move did not happen. A non-<see cref="Moved"/> result is a SKIP, never a thrown error. The shape
    /// is IDENTICAL to <see cref="DiskMover.MoveResult"/> so the executor call site is unchanged.
    /// </summary>
    /// <param name="Moved">True iff the primary file was copied→verified→promoted and the source deleted.</param>
    /// <param name="Outcome">The classification of the primary move attempt.</param>
    /// <param name="MovedSidecars">The sidecar pairs that actually moved (in move order) — what rollback reverses.</param>
    /// <param name="Warnings">Non-fatal notes (e.g. a skipped sidecar whose target already existed).</param>
    /// <param name="Reason">A human-readable reason when the primary move was skipped; null on success.</param>
    public sealed record MoveResult(
        bool Moved,
        MoveOutcome Outcome,
        IReadOnlyList<SidecarMove> MovedSidecars,
        IReadOnlyList<string> Warnings,
        string? Reason);

    /// <summary>How a primary cross-volume move attempt was classified. The first three members mirror
    /// <see cref="DiskMover.MoveOutcome"/>; the last two are the cross-volume additions.</summary>
    public enum MoveOutcome
    {
        /// <summary>The file was copied, verified, atomically promoted, and the source deleted.</summary>
        Moved,

        /// <summary>The source was locked/in-use OR the final destination (or the <c>.partial</c>) already existed.</summary>
        LockedOrExists,

        /// <summary>The OS denied permission for the copy/promote/delete.</summary>
        PermissionDenied,

        /// <summary>The destination read-back did not match the source by size or content hash — the copy
        /// was rejected, the suspect destination deleted, and the source left intact.</summary>
        VerifyFailed,

        /// <summary>The destination volume lacked free space. Reserved here; whole-batch free-space accounting lives in <see cref="FreeSpaceGuard"/>.</summary>
        InsufficientSpace,

        /// <summary>The caller cancelled the <see cref="CancellationToken"/> mid-move. The in-flight
        /// <c>.partial</c> is cleaned up and the source is left untouched — a cancel never loses or
        /// duplicates a file and never throws out (classify-not-throw).</summary>
        Cancelled,
    }

    /// <summary>
    /// Copies <paramref name="oldFull"/> → <paramref name="newFull"/> across volumes via the strict
    /// never-reordered copy → verify(size + hash) → atomic-renamer → delete-source-last sequence, then
    /// moves each planned sidecar skip-not-clobber through the SAME sequence. A locked source, an
    /// existing destination, or a leftover/locked <c>.partial</c> is caught and returned as a
    /// <see cref="MoveOutcome.LockedOrExists"/> skip; a permission failure as
    /// <see cref="MoveOutcome.PermissionDenied"/>; a destination that does not match the source by size
    /// or hash as <see cref="MoveOutcome.VerifyFailed"/>; a cancelled <paramref name="ct"/> as
    /// <see cref="MoveOutcome.Cancelled"/>. On any failure the source is never deleted and the suspect
    /// <c>.partial</c>/destination is removed — NEVER overwrites, NEVER leaves a corrupt or duplicated
    /// file, NEVER throws out (cancellation is classified, not propagated).
    /// </summary>
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
                    moved.Add(sc);
                }
                else
                {
                    // A locked/racy/unverifiable sidecar is non-fatal: warn and leave it (the primary moved).
                    warnings.Add($"sidecar move failed ({scResult.Outcome}), skipped: {sc.From} -> {sc.To}: {scResult.Reason}");
                }
            }
        }

        return new MoveResult(true, MoveOutcome.Moved, moved, warnings, null);
    }

    /// <summary>
    /// Reverses a successful <see cref="MoveAsync"/> for the rollback path (e.g. a DB save threw after a
    /// verified cross-move): copies each moved sidecar back to its source first (innermost-first), then
    /// the primary file <paramref name="newFull"/> → <paramref name="oldFull"/>, each through the same
    /// copy→verify→delete discipline. Best-effort: a secondary failure (the old slot got re-occupied, a
    /// verify failed, the target is locked) is swallowed into the returned warnings list rather than
    /// thrown, so a failed save's cleanup can never itself crash the batch. Returns the warnings (empty
    /// when the restore was clean).
    /// </summary>
    public async Task<IReadOnlyList<string>> RollbackAsync(
        string oldFull,
        string newFull,
        IReadOnlyList<SidecarMove> movedSidecars,
        CancellationToken ct)
    {
        var warnings = new List<string>();

        // Reverse sidecars first (innermost moves undone first), then the primary file.
        for (int i = movedSidecars.Count - 1; i >= 0; i--)
        {
            var sc = movedSidecars[i];
            await SafeCopyBackAsync(sc.To, sc.From, warnings, ct).ConfigureAwait(false);
        }

        await SafeCopyBackAsync(newFull, oldFull, warnings, ct).ConfigureAwait(false);
        return warnings;
    }

    /// <summary>
    /// The single-file engine: the strict copy → verify → atomic-promote → delete-source-last sequence
    /// with classify-not-throw. Returns whether it succeeded plus the outcome/reason to surface.
    /// </summary>
    private async Task<(bool Ok, MoveOutcome Outcome, string? Reason)> CopyVerifyPromoteDeleteAsync(
        string srcFull,
        string finalFull,
        CancellationToken ct)
    {
        // (0) No-clobber pre-check: an existing final destination is never overwritten.
        if (System.IO.File.Exists(finalFull))
        {
            return (false, MoveOutcome.LockedOrExists, $"target exists, not overwritten: {finalFull}");
        }

        var partialFull = finalFull + PartialSuffix;

        // Clean a stale .partial from a crashed prior run — it is unverified and must NEVER be promoted.
        // FileMode.CreateNew in the copy would otherwise throw on it; removing it lets a fresh, verified
        // copy proceed. (If it is locked, the copy's CreateNew surfaces a LockedOrExists skip below.)
        TryDelete(partialFull);

        try
        {
            EnsureParentDir(partialFull);

            // (1) Single-pass copy + source hash → .partial (CreateNew = no-clobber on the partial).
            var (srcSize, srcHash) = await CopyAndHashAsync(srcFull, partialFull, ct).ConfigureAwait(false);

            // TEST-ONLY fault seam: corrupt the closed .partial between copy and verify. No-op in production.
            if (_postCopyFaultForTests is not null)
            {
                await _postCopyFaultForTests(partialFull, ct).ConfigureAwait(false);
            }

            // (2) Verify against a FRESH destination read — size AND hash (never size-only, never the
            // in-flight buffer). On mismatch delete the suspect copy and keep the source untouched.
            var (dstSize, dstHash) = await HashFileAsync(partialFull, ct).ConfigureAwait(false);
            bool verified = dstSize == srcSize && dstHash.AsSpan().SequenceEqual(srcHash);
            if (!verified)
            {
                TryDelete(partialFull);
                return (false, MoveOutcome.VerifyFailed, "verify failed: destination size or hash mismatch");
            }

            // (3) Atomic same-volume promote .partial → final (2-arg Move = no-clobber on the final).
            try
            {
                System.IO.File.Move(partialFull, finalFull);
            }
            catch (IOException ex)
            {
                TryDelete(partialFull);
                return (false, MoveOutcome.LockedOrExists, $"final exists or locked: {ex.Message}");
            }

            // (4) Delete the source ONLY after the promote succeeds (delete-last). The source was the
            // durable fallback until now; a crash before here leaves the intact source, after here the
            // verified final.
            System.IO.File.Delete(srcFull);
            return (true, MoveOutcome.Moved, null);
        }
        catch (OperationCanceledException)
        {
            // A cancelled token throws OperationCanceledException out of the Read/WriteAsync loop.
            // Honor classify-not-throw (the executor relies on the mover NEVER throwing out, like the
            // synchronous DiskMover): clean the in-flight .partial so a cancel leaves no leaked,
            // unverified copy, and return a classified Cancelled skip. The source is untouched — the
            // delete only runs after a verified promote, which a cancel never reaches.
            TryDelete(partialFull);
            return (false, MoveOutcome.Cancelled, "cancelled");
        }
        catch (IOException ex)
        {
            // Covers a locked source, an existing/locked .partial (CreateNew), and torn I/O. Skip + report;
            // never force, never delete the source. Clean any suspect partial we may have created.
            TryDelete(partialFull);
            return (false, MoveOutcome.LockedOrExists, $"locked or target exists: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            TryDelete(partialFull);
            return (false, MoveOutcome.PermissionDenied, $"permission denied: {ex.Message}");
        }
    }

    /// <summary>
    /// Single-pass async copy + hash: reads <paramref name="srcNative"/> once into a reused 1 MiB
    /// buffer, feeding each slice to BOTH the destination <c>.partial</c> stream and a running
    /// <see cref="System.IO.Hashing.XxHash3"/>. The <c>.partial</c> is opened
    /// <see cref="FileMode.CreateNew"/> (no-clobber). Returns the source size + the source-pass hash
    /// digest computed in the SAME read pass (no second source read).
    /// </summary>
    private static async Task<(long Size, byte[] Hash)> CopyAndHashAsync(
        string srcNative,
        string partialNative,
        CancellationToken ct)
    {
        var hash = new System.IO.Hashing.XxHash3();
        long total = 0;

        await using var src = new FileStream(
            srcNative, FileMode.Open, FileAccess.Read, FileShare.Read,
            BufferSize, FileOptions.Asynchronous);
        // CreateNew → throws IOException if the .partial already exists (no-clobber).
        await using var dst = new FileStream(
            partialNative, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            BufferSize, FileOptions.Asynchronous);

        var buffer = new byte[BufferSize];
        int read;
        while ((read = await src.ReadAsync(buffer.AsMemory(0, BufferSize), ct).ConfigureAwait(false)) > 0)
        {
            var slice = buffer.AsMemory(0, read);
            hash.Append(slice.Span);                               // hash the source in the SAME pass
            await dst.WriteAsync(slice, ct).ConfigureAwait(false); // write the destination
            total += read;
        }

        await dst.FlushAsync(ct).ConfigureAwait(false);
        // Force the OS write-back cache → physical media BEFORE the stream closes, the verify
        // re-reads, and the source is deleted. FlushAsync alone only drains the managed/OS buffer
        // into the OS file cache; flushToDisk:true issues the FlushFileBuffers (fsync) so the bytes
        // are durable on the platter. Without this the verify would re-read the same volatile cache
        // and a power loss after File.Delete(source) could leave a non-durable destination — i.e.
        // data loss. This is what makes the "interrupted transfer never loses the original" contract
        // hold across power loss / OS crash, not merely a managed process crash.
        dst.Flush(flushToDisk: true);
        return (total, hash.GetCurrentHash());
    }

    /// <summary>
    /// Re-reads <paramref name="native"/> fresh from disk (after the copy stream is flushed and closed)
    /// and computes its size + <see cref="System.IO.Hashing.XxHash3"/> digest independently, so the
    /// verify confirms what ACTUALLY landed on disk rather than trusting the in-flight copy buffer.
    /// </summary>
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

    /// <summary>Best-effort copy-back <paramref name="from"/> → <paramref name="to"/> for rollback;
    /// records (never throws) on failure, mirroring <see cref="DiskMover"/>'s SafeMoveBack contract but
    /// using the verified cross-volume copy→verify→delete sequence.</summary>
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
        }
        catch (Exception ex)
        {
            warnings.Add($"rollback move failed {from} -> {to}: {ex.Message}");
        }
    }

    /// <summary>Deletes <paramref name="path"/> if present, swallowing any failure (best-effort cleanup
    /// of a suspect/leftover <c>.partial</c> — never throws).</summary>
    private static void TryDelete(string path)
    {
        try
        {
            if (System.IO.File.Exists(path))
            {
                System.IO.File.Delete(path);
            }
        }
        catch
        {
            // Best-effort: a leftover .partial we cannot delete is never promoted, so it is harmless.
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
