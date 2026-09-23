using Cove.Core.Events;
using Renamer.Options;
using Renamer.Planner;

using static global::Renamer.Execution.KindEvents;
using static global::Renamer.Planner.PathOps;

namespace Renamer.Execution;

// Applies each plan item independently; one item's failure does not abort the batch. The order per
// item is fixed: re-check the collision against disk and database, move on disk, set
// Basename/ParentFolderId and save once, then assert the Path Cove recomputed matches the on-disk
// path, then journal and publish. A move that succeeded followed by a save that failed is rolled back
// on disk.
//
// BaseFileEntity.Path is never assigned here: Cove recomputes it from Basename/ParentFolderId inside
// the save. A locked source comes back from DiskMover as a skip; the lock is not forced.
public sealed class RenamerExecutor
{
    private readonly IRenamerDataPort _port;
    private readonly IEventBus _eventBus;
    private readonly IRevertJournal _journal;
    private readonly string _runId;
    private readonly CrossVolumeMover _cross;

    // Bound on the execution-time collision suffix loop before giving up with a skip.
    private const int MaxSuffixAttempts = 1000;

    // The run id names the batch every journalled row belongs to. A row that names no batch can be
    // stored but never read back, so an undo would find nothing; the caller passes the run id it
    // opened the batch with.
    public RenamerExecutor(IRenamerDataPort port, IEventBus eventBus, IRevertJournal journal, string runId,
        CrossVolumeMover? cross = null)
    {
        _port = port;
        _eventBus = eventBus;
        _journal = journal;
        _runId = runId;
        _cross = cross ?? new CrossVolumeMover();
    }

    // NewPath is the intended path on a skip or a failure. Reason says why a skip or a failure
    // happened, or what went wrong alongside a rename that still succeeded (a rejected sidecar, an
    // unwritten revert-log entry, a source folder that could not be cleaned up); it is null when a
    // rename had nothing to report.
    public sealed record ItemResult(int FileId, string OldPath, string NewPath, RenamerStatus Status, string? Reason);

    // A failed item is one whose save threw or whose saved row could not be verified. Only the first
    // rolls the disk back; a save that committed is never reverted, so the two are told apart by the
    // item's Reason and not by the bucket.
    //
    // It carries no copy of the journalled rows. The journal table is the durable record of what can be
    // put back, so a second in-memory list would drift from it, and it would have to be thread-safe as
    // well: one result object is built per worker while one journal is shared by all of them.
    public sealed record RenamerRunResult(
        IReadOnlyList<ItemResult> Renamed,
        IReadOnlyList<ItemResult> Skipped,
        IReadOnlyList<ItemResult> Failed);

