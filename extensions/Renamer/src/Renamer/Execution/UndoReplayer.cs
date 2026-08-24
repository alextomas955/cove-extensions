using Cove.Core.Events;
using Renamer.Planner;

using static global::Renamer.Execution.PathOps;

namespace Renamer.Execution;

/// <summary>
/// Reverse-replays a logged renamer batch to restore files to their original locations. It is
/// path-driven from the <see cref="RevertLog.RevertBatch"/> (NOT metadata-driven — it does NOT
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
/// EXACT forward-equivalent reconstruction (kind from the batch HEADER, entityId from THIS row).</item>
/// </list>
/// Each entry is independent (one failure never aborts the rest, matching the forward executor).
/// Captions are out of undo scope for v1. The kind is the batch's single source — never a parameter,
/// never a hardcoded default. This class does NOT mark the batch consumed (that is the endpoint's
/// job), so it stays storage-agnostic.
/// </summary>
public sealed class UndoReplayer
{
    private readonly IRenamerDataPort _port;
    private readonly IEventBus _eventBus;
    private readonly DiskMover _disk;
    private readonly CrossVolumeMover _cross;
    private readonly IReadOnlyList<string> _allowedRoots;

    // The optional <paramref name="cross"/> mover is used when the reverse move crosses volumes
    // (the NEW and OLD paths have different roots). It defaults to a fresh CrossVolumeMover() when
    // omitted, so every existing 3-arg construction site (the /undo endpoint + the test suite) stays
    // source-compatible; a test may inject a fault-seam / recording mover via this parameter. This
    // mirrors RenamerExecutor's optional-cross-param ctor verbatim.
    //
    // The optional <paramref name="allowedRoots"/> is the owner-configured write boundary the undo
    // RE-GATES the restore target against, mirroring the forward executor's allowlist. It is
    // additive AFTER the cross param so every existing 3-/4-arg call site stays source-compatible;
    // omitted (or null) → an empty list, which makes the re-gate a no-op (an undo with no configured
    // allowlist is unaffected). The /undo endpoint passes options.AllowedRoots.
    public UndoReplayer(IRenamerDataPort port, IEventBus eventBus, DiskMover disk,
        CrossVolumeMover? cross = null, IReadOnlyList<string>? allowedRoots = null)
    {
        _port = port;
        _eventBus = eventBus;
        _disk = disk;
        _cross = cross ?? new CrossVolumeMover();
        _allowedRoots = allowedRoots ?? [];
    }

    /// <summary>One failed/skipped reverse-replay entry surfaced in the run result's buckets.</summary>
    /// <param name="FileId">The file row.</param>
    /// <param name="OldPath">The path the reverse move targeted (the original location).</param>
    /// <param name="NewPath">The path the file currently sits at; empty when it is no longer in the library.</param>
    /// <param name="Reason">A human-readable note for the skip/failure.</param>
    public sealed record UndoFailure(int FileId, string OldPath, string NewPath, string Reason);

    /// <summary>The result of reverse-replaying a batch: counts of restored entries + the failed/skipped buckets.</summary>
    /// <param name="Undone">How many entries were restored (disk + DB) and published.</param>
    /// <param name="Failed">Entries whose reverse move succeeded but the save threw (disk rolled back to NEW).</param>
    /// <param name="Skipped">Entries skipped because the OLD slot was occupied/locked (never clobbered).</param>
    public sealed record UndoRunResult(int Undone, IReadOnlyList<UndoFailure> Failed, IReadOnlyList<UndoFailure> Skipped);

