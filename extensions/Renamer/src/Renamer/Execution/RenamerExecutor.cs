using Cove.Core.Events;
using Renamer.Options;
using Renamer.Planner;

namespace Renamer.Execution;

/// <summary>
/// The mutating half of the renamer slice. Consumes a <see cref="RenamerPlan"/> and applies each item
/// INDEPENDENTLY (one failure never aborts the batch) with the safety spine: execution-time
/// collision re-check (disk + DB) → disk move FIRST → set Basename/ParentFolderId (+ caption.Filename)
/// and a single save → assert the recomputed Path matches the on-disk path → revert-log + event; on a
/// post-move save failure, roll the disk back.
///
/// It NEVER assigns <c>BaseFileEntity.Path</c> (that is <c>CoveRenamerDataPort</c>'s job, and it
/// only sets Basename/ParentFolderId; Cove recomputes Path). The OS file lock is never forced: a
/// locked source surfaces as a skip from <see cref="DiskMover"/> rather than killing whatever holds it.
/// </summary>
public sealed class RenamerExecutor
{
    private readonly CoveRenamerDataPort _port;
    private readonly IEventBus _eventBus;
    private readonly RevertLog _revertLog;
    private readonly DiskMover _disk;
    private readonly CrossVolumeMover _cross;

    /// <summary>Bound on the execution-time collision suffix loop before giving up with a skip.</summary>
    private const int MaxSuffixAttempts = 1000;

    // The optional <paramref name="cross"/> mover is used when a move crosses volumes (different path
    // roots). It defaults to a fresh CrossVolumeMover() when omitted, so every existing 4-arg
    // construction site (production wiring + the test suite) stays source-compatible; a test may
    // inject a fault-seam / recording mover via this parameter.
    public RenamerExecutor(CoveRenamerDataPort port, IEventBus eventBus, RevertLog revertLog, DiskMover disk,
        CrossVolumeMover? cross = null)
    {
        _port = port;
        _eventBus = eventBus;
        _revertLog = revertLog;
        _disk = disk;
        _cross = cross ?? new CrossVolumeMover();
    }

    /// <summary>A per-item execution outcome surfaced in the run result's buckets.</summary>
    /// <param name="FileId">The file row.</param>
    /// <param name="OldPath">The path before execution.</param>
    /// <param name="NewPath">The path after execution (or the intended/attempted path on skip/fail).</param>
    /// <param name="Status">The terminal status (Renamer/Move/NoOp/SkipGated/SkipCollision/SkipLocked/SkipBlocked/Failed).</param>
    /// <param name="Reason">A human-readable note for a skip/fail; null on success.</param>
    public sealed record ItemResult(int FileId, string OldPath, string NewPath, RenamerStatus Status, string? Reason);

    /// <summary>
    /// The result of executing a plan: the items that renamed/moved, the items skipped (gated /
    /// collision / locked / no-op), the items that failed (save threw → disk rolled back), and the
    /// revert-log rows written for the successes.
    /// </summary>
    public sealed record RenamerRunResult(
        IReadOnlyList<ItemResult> Renamed,
        IReadOnlyList<ItemResult> Skipped,
        IReadOnlyList<ItemResult> Failed,
        IReadOnlyList<RevertLog.RevertEntry> RevertLog);

