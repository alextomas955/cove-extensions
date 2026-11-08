using Cove.Core.Events;
using Renamer.Planner;

using static global::Renamer.Execution.KindEvents;
using static global::Renamer.Planner.PathOps;

namespace Renamer.Execution;

// Reverse-replays a journalled renamer batch to restore files to their original locations. The undo
// is driven by the paths recorded on each row: the metadata that produced the original name may have
// changed since, so a recomputed name is not a safe restore target.
//
// The file's current path comes from Cove's database. The forward executor appends a journal row only
// after asserting the recomputed database path equals the location it just moved to, and the one
// branch that lets disk and database diverge writes no row, so the database path is exact.
//
// Each entry runs the forward executor's safety order in reverse: re-check the old slot is free on
// both disk and database, move on disk, then save. A save that throws rolls the disk move back, and a
// save that commits to a path the file is not at rolls the disk back and writes the row back with it,
// so there is no state where disk and database disagree. Entries are independent; one failure does not
// abort the batch.
//
// Sidecars replay the delta the forward path recorded, reversed; see RevertDelta. A sidecar that
// cannot go back leaves the entry restored with a warning, since the media file and its row are both
// at the original location. A caption's stored filename is written back only when its file moved back.
//
// Restored rows are reported, not retired here; the caller retires exactly the rows it is given.
public sealed class UndoReplayer
{
    private readonly IRenamerDataPort _port;
    private readonly IEventBus _eventBus;
    private readonly CrossVolumeMover _cross;

    public UndoReplayer(IRenamerDataPort port, IEventBus eventBus, CrossVolumeMover? cross = null)
    {
        _port = port;
        _eventBus = eventBus;
        _cross = cross ?? new CrossVolumeMover();
    }

    // One failed or skipped reverse-replay entry. NewPath is empty when the file is no longer in the
    // library. Stop is what decides whether the row is retired as unrestorable or left pending.
    //
    // RunId and Seq carry the row's identity: a batch can hold two rows for one file id, so a caller
    // retiring this exact row cannot reconstruct it from the file id or the paths.
    public sealed record UndoFailure(
        string RunId,
        long Seq,
        int FileId,
        string OldPath,
        string NewPath,
        string Reason,
        UndoStopReason Stop);

    // A non-fatal note about an entry that was restored anyway.
    public sealed record UndoWarning(int FileId, string Detail);

    // Failed holds entries whose reverse move succeeded but whose save either threw or committed
    // without a row the restored path could be verified against. Both roll the disk back to the
    // renamed location, and a save that committed has its row written back to that location too, so
    // long as the media file got there. Skipped holds entries whose old slot was occupied or locked,
    // which are never clobbered. Restored names the rows the caller retires.
    //
    // Warnings is separate from the two problem buckets: those entries succeeded, and folding them in
    // would stop the caller retiring a row whose file did come back.
    public sealed record UndoRunResult(
        IReadOnlyList<UndoFailure> Failed,
        IReadOnlyList<UndoFailure> Skipped,
        IReadOnlyList<RevertRow> Restored,
        IReadOnlyList<UndoWarning> Warnings)
    {
        public int Undone => Restored.Count;
    }