    // Items the planner already classified as skip or no-op are carried into the skipped bucket
    // untouched. A cancellation aborts the run.
    //
    // preResolvedFolderIds maps a target folder path to its folder id, resolved once in the caller's
    // sequential phase. With it, no parallel worker does a check-then-act folder create on a shared
    // Folder row, which raced to duplicate rows and a DbUpdateException. When the map is absent, or
    // carries no entry for a path, the executor resolves the folder itself; that is safe only because
    // the callers that omit the map are single-threaded.
    public async Task<RenamerRunResult> ExecuteAsync(
        RenamerPlan plan, RenamerOptions options,
        IReadOnlyDictionary<string, int>? preResolvedFolderIds = null, CancellationToken ct = default)
    {
        var renamed = new List<ItemResult>();
        var skipped = new List<ItemResult>();
        var failed = new List<ItemResult>();

        // Plan items reference file ids, not captions, so the entity is loaded once here to resolve
        // each file's sidecar set.
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
                // A cancellation on host shutdown is not a per-item failure, so the filter excludes it.
                // Any other throw outside the save path fails that one item and the batch continues;
                // save-path throws are handled inside the item.
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
        if (item.Status is not (RenamerStatus.Rename or RenamerStatus.Move))
        {
            skipped.Add(new ItemResult(item.FileId, item.OldFullPath, item.NewFullPath, item.Status, item.Reason));
            return;
        }

        // A source that is in the database but gone from disk reaches the mover as a
        // FileNotFoundException or a DirectoryNotFoundException, both of which derive from IOException
        // and so bucket as SkipLocked, labelling a gone file "in use". This check runs before the folder
        // resolve, the collision loop and the mover, and nothing has been moved or saved yet.
        if (!System.IO.File.Exists(ToNative(item.OldFullPath)))
        {
            skipped.Add(new ItemResult(item.FileId, item.OldFullPath, item.NewFullPath,
                RenamerStatus.SkipMissingSource, "skipped: source file is missing on disk"));
            return;
        }

        bool isMove = item.Status == RenamerStatus.Move;

        // A move prefers the caller's pre-resolved folder id so that no parallel worker does a
        // check-then-act create on a shared Folder row. The parallel batch path always passes the map.
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

        // The planner's snapshot may be stale by now, so the collision is re-checked against both disk
        // and database and re-suffixed until the name is free.
        string targetFolder = item.TargetFolderPath;
        var (filename, ext) = SplitBasename(item.NewBasename);
        string candidate = item.NewBasename;
        string newFull = JoinPath(targetFolder, candidate);
        int attempt = 0;
        // The disk check excludes only the source file's own case-variant slot. On a case-insensitive
        // volume File.Exists("Movie.mkv") is true while "movie.mkv" exists, and a case-only rename of
        // the file onto itself is the move the OS performs, not a clobber. A different file at that name
        // still collides. The database check already excludes the source row.
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

        // The suffix loop works from a fresher snapshot than the plan saw, so it can settle on a name
        // longer than any the plan measured. This re-measure precedes the sidecar plan and every disk
        // write, so a rejected candidate leaves the source where it is.
        var budget = PathConfinement.WithinBudget(targetFolder, candidate, options);
        if (!budget.Accepted)
        {
            skipped.Add(new ItemResult(item.FileId, item.OldFullPath, newFull, RenamerStatus.SkipTooLong,
                budget.Reason));
            return;
        }

        var (plannedSidecars, captionRenames, sidecarWarnings) =
            PlanSidecarMoves(srcFile, item.OldFullPath, targetFolder, candidate, options);

        // The disk move runs before the database is touched, so a failed move leaves the database
        // untouched and never pointing at a missing file.
        string nativeOld = ToNative(item.OldFullPath);
        string nativeNew = ToNative(newFull);
        bool sameVolume = VolumeClassifier.SameVolume(item.OldFullPath, newFull);

        var move = await Movers.MoveAsync(
            sameVolume, _cross, nativeOld, nativeNew,
            [.. plannedSidecars.Select(s => new SidecarMove(ToNative(s.From), ToNative(s.To)))], ct);
        var movedSidecars = move.MovedSidecars;
        var moverWarnings = move.Warnings;

        if (!move.Moved)
        {
            // The mover's own classification decides the status, through the one mapping both tiers
            // share: a lock, a denial, a failed verify and a clean shutdown ask an operator for
            // different things. The lock is not forced and the batch continues.
            skipped.Add(new ItemResult(
                item.FileId, item.OldFullPath, newFull,
                MoveOutcomeClassifier.StatusFor(move.Outcome), move.Reason));
            return;
        }

        // Only the captions that actually moved on disk get their DB Filename updated.
        var movedCaptionNames = movedSidecars
            .Select(s => BasenameOf(NormalizeSlash(s.To)))
            .ToHashSet(StringComparer.Ordinal);
        var appliedCaptionRenames = captionRenames
            .Where(cr => movedCaptionNames.Contains(cr.NewFilename))
            .ToList();

        // The save follows the disk move, and a throw from it rolls the disk back. The filename-derived
        // title rides in the same save as the rename that produced it; MetadataProjector.DerivedTitle
        // covers why it is recorded at all.
        var mutation = new RenamerFileMutation(
            item.FileId, candidate, isMove ? targetFolderId : null,
            appliedCaptionRenames.Count > 0 ? appliedCaptionRenames : null,
            item.DerivedTitle is { Length: > 0 } derived
                ? new RenamerEntityTitleWrite(plan.Kind, plan.EntityId, derived)
                : null);

        try
        {
            // The Path Cove recomputed on save has to match the on-disk location just moved to, and a
            // divergence means disk and database disagree.
            string recomputed = await _port.ApplyAndSaveAsync(mutation, ct);
            string expected = NormalizeSlash(newFull);
            if (!PathsEqual(recomputed, expected))
            {
                // Disk and database disagree after a save that committed. The disk goes back to the old
                // path through the mover that moved it, and the rollback warnings are captured so an
                // incomplete rollback is not reported as a clean one. No journal row and no event on
                // this path: the move is being undone, so there is nothing to reindex or to undo.
                IReadOnlyList<string> rbWarnings = await Movers.RollbackAsync(sameVolume, _cross, nativeOld, nativeNew, movedSidecars, ct);

                string mismatch = $"recomputed Path '{recomputed}' != on-disk '{expected}'";
                string warned = rbWarnings.Count > 0
                    ? $"; rollback warnings: {string.Join("; ", rbWarnings)}"
                    : "";

                // Whether the row may be put back is read off the primary file's own location. A
                // rollback reports its sidecars and the primary in one warning list, so judging by the
                // list would let a caption that could not come back leave the row naming a location the
                // media file has left. A case-only rename is its own target on a case-insensitive
                // volume, so the vacated-target half of the check is skipped for one.
                bool primaryBack = System.IO.File.Exists(nativeOld)
                    && (IsSelfPath(newFull, item.OldFullPath) || !System.IO.File.Exists(nativeNew));

                if (!primaryBack)
                {
                    failed.Add(new ItemResult(item.FileId, item.OldFullPath, newFull, RenamerStatus.Failed,
                        $"{mismatch}; the file did NOT return to its old path, so the committed row is "
                        + $"left naming the new one{warned}"));
                    return;
                }

                // The file is back where it started, so the committed row is put back to match it.
                string? restoreFailure = await RestoreSavedRowAsync(
                    item.FileId, item.OldFullPath, srcFile, isMove, appliedCaptionRenames, ct);

                // The title that rode in the same save is not part of the row's location and is not
                // reverted with it, so a reader of this reason is told it is still there.
                string titleKept = item.DerivedTitle is { Length: > 0 }
                    ? "; the filename-derived title recorded in that save was not reverted"
                    : "";

                string note = restoreFailure is null
                    ? $"{mismatch}; rolled back{titleKept}{warned}"
                    : $"{mismatch}; file rolled back, database row NOT confirmed at the old path: "
                      + $"{restoreFailure}{titleKept}{warned}";
                failed.Add(new ItemResult(item.FileId, item.OldFullPath, newFull, RenamerStatus.Failed, note));
                return;
            }
        }
        catch (Exception ex)
        {
            // The save failed after the move succeeded, for instance when the
            // (ParentFolderId, Basename) unique index throws a DbUpdateException. The disk goes back
            // through the mover that moved it, so disk and database stay consistent: a same-volume move
            // rolls back through the atomic DiskMover.Rollback, and a cross-volume move copies the bytes
            // back and verifies them before the source is restored.
            // A best-effort rollback can fail to restore, when the old slot was re-occupied, a
            // cross-volume copy-back failed verify, or a target is locked, and it reports that in its
            // warnings. Discarding them would report a file as rolled back when it was not, leaving a
            // silent disk and database divergence.
            // The rollback runs on CancellationToken.None when the save was cancelled, because the
            // ambient token is already cancelled.
            var rollbackCt = ex is OperationCanceledException ? CancellationToken.None : ct;
            IReadOnlyList<string> rbWarnings = await Movers.RollbackAsync(sameVolume, _cross, nativeOld, nativeNew, movedSidecars, rollbackCt);

            if (ex is OperationCanceledException)
            {
                throw;
            }

            string note = rbWarnings.Count > 0
                ? $"DB save failed; rollback INCOMPLETE: {ex.Message}; rollback warnings: {string.Join("; ", rbWarnings)}"
                : $"DB save failed; file rolled back: {ex.Message}";
            failed.Add(new ItemResult(item.FileId, item.OldFullPath, newFull, RenamerStatus.Failed, note));
            return;
        }

        // Everything from here runs with the save committed and the on-disk path already checked, so it
        // sits outside the rollback catch above. A throw here is not a save failure: reverting the move
        // would undo a rename the database agrees with. Each step is best-effort and its failure becomes
        // a warning on the renamed result.
        var postCommitWarnings = new List<string>();

        // The journal row carries the same entity id the event below publishes, so undo reconstructs the
        // forward event from the row. Seq is 0 because the journal mints it on append.
        //
        // The delta is built from what actually moved and is recorded now, not recomputed at undo time.
        // RetargetCaption only rewrites a caption whose name starts with the old stem, so the forward
        // transform is not invertible in general, and a caption rename is applied only for a sidecar
        // whose file really moved on disk, which is a runtime fact no later string arithmetic recovers.
        // The row and the delta are appended at this one site, so a crash cannot leave them disagreeing.
        //
        // A failed append costs this item its undo entry and nothing more: the file is where the
        // database says it is.
        try
        {
            var delta = BuildRevertDelta(srcFile, movedSidecars, appliedCaptionRenames);
            await _journal.AppendAsync(
                new RevertRow(_runId, Seq: 0, plan.EntityId, item.FileId, item.OldFullPath, delta.Serialize()), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A cancellation aborts the run without rolling a committed move back, which is why this
            // region sits outside the catch above.
            postCommitWarnings.Add($"revert-log entry not written: {ex.Message}");
        }

        // A throwing bus leaves the journal row standing, so its failure carries its own warning.
        try
        {
            _eventBus.Publish(new EntityEvent(EventTypeFor(plan.Kind), EntityTypeName(plan.Kind), plan.EntityId));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            postCommitWarnings.Add($"rename event not published: {ex.Message}");
        }

        // The empty-source-folder cleanup runs last, never before the save: a failed save rolls the disk
        // back, so deleting the source directory earlier could be unrecoverable. It runs only when the
        // move changed the parent directory, since a same-folder rename leaves the file there. The
        // cleaner classifies the states it anticipates; the try covers the ones it does not.
        if (isMove && options.RemoveEmptyFolder && !PathsEqual(DirOf(item.OldFullPath), DirOf(newFull)))
        {
            try
            {
                var (_, cleanupWarning) = EmptySourceFolderCleaner.TryRemoveIfEmpty(DirOf(item.OldFullPath));
                if (cleanupWarning is not null)
                {
                    postCommitWarnings.Add(cleanupWarning);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                postCommitWarnings.Add($"empty-folder cleanup skipped: {ex.Message}");
            }
        }

        var itemWarnings = sidecarWarnings.Concat(moverWarnings).Concat(postCommitWarnings).ToList();
        renamed.Add(new ItemResult(item.FileId, item.OldFullPath, newFull, item.Status,
            itemWarnings.Count > 0 ? string.Join("; ", itemWarnings) : null));
    }

    // A configured same-stem neighbour goes to the disk-move list alone and never to the caption rename
    // list, because it carries no database row to update.
    private static (List<SidecarMove> plannedSidecars,
        List<(int CaptionId, string NewFilename)> captionRenames,
        List<string> warnings)
        PlanSidecarMoves(RenamerFile? srcFile, string oldFullPath, string targetFolder, string candidate, RenamerOptions options)
    {
        string oldDir = DirOf(oldFullPath);
        string newStem = StemOf(candidate);
        var captions = srcFile?.Captions ?? [];
        var plannedSidecars = new List<SidecarMove>(captions.Count);
        var captionRenames = new List<(int CaptionId, string NewFilename)>(captions.Count);
        var warnings = new List<string>();
        foreach (var cap in captions)
        {
            // A caption filename is a basename, never a path fragment: a separator or a parent-traversal
            // segment would let a malformed row build a sidecar move reaching outside the primary's
            // source and target folders. Nothing downstream re-checks it: the movers apply no
            // confinement.
            if (!IsPlainBasename(cap.Filename))
            {
                warnings.Add($"sidecar skipped: caption filename '{cap.Filename}' is not a plain basename");
                continue;
            }

            string newCaptionName = RetargetCaption(cap.Filename, oldStem: StemOf(srcFile!.Basename), newStem);
            plannedSidecars.Add(new SidecarMove(
                JoinPath(oldDir, cap.Filename), JoinPath(targetFolder, newCaptionName)));
            captionRenames.Add((cap.CaptionId, newCaptionName));
        }

        // Only the exact stem plus a listed extension is taken, so the candidate set is the stem's own
        // neighbours and never a directory sweep. The extension compare is ordinal-ignore-case in code:
        // File.Exists on a composed path answers case-insensitively only where the filesystem does, so a
        // listed `SRT` would miss an on-disk `clip.srt` on the case-sensitive volume the host runs on.
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

                // A configured extension is a leaf extension, never a path fragment: a malformed entry
                // such as "srt/../../elsewhere" would build a sidecar target outside the primary's
                // folder.
                if (normExt.IndexOfAny(['/', '\\']) >= 0 || normExt.Contains(".."))
                {
                    continue;
                }

                if (!neighborExtensions.TryGetValue(normExt, out string? diskExt))
                {
                    continue;
                }

                // Both sides use the on-disk spelling, so a moved sidecar keeps the extension casing it
                // had on disk and not the casing in the setting.
                string source = JoinPath(oldDir, srcStem + "." + diskExt);
                string target = JoinPath(targetFolder, newStem + "." + diskExt);

                // An in-place or case-only rename leaves source and target equal; skipping it mirrors
                // the primary's self-path handling and avoids a spurious skip-not-clobber warning.
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

                plannedSidecars.Add(new SidecarMove(source, target));
            }
        }

        return (plannedSidecars, captionRenames, warnings);
    }

