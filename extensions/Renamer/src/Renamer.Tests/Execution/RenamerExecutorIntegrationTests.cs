using Cove.Core.Events;
using Renamer.Execution;
using Renamer.Options;
using Renamer.Planner;
using Renamer.Tests.Execution.Collisions;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Execution;

/// <summary>
/// Full renamer path: seed a Folder + VideoFile on a SQLite-in-memory <see cref="Cove.Data.CoveContext"/>
/// AND a matching file in a real <see cref="TempDir"/>, plan + execute an in-place renamer, and assert:
/// (a) the file is at the new on-disk path and absent at the old; (b) the DB VideoFile.Basename is the
/// new name and its RECOMPUTED Path == folder.Path + "/" + newBasename (Cove recomputed it on save — the
/// executor never set .Path); (c) the IEventBus received exactly one VideoUpdated for the entity id
/// (asserting the call ARGS, not merely that Publish was called).
///
/// Uses SQLite (relational) so the unique index + ComputeFilePaths are faithful; the real temp dir is
/// the disk tier. Both disposables are released in a finally.
/// </summary>
[Trait("Tier", "L1")]
public sealed class RenamerExecutorIntegrationTests
{
    [Fact]
    public async Task MovesDiskAndUpdatesRecord_RecomputedPathMatches_PublishesEvent()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            // The Folder.Path is the real temp-dir root so disk + DB align on one absolute location.
            string folderPath = dir.Root.Replace('\\', '/');
            var (_, videoId, fileId) =
                await ExecutorTestSeed.SeedVideoAsync(db, folderPath, "raw clip.mkv", "My Film");

            // Real on-disk source matching the seeded row.
            string oldFull = Path.Combine(dir.Root, "raw clip.mkv");
            File.WriteAllText(oldFull, "video-bytes");

            var port = new CoveRenamerDataPort(db);
            var bus = new CapturingEventBus();
            var journal = new FakeRevertJournal();
            var executor = new RenamerExecutor(port, bus, journal, "run-test", new DiskMover());

            var options = new RenamerOptions { FilenameTemplate = "$title" }; // → "My Film.mkv"

            // Plan via the live port (read-only), then execute.
            var plan = await new RenamerPlanner(port).PlanAsync(RenamerFileKind.Video, videoId, options, default);
            var result = await executor.ExecuteAsync(plan, options, default);

            // (a) disk: new exists, old gone, content intact.
            string newFull = Path.Combine(dir.Root, "My Film.mkv");
            Assert.True(File.Exists(newFull), "renamed file must exist on disk");
            Assert.False(File.Exists(oldFull), "old file must be gone");
            Assert.Equal("video-bytes", File.ReadAllText(newFull));

            // (b) DB: basename updated; Path RECOMPUTED (not set) to folder + new basename.
            var (basename, path) = await ExecutorTestSeed.ReadFileAsync(db, fileId);
            Assert.Equal("My Film.mkv", basename);
            Assert.Equal(folderPath + "/My Film.mkv", path);

            // Result buckets: one renamed, none skipped/failed; one journal row written.
            var renamedItem = Assert.Single(result.Renamed);
            Assert.Equal(RenamerStatus.Rename, renamedItem.Status);
            Assert.Empty(result.Failed);
            Assert.Empty(result.Skipped);
            var revert = Assert.Single(journal.Rows);
            Assert.Equal(fileId, revert.FileId);
            Assert.EndsWith("raw clip.mkv", revert.OldPath);

            // (c) event ARGS: exactly one VideoUpdated for this video id.
            var evt = Assert.IsType<EntityEvent>(Assert.Single(bus.Published));
            Assert.Equal(EventType.VideoUpdated, evt.Type);
            Assert.Equal("Video", evt.EntityType);
            Assert.Equal(videoId, evt.EntityId);

