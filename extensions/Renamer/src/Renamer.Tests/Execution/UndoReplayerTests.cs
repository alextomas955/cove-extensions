using Cove.Core.Entities;
using Cove.Core.Events;
using Cove.Data;
using Renamer.Execution;
using Renamer.Options;
using Renamer.Planner;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Execution;

/// <summary>
/// The reverse-replay safety spine. Seeds Folder+Video+VideoFile on SQLite + a real file in
/// a <see cref="TempDir"/>, renames it via the live planner+executor, then reverse-replays the logged
/// batch with <see cref="UndoReplayer"/> and asserts: the file is back at the OLD on-disk path, the DB
/// Basename/Path are restored, and exactly one entity-updated event is published whose EntityId is the
/// PARENT entity id from the log ROW (== seeded videoId, ≠ fileId). Also covers a multi-entity batch
/// (two correct entityIds), partial failure (pre-occupied OLD slot → skip, no clobber), save-throw
/// rollback (disk moved back to NEW), and an empty batch no-op. SQLite (not EF-InMemory) so the unique
/// index + transactions are faithful.
/// </summary>
[Trait("Tier", "Integration")]
public sealed class UndoReplayerTests
{
    [Fact]
    public async Task RoundTrip_RestoresDiskAndDb_PublishesEvent_WithEntityId_NotFileId()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string folderPath = dir.Root.Replace('\\', '/');
            // Offset the Video id sequence so the real video's id differs from its file's id — the
            // whole point of the entityId-on-row design is that the published event uses the ENTITY
            // id, not the file id, so the test data must make them distinguishable.
            await SeedDecoyVideoAsync(db);
            var (_, videoId, fileId) =
                await ExecutorTestSeed.SeedVideoAsync(db, folderPath, "raw clip.mkv", "My Film");

            // The whole point: the video id and the file id differ. Prove it before relying on it.
            Assert.NotEqual(videoId, fileId);

            string oldFull = Path.Combine(dir.Root, "raw clip.mkv");
            File.WriteAllText(oldFull, "video-bytes");

            var port = new CoveRenamerDataPort(db);
            var revertLog = new RevertLog(new FakeStore());
            var options = new RenamerOptions { FilenameTemplate = "$title" }; // → "My Film.mkv"

            // Forward: plan + execute through the real spine, opening a batch first (the endpoint's job).
            await revertLog.BeginBatchAsync("RUN-1", RenamerFileKind.Video);
            var plan = await new RenamerPlanner(port).PlanAsync(RenamerFileKind.Video, videoId, options, default);
            var fwd = await new RenamerExecutor(port, new CapturingEventBus(), revertLog, new DiskMover())
                .ExecuteAsync(plan, options, default);
            Assert.Single(fwd.Renamed);

            string newFull = Path.Combine(dir.Root, "My Film.mkv");
            Assert.True(File.Exists(newFull));
            Assert.False(File.Exists(oldFull));

            // Reverse-replay the logged batch.
            var batch = await revertLog.ReadLastOpenBatchAsync();
            Assert.NotNull(batch);
            var undoBus = new CapturingEventBus();
            var replayer = new UndoReplayer(port, undoBus, new DiskMover());
            var result = await replayer.RevertAsync(batch!, default);

            // Result: one undone, none failed/skipped.
            Assert.Equal(1, result.Undone);
            Assert.Empty(result.Failed);
            Assert.Empty(result.Skipped);

            // Disk: file back at OLD, NEW gone, content intact.
            Assert.True(File.Exists(oldFull), "file restored to old path");
            Assert.False(File.Exists(newFull), "new path gone after undo");
            Assert.Equal("video-bytes", File.ReadAllText(oldFull));

            // DB: Basename restored, recomputed Path == OLD path.
            var (basename, path) = await ExecutorTestSeed.ReadFileAsync(db, fileId);
            Assert.Equal("raw clip.mkv", basename);
            Assert.Equal(folderPath + "/raw clip.mkv", path);

            // Event: exactly one VideoUpdated whose EntityId is the VIDEO id (≠ fileId).
            var evt = Assert.IsType<EntityEvent>(Assert.Single(undoBus.Published));
            Assert.Equal(EventType.VideoUpdated, evt.Type);
            Assert.Equal("Video", evt.EntityType);
            Assert.Equal(videoId, evt.EntityId);
            Assert.NotEqual(fileId, evt.EntityId);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

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
            // The second video shares the SAME folder (folders.Path is unique — cannot seed a 2nd folder).
            var (video2, file2) = await SeedSecondVideoInFolderAsync(db, folderId, "two.mkv", "Second");
            Assert.NotEqual(video1, file1);
            Assert.NotEqual(video2, file2);