    /// <summary>
    /// Executes every item of <paramref name="plan"/> independently. Items the planner already
    /// classified as skip/no-op are carried into the skipped bucket untouched.
    /// </summary>
    /// <param name="plan">The single-entity plan whose items this executor acts on.</param>
    /// <param name="options">The renamer options (template / allowlist / suffix format).</param>
    /// <param name="preResolvedFolderIds">
    /// An optional <c>TargetFolderPath → folderId</c> map pre-resolved ONCE in the caller's
    /// sequential phase. When supplied, a Move item reads its destination folder id from this map
    /// instead of calling <see cref="CoveRenamerDataPort.GetOrCreateFolderIdAsync"/> here — so a batch
    /// running this executor across many parallel workers NEVER does a check-then-act folder create on
    /// shared <c>Folder</c> rows (which raced to duplicate-row creation / <c>DbUpdateException</c>). For
    /// a single-threaded caller (tests, the in-place path) the map may be null/absent for a path, in
    /// which case the executor falls back to resolving the folder itself — safe because there is no
    /// concurrency on that call path.
    /// </param>
    /// <param name="ct">Cancellation token; a genuine cancellation aborts the run.</param>
    public async Task<RenamerRunResult> ExecuteAsync(
        RenamerPlan plan, RenamerOptions options,
        IReadOnlyDictionary<string, int>? preResolvedFolderIds = null, CancellationToken ct = default)
    {
        var renamed = new List<ItemResult>();
        var skipped = new List<ItemResult>();
        var failed = new List<ItemResult>();

        // The plan carries DTO captions only via the loaded entity; reload it once so the executor
        // can resolve each file's sidecar set (the plan items reference file ids, not captions).
        var entity = await _port.LoadEntityAsync(plan.Kind, plan.EntityId, ct);
        var filesById = entity?.Files.ToDictionary(f => f.FileId) ?? [];

        foreach (var item in plan.Items)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await ExecuteItemAsync(plan, item, options, filesById, preResolvedFolderIds, renamed, skipped, failed, ct);
            }
            catch (Exception ex)
            {
                // Any unexpected throw outside the save path is reported as a failure for that item
                // only — the batch continues. (Save-path throws are handled inside the item.)
                failed.Add(new ItemResult(item.FileId, item.OldFullPath, item.NewFullPath, RenamerStatus.Failed,
                    $"unexpected error: {ex.Message}"));
            }
        }

        return new RenamerRunResult(renamed, skipped, failed, _revertLog.Rows);
    }

    private async Task ExecuteItemAsync(
        RenamerPlan plan, RenamerPlanItem item, RenamerOptions options,
        IReadOnlyDictionary<int, RenamerFile> filesById,
        IReadOnlyDictionary<string, int>? preResolvedFolderIds,
        List<ItemResult> renamed, List<ItemResult> skipped, List<ItemResult> failed,
        CancellationToken ct)
    {
        // (1) Carry planner skips/no-ops straight into the skipped bucket (act only on Renamer/Move).
        if (item.Status is not (RenamerStatus.Renamer or RenamerStatus.Move))
        {
            skipped.Add(new ItemResult(item.FileId, item.OldFullPath, item.NewFullPath, item.Status, item.Reason));
            return;
        }

        bool isMove = item.Status == RenamerStatus.Move;

        // (1b) CANONICAL ALLOWLIST GUARD — PRE-MUTATION. This MUST precede GetOrCreateFolderIdAsync
        //      (which persists a Folder DB row) and the collision loop (which calls File.Exists),
        //      so a destination the allowlist will reject never materializes a DB folder row pointing
        //      outside the allowlist nor touches disk. We canonically resolve the destination FOLDER's
        //      real on-disk target (following any junction/symlink and expanding 8.3) and reject when
        //      it escapes every configured root. Only runs on a destination move with a configured
        //      allowlist — with empty AllowedRoots the source-confine path is byte-identical and
        //      there is no allowlist to canonically re-check. A reject is a SkipBlocked skip
        //      carrying the guard reason (never a throw); the source then stays put as the fallback.
        //      The FINAL full destination path is re-checked again at (4b) once the candidate basename
        //      settles, so the leaf is guarded too — belt-and-suspenders for the security boundary.
        if (isMove && options.AllowedRoots.Count > 0)
        {
            var folderGuard = CanonicalPathGuard.Check(item.TargetFolderPath, options.AllowedRoots);
            if (!folderGuard.Accepted)
            {
                skipped.Add(new ItemResult(item.FileId, item.OldFullPath, item.NewFullPath,
                    RenamerStatus.SkipBlocked, folderGuard.Reason));
                return;
            }
        }

        // (2) Resolve the destination folder id. For an in-place renamer it is the source folder.
        //     For a Move, prefer the folder id the caller PRE-RESOLVED once in its sequential
        //     phase (keyed by TargetFolderPath) so no parallel worker does a check-then-act create on a
        //     shared Folder row. Fall back to resolving here only when no pre-resolved map is supplied
        //     (single-threaded callers / tests) — never on the parallel batch path, which always passes
        //     the map.
        var srcFile = filesById.GetValueOrDefault(item.FileId);
        int targetFolderId;
        if (isMove)
        {
            targetFolderId =
                preResolvedFolderIds is not null
                && preResolvedFolderIds.TryGetValue(item.TargetFolderPath, out var preId)
                    ? preId
                    : await _port.GetOrCreateFolderIdAsync(item.TargetFolderPath, ct);
        }
        else
        {
            targetFolderId = srcFile?.ParentFolderId ?? 0;
        }

        // (3) Execution-time COLLISION re-check: the planner's snapshot may be stale by now.
        //     Re-suffix against BOTH disk and DB until free; if never free → skip-collision.
        string targetFolder = item.TargetFolderPath;
        var (filename, ext) = SplitBasename(item.NewBasename);
        string candidate = item.NewBasename;
        string newFull = JoinPath(targetFolder, candidate);
        int attempt = 0;
        // The disk-side File.Exists check excludes only the source file's OWN case-variant slot: on a
        // case-insensitive volume File.Exists("Movie.mkv") is true while "movie.mkv" exists, but a
        // case-only renamer of the file onto itself is exactly the move the OS performs, not a clobber —
        // counting it as a collision would needlessly suffix it. A DIFFERENT file at that name (paths
        // not equal under the platform comparer) still collides. This restores symmetry with the
        // DB-side CollisionExistsAsync, which already excludes the source row (item.FileId).
        while ((System.IO.File.Exists(ToNative(newFull)) && !IsSelfPath(newFull, item.OldFullPath))
               || await _port.CollisionExistsAsync(targetFolderId, candidate, item.FileId, ct))
        {
            attempt++;
            if (attempt > MaxSuffixAttempts)
            {
                skipped.Add(new ItemResult(item.FileId, item.OldFullPath, newFull, RenamerStatus.SkipCollision,
                    $"skipped: no free target name within {MaxSuffixAttempts} suffix attempts"));
                return;
            }
            candidate = ApplySuffix(filename, ext, options.DuplicateSuffixFormat, attempt);
            newFull = JoinPath(targetFolder, candidate);
        }

        // (4) Compute sidecar moves: captions live next to the file; their Filename is
        //     a basename resolved against the OLD file dir, and tracks the NEW stem in the target dir.
        string oldDir = DirOf(item.OldFullPath);
        string newStem = StemOf(candidate);
        var captions = srcFile?.Captions ?? [];
        var plannedSidecars = new List<DiskMover.SidecarMove>(captions.Count);
        var captionRenames = new List<(int CaptionId, string NewFilename)>(captions.Count);
        foreach (var cap in captions)
        {
            string newCaptionName = RetargetCaption(cap.Filename, oldStem: StemOf(srcFile!.Basename), newStem);
            plannedSidecars.Add(new DiskMover.SidecarMove(
                JoinPath(oldDir, cap.Filename), JoinPath(targetFolder, newCaptionName)));
            captionRenames.Add((cap.CaptionId, newCaptionName));
        }

        // (4a) Extension-list sidecar discovery: a same-stem neighbor whose extension is configured
        //      moves with the primary, supplementing the captions above. Probe the PRECISE per-extension
        //      path (never a glob/EnumerateFiles) so only the exact stem + a listed extension is taken.
        //      These go to plannedSidecars ONLY and NEVER to captionRenames: unlike captions they are
        //      not DB-tracked, so there is no caption row to update — they are a disk-only move that
        //      rides the same skip-not-clobber + rollback-with-primary machinery the captions use.
        if (srcFile is not null && options.AssociatedExtensions.Count > 0)
        {
            string srcStem = StemOf(srcFile.Basename);
            foreach (var raw in options.AssociatedExtensions)
            {
                string normExt = raw.StartsWith('.') ? raw[1..] : raw;
                if (normExt.Length == 0)
                {
                    continue;
                }

                // A configured extension is a leaf extension, never a path fragment: reject any
                // separator or parent-traversal so a malformed entry (e.g. "srt/../../elsewhere")
                // can't build a sidecar target outside the primary's folder. The sidecars otherwise
                // inherit the primary's confinement only because they stay under oldDir/targetFolder.
                if (normExt.IndexOfAny(['/', '\\']) >= 0 || normExt.Contains(".."))
                {
                    continue;
                }

                string source = JoinPath(oldDir, srcStem + "." + normExt);
                if (!System.IO.File.Exists(ToNative(source)))
                {
                    continue;
                }

                string target = JoinPath(targetFolder, newStem + "." + normExt);

                // An in-place / case-only renamer leaves source == target; skipping it mirrors the
                // primary's self-path discipline and avoids a spurious skip-not-clobber warning.
                if (PathsEqual(source, target))
                {
                    continue;
                }

                // De-dupe against the captions already planned so a tracked caption that also matches
                // a listed extension is never moved twice.
                if (plannedSidecars.Any(s => PathsEqual(s.From, source)))
                {
                    continue;
                }

                plannedSidecars.Add(new DiskMover.SidecarMove(source, target));
            }
        }

        // (4b) CANONICAL ALLOWLIST RE-CHECK on the FINAL FULL DESTINATION PATH — the latest point
        //      before disk is touched, now that the candidate basename has settled. Unlike the
        //      pre-mutation folder check at (1b), this resolves the REAL on-disk target of the WHOLE
        //      path Move() actually writes to (folder + candidate), so a reparse point / 8.3 alias /
        //      separator introduced at the LEAF level — including a check/use swap of the leaf into a
        //      junction-to-elsewhere between (1b) and here — is re-resolved and re-contained.
        //      ResolveRealTargetFolder stacks the non-existent leaf, so passing the file path is safe.
        //      A reject is a SkipBlocked skip carrying the guard reason (never a throw); the source
        //      then stays put as the durable fallback.
        if (isMove && options.AllowedRoots.Count > 0)
        {
            var guard = CanonicalPathGuard.Check(newFull, options.AllowedRoots);
            if (!guard.Accepted)
            {
                skipped.Add(new ItemResult(item.FileId, item.OldFullPath, newFull, RenamerStatus.SkipBlocked, guard.Reason));
                return;
            }
        }

        // (5) DISK MOVE FIRST — move on disk before touching the DB, so a failed move leaves the
        //     database untouched (the DB stays authoritative and never points at a missing file).
        //     VOLUME BRANCH (runs STRICTLY AFTER the allowlist guards at (1b)/(4b)): a
        //     same-volume renamer takes the atomic synchronous DiskMover.Move fast path; a
        //     cross-volume move takes the verified copy→verify→promote→delete-source-last
        //     CrossVolumeMover.MoveAsync. Both return the identical MoveResult shape, so the skip
        //     handling and the DB-save flow below are unchanged. The matching mover is also used for
        //     rollback in the save-failure catch.
        string nativeOld = ToNative(item.OldFullPath);
        string nativeNew = ToNative(newFull);
        bool sameVolume = VolumeClassifier.SameVolume(item.OldFullPath, newFull);

        bool moved;
        string? moveReason;
        IReadOnlyList<(string From, string To)> movedSidecars;
        if (sameVolume)
        {
            var move = _disk.Move(nativeOld, nativeNew,
                [.. plannedSidecars.Select(s => new DiskMover.SidecarMove(ToNative(s.From), ToNative(s.To)))]);
            moved = move.Moved;
            moveReason = move.Reason;
            movedSidecars = [.. move.MovedSidecars.Select(s => (s.From, s.To))];
        }
        else
        {
            var move = await _cross.MoveAsync(nativeOld, nativeNew,
                [.. plannedSidecars.Select(s => new CrossVolumeMover.SidecarMove(ToNative(s.From), ToNative(s.To)))], ct);
            moved = move.Moved;
            moveReason = move.Reason;
            movedSidecars = [.. move.MovedSidecars.Select(s => (s.From, s.To))];
        }

        if (!moved)
        {
            // Locked source / existing destination / failed verify at move time → skip+report.
            // The cross MoveResult shape matches DiskMover's, so VerifyFailed/LockedOrExists/
            // PermissionDenied all flow into the SkipLocked bucket exactly as today —
            // one item's failure never aborts the batch. Never force the lock.
            skipped.Add(new ItemResult(item.FileId, item.OldFullPath, newFull, RenamerStatus.SkipLocked, moveReason));
            return;
        }

        // Only the captions that actually moved on disk get their DB Filename updated.
        var movedCaptionNames = movedSidecars
            .Select(s => BasenameOf(NormalizeSlash(s.To)))
            .ToHashSet(StringComparer.Ordinal);
        var appliedCaptionRenames = captionRenames
            .Where(cr => movedCaptionNames.Contains(cr.NewFilename))
            .ToList();

        // (6) DB SAVE second; on a save throw, ROLLBACK the disk.
        var mutation = new RenamerFileMutation(
            item.FileId, candidate, isMove ? targetFolderId : null,
            appliedCaptionRenames.Count > 0 ? appliedCaptionRenames : null);

        try
        {
            var saved = await _port.ApplyAndSaveAsync([mutation], ct);

            // (7) RUNTIME assertion (NOT Debug.Assert — that no-ops in Release): the Path Cove
            //     recomputed on save must match the on-disk location we just moved to. A divergence
            //     means disk and DB disagree → surface a Failed result (do NOT silently accept).
            var savedFile = saved.FirstOrDefault(s => s.FileId == item.FileId);
            string expected = NormalizeSlash(newFull);
            if (!PathsEqual(savedFile.RecomputedPath, expected))
            {
                failed.Add(new ItemResult(item.FileId, item.OldFullPath, newFull, RenamerStatus.Failed,
                    $"recomputed Path '{savedFile.RecomputedPath}' != on-disk path '{expected}'"));
                return;
            }

            // (8) Success: revert-log row + reindex event. The logged row carries plan.EntityId —
            //     the SAME value published on the next line — so the logged id and the event id are
            //     identical by construction (undo reconstructs the exact forward event from the row).
            await _revertLog.AppendAsync(plan.EntityId, item.FileId, item.OldFullPath, newFull, ct);
            _eventBus.Publish(new EntityEvent(EventTypeFor(plan.Kind), EntityTypeName(plan.Kind), plan.EntityId));

            // (9) Opt-in empty-source-folder cleanup, AFTER the save + assertion pass — never before
            //     (a failed save rolls the disk back, so deleting the source dir earlier could be
            //     unrecoverable). Fires only when a move actually changed the parent directory: a
            //     same-folder renamer leaves the file in that dir, so the dir is never empty. A cleanup
            //     failure is a non-fatal warning carried on the moved result — the move already
            //     succeeded and the DB agrees, so it must never reclassify the item as failed.
            string? cleanupWarning = null;
            if (isMove && options.RemoveEmptyFolder && !PathsEqual(DirOf(item.OldFullPath), DirOf(newFull)))
            {
                (_, cleanupWarning) = EmptySourceFolderCleaner.TryRemoveIfEmpty(DirOf(item.OldFullPath), options.AllowedRoots);
            }

            renamed.Add(new ItemResult(item.FileId, item.OldFullPath, newFull, item.Status, cleanupWarning));
        }
        catch (Exception ex)
        {
            // Save failed AFTER a successful move (e.g. the (ParentFolderId,Basename) unique index
            // threw a DbUpdateException). Roll the disk back through the SAME mover that performed the
            // move so disk + DB stay consistent: a same-volume move rolls back via the atomic
            // DiskMover.Rollback; a verified cross-volume move rolls back via the copy-back→verify→
            // delete CrossVolumeMover.RollbackAsync — the disk-first/DB-second discipline still holds for
            // the cross path (the bytes that crossed the volume are copied back and the source is restored).
            // Capture the rollback warnings. A best-effort rollback can FAIL to restore (the old slot
            // got re-occupied, a cross-volume copy-back failed verify, a target is locked) and reports
            // that in its warnings list. Discarding them would claim "file rolled back" on a rollback
            // that did not happen — a silent disk/DB divergence. Surface them so a failed restore is
            // visible (especially on the cross path, where a copy-back can fail in ways an atomic
            // same-volume File.Move rollback cannot).
            IReadOnlyList<string> rbWarnings = sameVolume
                ? _disk.Rollback(nativeOld, nativeNew,
                    [.. movedSidecars.Select(s => new DiskMover.SidecarMove(s.From, s.To))])
                : await _cross.RollbackAsync(nativeOld, nativeNew,
                    [.. movedSidecars.Select(s => new CrossVolumeMover.SidecarMove(s.From, s.To))], ct);

            string note = rbWarnings.Count > 0
                ? $"DB save failed; rollback INCOMPLETE: {ex.Message}; rollback warnings: {string.Join("; ", rbWarnings)}"
                : $"DB save failed; file rolled back: {ex.Message}";
            failed.Add(new ItemResult(item.FileId, item.OldFullPath, newFull, RenamerStatus.Failed, note));
        }
    }

    // ── event mapping ────────────────────────────────────────────────────────

    private static EventType EventTypeFor(RenamerFileKind kind) => kind switch
    {
        RenamerFileKind.Video => EventType.VideoUpdated,
        RenamerFileKind.Image => EventType.ImageUpdated,
        RenamerFileKind.Audio => EventType.AudioUpdated,
        _ => EventType.VideoUpdated,
    };

    private static string EntityTypeName(RenamerFileKind kind) => kind switch
    {
        RenamerFileKind.Video => "Video",
        RenamerFileKind.Image => "Image",
        RenamerFileKind.Audio => "Audio",
        _ => "Video",
    };

    // ── path/name helpers (pure string math) ─────────────────────────────────

    private static (string filename, string ext) SplitBasename(string basename)
    {
        int dot = basename.LastIndexOf('.');
        return dot > 0 ? (basename[..dot], basename[dot..]) : (basename, "");
    }

    private static string ApplySuffix(string filename, string ext, string suffixFormat, int counter)
        => filename + suffixFormat.Replace("{n}", counter.ToString(System.Globalization.CultureInfo.InvariantCulture)) + ext;

    /// <summary>The stem (name without its final extension): "video.mkv" → "video"; "video.en.vtt" → "video.en".</summary>
    private static string StemOf(string basename)
    {
        int dot = basename.LastIndexOf('.');
        return dot > 0 ? basename[..dot] : basename;
    }

    /// <summary>
    /// Retargets a caption basename from the old stem to the new stem. A caption "video.en.vtt"
    /// alongside "video.mkv" (oldStem "video") becomes "&lt;newStem&gt;.en.vtt". If the caption does
    /// not start with the old stem (unexpected), it is left unchanged so nothing is corrupted.
    /// </summary>
    private static string RetargetCaption(string captionFilename, string oldStem, string newStem)
        => captionFilename.StartsWith(oldStem, StringComparison.Ordinal)
            ? newStem + captionFilename[oldStem.Length..]
            : captionFilename;

    private static string JoinPath(string a, string b)
    {
        if (string.IsNullOrEmpty(a))
        {
            return b;
        }

        if (string.IsNullOrEmpty(b))
        {
            return a;
        }

        return a.TrimEnd('/', '\\') + "/" + b.TrimStart('/', '\\');
    }

    private static string DirOf(string fullPath)
    {
        string p = NormalizeSlash(fullPath);
        int slash = p.LastIndexOf('/');
        return slash >= 0 ? p[..slash] : "";
    }

    private static string BasenameOf(string fullPath)
    {
        string p = NormalizeSlash(fullPath);
        int slash = p.LastIndexOf('/');
        return slash >= 0 ? p[(slash + 1)..] : p;
    }

    private static string NormalizeSlash(string p) => p.Replace('\\', '/');

    private static string ToNative(string p) => p.Replace('/', Path.DirectorySeparatorChar);

    private static bool PathsEqual(string? a, string b)
    {
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(NormalizeSlash(a ?? ""), NormalizeSlash(b), cmp);
    }

    /// <summary>True iff <paramref name="candidate"/> is the source file's own path — the same
    /// canonical location differing at most by case on a case-insensitive volume. Mirrors the
    /// <see cref="PathsEqual"/>/<c>VolumeClassifier</c> OS-aware policy so the disk-side self-exclusion
    /// agrees with the rest of the slice on what counts as the same path.</summary>
    private static bool IsSelfPath(string candidate, string sourceFullPath) => PathsEqual(candidate, sourceFullPath);
}
