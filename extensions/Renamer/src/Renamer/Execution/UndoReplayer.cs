using Cove.Core.Events;
using Renamer.Planner;

using static global::Renamer.Execution.PathOps;

namespace Renamer.Execution;

/// <summary>
/// Reverse-replays a journalled renamer batch to restore files to their original locations. It is
/// path-driven from the <see cref="RevertBatch"/> (NOT metadata-driven — it does NOT
/// synthesize a <see cref="RenamerPlan"/> nor reuse <see cref="RenamerExecutor"/>, because the
/// original metadata may have changed since the renamer; replaying recorded paths is the only safe
/// way to undo). It composes the SAME collaborators the forward executor uses:
/// <see cref="IRenamerDataPort"/>, <see cref="IEventBus"/>, <see cref="DiskMover"/>.
///
/// The file's CURRENT path comes from Cove's database, not the journal. That is exact rather than
/// approximate: the forward executor appends a row only after asserting the recomputed database path
/// equals the location it just moved to, and its one branch that lets disk and database diverge
/// writes no row at all.
///
/// For each entry it mirrors <see cref="RenamerExecutor.ExecuteItemAsync"/> REVERSED, over the same
/// safety spine:
/// <list type="number">
/// <item>resolve the file's current path from the database, and the OLD directory + OLD basename from
/// <c>entry.OldPath</c> and the old folder id;</item>
/// <item>collision re-check the OLD slot is free on BOTH disk and DB → skip+report on conflict
/// (never clobber an existing file);</item>
/// <item>disk move NEW→OLD with the 2-arg never-overwrite <see cref="DiskMover.Move"/> → a non-moved
/// result is a skip+report (locked/exists);</item>
/// <item>set Basename/ParentFolderId back via <see cref="IRenamerDataPort.ApplyAndSaveAsync"/>; on a
/// save throw, <see cref="DiskMover.Rollback"/> puts the file back at NEW and the entry is reported
/// failed (no half-state where disk and DB disagree);</item>
/// <item>on success, assert the recomputed Path equals the OLD path and publish
/// <c>EntityEvent(EventTypeFor(batch.Kind), EntityTypeName(batch.Kind), entry.EntityId)</c> — the
/// EXACT forward-equivalent reconstruction (kind from the batch, entityId from THIS row).</item>
/// </list>
/// Each entry is independent (one failure never aborts the rest, matching the forward executor).
///
/// Both sidecar kinds ride with the file: the database-tracked captions and the configured same-stem
/// neighbours. What is replayed is the delta the FORWARD path RECORDED on the row, reversed — never a
/// target recomputed from the old and new stems, because the forward caption transform is not
/// invertible in general and because a caption rename was applied only for a sidecar whose file really
/// moved on disk. A sidecar that cannot go back leaves its entry RESTORED with a warning: the media
/// file is at its original path and its row agrees, which is what undo promises, and it mirrors the
/// forward path's own non-fatal treatment of a failed sidecar. A caption's stored filename is written
/// back only when its file actually moved back, so disk and database never disagree.
///
/// The kind is the batch's single source — never a parameter, never a hardcoded default. This class
/// does NOT retire the rows it restored (that is the endpoint's job), so it stays storage-agnostic; it
/// reports WHICH rows it restored and the endpoint retires exactly those.
/// </summary>
public sealed class UndoReplayer
{
    private readonly IRenamerDataPort _port;
    private readonly IEventBus _eventBus;
    private readonly DiskMover _disk;
    private readonly CrossVolumeMover _cross;

    // The optional <paramref name="cross"/> mover is used when the reverse move crosses volumes
    // (the NEW and OLD paths have different roots). It defaults to a fresh CrossVolumeMover() when
    // omitted, so every existing 3-arg construction site (the /undo endpoint + the test suite) stays
    // source-compatible; a test may inject a fault-seam / recording mover via this parameter. This
    // mirrors RenamerExecutor's optional-cross-param ctor verbatim.
    public UndoReplayer(IRenamerDataPort port, IEventBus eventBus, DiskMover disk,
        CrossVolumeMover? cross = null)
    {
        _port = port;
        _eventBus = eventBus;
        _disk = disk;
        _cross = cross ?? new CrossVolumeMover();
    }

