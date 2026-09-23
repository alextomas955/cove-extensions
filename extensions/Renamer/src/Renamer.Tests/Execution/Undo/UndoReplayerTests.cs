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
    [Fact]
    public async Task MultiEntityBatch_PublishesTwoCorrectEntityIds()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string folderPath = dir.Root.Replace('\\', '/');
            // Two distinct videos, each with one file. Offset the Video id sequence so video ids
            // differ from file ids (the published events must carry entity ids, never file ids).
            await SeedDecoyVideoAsync(db);
            var (folderId, video1, file1) = await ExecutorTestSeed.SeedVideoAsync(db, folderPath, "one.mkv", "First");
            // The second video shares the same folder (folders.Path is unique - cannot seed a 2nd folder).
            var (video2, file2) = await SeedSecondVideoInFolderAsync(db, folderId, "two.mkv", "Second");
            Assert.NotEqual(video1, file1);
            Assert.NotEqual(video2, file2);

            File.WriteAllText(Path.Combine(dir.Root, "one.mkv"), "1");
            File.WriteAllText(Path.Combine(dir.Root, "two.mkv"), "2");

            var port = new CoveRenamerDataPort(db);
            var journal = new FakeRevertJournal();
            var options = new RenamerOptions { FilenameTemplate = "$title" };

            await journal.BeginBatchAsync("run-test", "run-test", RenamerFileKind.Video, DateTime.UtcNow);
            foreach (var vid in new[] { video1, video2 })
            {
                var plan = await new RenamerPlanner(port).PlanAsync(RenamerFileKind.Video, vid, options, default);
                await new RenamerExecutor(port, new CapturingEventBus(), journal, "run-test")
                    .ExecuteAsync(plan, options, default);
            }

            var batch = await JournalPageReader.ReadWholeUndoTargetAsync(journal);
            Assert.NotNull(batch);
            Assert.Equal(2, batch!.Rows.Count);

            var undoBus = new CapturingEventBus();
            var result = await new UndoReplayer(port, undoBus).RevertAsync(batch, default);

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
            var journal = new FakeRevertJournal();
            var options = new RenamerOptions { FilenameTemplate = "$title" };
            await journal.BeginBatchAsync("run-test", "run-test", RenamerFileKind.Video, DateTime.UtcNow);
            var plan = await new RenamerPlanner(port).PlanAsync(RenamerFileKind.Video, videoId, options, default);
            await new RenamerExecutor(port, new CapturingEventBus(), journal, "run-test")
                .ExecuteAsync(plan, options, default);

            // The folder's row now names another path, so the original folder exists on disk only.
            var folder = await db.Set<Folder>().SingleAsync(f => f.Id == folderId);
            folder.Path = folderPath + "-elsewhere";
            await db.SaveChangesAsync();
            File.WriteAllText(Path.Combine(dir.Root, "one.mkv"), "squatter");

            var batch = await JournalPageReader.ReadWholeUndoTargetAsync(journal);
            var result = await new UndoReplayer(port, new CapturingEventBus()).RevertAsync(batch!, default);

            Assert.Single(result.Skipped);
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
            var journal = new FakeRevertJournal();
            var options = new RenamerOptions { FilenameTemplate = "$title" };

            await journal.BeginBatchAsync("run-test", "run-test", RenamerFileKind.Video, DateTime.UtcNow);
            foreach (var vid in new[] { video1, video2 })
            {
                var plan = await new RenamerPlanner(port).PlanAsync(RenamerFileKind.Video, vid, options, default);
                await new RenamerExecutor(port, new CapturingEventBus(), journal, "run-test")
                    .ExecuteAsync(plan, options, default);
            }

            // Pre-occupy the old slot of "one.mkv" (video1) on disk so its reverse move must skip.
            File.WriteAllText(Path.Combine(dir.Root, "one.mkv"), "squatter");

            var batch = await JournalPageReader.ReadWholeUndoTargetAsync(journal);
            Assert.NotNull(batch);
            var undoBus = new CapturingEventBus();
            var result = await new UndoReplayer(port, undoBus).RevertAsync(batch!, default);

            // video2 restored; video1 reported as skipped/failed (never clobbered).
            Assert.Equal(1, result.Undone);
            int problems = result.Skipped.Count + result.Failed.Count;
            Assert.Equal(1, problems);

            // The squatter at the old slot is untouched, and "First.mkv" still exists (not clobbered).
            Assert.Equal("squatter", File.ReadAllText(Path.Combine(dir.Root, "one.mkv")));
            Assert.True(File.Exists(Path.Combine(dir.Root, "First.mkv")), "video1 left at NEW, not clobbered");

            // video2 fully restored on disk.
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
            var journal = new FakeRevertJournal();
            var options = new RenamerOptions { FilenameTemplate = "$title" };

            await journal.BeginBatchAsync("run-test", "run-test", RenamerFileKind.Video, DateTime.UtcNow);
            var plan = await new RenamerPlanner(port).PlanAsync(RenamerFileKind.Video, videoId, options, default);
            await new RenamerExecutor(port, new CapturingEventBus(), journal, "run-test")
                .ExecuteAsync(plan, options, default);

            string newFull = Path.Combine(dir.Root, "My Film.mkv");
            Assert.True(File.Exists(newFull));

            var batch = await JournalPageReader.ReadWholeUndoTargetAsync(journal);
            Assert.NotNull(batch);

            // A port that throws on the reverse save forces the rollback path.
            var throwingPort = new ThrowOnSaveDataPort(db);
            var undoBus = new CapturingEventBus();
            var result = await new UndoReplayer(throwingPort, undoBus).RevertAsync(batch!, default);

            Assert.Equal(0, result.Undone);
            Assert.Single(result.Failed);
            Assert.Empty(undoBus.Published);

            // Disk rolled back to new (no half-state): the file is at new, not old.
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
            var journal = new FakeRevertJournal();
            var options = new RenamerOptions { FilenameTemplate = "$title" };

            await journal.BeginBatchAsync("run-test", "run-test", RenamerFileKind.Video, DateTime.UtcNow);
            var plan = await new RenamerPlanner(port).PlanAsync(RenamerFileKind.Video, videoId, options, default);
            await new RenamerExecutor(port, new CapturingEventBus(), journal, "run-test")
                .ExecuteAsync(plan, options, default);

            string newFull = Path.Combine(dir.Root, "My Film.mkv");
            Assert.True(File.Exists(newFull));

            var batch = await JournalPageReader.ReadWholeUndoTargetAsync(journal);
            Assert.NotNull(batch);

            // A reverse save that cancels forces the OCE path: rollback to new, then propagate.
            var undoBus = new CapturingEventBus();
            var replayer = new UndoReplayer(new CancelOnReverseSaveDataPort(db), undoBus);
            await Assert.ThrowsAsync<OperationCanceledException>(() => replayer.RevertAsync(batch!, default));

            // Disk rolled back to new (no half-state), no event published - a cancel, not an UndoFailure.
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
            var journal = new FakeRevertJournal();
            var options = new RenamerOptions { FilenameTemplate = "$title" };

            // Forward renamer under one root → an in-place same-volume pair.
            await journal.BeginBatchAsync("run-test", "run-test", RenamerFileKind.Video, DateTime.UtcNow);
            var plan = await new RenamerPlanner(port).PlanAsync(RenamerFileKind.Video, videoId, options, default);
            await new RenamerExecutor(port, new CapturingEventBus(), journal, "run-test")
                .ExecuteAsync(plan, options, default);

            var batch = await JournalPageReader.ReadWholeUndoTargetAsync(journal);
            Assert.NotNull(batch);
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
                .RevertAsync(batch!, default);

            Assert.Equal(1, result.Undone);
            Assert.Empty(result.Failed);
            Assert.Empty(result.Skipped);
            Assert.False(crossTouched, "a same-volume undo must NOT invoke the CrossVolumeMover");

            // Disk + DB restored exactly as the verbatim same-volume path does.
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
    public async Task EmptyBatch_NoOp_AllZero()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            var port = new CoveRenamerDataPort(db);
            var undoBus = new CapturingEventBus();
            var batch = new RevertBatch("run-test", RenamerFileKind.Video, Array.Empty<RevertRow>());

            var result = await new UndoReplayer(port, undoBus).RevertAsync(batch, default);

            Assert.Equal(0, result.Undone);
            Assert.Empty(result.Failed);
            Assert.Empty(result.Skipped);
            Assert.Empty(undoBus.Published);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task AStoredJournalMigratedIntoTheTable_StillReplays()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string folderPath = dir.Root.Replace('\\', '/');
            // Seed a file at its current (new) location so a same-folder same-drive undo can restore it.
            var (_, videoId, fileId) =
                await ExecutorTestSeed.SeedVideoAsync(db, folderPath, "My Film.mkv", "My Film");

            string oldFull = Path.Combine(dir.Root, "raw.mkv");
            string newFull = Path.Combine(dir.Root, "My Film.mkv");
            File.WriteAllText(newFull, "legacy-bytes");

            // Hand-build the stored journal an installation upgrading into the table still carries: one
            // batch header and an entityId|fileId|old row. The migration moves it into the table, which is
            // where undo now looks.
            string oldPath = oldFull.Replace('\\', '/');
            var store = new FakeStore();
            await store.SetAsync(JournalBlobMigration.SchemaKey, JournalBlobMigration.CurrentSchema);
            await store.SetAsync(
                JournalBlobMigration.Key,
                $"#batch|R1|{DateTime.UtcNow.Ticks}|Video|open\n{videoId}|{fileId}|{oldPath}");

            await using var journal = new CoveRevertJournal(db);
            Assert.Equal(1, await JournalBlobMigration.RunAsync(store, journal, DateTime.UtcNow));

            var batch = await JournalPageReader.ReadWholeUndoTargetAsync(journal);
            Assert.NotNull(batch);
            Assert.Single(batch!.Rows);
            Assert.Equal(fileId, batch.Rows[0].FileId);

            // Replay: the volume class is derived from the recorded old/new path roots (same dir → same
            // volume) - no stored field is read.
            var port = new CoveRenamerDataPort(db);
            var undoBus = new CapturingEventBus();
            var result = await new UndoReplayer(port, undoBus).RevertAsync(batch, default);

            Assert.Equal(1, result.Undone);
            Assert.Empty(result.Failed);
            Assert.Empty(result.Skipped);

            // Disk restored to old; new gone - a migrated batch behaves exactly like a fresh one.
            Assert.True(File.Exists(oldFull), "a migrated stored journal restores to OLD");
            Assert.False(File.Exists(newFull));
            Assert.Equal("legacy-bytes", File.ReadAllText(oldFull));
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    // Seeds one throwaway Video (no file) so the next SeedVideoAsync hands back a Video id that is
    // one ahead of its VideoFile id - guaranteeing videoId ≠ fileId so the round-trip test can
    // prove the published event uses the entity id, not the file id.
    private static async Task SeedDecoyVideoAsync(DbContext db)
    {
        db.Set<Video>().Add(new Video { Title = "decoy", Organized = true });
        await db.SaveChangesAsync();
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

    private sealed class ThrowOnSaveDataPort : CoveRenamerDataPort
    {
        public ThrowOnSaveDataPort(DbContext db) : base(db) { }

        public override Task<string> ApplyAndSaveAsync(
            RenamerFileMutation mutation, CancellationToken ct = default)
            => throw new InvalidOperationException("forced save failure");
    }

    private sealed class CancelOnReverseSaveDataPort : CoveRenamerDataPort
    {
        public CancelOnReverseSaveDataPort(DbContext db) : base(db) { }

        public override Task<string> ApplyAndSaveAsync(
            RenamerFileMutation mutation, CancellationToken ct = default)
            => throw new OperationCanceledException("host shutting down mid-replay");
    }

}