    /// <summary>
    /// Reverse-replays <paramref name="batch"/> (already newest-first from <see cref="RevertLog"/>),
    /// restoring each entry independently. The kind comes from <c>batch.Kind</c>; the entity id of
    /// each published event comes from the row.
    /// </summary>
    public async Task<UndoRunResult> RevertAsync(RevertLog.RevertBatch batch, CancellationToken ct = default)
    {
        int undone = 0;
        var failed = new List<UndoFailure>();
        var skipped = new List<UndoFailure>();

        // One load per ENTITY, not per row: a multi-file item costs one query, not one per file.
        var currentPaths = new Dictionary<int, string>();
        var loadedEntities = new HashSet<int>();

        foreach (var entry in batch.Entries)
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
                        entry.FileId, entry.OldPath, "", "skipped: the renamed file is no longer in the library"));
                    continue;
                }

                currentPath = resolved;
                var outcome = await RevertEntryAsync(batch.Kind, entry, currentPath, ct);
                switch (outcome)
                {
                    case RevertOutcome.Undone: undone++; break;
                    case RevertOutcome.Skipped skip: skipped.Add(skip.Failure); break;
                    case RevertOutcome.Failed fail: failed.Add(fail.Failure); break;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A cancellation (host shutdown) is cancellation, not a per-entry failure — the filter
                // excludes it. Any other unexpected throw outside the save path is reported as a failure
                // for that entry only — the batch continues (each entry is independent).
                failed.Add(new UndoFailure(entry.FileId, entry.OldPath, currentPath, $"unexpected error: {ex.Message}"));
            }
        }

        return new UndoRunResult(undone, failed, skipped);
    }

    private async Task<RevertOutcome> RevertEntryAsync(
        RenamerFileKind kind, RevertLog.RevertEntry entry, string currentPath, CancellationToken ct)
    {
        // (1) Resolve the OLD directory + OLD basename, then validate + resolve the restore target
        //     (allowlist re-gate → dir-missing → old-slot collision). A rejected target is a reported
        //     skip that never advances to a folder-row create or a disk write.
        string oldDir = DirOf(entry.OldPath);
        string oldBasename = BasenameOf(entry.OldPath);

        var (skip, oldFolderId) = await PrepareRestoreTargetAsync(entry, currentPath, oldDir, oldBasename, ct);
        if (skip is not null)
        {
            return skip;
        }

        // (2) Reverse disk move NEW→OLD on the matching volume tier; a non-moved result (locked /
        //     target-exists / verify-failed / disk-full / offline) is a skip+report, never a clobber.
        string nativeNew = ToNative(currentPath);
        string nativeOld = ToNative(entry.OldPath);
        bool sameVolume = VolumeClassifier.SameVolume(currentPath, entry.OldPath);

        var (moved, moveReason) = await ReverseMoveOnDisk(sameVolume, nativeNew, nativeOld, ct);

        if (!moved)
        {
            return new RevertOutcome.Skipped(new UndoFailure(
                entry.FileId, entry.OldPath, currentPath, moveReason ?? "skipped: reverse move did not happen"));
        }

        // (4) Reverse DB save: set Basename back (and the parent folder for the in-place/move case).
        //     Captions are out of undo scope for v1 (null caption renames).
        var mutation = new RenamerFileMutation(entry.FileId, oldBasename, oldFolderId, null);
        try
        {
            var saved = await _port.ApplyAndSaveAsync([mutation], ct);

            // (5) RUNTIME assertion: the recomputed Path must equal the OLD path we just restored to.
            var savedFile = saved.FirstOrDefault(s => s.FileId == entry.FileId);
            string expected = NormalizeSlash(entry.OldPath);
            if (!PathsEqual(savedFile.RecomputedPath, expected))
            {
                // Disk and DB disagree: roll the disk back to NEW through the MATCHING mover and
                // report failed (no half-state). The file currently sits at OLD (the reverse move
                // target); both movers' Rollback/RollbackAsync(oldFull, newFull) internally move
                // newFull→oldFull, so passing (nativeNew, nativeOld) moves it OLD→NEW — back to the
                // renamed location — on the SAME volume tier the reverse move used. Surface the rollback
                // warnings so an INCOMPLETE rollback (the NEW slot got re-occupied, a cross copy-back
                // failed verify, a target is locked) is visible rather than falsely claiming "rolled
                // back" — mirroring the forward executor's rollback reporting.
                IReadOnlyList<string> rbWarnings = await RollbackReverseMove(sameVolume, nativeNew, nativeOld, ct);
                string note = rbWarnings.Count > 0
                    ? $"recomputed Path '{savedFile.RecomputedPath}' != restored path '{expected}'; rollback INCOMPLETE: {string.Join("; ", rbWarnings)}"
                    : $"recomputed Path '{savedFile.RecomputedPath}' != restored path '{expected}'; rolled back";
                return new RevertOutcome.Failed(new UndoFailure(
                    entry.FileId, entry.OldPath, currentPath, note));
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
            IReadOnlyList<string> rbWarnings = await RollbackReverseMove(sameVolume, nativeNew, nativeOld, rollbackCt);

            if (ex is OperationCanceledException)
            {
                throw;
            }

            string note = rbWarnings.Count > 0
                ? $"DB save failed; rollback INCOMPLETE: {ex.Message}; rollback warnings: {string.Join("; ", rbWarnings)}"
                : $"DB save failed; file rolled back: {ex.Message}";
            return new RevertOutcome.Failed(new UndoFailure(
                entry.FileId, entry.OldPath, currentPath, note));
        }
    }

    // ── restore-spine sub-steps (extracted from RevertEntryAsync; control flow unchanged) ──────

    /// <summary>
    /// Validates and resolves the restore target BEFORE any mutation: the allowlist re-gate, the
    /// dir-missing guard, then the old-slot collision re-check — returning the skip that halts the
    /// entry, or the resolved OLD folder id when the target is clear to write.
    /// </summary>
    private async Task<(RevertOutcome.Skipped? Skip, int OldFolderId)> PrepareRestoreTargetAsync(
        RevertLog.RevertEntry entry, string currentPath, string oldDir, string oldBasename,
        CancellationToken ct)
    {
        // RESTORE-TARGET RE-GATE — an undo is a WRITE back to the recorded OLD location, so a RELOCATING
        // undo must pass the SAME write-boundary gate the forward move used (in case the allowlist changed
        // or the OLD dir is now a junction-to-elsewhere). RE-GATE ONLY RELOCATIONS — the undo-direction
        // analog of the forward executor's `isMove && AllowedRoots.Count > 0`: an in-place restore (OLD and
        // NEW share a directory) crosses no write boundary, so gating it would make ordinary in-place
        // renames permanently non-undoable the moment any AllowedRoot is set. With an EMPTY allowlist this
        // is skipped entirely (byte-identical to no-allowlist). Gate `oldDir` (the FOLDER), matching the
        // forward executor's primary folder gate; the collision re-check below proves the OLD leaf does not
        // currently exist, so the leaf cannot be a junction-to-elsewhere. Placed before the folder-row
        // create and any disk write so a rejected target leaves no trace.
        bool isRelocation = !PathsEqual(oldDir, DirOf(currentPath));
        if (isRelocation && _allowedRoots.Count > 0)
        {
            var guard = CanonicalPathGuard.Check(oldDir, _allowedRoots);
            if (!guard.Accepted)
            {
                return (new RevertOutcome.Skipped(new UndoFailure(
                    entry.FileId, entry.OldPath, currentPath,
                    $"skipped: restore target rejected by allowlist: {guard.Reason}")), 0);
            }
        }

        // DIR-MISSING / OFFLINE-OLD-DRIVE — do NOT recreate a missing OLD directory: recreating could
        // restore the file to a wrong place when the original drive is offline or the folder was deleted,
        // violating never-lose-track-of-a-file. Directory.Exists returns false (never throws) on an
        // unmapped/offline drive, so this classifies the offline-drive case too.
        if (!Directory.Exists(ToNative(oldDir)))
        {
            return (new RevertOutcome.Skipped(new UndoFailure(
                entry.FileId, entry.OldPath, currentPath, "skipped: original directory no longer exists")), 0);
        }

        int oldFolderId = await _port.GetOrCreateFolderIdAsync(oldDir, ct);

        // Collision re-check the OLD slot is free on BOTH disk and DB; if occupied → skip (no clobber).
        if (System.IO.File.Exists(ToNative(entry.OldPath))
            || await _port.CollisionExistsAsync(oldFolderId, oldBasename, entry.FileId, ct))
        {
            return (new RevertOutcome.Skipped(new UndoFailure(
                entry.FileId, entry.OldPath, currentPath, "skipped: old location is occupied on disk or in the database")), 0);
        }

        return (null, oldFolderId);
    }

    /// <summary>
    /// The reverse disk move NEW→OLD on the matching volume tier: a same-volume reverse takes the atomic
    /// 2-arg never-overwrite <see cref="DiskMover.Move"/>; a cross-volume reverse takes the verified
    /// copy-back→verify→promote→delete-source-last <see cref="CrossVolumeMover.MoveAsync"/>. Captions are
    /// out of undo scope (the cross reverse passes no sidecars).
    /// </summary>
    private async Task<(bool moved, string? reason)> ReverseMoveOnDisk(
        bool sameVolume, string nativeNew, string nativeOld, CancellationToken ct)
    {
        if (sameVolume)
        {
            var move = _disk.Move(nativeNew, nativeOld);
            return (move.Moved, move.Reason);
        }

        var cross = await _cross.MoveAsync(nativeNew, nativeOld, sidecars: null, ct);
        return (cross.Moved, cross.Reason);
    }

    /// <summary>
    /// Rolls a completed reverse move back to NEW through the SAME mover tier that performed it. Both
    /// movers' <c>Rollback</c>/<c>RollbackAsync(oldFull, newFull)</c> internally move newFull→oldFull;
    /// passing <paramref name="nativeNew"/>, <paramref name="nativeOld"/> therefore moves the file
    /// OLD→NEW — back to the renamed location. A non-empty return means an INCOMPLETE rollback to surface.
    /// </summary>
    private async Task<IReadOnlyList<string>> RollbackReverseMove(
        bool sameVolume, string nativeNew, string nativeOld, CancellationToken ct)
        => sameVolume
            ? _disk.Rollback(nativeNew, nativeOld, [])
            : await _cross.RollbackAsync(nativeNew, nativeOld, [], ct);

    // ── per-entry outcome (a tiny tagged union) ───────────────────────────────

    private abstract record RevertOutcome
    {
        public static readonly Undone UndoneInstance = new();
        public sealed record Undone : RevertOutcome;
        public sealed record Skipped(UndoFailure Failure) : RevertOutcome;
        public sealed record Failed(UndoFailure Failure) : RevertOutcome;
    }

    // ── event mapping — the exact forward-equivalent reconstruction ───────────

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

    // ── path/name helpers (pure string math — mirror RenamerExecutor) ──────────





}