    /// <summary>One failed/skipped reverse-replay entry surfaced in the run result's buckets.</summary>
    /// <param name="RunId">The batch the stopped row belongs to.</param>
    /// <param name="Seq">The row's position within that batch; the rest of its identity.</param>
    /// <param name="FileId">The file row.</param>
    /// <param name="OldPath">The path the reverse move targeted (the original location).</param>
    /// <param name="NewPath">The path the file currently sits at; empty when it is no longer in the library.</param>
    /// <param name="Reason">A human-readable note for the skip/failure.</param>
    /// <param name="Stop">
    /// The same fact as <paramref name="Reason"/> as a value, which is what decides whether the row is
    /// retired as unrestorable or left pending. Mapped from the typed outcome each arm already has, never
    /// parsed back out of <paramref name="Reason"/>.
    /// </param>
    /// <remarks>
    /// <paramref name="RunId"/> and <paramref name="Seq"/> ride along for the same reason
    /// <see cref="UndoRunResult.Restored"/> carries rows rather than paths: a single batch can hold two
    /// rows for one file id, so a caller that must retire exactly this row cannot reconstruct which one
    /// it was from the file id or the paths.
    /// </remarks>
    public sealed record UndoFailure(
        string RunId,
        long Seq,
        int FileId,
        string OldPath,
        string NewPath,
        string Reason,
        UndoStopReason Stop);

    /// <summary>A non-fatal note about an entry that was RESTORED anyway.</summary>
    /// <param name="FileId">The physical file row the note belongs to.</param>
    /// <param name="Detail">What could not be put back, naming the sidecar.</param>
    public sealed record UndoWarning(int FileId, string Detail);

    /// <summary>The result of reverse-replaying a batch: what was restored + the failed/skipped buckets.</summary>
    /// <param name="Undone">How many entries were restored (disk + DB) and published.</param>
    /// <param name="Failed">
    /// Entries whose reverse move succeeded but the save did not complete: it threw, or it committed
    /// without a row this replayer could verify the restored path against. Only the first rolls the disk
    /// back to NEW; a save that committed is never reverted.
    /// </param>
    /// <param name="Skipped">Entries skipped because the OLD slot was occupied/locked (never clobbered).</param>
    /// <param name="Restored">
    /// The rows that terminated as restored, so the caller can retire exactly those in the journal.
    /// Carried as rows rather than derived by the caller from the difference between the batch and the
    /// two problem buckets: each row's <c>(RunId, Seq)</c> is its exact identity, and a derived set has
    /// to reconstruct that identity from paths.
    /// </param>
    /// <param name="Warnings">
    /// Sidecars that could not be put back, on entries that were restored regardless. A separate
    /// channel from the two problem buckets on purpose: an entry here SUCCEEDED, and folding it into
    /// <paramref name="Failed"/> or <paramref name="Skipped"/> would make the caller's retirement
    /// decision wrong — the row's file did come back, so the row must retire. Empty is the normal case.
    /// </param>
    public sealed record UndoRunResult(
        int Undone,
        IReadOnlyList<UndoFailure> Failed,
        IReadOnlyList<UndoFailure> Skipped,
        IReadOnlyList<RevertRow> Restored,
        IReadOnlyList<UndoWarning> Warnings);

