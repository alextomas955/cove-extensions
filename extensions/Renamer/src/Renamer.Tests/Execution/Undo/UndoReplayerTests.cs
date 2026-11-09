using Cove.Core.Entities;
using Cove.Core.Events;
using Microsoft.EntityFrameworkCore;
using Renamer.Execution;
using Renamer.Options;
using Renamer.Planner;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Execution.Undo;

public sealed class UndoReplayerTests
{
    private const string RunId = "run-test";

    [Fact]
    public async Task MultiEntityBatch_PublishesTwoCorrectEntityIds()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string folderPath = dir.Root.Replace('\\', '/');
            // Offset the Video id sequence so video ids differ from file ids: the published events
            // must carry entity ids, never file ids.
            await ExecutorTestSeed.SeedDecoyVideoAsync(db);
            var (folderId, video1, file1) = await ExecutorTestSeed.SeedVideoAsync(db, folderPath, "one.mkv", "First");
            // The second video shares the same folder (folders.Path is unique - cannot seed a 2nd folder).
            var (video2, file2) = await SeedSecondVideoInFolderAsync(db, folderId, "two.mkv", "Second");
            Assert.NotEqual(video1, file1);
            Assert.NotEqual(video2, file2);

            File.WriteAllText(Path.Combine(dir.Root, "one.mkv"), "1");
            File.WriteAllText(Path.Combine(dir.Root, "two.mkv"), "2");

            var port = new CoveRenamerDataPort(db);
            var batch = await RenameIntoOneBatchAsync(port, video1, video2);

            Assert.Equal(2, batch.Rows.Count);

            var undoBus = new CapturingEventBus();
            var result = await new UndoReplayer(port, undoBus).RevertAsync(batch);

            Assert.Equal(2, result.Undone);
            // The two published events carry exactly the two entity ids (each from its own row),
            // never a fileId - proven by the entity ids being distinct from the file ids (above).
            var ids = undoBus.Published.Cast<EntityEvent>().Select(e => e.EntityId).ToHashSet();
            Assert.Equal(new[] { video1, video2 }.ToHashSet(), ids);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task AnOccupiedOldSlot_WhoseFolderHasNoRow_IsSkipped_WithoutCreatingOne()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string folderPath = dir.Root.Replace('\\', '/');
            var (folderId, videoId, _) = await ExecutorTestSeed.SeedVideoAsync(db, folderPath, "one.mkv", "First");
            File.WriteAllText(Path.Combine(dir.Root, "one.mkv"), "1");

            var port = new CoveRenamerDataPort(db);
            var batch = await RenameIntoOneBatchAsync(port, videoId);

            // The folder's row now names another path, so the original folder exists on disk only.
            var folder = await db.Set<Folder>().SingleAsync(f => f.Id == folderId);
            folder.Path = folderPath + "-elsewhere";
            await db.SaveChangesAsync();
            File.WriteAllText(Path.Combine(dir.Root, "one.mkv"), "squatter");

            var result = await new UndoReplayer(port, new CapturingEventBus()).RevertAsync(batch);

            Assert.Equal(UndoStopReason.OriginalLocationOccupied, Assert.Single(result.Skipped).Stop);
            Assert.False(await db.Set<Folder>().AnyAsync(f => f.Path == folderPath));
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task PartialFailure_PreoccupiedOldSlot_SkippedNotClobbered_OthersRestored()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string folderPath = dir.Root.Replace('\\', '/');
            var (folderId, video1, _) = await ExecutorTestSeed.SeedVideoAsync(db, folderPath, "one.mkv", "First");
            var (video2, _) = await SeedSecondVideoInFolderAsync(db, folderId, "two.mkv", "Second");

            File.WriteAllText(Path.Combine(dir.Root, "one.mkv"), "1");
            File.WriteAllText(Path.Combine(dir.Root, "two.mkv"), "2");

            var port = new CoveRenamerDataPort(db);
            var batch = await RenameIntoOneBatchAsync(port, video1, video2);

            // Pre-occupy the old slot of "one.mkv" (video1) on disk so its reverse move must skip.
            File.WriteAllText(Path.Combine(dir.Root, "one.mkv"), "squatter");

            var undoBus = new CapturingEventBus();
            var result = await new UndoReplayer(port, undoBus).RevertAsync(batch);

            Assert.Equal(1, result.Undone);
            Assert.Empty(result.Failed);
            var skipped = Assert.Single(result.Skipped);
            Assert.Equal(UndoStopReason.OriginalLocationOccupied, skipped.Stop);
            Assert.EndsWith("/one.mkv", skipped.OldPath);

            Assert.Equal("squatter", File.ReadAllText(Path.Combine(dir.Root, "one.mkv")));
            Assert.True(File.Exists(Path.Combine(dir.Root, "First.mkv")), "video1 left at NEW, not clobbered");

