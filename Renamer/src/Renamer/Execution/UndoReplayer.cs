using Cove.Core.Events;
using Renamer.Planner;

namespace Renamer.Execution;

/// <summary>
/// Reverse-replays a logged renamer batch to restore files to their original locations. It is
/// path-driven from the <see cref="RevertLog.RevertBatch"/> (NOT metadata-driven — it does NOT
/// synthesize a <see cref="RenamerPlan"/> nor reuse <see cref="RenamerExecutor"/>, because the
/// original metadata may have changed since the renamer; replaying recorded paths is the only safe
/// way to undo). It composes the SAME collaborators the forward executor uses:
/// <see cref="CoveRenamerDataPort"/>, <see cref="IEventBus"/>, <see cref="DiskMover"/>.
///
/// For each entry it mirrors <see cref="RenamerExecutor.ExecuteItemAsync"/> REVERSED, over the same
/// safety spine:
/// <list type="number">
/// <item>resolve the OLD directory + OLD basename from <c>entry.OldPath</c> and the old folder id;</item>
/// <item>collision re-check the OLD slot is free on BOTH disk and DB → skip+report on conflict
/// (never clobber an existing file);</item>
/// <item>disk move NEW→OLD with the 2-arg never-overwrite <see cref="DiskMover.Move"/> → a non-moved
/// result is a skip+report (locked/exists);</item>
/// <item>set Basename/ParentFolderId back via <see cref="CoveRenamerDataPort.ApplyAndSaveAsync"/>; on a
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
    private readonly CoveRenamerDataPort _port;
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
    public UndoReplayer(CoveRenamerDataPort port, IEventBus eventBus, DiskMover disk,
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
    /// <param name="NewPath">The path the file currently sits at (its renamed location).</param>
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

        foreach (var entry in batch.Entries)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var outcome = await RevertEntryAsync(batch.Kind, entry, ct);
                switch (outcome)
                {
                    case RevertOutcome.Undone: undone++; break;
                    case RevertOutcome.Skipped skip: skipped.Add(skip.Failure); break;
                    case RevertOutcome.Failed fail: failed.Add(fail.Failure); break;
                }
            }
            catch (Exception ex)
            {
                // Any unexpected throw outside the save path is reported as a failure for that entry
                // only — the batch continues (each entry is independent).
                failed.Add(new UndoFailure(entry.FileId, entry.OldPath, entry.NewPath, $"unexpected error: {ex.Message}"));
            }
        }

        return new UndoRunResult(undone, failed, skipped);
    }

    private async Task<RevertOutcome> RevertEntryAsync(RenamerFileKind kind, RevertLog.RevertEntry entry, CancellationToken ct)
    {
        // (1) Resolve the OLD directory + OLD basename.
        string oldDir = DirOf(entry.OldPath);
        string oldBasename = BasenameOf(entry.OldPath);

        // (1a) RESTORE-TARGET RE-GATE — PRE-MUTATION. An undo is a WRITE back to the recorded OLD
        //      location, so a RELOCATING undo must pass the SAME write-boundary gate the forward move used
        //      (the allowlist guards in RenamerExecutor), in case the allowlist changed or the OLD dir is now a
        //      junction-to-elsewhere. CanonicalPathGuard.Check disk-resolves the real on-disk target
        //      (junction/symlink/8.3/UNC-aware, fail-closed) and rejects a target outside every allowed
        //      root.
        //
        //      RE-GATE ONLY RELOCATIONS — the undo-direction analog of the forward executor's
        //      `isMove && AllowedRoots.Count > 0` predicate. The forward path
        //      NEVER gates an in-place renamer (it has no `isMove`), and `AllowedRoots` is an opt-in
        //      WIDENING governing relocation DESTINATIONS — not the library's existing folders. An
        //      in-place restore (OLD and NEW share the same directory) writes back into the directory the
        //      file already legitimately occupies — no write boundary is crossed — so gating it would make
        //      ordinary in-place renamers permanently non-undoable the moment any AllowedRoot is set. We
        //      therefore gate ONLY when the restore changes directory (DirOf(OLD) != DirOf(NEW)), using the
        //      same OS-aware PathsEqual comparison the rest of this class uses.
        //
        //      With an EMPTY allowlist this block is skipped entirely, so a same-folder undo stays
        //      byte-identical to the no-allowlist behavior. Use CanonicalPathGuard.Check ONLY; it reuses
        //      PathConfinement.IsUnderRoot internally — do NOT separately call PathConfinement.Resolve. A
        //      reject is a reported skip, never a clobber. Placed BEFORE GetOrCreateFolderIdAsync (a DB
        //      folder-row create) and before any disk write so a rejected target never materializes a
        //      folder row nor touches disk (the ordering that keeps a rejected target from leaving a trace).
        //
        //      Gate-target granularity: we gate `oldDir` (the FOLDER), matching the forward
        //      executor's primary folder gate at (1b) (CanonicalPathGuard.Check(item.TargetFolderPath…)).
        //      The OLD basename adds no escape — the collision re-check below proves the OLD leaf does not
        //      currently exist, so the leaf cannot be a junction-to-elsewhere, and File.Move follows the
        //      directory junction to the exact place the guard resolved. The folder is the correct
        //      granularity here.
        bool isRelocation = !PathsEqual(oldDir, DirOf(entry.NewPath));
        if (isRelocation && _allowedRoots.Count > 0)
        {
            var guard = CanonicalPathGuard.Check(oldDir, _allowedRoots);
            if (!guard.Accepted)
            {
                return new RevertOutcome.Skipped(new UndoFailure(
                    entry.FileId, entry.OldPath, entry.NewPath,
                    $"skipped: restore target rejected by allowlist: {guard.Reason}"));
            }
        }

        // (1b) DIR-MISSING / OFFLINE-OLD-DRIVE classify point — also PRE-MUTATION. If the resolved
        //      OLD directory no longer exists, report a skip and do NOT recreate it: recreating could
        //      restore the file to a wrong/relocated place when the original drive is offline or the
        //      folder was deleted, violating the never-lose-track-of-a-file core value. Directory.Exists
        //      returns false (never throws) on an unmapped/offline drive, so this cleanly classifies the
        //      offline-OLD-drive case too (no catch(DriveNotFoundException) is needed). Placed before the
        //      folder-row create and the disk write so a missing target never advances.
        if (!Directory.Exists(ToNative(oldDir)))
        {
            return new RevertOutcome.Skipped(new UndoFailure(
                entry.FileId, entry.OldPath, entry.NewPath, "skipped: original directory no longer exists"));
        }

        int oldFolderId = await _port.GetOrCreateFolderIdAsync(oldDir, ct);

        // (2) Collision re-check the OLD slot is free on BOTH disk and DB; if occupied → skip (no clobber).
        if (System.IO.File.Exists(ToNative(entry.OldPath))
            || await _port.CollisionExistsAsync(oldFolderId, oldBasename, entry.FileId, ct))
        {
            return new RevertOutcome.Skipped(new UndoFailure(
                entry.FileId, entry.OldPath, entry.NewPath, "skipped: old location is occupied on disk or in the database"));
        }

        // (3) Reverse disk move NEW→OLD. VOLUME BRANCH, mirroring the forward executor's move branch
        //     REVERSED (src = NEW, dst = OLD): a same-volume reverse takes the atomic synchronous
        //     2-arg never-overwrite DiskMover.Move fast path; a
        //     cross-volume reverse takes the verified copy-back → verify(size+hash) → promote →
        //     delete-source-last CrossVolumeMover.MoveAsync. Both return the identical
        //     MoveResult shape, so a non-moved result (locked / target-exists / verify-failed /
        //     disk-full / offline) flows into the SAME skip+report below — never a clobber. Captions
        //     are out of undo scope, so the cross reverse passes sidecars: null. The matching mover is
        //     also used for the save-throw rollback at (4)/(5) below.
        string nativeNew = ToNative(entry.NewPath);
        string nativeOld = ToNative(entry.OldPath);
        bool sameVolume = VolumeClassifier.SameVolume(entry.NewPath, entry.OldPath);

        bool moved;
        string? moveReason;
        if (sameVolume)
        {
            var move = _disk.Move(nativeNew, nativeOld);
            moved = move.Moved;
            moveReason = move.Reason;
        }
        else
        {
            var move = await _cross.MoveAsync(nativeNew, nativeOld, sidecars: null, ct);
            moved = move.Moved;
            moveReason = move.Reason;
        }

        if (!moved)
        {
            return new RevertOutcome.Skipped(new UndoFailure(
                entry.FileId, entry.OldPath, entry.NewPath, moveReason ?? "skipped: reverse move did not happen"));
        }

        // (4) Reverse DB save: set Basename back (and the parent folder for the in-place/move case).
        //     Captions are out of undo scope for v1 (null caption renamers).
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
                IReadOnlyList<string> rbWarnings = sameVolume
                    ? _disk.Rollback(nativeNew, nativeOld, [])
                    : await _cross.RollbackAsync(nativeNew, nativeOld, [], ct);
                string note = rbWarnings.Count > 0
                    ? $"recomputed Path '{savedFile.RecomputedPath}' != restored path '{expected}'; rollback INCOMPLETE: {string.Join("; ", rbWarnings)}"
                    : $"recomputed Path '{savedFile.RecomputedPath}' != restored path '{expected}'; rolled back";
                return new RevertOutcome.Failed(new UndoFailure(
                    entry.FileId, entry.OldPath, entry.NewPath, note));
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
            IReadOnlyList<string> rbWarnings = sameVolume
                ? _disk.Rollback(nativeNew, nativeOld, [])
                : await _cross.RollbackAsync(nativeNew, nativeOld, [], ct);
            string note = rbWarnings.Count > 0
                ? $"DB save failed; rollback INCOMPLETE: {ex.Message}; rollback warnings: {string.Join("; ", rbWarnings)}"
                : $"DB save failed; file rolled back: {ex.Message}";
            return new RevertOutcome.Failed(new UndoFailure(
                entry.FileId, entry.OldPath, entry.NewPath, note));
        }
    }

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
}