    // The batch arrives newest-first from the journal, which is the order the rows must be replayed in.
    public async Task<UndoRunResult> RevertAsync(RevertBatch batch, CancellationToken ct = default)
    {
        var failed = new List<UndoFailure>();
        var skipped = new List<UndoFailure>();
        var restored = new List<RevertRow>();
        var warnings = new List<UndoWarning>();

        // One load per entity, so a multi-file item costs one query. The whole row is kept, not just
        // its path: a save that commits to the wrong path is put back from the folder id and caption
        // filenames recorded here, which no longer exist once the save has overwritten them.
        var currentFiles = new Dictionary<int, RenamerFile>();
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
                        currentFiles[file.FileId] = file;
                    }
                }

                if (!currentFiles.TryGetValue(entry.FileId, out var current))
                {
                    // The row outlived its file, deleted since the rename: nothing to move back, and no
                    // current path to name.
                    skipped.Add(new UndoFailure(
                        entry.RunId, entry.Seq, entry.FileId, entry.OldPath, "",
                        "skipped: the renamed file is no longer in the library",
                        UndoStopReason.FileNoLongerInLibrary));
                    continue;
                }

                currentPath = JoinPath(current.ParentFolderPath, current.Basename);
                var outcome = await RevertEntryAsync(batch.Kind, entry, current, currentPath, warnings, ct);
                switch (outcome)
                {
                    case RevertOutcome.Undone: restored.Add(entry); break;
                    case RevertOutcome.Skipped skip: skipped.Add(skip.Failure); break;
                    case RevertOutcome.Failed fail: failed.Add(fail.Failure); break;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Host shutdown is cancellation, not a per-entry failure, so the filter excludes it. Any
                // other throw fails only this entry; the batch continues.
                failed.Add(new UndoFailure(
                    entry.RunId, entry.Seq, entry.FileId, entry.OldPath, currentPath,
                    $"unexpected error: {ex.Message}", UndoStopReason.UnexpectedError));
            }
        }

        return new UndoRunResult(failed, skipped, restored, warnings);
    }

    private async Task<RevertOutcome> RevertEntryAsync(
        RenamerFileKind kind, RevertRow entry, RenamerFile current, string currentPath,
        List<UndoWarning> warnings, CancellationToken ct)
    {
        // A rejected restore target is a reported skip that never reaches a folder-row create or a disk
        // write.
        string oldDir = DirOf(entry.OldPath);
        string oldBasename = BasenameOf(entry.OldPath);

        var (skip, oldFolderId) = await PrepareRestoreTargetAsync(entry, currentPath, oldDir, oldBasename, ct);
        if (skip is not null)
        {
            return skip;
        }

        // The recorded delta, reversed: every sidecar the forward move actually made, replayed from its
        // destination back to its source. An unreadable delta yields an empty one, so the media file
        // this row names still comes back.
        _ = RevertDelta.TryParse(entry.SidecarsJson, out var delta);
        var reverseSidecars = delta.Sidecars
            .Select(s => new SidecarMove(ToNative(s.ToPath), ToNative(s.FromPath)))
            .ToList();

        // A non-moved result, whether locked, target-exists, verify-failed, disk-full or offline, is a
        // reported skip and never a clobber.
        string nativeNew = ToNative(currentPath);
        string nativeOld = ToNative(entry.OldPath);
        bool sameVolume = VolumeClassifier.SameVolume(currentPath, entry.OldPath);

        // The moved sidecars are the ones that actually came back: a rollback reverses exactly those, and
        // they decide which caption filenames may be written back.
        var move = await Movers.MoveAsync(sameVolume, _cross, nativeNew, nativeOld, reverseSidecars, ct);
        var movedSidecars = move.MovedSidecars;
        var sidecarWarnings = move.Warnings;

        if (!move.Moved)
        {
            return new RevertOutcome.Skipped(new UndoFailure(
                entry.RunId, entry.Seq, entry.FileId, entry.OldPath, currentPath,
                move.Reason ?? "skipped: reverse move did not happen", StopFor(move.Outcome)));
        }

        // A sidecar that could not go back, because its old slot is occupied or it is locked, is a
        // warning and not a failure. The media file is at its original path and its row is about to
        // agree.
        foreach (var warning in sidecarWarnings)
        {
            warnings.Add(new UndoWarning(entry.FileId, warning));
        }

        // Only a caption whose file actually moved back gets its stored filename written back. Writing
        // back a caption still sitting at its renamed name would leave the database naming a file that
        // does not exist.
        var movedBackNames = movedSidecars
            .Select(s => BasenameOf(NormalizeSlash(s.To)))
            .ToHashSet(StringComparer.Ordinal);
        var restoredCaptions = delta.Captions
            .Where(c => movedBackNames.Contains(c.OriginalFilename))
            .Select(c => (c.CaptionId, NewFilename: c.OriginalFilename))
            .ToList();

        var mutation = new RenamerFileMutation(
            entry.FileId, oldBasename, oldFolderId, restoredCaptions.Count > 0 ? restoredCaptions : null);
        try
        {
            string recomputed = await _port.ApplyAndSaveAsync(mutation, ct);
            string expected = NormalizeSlash(entry.OldPath);
            if (!PathsEqual(recomputed, expected))
            {
                return await RollBackRecomputeMismatchAsync(
                    entry, current, recomputed, sameVolume, movedSidecars, restoredCaptions, ct);
            }

            _eventBus.Publish(new EntityEvent(EventTypeFor(kind), EntityTypeName(kind), entry.EntityId));
            return RevertOutcome.UndoneInstance;
        }
        catch (Exception ex)
        {
            // The save failed after a successful reverse move, so the disk goes back to the renamed
            // location, as in the mismatch branch above. On the cancel path the ambient token is already
            // cancelled, so the rollback uses None.
            var rollbackCt = ex is OperationCanceledException ? CancellationToken.None : ct;
            IReadOnlyList<string> rbWarnings =
                await Movers.RollbackAsync(sameVolume, _cross, nativeNew, nativeOld, movedSidecars, rollbackCt);

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

    // The save committed, but the row it wrote recomputes to neither the old path nor anywhere the file
    // is. The disk goes back to the renamed location, and the row goes back with it, so the two agree
    // on where the file is. Both movers' Rollback(oldFull, newFull) move newFull to oldFull, so the
    // arguments are passed swapped. Rollback warnings are surfaced so an incomplete rollback, such as
    // the renamed slot being re-occupied or a cross-volume copy-back failing verification, is visible.
    private async Task<RevertOutcome.Failed> RollBackRecomputeMismatchAsync(
        RevertRow entry, RenamerFile current, string recomputed, bool sameVolume,
        IReadOnlyList<SidecarMove> movedSidecars,
        IReadOnlyList<(int CaptionId, string NewFilename)> restoredCaptions, CancellationToken ct)
    {
        string currentPath = JoinPath(current.ParentFolderPath, current.Basename);
        string nativeNew = ToNative(currentPath);
        string nativeOld = ToNative(entry.OldPath);
        IReadOnlyList<string> rbWarnings =
            await Movers.RollbackAsync(sameVolume, _cross, nativeNew, nativeOld, movedSidecars, ct);

        string mismatch = $"recomputed Path '{recomputed}' != restored path '{NormalizeSlash(entry.OldPath)}'";
        string warned = rbWarnings.Count > 0
            ? $"; rollback warnings: {string.Join("; ", rbWarnings)}"
            : "";

        // Whether the row may be put back is read off the media file's own location, not off the
        // rollback's warning list: that list folds the sidecars in, so a caption that could not come
        // back would stop the row returning to the file it names. A case-only undo is its own target, so
        // the vacated-old-slot half of the check cannot hold for one and is skipped.
        bool primaryBack = System.IO.File.Exists(nativeNew)
            && (PathsEqual(entry.OldPath, currentPath) || !System.IO.File.Exists(nativeOld));

        if (!primaryBack)
        {
            return new RevertOutcome.Failed(new UndoFailure(
                entry.RunId, entry.Seq, entry.FileId, entry.OldPath, currentPath,
                $"{mismatch}; the file did not return to the renamed path, so the committed row is left "
                + $"as the save wrote it{warned}",
                UndoStopReason.RestoredPathMismatch));
        }

        string? restoreFailure = await RestoreSavedRowAsync(entry.FileId, current, currentPath, restoredCaptions, ct);
        string note = restoreFailure is null
            ? $"{mismatch}; rolled back{warned}"
            : $"{mismatch}; file rolled back, database row NOT confirmed at the renamed path: "
              + $"{restoreFailure}{warned}";

        return new RevertOutcome.Failed(new UndoFailure(
            entry.RunId, entry.Seq, entry.FileId, entry.OldPath, currentPath, note,
            UndoStopReason.RestoredPathMismatch));
    }

    // Writes the file row back to the basename, folder and caption filenames it held before the undo
    // save, and confirms it recomputes to the path the file is now at. Returns null on success,
    // otherwise why the row could not be confirmed there.
    private async Task<string?> RestoreSavedRowAsync(
        int fileId, RenamerFile current, string currentPath,
        IReadOnlyList<(int CaptionId, string NewFilename)> restoredCaptions, CancellationToken ct)
    {
        // Only the captions the undo save rewrote are written back, and each to the filename it held
        // before that save. A caption the undo left alone must not be touched here.
        var revertedCaptions = (current.Captions ?? [])
            .Where(c => restoredCaptions.Any(r => r.CaptionId == c.CaptionId))
            .Select(c => (c.CaptionId, NewFilename: c.Filename))
            .ToList();

        string recomputed;
        try
        {
            recomputed = await _port.ApplyAndSaveAsync(
                new RenamerFileMutation(
                    fileId, current.Basename, current.ParentFolderId,
                    revertedCaptions.Count > 0 ? revertedCaptions : null),
                ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ex.Message;
        }

        return PathsEqual(recomputed, currentPath)
            ? null
            : $"it recomputed to '{recomputed}', not '{NormalizeSlash(currentPath)}'";
    }

    // Validates the restore target before any mutation, returning either the skip that halts the entry
    // or the resolved old folder id.
    private async Task<(RevertOutcome.Skipped? Skip, int OldFolderId)> PrepareRestoreTargetAsync(
        RevertRow entry, string currentPath, string oldDir, string oldBasename,
        CancellationToken ct)
    {
        // A missing original directory is never recreated: doing so could restore the file to a wrong
        // place when the original drive is offline or the folder was deleted. Directory.Exists returns
        // false without throwing on an unmapped or offline drive, so that case lands here too.
        if (!Directory.Exists(ToNative(oldDir)))
        {
            return (new RevertOutcome.Skipped(new UndoFailure(
                entry.RunId, entry.Seq, entry.FileId, entry.OldPath, currentPath,
                "skipped: original directory no longer exists",
                UndoStopReason.OriginalDirectoryUnavailable)), 0);
        }

        // The old slot must be free on both disk and database; an occupied slot is skipped, never
        // clobbered. A folder with no row holds no database collision.
        int? existingFolderId = await _port.TryGetFolderIdAsync(oldDir, ct);
        if (System.IO.File.Exists(ToNative(entry.OldPath))
            || (existingFolderId is int folderId
                && await _port.CollisionExistsAsync(folderId, oldBasename, entry.FileId, ct)))
        {
            return (new RevertOutcome.Skipped(new UndoFailure(
                entry.RunId, entry.Seq, entry.FileId, entry.OldPath, currentPath,
                "skipped: old location is occupied on disk or in the database",
                UndoStopReason.OriginalLocationOccupied)), 0);
        }

        return (null, existingFolderId ?? await _port.GetOrCreateFolderIdAsync(oldDir, ct));
    }

    // The Moved arm is unreachable while callers only ask about a move that did not happen. It maps to
    // a retryable reason so that arm becoming reachable cannot silently retire a row. Every member is
    // named, so a new MoveOutcome member fails the build instead of collapsing into a default.
    private static UndoStopReason StopFor(MoveOutcome outcome) => outcome switch
    {
        // Locked and TargetExists share one stop reason: an undo stop is retryable either way, so the
        // distinction changes no recovery route.
        MoveOutcome.Locked => UndoStopReason.ReverseMoveLockedOrTargetExists,
        MoveOutcome.TargetExists => UndoStopReason.ReverseMoveLockedOrTargetExists,
        MoveOutcome.PermissionDenied => UndoStopReason.ReverseMovePermissionDenied,
        MoveOutcome.VerifyFailed => UndoStopReason.ReverseMoveVerifyFailed,
        MoveOutcome.Cancelled => UndoStopReason.ReverseMoveCancelled,
        MoveOutcome.Moved => UndoStopReason.UnexpectedError,
    };

    private abstract record RevertOutcome
    {
        public static readonly Undone UndoneInstance = new();
        public sealed record Undone : RevertOutcome;
        public sealed record Skipped(UndoFailure Failure) : RevertOutcome;
        public sealed record Failed(UndoFailure Failure) : RevertOutcome;
    }
}