            Assert.True(File.Exists(Path.Combine(dir.Root, "two.mkv")));
            Assert.False(File.Exists(Path.Combine(dir.Root, "Second.mkv")));
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task SaveThrow_RollsDiskBackToNew_ReportsFailed()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string folderPath = dir.Root.Replace('\\', '/');
            var (_, videoId, _) =
                await ExecutorTestSeed.SeedVideoAsync(db, folderPath, "raw.mkv", "My Film");

            File.WriteAllText(Path.Combine(dir.Root, "raw.mkv"), "bytes");

            var port = new CoveRenamerDataPort(db);
            var batch = await RenameIntoOneBatchAsync(port, videoId);

            string newFull = Path.Combine(dir.Root, "My Film.mkv");
            Assert.True(File.Exists(newFull));


            // A port that throws on the reverse save forces the rollback path.
            var throwingPort = new ThrowOnSaveDataPort(db);
            var undoBus = new CapturingEventBus();
            var result = await new UndoReplayer(throwingPort, undoBus).RevertAsync(batch);

            Assert.Equal(0, result.Undone);
            Assert.Single(result.Failed);
            Assert.Empty(undoBus.Published);

            Assert.True(File.Exists(newFull), "disk rolled back to new on save throw");
            Assert.False(File.Exists(Path.Combine(dir.Root, "raw.mkv")), "old slot must not hold the file after rollback");
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task ReverseSaveCancelled_RollsDiskBackToNew_Propagates_NeverUndoFailure()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string folderPath = dir.Root.Replace('\\', '/');
            var (_, videoId, _) =
                await ExecutorTestSeed.SeedVideoAsync(db, folderPath, "raw.mkv", "My Film");

            File.WriteAllText(Path.Combine(dir.Root, "raw.mkv"), "bytes");

            var port = new CoveRenamerDataPort(db);
            var batch = await RenameIntoOneBatchAsync(port, videoId);

            string newFull = Path.Combine(dir.Root, "My Film.mkv");
            Assert.True(File.Exists(newFull));


            // A reverse save that cancels forces the OCE path: rollback to new, then propagate.
            var undoBus = new CapturingEventBus();
            var replayer = new UndoReplayer(new CancelOnSaveDataPort(db), undoBus);
            await Assert.ThrowsAsync<OperationCanceledException>(() => replayer.RevertAsync(batch));

            Assert.True(File.Exists(newFull), "disk rolled back to new on a cancelled reverse save");
            Assert.False(File.Exists(Path.Combine(dir.Root, "raw.mkv")), "old slot must not hold the file after rollback");
            Assert.Empty(undoBus.Published);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task SameVolume_DoesNotInvokeCrossMover()
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

            var port = new CoveRenamerDataPort(db);
            var batch = await RenameIntoOneBatchAsync(port, videoId);

            string newFull = Path.Combine(dir.Root, "My Film.mkv");
            Assert.True(VolumeClassifier.SameVolume(newFull, oldFull),
                "precondition: an in-place renamer under one root is same-volume");

            // Inject a cross mover whose post-copy fault seam sets a sentinel: it cannot fire unless the
            // cross path's CopyVerifyPromoteDelete actually runs. A same-volume reverse must take the
            // DiskMover path and never touch the cross mover, so the sentinel stays false.
            bool crossTouched = false;
            var recordingCross = new CrossVolumeMover((_, _) =>
            {
                crossTouched = true;
                return Task.CompletedTask;
            });

            var undoBus = new CapturingEventBus();
            var result = await new UndoReplayer(port, undoBus, cross: recordingCross)
                .RevertAsync(batch);

            Assert.Equal(1, result.Undone);
            Assert.Empty(result.Failed);
            Assert.Empty(result.Skipped);
            Assert.False(crossTouched, "a same-volume undo must NOT invoke the CrossVolumeMover");

            Assert.True(File.Exists(oldFull));
            Assert.False(File.Exists(newFull));
            Assert.Equal("video-bytes", File.ReadAllText(oldFull));
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task UndoSaveCommitsToAPathTheFileIsNotAt_DiskAndRowBothGoBackToTheRenamedPath()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string folderPath = dir.Root.Replace('\\', '/');
            var (_, videoId, fileA) =
                await ExecutorTestSeed.SeedVideoAsync(db, folderPath, "a.mkv", "Film A");
            File.WriteAllText(Path.Combine(dir.Root, "a.mkv"), "A-bytes");

            var journal = new FakeRevertJournal();
            await journal.BeginBatchAsync("run-test", "run-test", RenamerFileKind.Video, DateTime.UtcNow);
            await RenameAToBAsync(db, journal, videoId, fileA, folderPath);

            string renamed = Path.Combine(dir.Root, "b.mkv");
            Assert.True(File.Exists(renamed), "precondition: the forward rename must have happened");