    /// <summary>
    /// Reverse-replays <paramref name="batch"/> (already newest-first from the journal), restoring each
    /// row independently. The kind comes from <c>batch.Kind</c>; the entity id of each published event
    /// comes from the row.
    /// </summary>
    public async Task<UndoRunResult> RevertAsync(RevertBatch batch, CancellationToken ct = default)
    {
        int undone = 0;
        var failed = new List<UndoFailure>();
        var skipped = new List<UndoFailure>();
        var restored = new List<RevertRow>();
        var warnings = new List<UndoWarning>();

        // One load per ENTITY, not per row: a multi-file item costs one query, not one per file.
        var currentPaths = new Dictionary<int, string>();
        var loadedEntities = new HashSet<int>();

        foreach (var entry in batch.Rows)
        {
            ct.ThrowIfCancellationRequested();
            string currentPath = "";
            try
            {
                if (loadedEntities.Add(entry.EntityId)
                    && await _port.LoadEntityAsync(batch.Kind, entry.EntityId, ct) is { } entity)
                {
                    foreach (var file in entity.Files)
                    {
                        currentPaths[file.FileId] = JoinPath(file.ParentFolderPath, file.Basename);
                    }
                }

                if (!currentPaths.TryGetValue(entry.FileId, out var resolved))
                {
                    // The row outlived its file (deleted since the rename): nothing to move back, and no
                    // current path to name. Reported rather than guessed at from the logged old location.
                    skipped.Add(new UndoFailure(
                        entry.RunId, entry.Seq, entry.FileId, entry.OldPath, "",
                        "skipped: the renamed file is no longer in the library",
                        UndoStopReason.FileNoLongerInLibrary));
                    continue;
                }

                currentPath = resolved;
                var outcome = await RevertEntryAsync(batch.Kind, entry, currentPath, warnings, ct);
                switch (outcome)
                {
                    case RevertOutcome.Undone: undone++; restored.Add(entry); break;
                    case RevertOutcome.Skipped skip: skipped.Add(skip.Failure); break;
                    case RevertOutcome.Failed fail: failed.Add(fail.Failure); break;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A cancellation (host shutdown) is cancellation, not a per-entry failure — the filter
                // excludes it. Any other unexpected throw outside the save path is reported as a failure
                // for that entry only — the batch continues (each entry is independent).
                failed.Add(new UndoFailure(
                    entry.RunId, entry.Seq, entry.FileId, entry.OldPath, currentPath,
                    $"unexpected error: {ex.Message}", UndoStopReason.UnexpectedError));
            }
        }

        return new UndoRunResult(undone, failed, skipped, restored, warnings);
    }

    private async Task<RevertOutcome> RevertEntryAsync(
        RenamerFileKind kind, RevertRow entry, string currentPath, List<UndoWarning> warnings,
        CancellationToken ct)
    {
        // (1) Resolve the OLD directory + OLD basename, then validate + resolve the restore target
        //     (dir-missing → old-slot collision). A rejected target is a reported
        //     skip that never advances to a folder-row create or a disk write.
        string oldDir = DirOf(entry.OldPath);
        string oldBasename = BasenameOf(entry.OldPath);

        var (skip, oldFolderId) = await PrepareRestoreTargetAsync(entry, currentPath, oldDir, oldBasename, ct);
        if (skip is not null)
        {
            return skip;
        }

        // (2) The RECORDED delta, reversed: every sidecar the forward move actually made, replayed from
        //     its destination back to its source. Nothing here is recomputed from the old and new
        //     stems — the forward caption transform is not invertible in general, and a caption rename
        //     was applied only for a sidecar whose file really moved, which is a runtime fact. An
        //     unreadable delta yields an empty one, so the media file this row names still comes back.
        _ = RevertDelta.TryParse(entry.SidecarsJson, out var delta);
        var reverseSidecars = delta.Sidecars
            .Select(s => new DiskMover.SidecarMove(ToNative(s.ToPath), ToNative(s.FromPath)))
            .ToList();

        // (3) Reverse disk move NEW→OLD on the matching volume tier; a non-moved result (locked /
        //     target-exists / verify-failed / disk-full / offline) is a skip+report, never a clobber.
        string nativeNew = ToNative(currentPath);
        string nativeOld = ToNative(entry.OldPath);
        bool sameVolume = VolumeClassifier.SameVolume(currentPath, entry.OldPath);

        var (moved, moveReason, moveStop, movedSidecars, sidecarWarnings) =
            await ReverseMoveOnDisk(sameVolume, nativeNew, nativeOld, reverseSidecars, ct);

        if (!moved)
        {
            return new RevertOutcome.Skipped(new UndoFailure(
                entry.RunId, entry.Seq, entry.FileId, entry.OldPath, currentPath,
                moveReason ?? "skipped: reverse move did not happen", moveStop));
        }

        // A sidecar that could not go back — its old slot is occupied, or it is locked — is a WARNING,
        // never a failure. The media file is at its original path and its row is about to agree, which
        // is exactly what undo promises, and this mirrors the forward path's own non-fatal treatment of
        // a failed sidecar. Failing the entry instead would strand a user's whole recovery on a subtitle.
        foreach (var warning in sidecarWarnings)
        {
            warnings.Add(new UndoWarning(entry.FileId, warning));
        }

        // Only the captions whose FILE actually moved back get their stored filename written back — the
        // same runtime-fact discipline the forward path uses in the other direction. Writing back a
        // caption whose file is still at its renamed name would leave the database naming a file that
        // does not exist.
        var movedBackNames = movedSidecars
            .Select(s => BasenameOf(NormalizeSlash(s.To)))
            .ToHashSet(StringComparer.Ordinal);
        var restoredCaptions = delta.Captions
            .Where(c => movedBackNames.Contains(c.OriginalFilename))
            .Select(c => (c.CaptionId, NewFilename: c.OriginalFilename))
            .ToList();

        // (4) Reverse DB save: set Basename back (and the parent folder for the in-place/move case),
        //     plus each caption filename whose file came back with it.
        var mutation = new RenamerFileMutation(
            entry.FileId, oldBasename, oldFolderId, restoredCaptions.Count > 0 ? restoredCaptions : null);
        try
        {
            var saved = await _port.ApplyAndSaveAsync([mutation], ct);

            // (5) RUNTIME assertion: the recomputed Path must equal the OLD path we just restored to.
            //     Found as a nullable, never as default(SavedFile): that default carries a null
            //     RecomputedPath, which the comparison below reads as a path that differs — so a save
            //     reporting no row for this file would take the rollback branch and undo a restore that
            //     committed, blaming a path mismatch that never happened.
            SavedFile? savedFile = saved
                .Where(s => s.FileId == entry.FileId)
                .Select(s => (SavedFile?)s)
                .FirstOrDefault();
            if (savedFile is null)
            {
                return new RevertOutcome.Failed(new UndoFailure(
                    entry.RunId, entry.Seq, entry.FileId, entry.OldPath, currentPath,
                    "the save reported no row for this file, so the restored path could not be verified",
                    UndoStopReason.UnexpectedError));
            }

            string recomputed = savedFile.Value.RecomputedPath;
            string expected = NormalizeSlash(entry.OldPath);
            if (!PathsEqual(recomputed, expected))
            {
                // Disk and DB disagree: roll the disk back to NEW through the MATCHING mover and
                // report failed (no half-state). The file currently sits at OLD (the reverse move
                // target); both movers' Rollback/RollbackAsync(oldFull, newFull) internally move
                // newFull→oldFull, so passing (nativeNew, nativeOld) moves it OLD→NEW — back to the
                // renamed location — on the SAME volume tier the reverse move used. Surface the rollback
                // warnings so an INCOMPLETE rollback (the NEW slot got re-occupied, a cross copy-back
                // failed verify, a target is locked) is visible rather than falsely claiming "rolled
                // back" — mirroring the forward executor's rollback reporting.
                IReadOnlyList<string> rbWarnings = await RollbackReverseMove(sameVolume, nativeNew, nativeOld, movedSidecars, ct);
                string note = rbWarnings.Count > 0
                    ? $"recomputed Path '{recomputed}' != restored path '{expected}'; rollback INCOMPLETE: {string.Join("; ", rbWarnings)}"
                    : $"recomputed Path '{recomputed}' != restored path '{expected}'; rolled back";
                return new RevertOutcome.Failed(new UndoFailure(
                    entry.RunId, entry.Seq, entry.FileId, entry.OldPath, currentPath, note,
                    UndoStopReason.RestoredPathMismatch));
            }

            // (6) Success: publish the EXACT forward-equivalent event — kind from the batch header,
            //     entityId from THIS row (matching the forward executor's success path).
            _eventBus.Publish(new EntityEvent(EventTypeFor(kind), EntityTypeName(kind), entry.EntityId));
            return RevertOutcome.UndoneInstance;
        }
        catch (Exception ex)
        {
            // Save failed AFTER a successful reverse move → roll the disk back to NEW through the
            // MATCHING mover so disk + DB stay consistent, and report failed (no half-state). The
            // file sits at OLD; both movers' Rollback/RollbackAsync(oldFull, newFull) internally move
            // newFull→oldFull, so passing (nativeNew, nativeOld) moves it OLD→NEW — back to the renamed
            // location — on the SAME volume tier the reverse move used (a verified cross copy-back when
            // the reverse crossed volumes). Surface the rollback warnings so an INCOMPLETE rollback is
            // visible rather than falsely claiming a rollback that did not happen.
            // Rollback token is None on the cancel path: the ambient ct is already cancelled.
            var rollbackCt = ex is OperationCanceledException ? CancellationToken.None : ct;
            IReadOnlyList<string> rbWarnings =
                await RollbackReverseMove(sameVolume, nativeNew, nativeOld, movedSidecars, rollbackCt);

            if (ex is OperationCanceledException)
            {
                throw;
            }

            string note = rbWarnings.Count > 0
                ? $"DB save failed; rollback INCOMPLETE: {ex.Message}; rollback warnings: {string.Join("; ", rbWarnings)}"
                : $"DB save failed; file rolled back: {ex.Message}";
            return new RevertOutcome.Failed(new UndoFailure(
                entry.RunId, entry.Seq, entry.FileId, entry.OldPath, currentPath, note,
                UndoStopReason.DatabaseSaveFailed));
        }
    }

    /// <summary>
    /// Validates and resolves the restore target BEFORE any mutation: the
    /// dir-missing guard, then the old-slot collision re-check — returning the skip that halts the
    /// entry, or the resolved OLD folder id when the target is clear to write.
    /// </summary>
    private async Task<(RevertOutcome.Skipped? Skip, int OldFolderId)> PrepareRestoreTargetAsync(
        RevertRow entry, string currentPath, string oldDir, string oldBasename,
        CancellationToken ct)
    {
        // DIR-MISSING / OFFLINE-OLD-DRIVE — do NOT recreate a missing OLD directory: recreating could
        // restore the file to a wrong place when the original drive is offline or the folder was deleted,
        // violating never-lose-track-of-a-file. Directory.Exists returns false (never throws) on an
        // unmapped/offline drive, so this classifies the offline-drive case too.
        if (!Directory.Exists(ToNative(oldDir)))
        {
            return (new RevertOutcome.Skipped(new UndoFailure(
                entry.RunId, entry.Seq, entry.FileId, entry.OldPath, currentPath,
                "skipped: original directory no longer exists",
                UndoStopReason.OriginalDirectoryUnavailable)), 0);
        }

        int oldFolderId = await _port.GetOrCreateFolderIdAsync(oldDir, ct);

        // Collision re-check the OLD slot is free on BOTH disk and DB; if occupied → skip (no clobber).
        if (System.IO.File.Exists(ToNative(entry.OldPath))
            || await _port.CollisionExistsAsync(oldFolderId, oldBasename, entry.FileId, ct))
        {
            return (new RevertOutcome.Skipped(new UndoFailure(
                entry.RunId, entry.Seq, entry.FileId, entry.OldPath, currentPath,
                "skipped: old location is occupied on disk or in the database",
                UndoStopReason.OriginalLocationOccupied)), 0);
        }

        return (null, oldFolderId);
    }

    /// <summary>
    /// The reverse disk move NEW→OLD on the matching volume tier, carrying the recorded sidecar moves
    /// reversed: a same-volume reverse takes the atomic never-overwrite <see cref="DiskMover.Move"/>; a
    /// cross-volume reverse takes the verified copy-back→verify→promote→delete-source-last
    /// <see cref="CrossVolumeMover.MoveAsync"/>. Both tiers return the identical shape.
    /// </summary>
    /// <returns>
    /// Whether the PRIMARY moved, the skip reason when it did not — both as prose for the panel and as
    /// the value the retirement decision reads — the sidecars that ACTUALLY moved back (what a rollback
    /// reverses, and what decides which caption filenames may be written back), and the non-fatal notes
    /// naming any sidecar that stayed put.
    /// </returns>
    private async Task<(bool moved, string? reason, UndoStopReason stop,
        IReadOnlyList<(string From, string To)> movedSidecars,
        IReadOnlyList<string> warnings)> ReverseMoveOnDisk(
        bool sameVolume, string nativeNew, string nativeOld,
        List<DiskMover.SidecarMove> sidecars, CancellationToken ct)
    {
        if (sameVolume)
        {
            var move = _disk.Move(nativeNew, nativeOld, sidecars);
            return (move.Moved, move.Reason, StopFor(move.Outcome),
                [.. move.MovedSidecars.Select(s => (s.From, s.To))], move.Warnings);
        }

        var cross = await _cross.MoveAsync(nativeNew, nativeOld,
            [.. sidecars.Select(s => new CrossVolumeMover.SidecarMove(s.From, s.To))], ct);
        return (cross.Moved, cross.Reason, StopFor(cross.Outcome),
            [.. cross.MovedSidecars.Select(s => (s.From, s.To))], cross.Warnings);
    }

    // The two movers classify their own failures, so a reverse move's stop reason is a translation of a
    // value that already exists rather than a fresh judgement about what went wrong. The `Moved` arm is
    // unreachable from a caller that only asks when the move did NOT happen; it maps to the retryable
    // reason so an unreachable arm becoming reachable cannot silently retire a row. Every member is
    // named, so a member added to MoveOutcome fails this build instead of collapsing into a default.
    private static UndoStopReason StopFor(MoveOutcome outcome) => outcome switch
    {
        // Both share one stop reason, whose own name already covers both causes: an undo stop is
        // retryable either way, so the distinction changes no recovery route the user can take.
        MoveOutcome.Locked => UndoStopReason.ReverseMoveLockedOrTargetExists,
        MoveOutcome.TargetExists => UndoStopReason.ReverseMoveLockedOrTargetExists,
        MoveOutcome.PermissionDenied => UndoStopReason.ReverseMovePermissionDenied,
        MoveOutcome.VerifyFailed => UndoStopReason.ReverseMoveVerifyFailed,
        MoveOutcome.Cancelled => UndoStopReason.ReverseMoveCancelled,
        MoveOutcome.Moved => UndoStopReason.UnexpectedError,
    };

    /// <summary>
    /// Rolls a completed reverse move back to NEW through the SAME mover tier that performed it, taking
    /// the sidecars that came back with it. Both movers'
    /// <c>Rollback</c>/<c>RollbackAsync(oldFull, newFull)</c> internally move newFull→oldFull; passing
    /// <paramref name="nativeNew"/>, <paramref name="nativeOld"/> therefore moves the file OLD→NEW —
    /// back to the renamed location. A non-empty return means an INCOMPLETE rollback to surface.
    /// </summary>
    private async Task<IReadOnlyList<string>> RollbackReverseMove(
        bool sameVolume, string nativeNew, string nativeOld,
        IReadOnlyList<(string From, string To)> movedSidecars, CancellationToken ct)
        => sameVolume
            ? _disk.Rollback(nativeNew, nativeOld, [.. movedSidecars.Select(s => new DiskMover.SidecarMove(s.From, s.To))])
            : await _cross.RollbackAsync(nativeNew, nativeOld,
                [.. movedSidecars.Select(s => new CrossVolumeMover.SidecarMove(s.From, s.To))], ct);

    private abstract record RevertOutcome
    {
        public static readonly Undone UndoneInstance = new();
        public sealed record Undone : RevertOutcome;
        public sealed record Skipped(UndoFailure Failure) : RevertOutcome;
        public sealed record Failed(UndoFailure Failure) : RevertOutcome;
    }

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
}
