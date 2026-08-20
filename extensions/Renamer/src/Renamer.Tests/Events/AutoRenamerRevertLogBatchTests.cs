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
/// must open its own journal batch, and the row it writes must carry the PARENT entity id alongside the
/// file id so /undo republishes the right entity. The decoy video makes <c>videoId ≠ fileId</c>, so a row
/// that confused the two is distinguishable from a correct one.
/// </summary>
[Trait("Tier", "L1")]
public sealed class AutoRenamerRevertLogBatchTests
{
    [Fact]
    public async Task AutoRename_OpensItsOwnBatch_RowCarriesEntityAndFileId_UndoRestoresDiskAndDb()
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
            var (ext, _, _) = await EventTestHarness.BuildAsync(db, options);

            // Drive the hook for the one entity that WILL act: raw.mkv → My Film.mkv.
            await ext.OnEventAsync(new ExtensionEvent("video.updated", "video", videoId), default);

            string newFull = Path.Combine(dir.Root, "My Film.mkv");
            Assert.True(File.Exists(newFull));
            Assert.False(File.Exists(oldFull));

            // (a) A fresh reader over the journal sees exactly one batch with rows, carrying the kind.
            var readBack = new CoveRevertJournal(db);
            var batch = await JournalPageReader.ReadWholeUndoTargetAsync(readBack);
            Assert.NotNull(batch);
            Assert.Equal(RenamerFileKind.Video, batch!.Kind);

            // (b) EntityId is the VIDEO id and FileId is the FILE id, and they differ — a row that
            // confused the two would have EntityId == FileId.
            var entry = Assert.Single(batch.Rows);
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