            var batch = await JournalPageReader.ReadWholeUndoTargetAsync(journal);
            Assert.NotNull(batch);
            var result = await new UndoReplayer(new MisreportingSaveDataPort(db), new CapturingEventBus())
                .RevertAsync(batch);

            var failure = Assert.Single(result.Failed);
            Assert.Equal(UndoStopReason.RestoredPathMismatch, failure.Stop);
            Assert.Contains("rolled back", failure.Reason);
            Assert.DoesNotContain("NOT confirmed", failure.Reason);
            Assert.Empty(result.Restored);

            Assert.True(File.Exists(renamed), "the file must go back to the renamed path");
            Assert.False(File.Exists(Path.Combine(dir.Root, "a.mkv")));
            var (basename, _) = await ExecutorTestSeed.ReadFileAsync(db, fileA);
            Assert.Equal("b.mkv", basename);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task UndoSaveCommitsWrongAndTheFileCannotGoBack_RowIsLeftWhereTheSaveWroteIt()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string folderPath = dir.Root.Replace('\\', '/');
            var (_, videoId, fileA) =
                await ExecutorTestSeed.SeedVideoAsync(db, folderPath, "a.mkv", "Film A");
            File.WriteAllText(Path.Combine(dir.Root, "a.mkv"), "A-bytes");

            var journal = new FakeRevertJournal();
            await journal.BeginBatchAsync("run-test", "run-test", RenamerFileKind.Video, DateTime.UtcNow);
            await RenameAToBAsync(db, journal, videoId, fileA, folderPath);

            string renamed = Path.Combine(dir.Root, "b.mkv");
            var batch = await JournalPageReader.ReadWholeUndoTargetAsync(journal);
            Assert.NotNull(batch);
            var result = await new UndoReplayer(
                    new ReoccupySlotThenMisreportDataPort(db, renamed), new CapturingEventBus())
                .RevertAsync(batch);

            var failure = Assert.Single(result.Failed);
            Assert.Contains("did not return to the renamed path", failure.Reason);
            Assert.DoesNotContain("; rolled back", failure.Reason);

            // The media file is at the old path and the row names it, so the two still agree. Writing
            // the row to a renamed slot the file never reclaimed is what this arm refuses.
            Assert.True(File.Exists(Path.Combine(dir.Root, "a.mkv")));
            var (basename, _) = await ExecutorTestSeed.ReadFileAsync(db, fileA);
            Assert.Equal("a.mkv", basename);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    // The run every case here undoes: one journal batch holding each entity really renamed through
    // the planner and the executor, read back as an undo would be handed it. The forward half runs
    // the production path rather than a hand-built plan, so a row an undo acts on is a row a rename
    // wrote.
    private static async Task<RevertBatch> RenameIntoOneBatchAsync(
        CoveRenamerDataPort port, params int[] entityIds)
    {
        var journal = new FakeRevertJournal();
        var options = new RenamerOptions { FilenameTemplate = "$title" };

        await journal.BeginBatchAsync(RunId, RunId, RenamerFileKind.Video, DateTime.UtcNow);
        foreach (int entityId in entityIds)
        {
            var plan = await new RenamerPlanner(port)
                .PlanAsync(RenamerFileKind.Video, entityId, options, default);
            await new RenamerExecutor(port, new CapturingEventBus(), journal, RunId)
                .ExecuteAsync(plan, options);
        }

        var batch = await JournalPageReader.ReadWholeUndoTargetAsync(journal);
        Assert.NotNull(batch);
        return batch!;
    }

    private static async Task RenameAToBAsync(
        DbContext db, IRevertJournal journal, int videoId, int fileId, string folderPath)
    {
        var plan = new RenamerPlan(videoId, RenamerFileKind.Video,
        [
            new RenamerPlanItem(fileId, folderPath + "/a.mkv", folderPath + "/b.mkv",
                RenamerStatus.Rename, "b.mkv", folderPath),
        ]);
        var result = await new RenamerExecutor(
                new CoveRenamerDataPort(db), new CapturingEventBus(), journal, RunId)
            .ExecuteAsync(plan, new RenamerOptions());
        Assert.Empty(result.Failed);
    }

    // Seeds a second Video + one VideoFile in the same existing folder (folders.Path is unique, so
    // a second folder cannot be seeded). Returns the (videoId, fileId), which differ.
    private static async Task<(int videoId, int fileId)> SeedSecondVideoInFolderAsync(
        DbContext db, int folderId, string basename, string title)
    {
        var video = new Video { Title = title, Organized = true };
        db.Set<Video>().Add(video);
        await db.SaveChangesAsync();

        var file = new VideoFile
        {
            Basename = basename,
            ParentFolderId = folderId,
            Format = basename.Contains('.') ? basename[(basename.LastIndexOf('.') + 1)..] : "",
            VideoId = video.Id,
        };
        db.Set<VideoFile>().Add(file);
        await db.SaveChangesAsync();
        return (video.Id, file.Id);
    }
}
