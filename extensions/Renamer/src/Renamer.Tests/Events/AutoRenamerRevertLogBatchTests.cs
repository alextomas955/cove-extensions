using Cove.Core.Entities;
using Cove.Core.Events;
using Cove.Data;
using Cove.Plugins;
using Renamer.Execution;
using Renamer.Options;
using Renamer.Planner;
using Renamer.Tests.Execution;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Events;

/// <summary>
/// The data-recovery spine for the auto-renamer hook: a rename driven by the <c>video.updated</c> event
/// must open a proper batch HEADER so its revert-log rows are the 4-field <c>entityId|fileId|old|new</c>
/// shape and /undo can restore them. The regression this guards: the hook used to run the executor with
/// a fresh headerless <c>RevertLog</c>, so a headerless blob was misparsed on undo as legacy 3-field
/// rows (entityId→fileId), and a prior manual header would silently swallow the auto rows. The decoy
/// video makes <c>videoId ≠ fileId</c>, so the misparsed-legacy shape (EntityId == FileId) is
/// distinguishable from the correct one.
/// </summary>
[Trait("Tier", "Integration")]
public sealed class AutoRenamerRevertLogBatchTests
{
    [Fact]
    public async Task AutoRename_OpensHeaderedBatch_FourFieldRow_UndoRestoresDiskAndDb()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string folderPath = dir.Root.Replace('\\', '/');
            // Offset the Video id sequence so videoId ≠ fileId: the misparse writes EntityId = FileId,
            // so distinct ids are what prove the row parsed as the correct 4-field shape.
            await SeedDecoyVideoAsync(db);
            var (_, videoId, fileId) =
                await ExecutorTestSeed.SeedVideoAsync(db, folderPath, "raw.mkv", "My Film");
            Assert.NotEqual(videoId, fileId);

            string oldFull = Path.Combine(dir.Root, "raw.mkv");
            File.WriteAllText(oldFull, "video-bytes");

            var options = new RenamerOptions
            {
                AutoRenamerOnUpdate = true,
                FilenameTemplate = "$title",
            };
            var (ext, _, store) = await EventTestHarness.BuildAsync(db, options);

            // Drive the hook for the one entity that WILL act: raw.mkv → My Film.mkv.
            await ext.OnEventAsync(new ExtensionEvent("video.updated", "video", videoId), default);

            string newFull = Path.Combine(dir.Root, "My Film.mkv");
            Assert.True(File.Exists(newFull));
            Assert.False(File.Exists(oldFull));

            // (a) The stored blob carries a header line (no orphan rows) ...
            var blob = await store.GetAsync(RevertLog.Key);
            Assert.NotNull(blob);
            Assert.Contains("#batch", blob!);

            // ... and a fresh reader sees exactly one open batch with the correct kind.
            var readBack = new RevertLog(store);
            var batch = await readBack.ReadLastOpenBatchAsync();
            Assert.NotNull(batch);
            Assert.Equal(RenamerFileKind.Video, batch!.Kind);

            // (b) The row parsed as the 4-field shape: EntityId is the VIDEO id and FileId is the FILE
            // id, and they differ — the misparsed legacy shape would have EntityId == FileId.
            var entry = Assert.Single(batch.Entries);
            Assert.Equal(videoId, entry.EntityId);
            Assert.Equal(fileId, entry.FileId);
            Assert.NotEqual(entry.EntityId, entry.FileId);

            // (c) Reverse-replay the batch restores disk + DB.
            var port = new CoveRenamerDataPort(db);
            var undoBus = new CapturingEventBus();
            var result = await new UndoReplayer(port, undoBus, new DiskMover()).RevertAsync(batch, default);

            Assert.Equal(1, result.Undone);
            Assert.Empty(result.Failed);
            Assert.Empty(result.Skipped);

            Assert.True(File.Exists(oldFull), "file restored to old path");
            Assert.False(File.Exists(newFull), "new path gone after undo");
            Assert.Equal("video-bytes", File.ReadAllText(oldFull));

            var (basename, path) = await ExecutorTestSeed.ReadFileAsync(db, fileId);
            Assert.Equal("raw.mkv", basename);
            Assert.Equal(folderPath + "/raw.mkv", path);

            // The undo republished the entity id from the row (the video id, not the file id).
            var evt = Assert.IsType<EntityEvent>(Assert.Single(undoBus.Published));
            Assert.Equal(videoId, evt.EntityId);
            Assert.NotEqual(fileId, evt.EntityId);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    /// <summary>
    /// Seeds one throwaway Video so the next <see cref="ExecutorTestSeed.SeedVideoAsync"/> hands back a
    /// Video id one ahead of its VideoFile id — guaranteeing videoId ≠ fileId.
    /// </summary>
    private static async Task SeedDecoyVideoAsync(CoveContext db)
    {
        db.Set<Video>().Add(new Video { Title = "decoy", Organized = true });
        await db.SaveChangesAsync();
    }
}