    // True for a leaf name: no directory part under either separator convention, and no parent-traversal
    // segment. Both separators are tested whatever the host is, because the value comes from a database
    // row a different platform may have written, and Path.GetFileName splits on the running platform's
    // separator only.
    private static bool IsPlainBasename(string filename)
        => filename.Length > 0
            && filename.IndexOfAny(['/', '\\']) < 0
            && !filename.Contains("..", StringComparison.Ordinal)
            && string.Equals(Path.GetFileName(filename), filename, StringComparison.Ordinal);

    // Keyed ordinal-ignore-case with the on-disk spelling as the value. The filesystem filter is the
    // stem, so the candidate set is the primary's own neighbours and does not grow with the folder. A
    // wildcard character inside a stem widens that filter but changes nothing that is taken, because the
    // exact stem comparison here decides. When one extension is present in more than one casing, the
    // ordinal-smallest spelling wins, so the choice does not depend on the filesystem's order.
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

    // The reverse-replay payload for one journalled file, built from what the mover and the save
    // actually did. A caption goes in under its original stored filename, because the reverse direction
    // needs the value it restores to, and deriving it later would reintroduce the non-invertible
    // transform this payload exists to avoid. A rename that carried nothing yields RevertDelta.Empty,
    // which serializes to the journal column's empty marker.
    private static RevertDelta BuildRevertDelta(
        RenamerFile? srcFile,
        IReadOnlyList<SidecarMove> movedSidecars,
        List<(int CaptionId, string NewFilename)> appliedCaptionRenames)
    {
        if (movedSidecars.Count == 0 && appliedCaptionRenames.Count == 0)
        {
            return RevertDelta.Empty;
        }

        // The names as they stood before the save rewrote them. The loaded entity is the only place they
        // still exist by the time this runs.
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

    // Writes a committed rename back off the file row: the basename, the parent folder for a move, and
    // each caption filename the save changed. Returns null once the row recomputes to the old path, or
    // the reason it could not be confirmed there.
    private async Task<string?> RestoreSavedRowAsync(
        int fileId, string oldFullPath, RenamerFile? srcFile, bool isMove,
        IReadOnlyList<(int CaptionId, string NewFilename)> appliedCaptionRenames, CancellationToken ct)
    {
        if (srcFile is null)
        {
            return "the file's pre-rename row was not loaded";
        }

        var restoredCaptions = (srcFile.Captions ?? [])
            .Where(c => appliedCaptionRenames.Any(cr => cr.CaptionId == c.CaptionId))
            .Select(c => (c.CaptionId, NewFilename: c.Filename))
            .ToList();

        string recomputed;
        try
        {
            recomputed = await _port.ApplyAndSaveAsync(
                new RenamerFileMutation(
                    fileId, srcFile.Basename, isMove ? srcFile.ParentFolderId : null,
                    restoredCaptions.Count > 0 ? restoredCaptions : null),
                ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ex.Message;
        }

        // A restore that recomputes somewhere else has not put the row back.
        return PathsEqual(recomputed, oldFullPath)
            ? null
            : $"it recomputed to '{recomputed}', not '{NormalizeSlash(oldFullPath)}'";
    }

    // A caption "video.en.vtt" alongside "video.mkv" becomes "<newStem>.en.vtt". A caption that does not
    // start with the old stem is left unchanged, so nothing is corrupted.
    private static string RetargetCaption(string captionFilename, string oldStem, string newStem)
        => captionFilename.StartsWith(oldStem, StringComparison.Ordinal)
            ? newStem + captionFilename[oldStem.Length..]
            : captionFilename;

    // True when the candidate is the source file's own path: the same canonical location, differing at
    // most by case on a case-insensitive volume. It follows the same OS-aware policy as PathsEqual and
    // VolumeClassifier, so the disk-side self-exclusion agrees with the rest of the slice.
    private static bool IsSelfPath(string candidate, string sourceFullPath) => PathsEqual(candidate, sourceFullPath);
}
