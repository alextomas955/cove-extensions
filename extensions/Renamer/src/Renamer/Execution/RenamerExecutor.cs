using Cove.Core.Events;
using Renamer.Options;
using Renamer.Planner;

using static global::Renamer.Execution.PathOps;

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
    private readonly IRenamerDataPort _port;
    private readonly IEventBus _eventBus;
    private readonly IRevertJournal _journal;
    private readonly string _runId;
    private readonly DiskMover _disk;
    private readonly CrossVolumeMover _cross;

    /// <summary>Bound on the execution-time collision suffix loop before giving up with a skip.</summary>
    private const int MaxSuffixAttempts = 1000;

    // The <paramref name="runId"/> names the batch every journalled row belongs to, and is required
    // rather than defaulted: a row that names no batch is one the journal can store but never read
    // back, so an undo would silently find nothing. The caller passes the SAME run id it opened the
    // batch with.
    //
    // The optional <paramref name="cross"/> mover is used when a move crosses volumes (different path
    // roots). It defaults to a fresh CrossVolumeMover() when omitted, so every existing construction
    // site (production wiring + the test suite) stays source-compatible; a test may inject a
    // fault-seam / recording mover via this parameter.
    public RenamerExecutor(IRenamerDataPort port, IEventBus eventBus, IRevertJournal journal, string runId,
        DiskMover disk, CrossVolumeMover? cross = null)
    {
        _port = port;
        _eventBus = eventBus;
        _journal = journal;
        _runId = runId;
        _disk = disk;
        _cross = cross ?? new CrossVolumeMover();
    }

    /// <summary>A per-item execution outcome surfaced in the run result's buckets.</summary>
    /// <param name="FileId">The file row.</param>
    /// <param name="OldPath">The path before execution.</param>
    /// <param name="NewPath">The path after execution (or the intended/attempted path on skip/fail).</param>
    /// <param name="Status">The terminal status (Renamer/Move/NoOp/SkipGated/SkipCollision/SkipLocked/SkipMissingSource/SkipBlocked/Failed).</param>
    /// <param name="Reason">A human-readable note for a skip/fail; null on success.</param>
    public sealed record ItemResult(int FileId, string OldPath, string NewPath, RenamerStatus Status, string? Reason);

    /// <summary>
    /// The result of executing a plan: the items that renamed/moved, the items skipped (gated /
    /// collision / locked / no-op), and the items that failed (save threw → disk rolled back).
    /// </summary>
    /// <remarks>
    /// It deliberately carries NO copy of the journalled rows. The journal table is the durable record
    /// of what can be put back, so a second in-memory list of the same facts could only drift from it —
    /// and it would have to be thread-safe as well, since one result object is built per worker while
    /// one journal is shared by all of them. A caller that wants to know what was journalled reads the
    /// journal.
    /// </remarks>
    public sealed record RenamerRunResult(
        IReadOnlyList<ItemResult> Renamed,
        IReadOnlyList<ItemResult> Skipped,
        IReadOnlyList<ItemResult> Failed);

    /// <summary>
    /// Executes every item of <paramref name="plan"/> independently. Items the planner already
    /// classified as skip/no-op are carried into the skipped bucket untouched.
    /// </summary>
    /// <param name="plan">The single-entity plan whose items this executor acts on.</param>
    /// <param name="options">The renamer options (template / suffix format).</param>
    /// <param name="preResolvedFolderIds">
    /// An optional <c>TargetFolderPath → folderId</c> map pre-resolved ONCE in the caller's
    /// sequential phase. When supplied, a Move item reads its destination folder id from this map
    /// instead of calling <see cref="IRenamerDataPort.GetOrCreateFolderIdAsync"/> here — so a batch
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
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A cancellation (host shutdown) is cancellation, not a per-item failure — the filter
                // excludes it. Any other unexpected throw outside the save path is reported as a failure
                // for that item only — the batch continues. (Save-path throws are handled inside the item.)
                failed.Add(new ItemResult(item.FileId, item.OldFullPath, item.NewFullPath, RenamerStatus.Failed,
                    $"unexpected error: {ex.Message}"));
            }
        }

        return new RenamerRunResult(renamed, skipped, failed);
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

        // (1a) SOURCE-PRESENCE PRE-CHECK — the first thing done for an item we will actually act on,
        //      BEFORE the folder resolve, the collision loop, and the mover.
        //      A source that is in the DB but gone from disk would otherwise fall through to the mover
        //      and surface as a swallowed FileNotFoundException/DirectoryNotFoundException — both
        //      derive from IOException, which the mover buckets as LockedOrExists → SkipLocked, so a
        //      genuinely-gone file would be mislabeled "in use". Pre-empting the mover here classifies
        //      it as SkipMissingSource instead. This is a safe no-op skip (nothing moves/saves).
        if (!System.IO.File.Exists(ToNative(item.OldFullPath)))
        {
            skipped.Add(new ItemResult(item.FileId, item.OldFullPath, item.NewFullPath,
                RenamerStatus.SkipMissingSource, "skipped: source file is missing on disk"));
            return;
        }

        bool isMove = item.Status == RenamerStatus.Move;

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

        // (3b) Re-measure the absolute-path budget against the SETTLED candidate. The loop above
        //      re-suffixes against a fresher snapshot than the plan saw, so it can reach a name longer
        //      than any the plan measured. Nothing is mutated yet - this precedes the sidecar plan, the
        //      canonical guard and every disk write - so the source stays where it is.
        var budget = PathConfinement.WithinBudget(targetFolder, candidate, options);
        if (!budget.Accepted)
        {
            skipped.Add(new ItemResult(item.FileId, item.OldFullPath, newFull, RenamerStatus.SkipTooLong,
                budget.Reason));
            return;
        }

        // (4) Sidecar moves: DB-tracked captions + configured same-stem neighbors that ride with the primary.
        var (plannedSidecars, captionRenames, sidecarWarnings) =
            PlanSidecarMoves(srcFile, item.OldFullPath, targetFolder, candidate, options);

        // (5) DISK MOVE FIRST — move on disk before touching the DB, so a failed move leaves the
        //     database untouched (the DB stays authoritative and never points at a missing file).
        //     VOLUME BRANCH: a
        //     same-volume renamer takes the atomic synchronous DiskMover.Move fast path; a
        //     cross-volume move takes the verified copy→verify→promote→delete-source-last
        //     CrossVolumeMover.MoveAsync. Both return the identical MoveResult shape, so the skip
        //     handling and the DB-save flow below are unchanged. The matching mover is also used for
        //     rollback in the save-failure catch.
        string nativeOld = ToNative(item.OldFullPath);
        string nativeNew = ToNative(newFull);
        bool sameVolume = VolumeClassifier.SameVolume(item.OldFullPath, newFull);

        var (moved, moveReason, moveOutcome, movedSidecars) =
            await MoveOnDisk(sameVolume, nativeOld, nativeNew, plannedSidecars, ct);

        if (!moved)
        {
            // The mover's own classification decides the status, through the one mapping both tiers
            // share: a lock, a denial, a failed verify and a clean shutdown ask an operator for
            // different things. One item's failure never aborts the batch, and the lock is never forced.
            skipped.Add(new ItemResult(
                item.FileId, item.OldFullPath, newFull,
                MoveOutcomeClassifier.StatusFor(moveOutcome), moveReason));
            return;
        }

        // Only the captions that actually moved on disk get their DB Filename updated.
        var movedCaptionNames = movedSidecars
            .Select(s => BasenameOf(NormalizeSlash(s.To)))
            .ToHashSet(StringComparer.Ordinal);
        var appliedCaptionRenames = captionRenames
            .Where(cr => movedCaptionNames.Contains(cr.NewFilename))
            .ToList();

        // (6) DB SAVE second; on a save throw, ROLLBACK the disk. The filename-derived title rides in
        //     the SAME save as the rename that produced it - see MetadataProjector.DerivedTitle for why
        //     recording it at all is what makes the rename converge.
        var mutation = new RenamerFileMutation(
            item.FileId, candidate, isMove ? targetFolderId : null,
            appliedCaptionRenames.Count > 0 ? appliedCaptionRenames : null,
            item.DerivedTitle is { Length: > 0 } derived
                ? new RenamerEntityTitleWrite(plan.Kind, plan.EntityId, derived)
                : null);

        try
        {
            var saved = await _port.ApplyAndSaveAsync([mutation], ct);

            // (7) RUNTIME assertion (NOT Debug.Assert — that no-ops in Release): the Path Cove
            //     recomputed on save must match the on-disk location we just moved to. A divergence
            //     means disk and DB disagree → surface a Failed result (do NOT silently accept).
            // Found as a nullable, never as default(SavedFile): that default carries a null
            // RecomputedPath, which the comparison below reads as a path that differs — so a save
            // reporting no row for this file would take the rollback branch and revert a save that
            // committed, blaming a path mismatch that never happened.
            SavedFile? savedFile = saved
                .Where(s => s.FileId == item.FileId)
                .Select(s => (SavedFile?)s)
                .FirstOrDefault();
            if (savedFile is null)
            {
                failed.Add(new ItemResult(item.FileId, item.OldFullPath, newFull, RenamerStatus.Failed,
                    "the save reported no row for this file, so the on-disk path could not be verified"));
                return;
            }

            string recomputed = savedFile.Value.RecomputedPath;
            string expected = NormalizeSlash(newFull);
            if (!PathsEqual(recomputed, expected))
            {
                // Disk and DB disagree after a SUCCESSFUL save. Roll the disk back to the OLD path
                // through the SAME mover the move used (and the catch below uses), capturing rollback
                // warnings so an INCOMPLETE rollback is surfaced rather than falsely claiming "rolled
                // back" — mirroring the save-throw catch and the UndoReplayer's assertion branch. Do NOT
                // write a revert-log row or publish an event on this path: the move is being undone, so
                // there is nothing to reindex or to offer /undo.
                //
                // WHY this remains a reported inconsistency: the DB save already COMMITTED (the row now
                // points at the NEW basename/folder), yet we roll the FILE back to the OLD path. Disk and
                // DB therefore diverge — the row says NEW, the bytes are at OLD. This is deliberate: the
                // fix's goal is that the file is no longer SILENTLY abandoned at the new path with no undo
                // record (the pre-fix bug), and that the failure is fully surfaced. The operator must
                // reconcile the committed DB row against the rolled-back file; the Failed reason names the
                // exact path mismatch and any rollback warnings so the divergence is visible, not hidden.
                IReadOnlyList<string> rbWarnings = await RollbackMove(sameVolume, nativeOld, nativeNew, movedSidecars, ct);

                string note = rbWarnings.Count > 0
                    ? $"recomputed Path '{recomputed}' != on-disk '{expected}'; rollback INCOMPLETE: {string.Join("; ", rbWarnings)}"
                    : $"recomputed Path '{recomputed}' != on-disk '{expected}'; rolled back";
                failed.Add(new ItemResult(item.FileId, item.OldFullPath, newFull, RenamerStatus.Failed, note));
                return;
            }

            // (8) Success: journal row + reindex event. The row carries plan.EntityId — the SAME value
            //     published on the next line — so the journalled id and the event id are identical by
            //     construction (undo reconstructs the exact forward event from the row).
            //     Seq is passed as 0 because the journal mints it on append.
            //
            //     The delta is built from what ACTUALLY moved, never from what was planned, and it is
            //     RECORDED rather than left to be recomputed at undo time. Two reasons, neither of them
            //     stylistic: RetargetCaption only rewrites a caption whose name starts with the old
            //     stem, so the forward transform is not invertible in general; and a caption rename is
            //     applied only for a sidecar whose file really moved on disk (see the moved-only filter
            //     above), which is a runtime fact no string arithmetic recovers afterwards. One row and
            //     one delta at this single append site, so a crash cannot leave the two disagreeing.
            var delta = BuildRevertDelta(srcFile, movedSidecars, appliedCaptionRenames);
            await _journal.AppendAsync(
                new RevertRow(_runId, Seq: 0, plan.EntityId, item.FileId, item.OldFullPath, delta.Serialize()), ct);
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
                (_, cleanupWarning) = EmptySourceFolderCleaner.TryRemoveIfEmpty(DirOf(item.OldFullPath));
            }

            if (cleanupWarning is not null)
            {
                sidecarWarnings.Add(cleanupWarning);
            }

            renamed.Add(new ItemResult(item.FileId, item.OldFullPath, newFull, item.Status,
                sidecarWarnings.Count > 0 ? string.Join("; ", sidecarWarnings) : null));
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
            // Rollback token is None on the cancel path: the ambient ct is already cancelled.
            var rollbackCt = ex is OperationCanceledException ? CancellationToken.None : ct;
            IReadOnlyList<string> rbWarnings = await RollbackMove(sameVolume, nativeOld, nativeNew, movedSidecars, rollbackCt);

            if (ex is OperationCanceledException)
            {
                throw;
            }

            string note = rbWarnings.Count > 0
                ? $"DB save failed; rollback INCOMPLETE: {ex.Message}; rollback warnings: {string.Join("; ", rbWarnings)}"
                : $"DB save failed; file rolled back: {ex.Message}";
            failed.Add(new ItemResult(item.FileId, item.OldFullPath, newFull, RenamerStatus.Failed, note));
        }
    }

    // ── move-spine sub-steps (extracted from ExecuteItemAsync; control flow unchanged) ─────────

    /// <summary>
    /// Plans the sidecar moves that ride with the primary: each DB-tracked caption (retargeted to the
    /// new stem, with its rename recorded for the DB save) and each configured same-stem neighbor whose
    /// extension is listed. Extension neighbors go to the disk-move list ONLY — never to the caption
    /// rename list — because they carry no DB row to update.
    /// </summary>
    private static (List<DiskMover.SidecarMove> plannedSidecars,
        List<(int CaptionId, string NewFilename)> captionRenames,
        List<string> warnings)
        PlanSidecarMoves(RenamerFile? srcFile, string oldFullPath, string targetFolder, string candidate, RenamerOptions options)
    {
        string oldDir = DirOf(oldFullPath);
        string newStem = StemOf(candidate);
        var captions = srcFile?.Captions ?? [];
        var plannedSidecars = new List<DiskMover.SidecarMove>(captions.Count);
        var captionRenames = new List<(int CaptionId, string NewFilename)>(captions.Count);
        var warnings = new List<string>();
        foreach (var cap in captions)
        {
            // A caption filename is a basename only, never a path fragment: reject any separator or
            // parent-traversal so a malformed row can't build a sidecar move that reaches outside the
            // primary's source and target folders. The invariant is only DOCUMENTED on the port, and
            // nothing downstream re-checks it — the canonical guard resolves the primary, not the
            // sidecars, and the movers apply no confinement.
            if (!IsPlainBasename(cap.Filename))
            {
                warnings.Add($"sidecar skipped: caption filename '{cap.Filename}' is not a plain basename");
                continue;
            }

            string newCaptionName = RetargetCaption(cap.Filename, oldStem: StemOf(srcFile!.Basename), newStem);
            plannedSidecars.Add(new DiskMover.SidecarMove(
                JoinPath(oldDir, cap.Filename), JoinPath(targetFolder, newCaptionName)));
            captionRenames.Add((cap.CaptionId, newCaptionName));
        }

        // Only the exact stem plus a listed extension is taken, so the candidate set is the stem's own
        // neighbours and never a directory sweep. The extension compare is ordinal-ignore-case in code
        // rather than delegated to File.Exists on a composed path: File.Exists answers
        // case-insensitively only where the filesystem does, so a listed `SRT` silently misses an
        // on-disk `clip.srt` on a case-sensitive volume, which is what the host runs on.
        if (srcFile is not null && options.AssociatedExtensions.Count > 0)
        {
            string srcStem = StemOf(srcFile.Basename);
            var neighborExtensions = SameStemExtensions(oldDir, srcStem);

            foreach (var raw in options.AssociatedExtensions)
            {
                string normExt = raw.StartsWith('.') ? raw[1..] : raw;
                if (normExt.Length == 0)
                {
                    continue;
                }

                // A configured extension is a leaf extension, never a path fragment: reject any
                // separator or parent-traversal so a malformed entry (e.g. "srt/../../elsewhere")
                // can't build a sidecar target outside the primary's folder.
                if (normExt.IndexOfAny(['/', '\\']) >= 0 || normExt.Contains(".."))
                {
                    continue;
                }

                if (!neighborExtensions.TryGetValue(normExt, out string? diskExt))
                {
                    continue;
                }

                // The on-disk spelling on both sides, so a moved sidecar keeps the extension casing it
                // had rather than taking the casing whoever typed the setting used.
                string source = JoinPath(oldDir, srcStem + "." + diskExt);
                string target = JoinPath(targetFolder, newStem + "." + diskExt);

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

        return (plannedSidecars, captionRenames, warnings);
    }

    /// <summary>
    /// True iff <paramref name="filename"/> is a leaf name: no directory part under either separator
    /// convention, and no parent-traversal segment.
    /// </summary>
    /// <remarks>
    /// Both separators are tested whatever the host is, because the value comes from a database row a
    /// different platform may have written; <see cref="Path.GetFileName(string)"/> alone splits on the
    /// running platform's separator only.
    /// </remarks>
    private static bool IsPlainBasename(string filename)
        => filename.Length > 0
            && filename.IndexOfAny(['/', '\\']) < 0
            && !filename.Contains("..", StringComparison.Ordinal)
            && string.Equals(Path.GetFileName(filename), filename, StringComparison.Ordinal);

    /// <summary>
    /// The extensions of the files in <paramref name="dir"/> whose stem is exactly
    /// <paramref name="stem"/>, keyed ordinal-ignore-case with the on-disk spelling as the value.
    /// </summary>
    /// <remarks>
    /// The filesystem filter is the stem, so the candidate set is the primary's own neighbours and does
    /// not grow with the folder. A wildcard character inside a stem widens that filter, which costs
    /// extra candidates and changes nothing that is taken: the exact stem comparison here is what
    /// decides. When one extension is present in more than one casing, the ordinal-smallest spelling
    /// wins, so the choice does not depend on the order the filesystem returned.
    /// </remarks>
    private static Dictionary<string, string> SameStemExtensions(string dir, string stem)
    {
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string nativeDir = ToNative(dir);
        if (!System.IO.Directory.Exists(nativeDir))
        {
            return found;
        }

        foreach (string path in System.IO.Directory.EnumerateFiles(nativeDir, stem + ".*"))
        {
            string name = System.IO.Path.GetFileName(path);
            if (name.Length <= stem.Length + 1
                || name[stem.Length] != '.'
                || !name.AsSpan(0, stem.Length).Equals(stem, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string ext = name[(stem.Length + 1)..];
            if (!found.TryGetValue(ext, out string? existing)
                || string.CompareOrdinal(ext, existing) < 0)
            {
                found[ext] = ext;
            }
        }

        return found;
    }

    /// <summary>
    /// Builds the reverse-replay payload for one journalled file from what the mover and the save
    /// actually did: the sidecar moves that happened, and each renamed caption's ORIGINAL stored
    /// filename.
    /// </summary>
    /// <remarks>
    /// The caption's ORIGINAL filename is what goes in, not the new one: the reverse direction needs
    /// the value it restores TO, and deriving it later would reintroduce the non-invertible transform
    /// this whole payload exists to avoid. A rename that carried nothing yields
    /// <see cref="RevertDelta.Empty"/>, which serializes to the journal column's existing empty marker.
    /// </remarks>
    private static RevertDelta BuildRevertDelta(
        RenamerFile? srcFile,
        IReadOnlyList<(string From, string To)> movedSidecars,
        List<(int CaptionId, string NewFilename)> appliedCaptionRenames)
    {
        if (movedSidecars.Count == 0 && appliedCaptionRenames.Count == 0)
        {
            return RevertDelta.Empty;
        }

        // The names as they stood BEFORE the save rewrote them — the loaded entity is the only place
        // they still exist by the time this runs.
        var originalCaptionNames = new Dictionary<int, string>();
        foreach (var cap in srcFile?.Captions ?? [])
        {
            originalCaptionNames[cap.CaptionId] = cap.Filename;
        }

        var captions = new List<RevertCaptionDelta>(appliedCaptionRenames.Count);
        foreach (var (captionId, _) in appliedCaptionRenames)
        {
            if (originalCaptionNames.TryGetValue(captionId, out var originalFilename))
            {
                captions.Add(new RevertCaptionDelta(captionId, originalFilename));
            }
        }

        // The movers speak native separators; the journal speaks forward-slash, as every other path it
        // stores does.
        return new RevertDelta(
            [.. movedSidecars.Select(s => new RevertSidecarDelta(NormalizeSlash(s.From), NormalizeSlash(s.To)))],
            captions);
    }

    /// <summary>
    /// Performs the primary move on the volume tier the source/destination imply: a same-volume renamer
    /// takes the atomic <see cref="DiskMover.Move"/>; a cross-volume move takes the verified
    /// copy→verify→promote→delete-source-last <see cref="CrossVolumeMover.MoveAsync"/>. Both tiers return
    /// the identical shape.
    /// </summary>
    private async Task<(bool moved, string? reason, MoveOutcome outcome,
        IReadOnlyList<(string From, string To)> movedSidecars)> MoveOnDisk(
        bool sameVolume, string nativeOld, string nativeNew,
        IReadOnlyList<DiskMover.SidecarMove> plannedSidecars, CancellationToken ct)
    {
        if (sameVolume)
        {
            var move = _disk.Move(nativeOld, nativeNew,
                [.. plannedSidecars.Select(s => new DiskMover.SidecarMove(ToNative(s.From), ToNative(s.To)))]);
            return (move.Moved, move.Reason, move.Outcome, [.. move.MovedSidecars.Select(s => (s.From, s.To))]);
        }

        var cross = await _cross.MoveAsync(nativeOld, nativeNew,
            [.. plannedSidecars.Select(s => new CrossVolumeMover.SidecarMove(ToNative(s.From), ToNative(s.To)))], ct);
        return (cross.Moved, cross.Reason, cross.Outcome, [.. cross.MovedSidecars.Select(s => (s.From, s.To))]);
    }

    /// <summary>
    /// Rolls a completed move back through the SAME mover tier that performed it — the atomic
    /// <see cref="DiskMover.Rollback"/> for a same-volume move, the copy-back→verify→delete
    /// <see cref="CrossVolumeMover.RollbackAsync"/> for a cross-volume one. Returns rollback warnings; a
    /// non-empty list means the restore was INCOMPLETE (re-occupied slot, failed copy-back verify, locked
    /// target) — the caller must surface it rather than claim the file was restored.
    /// </summary>
    private async Task<IReadOnlyList<string>> RollbackMove(
        bool sameVolume, string nativeOld, string nativeNew,
        IReadOnlyList<(string From, string To)> movedSidecars, CancellationToken ct)
        => sameVolume
            ? _disk.Rollback(nativeOld, nativeNew, [.. movedSidecars.Select(s => new DiskMover.SidecarMove(s.From, s.To))])
            : await _cross.RollbackAsync(nativeOld, nativeNew, [.. movedSidecars.Select(s => new CrossVolumeMover.SidecarMove(s.From, s.To))], ct);

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

    /// <summary>
    /// Retargets a caption basename from the old stem to the new stem. A caption "video.en.vtt"
    /// alongside "video.mkv" (oldStem "video") becomes "&lt;newStem&gt;.en.vtt". If the caption does
    /// not start with the old stem (unexpected), it is left unchanged so nothing is corrupted.
    /// </summary>
    private static string RetargetCaption(string captionFilename, string oldStem, string newStem)
        => captionFilename.StartsWith(oldStem, StringComparison.Ordinal)
            ? newStem + captionFilename[oldStem.Length..]
            : captionFilename;

    /// <summary>True iff <paramref name="candidate"/> is the source file's own path — the same
    /// canonical location differing at most by case on a case-insensitive volume. Mirrors the
    /// <see cref="PathsEqual"/>/<c>VolumeClassifier</c> OS-aware policy so the disk-side self-exclusion
    /// agrees with the rest of the slice on what counts as the same path.</summary>
    private static bool IsSelfPath(string candidate, string sourceFullPath) => PathsEqual(candidate, sourceFullPath);
}