            // MOVE-01 explicit: the classifier verdict for the executed in-place pair is same-volume,
            // so the atomic DiskMover fast path (above) is the one that ran.
            Assert.True(VolumeClassifier.SameVolume(oldFull, newFull),
                "an in-place renamer under one root must classify as same-volume (DiskMover path)");
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    /// <summary>
    /// T3: a host shutdown mid-save (an <see cref="OperationCanceledException"/> from the DB save) is
    /// cancellation, not a data failure. The post-move rollback still restores the disk, then the OCE
    /// propagates out of the batch — it must NOT land as a <see cref="RenamerStatus.Failed"/> item row.
    /// Same-volume so it runs on every platform.
    /// </summary>
    [Fact]
    public async Task SaveCancelled_RollsBackAndPropagates_NeverFailed()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string folderPath = dir.Root.Replace('\\', '/');
            var (_, videoId, _) =
                await ExecutorTestSeed.SeedVideoAsync(db, folderPath, "raw clip.mkv", "My Film");

            string oldFull = Path.Combine(dir.Root, "raw clip.mkv");
            File.WriteAllText(oldFull, "video-bytes");

            var options = new RenamerOptions { FilenameTemplate = "$title" }; // → "My Film.mkv"
            var plan = await new RenamerPlanner(new CoveRenamerDataPort(db))
                .PlanAsync(RenamerFileKind.Video, videoId, options, default);

            var executor = new RenamerExecutor(
                new CancelOnSaveDataPort(db), new CapturingEventBus(), new FakeRevertJournal(), "run-test", new DiskMover());

            // The cancel flows out as cancellation (the batch ends), never a Failed row.
            await Assert.ThrowsAsync<OperationCanceledException>(() => executor.ExecuteAsync(plan, options, default));

            // The post-move rollback still ran on the cancel path: the file is back at OLD, none at NEW.
            string newFull = Path.Combine(dir.Root, "My Film.mkv");
            Assert.True(File.Exists(oldFull), "cancel rollback must restore the source");
            Assert.False(File.Exists(newFull), "no file may linger at the new path after a cancelled save");
            Assert.Equal("video-bytes", File.ReadAllText(oldFull));
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    /// <summary>
    /// A source row present in the DB but ABSENT on disk must be classified SkipMissingSource by the
    /// executor's source pre-check — NOT SkipLocked (the swallowed-IOException bucket it would fall
    /// into if it reached the mover). It is a safe no-op skip: nothing renamed/failed, no revert-log
    /// row, no event published.
    /// </summary>
    [Fact]
    public async Task MissingSource_ClassifiedSkipMissingSource_NotSkipLocked()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            // Seed the Folder + VideoFile on the real temp-dir root, but write NO on-disk source file.
            string folderPath = dir.Root.Replace('\\', '/');
            var (_, videoId, _) =
                await ExecutorTestSeed.SeedVideoAsync(db, folderPath, "gone.mkv", "My Film");

            var port = new CoveRenamerDataPort(db);
            var bus = new CapturingEventBus();
            var journal = new FakeRevertJournal();
            var executor = new RenamerExecutor(port, bus, journal, "run-test", new DiskMover());

            var options = new RenamerOptions { FilenameTemplate = "$title" };

            var plan = await new RenamerPlanner(port).PlanAsync(RenamerFileKind.Video, videoId, options, default);
            var result = await executor.ExecuteAsync(plan, options, default);

            // Classified as SkipMissingSource — not SkipLocked — with a missing-source reason.
            var skippedItem = Assert.Single(result.Skipped);
            Assert.Equal(RenamerStatus.SkipMissingSource, skippedItem.Status);
            Assert.Contains("missing", skippedItem.Reason);

