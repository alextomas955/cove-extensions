using Cove.Core.Events;
using Microsoft.EntityFrameworkCore;
using Renamer.Execution;
using Renamer.Options;
using Renamer.Planner;
using Renamer.Tests.Execution.Collisions;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Execution;

[Collection(SubstDriveScope.CollectionName)]
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
            var executor = new RenamerExecutor(port, bus, journal, "run-test");

            var options = new RenamerOptions { FilenameTemplate = "$title" }; // → "My Film.mkv"

            // Plan via the live port (read-only), then execute.
            var plan = await new RenamerPlanner(port).PlanAsync(RenamerFileKind.Video, videoId, options, default);
            var result = await executor.ExecuteAsync(plan, options, default);

            // (a) disk: new exists, old gone, content intact.
            string newFull = Path.Combine(dir.Root, "My Film.mkv");
            Assert.True(File.Exists(newFull), "renamed file must exist on disk");
            Assert.False(File.Exists(oldFull), "old file must be gone");
            Assert.Equal("video-bytes", File.ReadAllText(newFull));

            // (b) DB: basename updated; Path recomputed (not set) to folder + new basename.
            var (basename, path) = await ExecutorTestSeed.ReadFileAsync(db, fileId);
            Assert.Equal("My Film.mkv", basename);
            Assert.Equal(folderPath + "/My Film.mkv", path);

            // Result buckets: one renamed, none skipped/failed; revert-log row written.
            var renamedItem = Assert.Single(result.Renamed);
            Assert.Equal(RenamerStatus.Rename, renamedItem.Status);
            Assert.Empty(result.Failed);
            Assert.Empty(result.Skipped);
            var revert = Assert.Single(journal.Rows);
            Assert.Equal(fileId, revert.FileId);
            Assert.EndsWith("raw clip.mkv", revert.OldPath);

            // (c) event args: exactly one VideoUpdated for this video id.
            var evt = Assert.IsType<EntityEvent>(Assert.Single(bus.Published));
            Assert.Equal(EventType.VideoUpdated, evt.Type);
            Assert.Equal("Video", evt.EntityType);
            Assert.Equal(videoId, evt.EntityId);

            // The classifier verdict for the executed in-place pair is same-volume,
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
                new CancelOnSaveDataPort(db), new CapturingEventBus(), new FakeRevertJournal(), "run-test");

            // The cancel flows out as cancellation (the batch ends), never a Failed row.
            await Assert.ThrowsAsync<OperationCanceledException>(() => executor.ExecuteAsync(plan, options, default));

            // The post-move rollback still ran on the cancel path: the file is back at old, none at new.
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

    [Fact]
    public async Task MissingSource_ClassifiedSkipMissingSource_NotSkipLocked()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            // Seed the Folder + VideoFile on the real temp-dir root, but write no on-disk source file.
            string folderPath = dir.Root.Replace('\\', '/');
            var (_, videoId, _) =
                await ExecutorTestSeed.SeedVideoAsync(db, folderPath, "gone.mkv", "My Film");

            var port = new CoveRenamerDataPort(db);
            var bus = new CapturingEventBus();
            var journal = new FakeRevertJournal();
            var executor = new RenamerExecutor(port, bus, journal, "run-test");

            var options = new RenamerOptions { FilenameTemplate = "$title" };

            var plan = await new RenamerPlanner(port).PlanAsync(RenamerFileKind.Video, videoId, options, default);
            var result = await executor.ExecuteAsync(plan, options, default);

            // Classified as SkipMissingSource - not SkipLocked - with a missing-source reason.
            var skippedItem = Assert.Single(result.Skipped);
            Assert.Equal(RenamerStatus.SkipMissingSource, skippedItem.Status);
            Assert.Contains("missing", skippedItem.Reason);

            // A missing source is a safe no-op skip: nothing moved/failed, no revert-log, no event.
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

    [Fact]
    public async Task CrossVolumeBranch_HappyMove_UsesCrossMover_DiskAndDbUpdated()
    {
        Assert.SkipUnless(SecondVolume.IsAvailable, SecondVolume.UnavailableReason);

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

            // Sanity: the source and the subst destination are on different path roots → cross-volume.
            string newFull = dstFolder + "/My Film.mkv";
            Assert.False(VolumeClassifier.SameVolume(srcFolder + "/clip.mkv", newFull),
                "precondition: subst destination must be a different path root than the temp source");

            var port = new CoveRenamerDataPort(db);
            var bus = new CapturingEventBus();
            var journal = new FakeRevertJournal();
            // Inject a real CrossVolumeMover (the production mover) so the cross branch runs end-to-end.
            var executor = new RenamerExecutor(port, bus, journal, "run-test", new CrossVolumeMover());

            // Explicit move plan: source on the temp drive, target folder on the subst drive.
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
            Assert.Equal([newOnDisk], Directory.GetFileSystemEntries(dst.Root));

            // Result buckets: one moved, none skipped/failed; revert-log row written.
            var movedItem = Assert.Single(result.Renamed);
            Assert.Equal(RenamerStatus.Move, movedItem.Status);
            Assert.Empty(result.Failed);
            Assert.Empty(result.Skipped);
            Assert.Single(journal.Rows);

            // DB: Basename updated, ParentFolderId moved to the (new) dest folder, recomputed Path matches.
            var (basename, path) = await ExecutorTestSeed.ReadFileAsync(db, fileId);
            Assert.Equal("My Film.mkv", basename);
            Assert.Equal(dstFolder + "/My Film.mkv", path);

            // Event args: exactly one VideoUpdated for this video id.
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

    [Fact]
    public async Task CrossVolumeSaveFailure_RollsBackThroughCrossMover_SourceRestored()
    {
        Assert.SkipUnless(SecondVolume.IsAvailable, SecondVolume.UnavailableReason);

        using var src = new TempDir();
        using var dst = new SecondVolume();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string srcFolder = src.Root.Replace('\\', '/');
            string dstFolder = dst.Root.Replace('\\', '/').TrimEnd('/');

            var (_, videoId, fileA) =
                await ExecutorTestSeed.SeedVideoAsync(db, srcFolder, "a.mkv", "Film A");

            // Pre-seed the dest folder (same Path the executor will GetOrCreate) holding a row that
            // already occupies "taken.mkv", so the cross-move's save of (destFolderId, "taken.mkv")
            // hits the unique index and throws - after the verified cross move has happened.
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
            var executor = new RenamerExecutor(
                new CollisionBlindDataPort(db), new CapturingEventBus(), journal, "run-test",
                new CrossVolumeMover());

            var result = await executor.ExecuteAsync(plan, new RenamerOptions(), default);

            // The save threw after the verified cross move → item failed with a rollback reason.
            var failedItem = Assert.Single(result.Failed);
            Assert.Equal(RenamerStatus.Failed, failedItem.Status);
            Assert.Contains("rolled back", failedItem.Reason);
            Assert.Empty(result.Renamed);
            Assert.Empty(journal.Rows);

            // (a) the source is restored across the volume (copy-back) with its original content.
            Assert.True(File.Exists(oldA), "cross rollback must copy the file back to its old path");
            Assert.Equal("A-bytes", File.ReadAllText(oldA));
            // and is not left on the dest volume.
            Assert.False(File.Exists(newOnDisk), "rolled-back file must not linger at the dest");
            Assert.DoesNotContain(Directory.GetFileSystemEntries(dst.Root), e => e.Contains(".rnm", StringComparison.Ordinal));

            // (c) the DB row still carries the old basename + source folder - disk and DB consistent.
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

    [Fact]
    public async Task CrossVolumeSaveFailure_RollbackWarnings_Surfaced_NotSilentlyRolledBack()
    {
        Assert.SkipUnless(SecondVolume.IsAvailable, SecondVolume.UnavailableReason);

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

            // A data port whose save re-occupies the old source slot (so the rollback copy-back finds the
            // target taken → "rollback target re-occupied" warning) and then throws.
            var port = new ReoccupyOldSlotThenThrowDataPort(db, oldA);
            var executor = new RenamerExecutor(
                port, new CapturingEventBus(), new FakeRevertJournal(), "run-test",
                new CrossVolumeMover());

            var result = await executor.ExecuteAsync(plan, new RenamerOptions(), default);

            var failedItem = Assert.Single(result.Failed);
            Assert.Equal(RenamerStatus.Failed, failedItem.Status);
            // The failed reason must report the incomplete rollback + the warning, not a clean "rolled back".
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

    [Fact]
    public async Task DerivedTitle_ReachesTheDatabase_OnlyOnAnItemThatHadNone_AndTheRenameSettles()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            // One folder row per seeded item: Folder.Path is unique and SeedVideoAsync mints its own.
            string folderPath = dir.Root.Replace('\\', '/');
            string siblingDir = Path.Combine(dir.Root, "sibling");
            Directory.CreateDirectory(siblingDir);

            var date = new DateOnly(2021, 3, 14);
            var (_, titlelessId, _) = await ExecutorTestSeed.SeedVideoAsync(
                db, folderPath, "raw clip.mkv", title: null!, date: date, height: 2160, width: 3840);
            var (_, titledId, _) = await ExecutorTestSeed.SeedVideoAsync(
                db, siblingDir.Replace('\\', '/'), "other raw.mkv", "Kept Title", date: date,
                height: 2160, width: 3840);
            File.WriteAllText(Path.Combine(dir.Root, "raw clip.mkv"), "a");
            File.WriteAllText(Path.Combine(siblingDir, "other raw.mkv"), "b");

            // A template rendering more than a bare $title - the shape whose derived title grew a
            // decoration per run. Without the $date group the derivation equals the stem it came from
            // and nothing acts, which looks like an absence of the defect.
            var options = new RenamerOptions
            {
                FilenameTemplate = "{$date - }$title{ [$resolution]}",
                FilenameAsTitle = true,
            };

            var port = new CoveRenamerDataPort(db);
            var planner = new RenamerPlanner(port);
            var executor = new RenamerExecutor(
                port, new CapturingEventBus(), new FakeRevertJournal(), "run-test");

            foreach (int id in new[] { titlelessId, titledId })
            {
                var plan = await planner.PlanAsync(RenamerFileKind.Video, id, options, default);
                var run = await executor.ExecuteAsync(plan, options, default);
                Assert.Empty(run.Failed);
                Assert.Single(run.Renamed);
            }

            // Transcribed by hand from the arrangement above, never computed from the engine.
            Assert.True(
                File.Exists(Path.Combine(dir.Root, "2021-03-14 - raw clip [4K].mkv")),
                "the title-less item was not renamed to the name its derived title produces");

            Assert.Equal("raw clip", await ExecutorTestSeed.ReadVideoTitleAsync(db, titlelessId));
            Assert.Equal("Kept Title", await ExecutorTestSeed.ReadVideoTitleAsync(db, titledId));

            // The loop closed: the recorded title is what the second pass reads, so it finds nothing to do.
            var second = await planner.PlanAsync(RenamerFileKind.Video, titlelessId, options, default);
            Assert.All(second.Items, i => Assert.Equal(RenamerStatus.NoOp, i.Status));
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task DerivedTitle_IsNotRecorded_WhenTheRenameSaveFails()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string folderPath = dir.Root.Replace('\\', '/');
            var (folderId, videoId, fileId) = await ExecutorTestSeed.SeedVideoAsync(
                db, folderPath, "raw clip.mkv", title: null!);

            // The row that already occupies the name the item below is aimed at. Only the row exists, so
            // the executor's on-disk pre-check passes and the save is what refuses.
            await ExecutorTestSeed.SeedAdditionalFileAsync(db, folderId, videoId, "taken.mkv");

            string oldFull = Path.Combine(dir.Root, "raw clip.mkv");
            File.WriteAllText(oldFull, "video-bytes");

            var plan = new RenamerPlan(videoId, RenamerFileKind.Video,
            [
                new RenamerPlanItem(
                    fileId, folderPath + "/raw clip.mkv", folderPath + "/taken.mkv",
                    RenamerStatus.Rename, "taken.mkv", folderPath, DerivedTitle: "raw clip"),
            ]);

            var executor = new RenamerExecutor(
                new CollisionBlindDataPort(db), new CapturingEventBus(), new FakeRevertJournal(),
                "run-test");

            var result = await executor.ExecuteAsync(plan, new RenamerOptions(), default);

            var failedItem = Assert.Single(result.Failed);
            Assert.Contains("rolled back", failedItem.Reason);
            Assert.True(File.Exists(oldFull), "the rollback must restore the source");

            Assert.True(
                string.IsNullOrEmpty(await ExecutorTestSeed.ReadVideoTitleAsync(db, videoId)),
                "a title was recorded for a rename that never committed");
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    private sealed class ReoccupyOldSlotThenThrowDataPort(DbContext db, string oldSlot)
        : CoveRenamerDataPort(db)
    {
        public override Task<string> ApplyAndSaveAsync(
            RenamerFileMutation mutation, CancellationToken ct = default)
        {
            File.WriteAllText(oldSlot, "intruder bytes re-occupying the old slot");
            throw new InvalidOperationException("forced save failure");
        }
    }

    private sealed class CancelOnSaveDataPort(DbContext db) : CoveRenamerDataPort(db)
    {
        public override Task<string> ApplyAndSaveAsync(
            RenamerFileMutation mutation, CancellationToken ct = default)
            => throw new OperationCanceledException("host shutting down mid-save");
    }
}