            File.WriteAllText(Path.Combine(dir.Root, "one.mkv"), "1");
            File.WriteAllText(Path.Combine(dir.Root, "two.mkv"), "2");

            var port = new CoveRenamerDataPort(db);
            var revertLog = new RevertLog(new FakeStore());
            var options = new RenamerOptions { FilenameTemplate = "$title" };

            await revertLog.BeginBatchAsync("RUN-1", RenamerFileKind.Video);
            foreach (var vid in new[] { video1, video2 })
            {
                var plan = await new RenamerPlanner(port).PlanAsync(RenamerFileKind.Video, vid, options, default);
                await new RenamerExecutor(port, new CapturingEventBus(), revertLog, new DiskMover())
                    .ExecuteAsync(plan, options, default);
            }

            var batch = await revertLog.ReadLastOpenBatchAsync();
            Assert.NotNull(batch);
            Assert.Equal(2, batch!.Entries.Count);

            var undoBus = new CapturingEventBus();
            var result = await new UndoReplayer(port, undoBus, new DiskMover()).RevertAsync(batch, default);

            Assert.Equal(2, result.Undone);
            // The two published events carry EXACTLY the two ENTITY ids (each from its own row),
            // never a fileId — proven by the entity ids being distinct from the file ids (above).
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
            var revertLog = new RevertLog(new FakeStore());
            var options = new RenamerOptions { FilenameTemplate = "$title" };

            await revertLog.BeginBatchAsync("RUN-1", RenamerFileKind.Video);
            foreach (var vid in new[] { video1, video2 })
            {
                var plan = await new RenamerPlanner(port).PlanAsync(RenamerFileKind.Video, vid, options, default);
                await new RenamerExecutor(port, new CapturingEventBus(), revertLog, new DiskMover())
                    .ExecuteAsync(plan, options, default);
            }

            // Pre-occupy the OLD slot of "one.mkv" (video1) on disk so its reverse move must skip.
            File.WriteAllText(Path.Combine(dir.Root, "one.mkv"), "squatter");

            var batch = await revertLog.ReadLastOpenBatchAsync();
            Assert.NotNull(batch);
            var undoBus = new CapturingEventBus();
            var result = await new UndoReplayer(port, undoBus, new DiskMover()).RevertAsync(batch!, default);

            // video2 restored; video1 reported as skipped/failed (never clobbered).
            Assert.Equal(1, result.Undone);
            int problems = result.Skipped.Count + result.Failed.Count;
            Assert.Equal(1, problems);

            // The squatter at the OLD slot is untouched, and "First.mkv" still exists (not clobbered).
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
            var revertLog = new RevertLog(new FakeStore());
            var options = new RenamerOptions { FilenameTemplate = "$title" };

            await revertLog.BeginBatchAsync("RUN-1", RenamerFileKind.Video);
            var plan = await new RenamerPlanner(port).PlanAsync(RenamerFileKind.Video, videoId, options, default);
            await new RenamerExecutor(port, new CapturingEventBus(), revertLog, new DiskMover())
                .ExecuteAsync(plan, options, default);

            string newFull = Path.Combine(dir.Root, "My Film.mkv");
            Assert.True(File.Exists(newFull));

            var batch = await revertLog.ReadLastOpenBatchAsync();
            Assert.NotNull(batch);

            // A port that throws on the reverse save forces the rollback path.
            var throwingPort = new ThrowOnSaveDataPort(db);
            var undoBus = new CapturingEventBus();
            var result = await new UndoReplayer(throwingPort, undoBus, new DiskMover()).RevertAsync(batch!, default);

            Assert.Equal(0, result.Undone);
            Assert.Single(result.Failed);
            Assert.Empty(undoBus.Published);

            // Disk rolled back to NEW (no half-state): the file is at NEW, not OLD.
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
            var revertLog = new RevertLog(new FakeStore());
            var options = new RenamerOptions { FilenameTemplate = "$title" };

            // Forward renamer under ONE root → an in-place same-volume pair.
            await revertLog.BeginBatchAsync("RUN-1", RenamerFileKind.Video);
            var plan = await new RenamerPlanner(port).PlanAsync(RenamerFileKind.Video, videoId, options, default);
            await new RenamerExecutor(port, new CapturingEventBus(), revertLog, new DiskMover())
                .ExecuteAsync(plan, options, default);

            var batch = await revertLog.ReadLastOpenBatchAsync();
            Assert.NotNull(batch);
            string newFull = Path.Combine(dir.Root, "My Film.mkv");
            Assert.True(VolumeClassifier.SameVolume(newFull, oldFull),
                "precondition: an in-place renamer under one root is same-volume");