            // A missing source is a safe no-op skip: nothing moved/failed, no journal row, no event.
            Assert.Empty(result.Renamed);
            Assert.Empty(result.Failed);
            Assert.Empty(journal.Rows);
            Assert.Empty(bus.Published);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    /// <summary>
    /// MOVE-01 cross-branch: force the executor's volume branch to take the verified
    /// <see cref="CrossVolumeMover"/> path by moving a file from a real <see cref="TempDir"/> to a
    /// SUBST-mapped second root (a distinct <see cref="Path.GetPathRoot(string)"/> on the same physical
    /// volume — no second drive). Assert the cross move executed end-to-end: the source is gone, the
    /// destination exists with the original content, the DB Basename + ParentFolderId + recomputed Path
    /// are updated, a journal row is written, and one VideoUpdated event fired.
    /// </summary>
    [SkippableFact]
    public async Task CrossVolumeBranch_HappyMove_UsesCrossMover_DiskAndDbUpdated()
    {
        Skip.IfNot(SecondVolume.IsAvailable, SecondVolume.UnavailableReason);

        using var src = new TempDir();
        using var dst = new SecondVolume();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string srcFolder = src.Root.Replace('\\', '/');
            string dstFolder = dst.Root.Replace('\\', '/').TrimEnd('/'); // "P:" (root, distinct from src)
            var (_, videoId, fileId) =
                await ExecutorTestSeed.SeedVideoAsync(db, srcFolder, "clip.mkv", "My Film");

            string oldFull = Path.Combine(src.Root, "clip.mkv");
            File.WriteAllText(oldFull, "cross-bytes");

            // Sanity: the source and the subst destination are on DIFFERENT path roots → cross-volume.
            string newFull = dstFolder + "/My Film.mkv";
            Assert.False(VolumeClassifier.SameVolume(srcFolder + "/clip.mkv", newFull),
                "precondition: subst destination must be a different path root than the temp source");

            var port = new CoveRenamerDataPort(db);
            var bus = new CapturingEventBus();
            var journal = new FakeRevertJournal();
            // Inject a real CrossVolumeMover (the production mover) so the cross branch runs end-to-end,
            // with the post-copy seam recording the in-flight name it mints. That name is unguessable, so
            // recording it is the only way the leftover assertion below can name the right path instead
            // of a path this test made up.
            var minted = new List<string>();
            var executor = new RenamerExecutor(
                port, bus, journal, "run-test", new DiskMover(), new CrossVolumeMover(Recorder(minted)));

            // Explicit MOVE plan: source on the temp drive, target folder on the subst drive.
            var plan = new RenamerPlan(videoId, RenamerFileKind.Video,
            [
                new RenamerPlanItem(fileId, srcFolder + "/clip.mkv", newFull,
                    RenamerStatus.Move, "My Film.mkv", dstFolder),
            ]);

            var result = await executor.ExecuteAsync(plan, new RenamerOptions(), default);

            // Disk: dest present with original content, source gone, no in-flight copy left behind.
            string newOnDisk = Path.Combine(dst.Root, "My Film.mkv");
            Assert.True(File.Exists(newOnDisk), "cross-moved file must exist at the dest root");
            Assert.Equal("cross-bytes", File.ReadAllText(newOnDisk));
            Assert.False(File.Exists(oldFull), "source must be deleted (delete-source-last) after a verified cross move");
            AssertMintedPathsGone(minted);

            // Result buckets: one moved, none skipped/failed; one journal row written.
            var movedItem = Assert.Single(result.Renamed);
            Assert.Equal(RenamerStatus.Move, movedItem.Status);
            Assert.Empty(result.Failed);
            Assert.Empty(result.Skipped);
            Assert.Single(journal.Rows);

            // DB: Basename updated, ParentFolderId moved to the (new) dest folder, recomputed Path matches.
            var (basename, path) = await ExecutorTestSeed.ReadFileAsync(db, fileId);
            Assert.Equal("My Film.mkv", basename);
            Assert.Equal(dstFolder + "/My Film.mkv", path);

            // Event ARGS: exactly one VideoUpdated for this video id.
            var evt = Assert.IsType<EntityEvent>(Assert.Single(bus.Published));
            Assert.Equal(EventType.VideoUpdated, evt.Type);
            Assert.Equal(videoId, evt.EntityId);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    /// <summary>
    /// A cross-volume move the mover classified <see cref="MoveOutcome.PermissionDenied"/> reaches the
    /// run result as <see cref="RenamerStatus.SkipPermissionDenied"/> — NOT as
    /// <see cref="RenamerStatus.SkipLocked"/>, which every non-moved outcome once collapsed into. A
    /// denial and a lock ask for opposite responses, so an operator can only act on the difference if
    /// the status carries it.
    /// </summary>
    [SkippableFact]
    public async Task CrossVolumeMove_PermissionDenied_ClassifiedSkipPermissionDenied_NotSkipLocked()
    {
        Skip.IfNot(SecondVolume.IsAvailable, SecondVolume.UnavailableReason);

        using var src = new TempDir();
        using var dst = new SecondVolume();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string srcFolder = src.Root.Replace('\\', '/');
            string dstFolder = dst.Root.Replace('\\', '/').TrimEnd('/');
            var (_, videoId, fileId) =
                await ExecutorTestSeed.SeedVideoAsync(db, srcFolder, "clip.mkv", "My Film");

            string oldFull = Path.Combine(src.Root, "clip.mkv");
            File.WriteAllText(oldFull, "cross-bytes");

            // Sanity: the source and the subst destination are on DIFFERENT path roots → cross-volume.
            string newFull = dstFolder + "/My Film.mkv";
            Assert.False(VolumeClassifier.SameVolume(srcFolder + "/clip.mkv", newFull),
                "precondition: subst destination must be a different path root than the temp source");

            var port = new CoveRenamerDataPort(db);
            var bus = new CapturingEventBus();
            var journal = new FakeRevertJournal();
            // The seam fires INSIDE CopyVerifyPromoteDeleteAsync's try, so the throw meets that method's
            // own classifying catches. UnauthorizedAccessException does not derive from IOException, so it
            // passes the locked-or-exists catch and lands in the permission one — the outcome asserted
            // below is the mover's real classification of a real throw, not a value this test handed it.
            var executor = new RenamerExecutor(
                port, bus, journal, "run-test", new DiskMover(),
                new CrossVolumeMover((_, _) => throw new UnauthorizedAccessException("denied at the post-copy seam")));

            var plan = new RenamerPlan(videoId, RenamerFileKind.Video,
            [
                new RenamerPlanItem(fileId, srcFolder + "/clip.mkv", newFull,
                    RenamerStatus.Move, "My Film.mkv", dstFolder),
            ]);

            var result = await executor.ExecuteAsync(plan, new RenamerOptions(), default);

            var skippedItem = Assert.Single(result.Skipped);
            Assert.Equal(RenamerStatus.SkipPermissionDenied, skippedItem.Status);
            Assert.StartsWith("permission denied:", skippedItem.Reason);
            Assert.Empty(result.Renamed);
            Assert.Empty(result.Failed);

            // Disk: the source survives at its original path and nothing was left at the destination.
            Assert.True(File.Exists(oldFull), "a denied move must leave the source where it was");
            Assert.Equal("cross-bytes", File.ReadAllText(oldFull));
            Assert.False(File.Exists(Path.Combine(dst.Root, "My Film.mkv")));

            // DB: disk-first means no save ran, so the row still names the original basename and folder.
            var (basename, path) = await ExecutorTestSeed.ReadFileAsync(db, fileId);
            Assert.Equal("clip.mkv", basename);
            Assert.Equal(srcFolder + "/clip.mkv", path);
            Assert.Empty(journal.Rows);
            Assert.Empty(bus.Published);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    /// <summary>
    /// A cross-volume move the mover classified <see cref="MoveOutcome.Cancelled"/> reaches the run
    /// result as <see cref="RenamerStatus.SkipCancelled"/>, and no exception leaves
    /// <c>ExecuteAsync</c>. The mover classifies a cancel rather than throwing it, and the executor
    /// must not convert that back into a failure: on shutdown, work is cancelled, never defective.
    /// </summary>
    [SkippableFact]
    public async Task CrossVolumeMove_Cancelled_ClassifiedSkipCancelled_NotSkipLocked()
    {
        Skip.IfNot(SecondVolume.IsAvailable, SecondVolume.UnavailableReason);

        using var src = new TempDir();
        using var dst = new SecondVolume();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string srcFolder = src.Root.Replace('\\', '/');
            string dstFolder = dst.Root.Replace('\\', '/').TrimEnd('/');
            var (_, videoId, fileId) =
                await ExecutorTestSeed.SeedVideoAsync(db, srcFolder, "clip.mkv", "My Film");

            string oldFull = Path.Combine(src.Root, "clip.mkv");
            File.WriteAllText(oldFull, "cross-bytes");

            // Sanity: the source and the subst destination are on DIFFERENT path roots → cross-volume.
            string newFull = dstFolder + "/My Film.mkv";
            Assert.False(VolumeClassifier.SameVolume(srcFolder + "/clip.mkv", newFull),
                "precondition: subst destination must be a different path root than the temp source");

            var port = new CoveRenamerDataPort(db);
            var bus = new CapturingEventBus();
            var journal = new FakeRevertJournal();
            // Same seam, and OperationCanceledException lands in the FIRST of the three catches — the one
            // that removes the in-flight copy and returns a classified Cancelled rather than rethrowing.
            // That catch is what makes this a real outcome instead of a cancellation this test forced past
            // the mover, and it is why nothing below has to tolerate a throw escaping ExecuteAsync.
            var executor = new RenamerExecutor(
                port, bus, journal, "run-test", new DiskMover(),
                new CrossVolumeMover((_, _) => throw new OperationCanceledException("host shutting down mid-copy")));

            var plan = new RenamerPlan(videoId, RenamerFileKind.Video,
            [
                new RenamerPlanItem(fileId, srcFolder + "/clip.mkv", newFull,
                    RenamerStatus.Move, "My Film.mkv", dstFolder),
            ]);

            var result = await executor.ExecuteAsync(plan, new RenamerOptions(), default);

            var skippedItem = Assert.Single(result.Skipped);
            Assert.Equal(RenamerStatus.SkipCancelled, skippedItem.Status);
            Assert.Empty(result.Renamed);
            Assert.Empty(result.Failed);

            // Disk: nothing renamed, and the source is exactly where and what it was.
            Assert.True(File.Exists(oldFull), "a cancelled move must leave the source where it was");
            Assert.Equal("cross-bytes", File.ReadAllText(oldFull));
            Assert.False(File.Exists(Path.Combine(dst.Root, "My Film.mkv")));

            // DB untouched, and nothing journalled — there is no move to put back.
            var (basename, path) = await ExecutorTestSeed.ReadFileAsync(db, fileId);
            Assert.Equal("clip.mkv", basename);
            Assert.Equal(srcFolder + "/clip.mkv", path);
            Assert.Empty(journal.Rows);
            Assert.Empty(bus.Published);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    /// <summary>
    /// MOVE-05 cross-path rollback: a VERIFIED cross-volume move whose subsequent DB save throws
    /// (a forced <c>(ParentFolderId, Basename)</c> unique-index clash, pre-check bypassed via
    /// <see cref="CollisionBlindDataPort"/>) must roll back through <see cref="CrossVolumeMover.RollbackAsync"/>
    /// — copy the bytes back across the volume and restore the source — leaving disk and DB consistent.
    /// Re-proves disk-first/DB-second for the cross path.
    /// </summary>
    [SkippableFact]
    public async Task CrossVolumeSaveFailure_RollsBackThroughCrossMover_SourceRestored()
    {
        Skip.IfNot(SecondVolume.IsAvailable, SecondVolume.UnavailableReason);

        using var src = new TempDir();
        using var dst = new SecondVolume();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string srcFolder = src.Root.Replace('\\', '/');
            string dstFolder = dst.Root.Replace('\\', '/').TrimEnd('/');

            var (_, videoId, fileA) =
                await ExecutorTestSeed.SeedVideoAsync(db, srcFolder, "a.mkv", "Film A");

            // Pre-seed the DEST folder (same Path the executor will GetOrCreate) holding a row that
            // already occupies "taken.mkv", so the cross-move's save of (destFolderId, "taken.mkv")
            // hits the unique index and throws — AFTER the verified cross move has happened.
            var destFolder = new Cove.Core.Entities.Folder { Path = dstFolder, ModTime = DateTime.UtcNow };
            db.Set<Cove.Core.Entities.Folder>().Add(destFolder);
            await db.SaveChangesAsync();
            await ExecutorTestSeed.SeedAdditionalFileAsync(db, destFolder.Id, videoId, "taken.mkv");

            string oldA = Path.Combine(src.Root, "a.mkv");
            File.WriteAllText(oldA, "A-bytes");
            string newOnDisk = Path.Combine(dst.Root, "taken.mkv");
            Assert.False(File.Exists(newOnDisk), "precondition: dest free so the CROSS move happens before the save");

            string newFull = dstFolder + "/taken.mkv";
            Assert.False(VolumeClassifier.SameVolume(srcFolder + "/a.mkv", newFull),
                "precondition: cross-volume pair");

            var plan = new RenamerPlan(videoId, RenamerFileKind.Video,
            [
                new RenamerPlanItem(fileA, srcFolder + "/a.mkv", newFull,
                    RenamerStatus.Move, "taken.mkv", dstFolder),
            ]);

            var journal = new FakeRevertJournal();
            var minted = new List<string>();
            var executor = new RenamerExecutor(
                new CollisionBlindDataPort(db), new CapturingEventBus(), journal, "run-test",
                new DiskMover(), new CrossVolumeMover(Recorder(minted)));

            var result = await executor.ExecuteAsync(plan, new RenamerOptions(), default);

            // The save threw after the verified cross move → item failed with a rollback reason.
            var failedItem = Assert.Single(result.Failed);
            Assert.Equal(RenamerStatus.Failed, failedItem.Status);
            Assert.Contains("rolled back", failedItem.Reason);
            Assert.Empty(result.Renamed);
            Assert.Empty(journal.Rows);

            // (a) the source is RESTORED across the volume (copy-back) with its original content.
            Assert.True(File.Exists(oldA), "cross rollback must copy the file back to its old path");
            Assert.Equal("A-bytes", File.ReadAllText(oldA));
            // and is NOT left on the dest volume.
            Assert.False(File.Exists(newOnDisk), "rolled-back file must not linger at the dest");
            // Both directions of the cross move minted their own in-flight name; neither may survive.
            AssertMintedPathsGone(minted);

            // (c) the DB row still carries the OLD basename + source folder — disk and DB consistent.
            var (basenameA, pathA) = await ExecutorTestSeed.ReadFileAsync(db, fileA);
            Assert.Equal("a.mkv", basenameA);
            Assert.Equal(srcFolder + "/a.mkv", pathA);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    /// <summary>
    /// Regression: when the cross-volume rollback FAILS to fully restore (here the old source slot
    /// is re-occupied before the copy-back runs, so <see cref="CrossVolumeMover.RollbackAsync"/> records a
    /// "rollback target re-occupied" warning rather than restoring), the executor must NOT report a clean
    /// "file rolled back". It must surface the rollback warnings so the disk/DB divergence is visible —
    /// silently discarding the warnings would falsely claim a rollback that did not happen.
    /// </summary>
    [SkippableFact]
    public async Task CrossVolumeSaveFailure_RollbackWarnings_Surfaced_NotSilentlyRolledBack()
    {
        Skip.IfNot(SecondVolume.IsAvailable, SecondVolume.UnavailableReason);

        using var src = new TempDir();
        using var dst = new SecondVolume();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string srcFolder = src.Root.Replace('\\', '/');
            string dstFolder = dst.Root.Replace('\\', '/').TrimEnd('/');

            var (_, videoId, fileA) =
                await ExecutorTestSeed.SeedVideoAsync(db, srcFolder, "a.mkv", "Film A");

            string oldA = Path.Combine(src.Root, "a.mkv");
            File.WriteAllText(oldA, "A-bytes");

            string newFull = dstFolder + "/My Film.mkv";
            Assert.False(VolumeClassifier.SameVolume(srcFolder + "/a.mkv", newFull),
                "precondition: cross-volume pair");

            var plan = new RenamerPlan(videoId, RenamerFileKind.Video,
            [
                new RenamerPlanItem(fileA, srcFolder + "/a.mkv", newFull,
                    RenamerStatus.Move, "My Film.mkv", dstFolder),
            ]);

            // A data port whose save re-occupies the OLD source slot (so the rollback copy-back finds the
            // target taken → "rollback target re-occupied" warning) and then throws.
            var port = new ReoccupyOldSlotThenThrowDataPort(db, oldA);
            var executor = new RenamerExecutor(
                port, new CapturingEventBus(), new FakeRevertJournal(), "run-test",
                new DiskMover(), new CrossVolumeMover());

            var result = await executor.ExecuteAsync(plan, new RenamerOptions(), default);

            var failedItem = Assert.Single(result.Failed);
            Assert.Equal(RenamerStatus.Failed, failedItem.Status);
            // The failed reason must report the INCOMPLETE rollback + the warning, NOT a clean "rolled back".
            Assert.Contains("rollback INCOMPLETE", failedItem.Reason);
            Assert.Contains("rollback target re-occupied", failedItem.Reason);
            Assert.DoesNotContain("file rolled back", failedItem.Reason);
            Assert.Empty(result.Renamed);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    /// <summary>
    /// A post-copy seam that only records the in-flight path the cross mover minted, leaving the copy
    /// untouched — the mover's real behaviour, plus the observation the cross cases need.
    /// </summary>
    private static Func<string, CancellationToken, Task> Recorder(List<string> minted) =>
        (inFlight, _) =>
        {
            minted.Add(inFlight);
            return Task.CompletedTask;
        };

    private static void AssertMintedPathsGone(List<string> minted)
    {
        // The seam must actually have fired, or the loop below asserts nothing at all.
        Assert.NotEmpty(minted);
        foreach (var path in minted)
        {
            Assert.False(File.Exists(path), $"no in-flight copy may be left at {path}");
        }
    }

    /// <summary>
    /// Test-only port: on save, re-creates a file at <c>oldSlot</c> (simulating the source slot getting
    /// re-occupied between the move and the rollback) and then throws, so the subsequent rollback's
    /// copy-back finds its target taken and records a warning instead of restoring.
    /// </summary>
    private sealed class ReoccupyOldSlotThenThrowDataPort(Cove.Data.CoveContext db, string oldSlot)
        : CoveRenamerDataPort(db)
    {
        public override Task<IReadOnlyList<SavedFile>> ApplyAndSaveAsync(
            IReadOnlyList<RenamerFileMutation> mutations, CancellationToken ct = default)
        {
            File.WriteAllText(oldSlot, "intruder bytes re-occupying the old slot");
            throw new InvalidOperationException("forced save failure");
        }
    }

    /// <summary>Test-only port: the save throws a cancellation (a host shutdown mid-save), forcing the
    /// executor's post-move OCE path — rollback, then propagate — rather than the data-failure path.</summary>
    private sealed class CancelOnSaveDataPort(Cove.Data.CoveContext db) : CoveRenamerDataPort(db)
    {
        public override Task<IReadOnlyList<SavedFile>> ApplyAndSaveAsync(
            IReadOnlyList<RenamerFileMutation> mutations, CancellationToken ct = default)
            => throw new OperationCanceledException("host shutting down mid-save");
    }
}