            // Inject a cross mover whose post-copy fault seam sets a sentinel: it CANNOT fire unless the
            // cross path's CopyVerifyPromoteDelete actually runs. A same-volume reverse must take the
            // DiskMover path and never touch the cross mover, so the sentinel stays false.
            bool crossTouched = false;
            var recordingCross = new CrossVolumeMover((_, _) =>
            {
                crossTouched = true;
                return Task.CompletedTask;
            });

            var undoBus = new CapturingEventBus();
            var result = await new UndoReplayer(port, undoBus, new DiskMover(), cross: recordingCross)
                .RevertAsync(batch!, default);

            // The entry was undone via the verbatim v1.3 DiskMover path; the cross mover was never invoked.
            Assert.Equal(1, result.Undone);
            Assert.Empty(result.Failed);
            Assert.Empty(result.Skipped);
            Assert.False(crossTouched, "a same-volume undo must NOT invoke the CrossVolumeMover (UNDO-01)");

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
            var batch = new RevertLog.RevertBatch(RenamerFileKind.Video, Array.Empty<RevertLog.RevertEntry>());

            var result = await new UndoReplayer(port, undoBus, new DiskMover()).RevertAsync(batch, default);

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
    public async Task LegacyV3Blob_ReplaysWithoutMigration()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string folderPath = dir.Root.Replace('\\', '/');
            // Seed a file at its CURRENT (NEW) location so a same-folder same-drive undo can restore it.
            var (_, _, fileId) =
                await ExecutorTestSeed.SeedVideoAsync(db, folderPath, "My Film.mkv", "My Film");

            string oldFull = Path.Combine(dir.Root, "raw.mkv");
            string newFull = Path.Combine(dir.Root, "My Film.mkv");
            File.WriteAllText(newFull, "legacy-bytes");

            // Hand-build a LEGACY v1.3 blob: flat `fileId|old|new` rows with NO #batch header and NO
            // trailing volume field. The tolerant RevertLog parser reads it as one implicit Video batch
            // (EntityId := FileId). This is the exact on-disk shape v1.3 wrote — proving zero migration.
            string oldPath = oldFull.Replace('\\', '/');
            string newPath = newFull.Replace('\\', '/');
            var store = new FakeStore();
            await store.SetAsync(RevertLog.Key, $"{fileId}|{oldPath}|{newPath}");

            var revertLog = new RevertLog(store);
            var batch = await revertLog.ReadLastOpenBatchAsync();
            Assert.NotNull(batch);
            Assert.Single(batch!.Entries);
            // The legacy row carries no entityId field; the parser sets EntityId = FileId as documented.
            Assert.Equal(fileId, batch.Entries[0].FileId);

            // Replay: the volume class is DERIVED from the recorded old/new path roots (same dir → same
            // volume) — no stored field is read. A legacy blob replays UNCHANGED.
            var port = new CoveRenamerDataPort(db);
            var undoBus = new CapturingEventBus();
            var result = await new UndoReplayer(port, undoBus, new DiskMover()).RevertAsync(batch, default);

            Assert.Equal(1, result.Undone);
            Assert.Empty(result.Failed);
            Assert.Empty(result.Skipped);

            // Disk restored to OLD; NEW gone — the legacy replay behaves exactly like a fresh same-drive undo.
            Assert.True(File.Exists(oldFull), "legacy v3 blob restores to OLD with zero migration");
            Assert.False(File.Exists(newFull));
            Assert.Equal("legacy-bytes", File.ReadAllText(oldFull));
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    /// <summary>
    /// Seeds one throwaway Video (no file) so the next <see cref="ExecutorTestSeed.SeedVideoAsync"/>
    /// hands back a Video id that is one ahead of its VideoFile id — guaranteeing videoId ≠ fileId so
    /// the round-trip test can PROVE the published event uses the entity id, not the file id.
    /// </summary>
    private static async Task SeedDecoyVideoAsync(CoveContext db)
    {
        db.Set<Video>().Add(new Video { Title = "decoy", Organized = true });
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Seeds a second Video + one VideoFile in the SAME existing folder (folders.Path is unique, so a
    /// second folder cannot be seeded). Returns the (videoId, fileId), which differ.
    /// </summary>
    private static async Task<(int videoId, int fileId)> SeedSecondVideoInFolderAsync(
        CoveContext db, int folderId, string basename, string title)
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

    /// <summary>A port whose reverse save always throws, forcing the UndoReplayer rollback path.</summary>
    private sealed class ThrowOnSaveDataPort : CoveRenamerDataPort
    {
        public ThrowOnSaveDataPort(CoveContext db) : base(db) { }

        public override Task<IReadOnlyList<SavedFile>> ApplyAndSaveAsync(
            IReadOnlyList<RenamerFileMutation> mutations, CancellationToken ct = default)
            => throw new InvalidOperationException("forced save failure");
    }
}
