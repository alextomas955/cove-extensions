using Cove.Core.Entities;
using Cove.Core.Events;
using Cove.Plugins;
using Microsoft.EntityFrameworkCore;
using Renamer.Execution;
using Renamer.Options;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Events;

public sealed class AutoRenamerRevertLogBatchTests
{
    [Fact]
    public async Task AutoRename_JournalsEntityAndFileIdSeparately_UndoRestoresDiskAndDb()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string folderPath = dir.Root.Replace('\\', '/');
            // Offset the video id sequence from the file id sequence, so a row that recorded one id in
            // place of the other is visible.
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
            var (ext, _) = await ExtensionHarness.CreateWithSharedContextAsync(db, options);

            await ext.OnEventAsync(new ExtensionEvent("video.updated", "video", videoId), default);

            string newFull = Path.Combine(dir.Root, "My Film.mkv");
            Assert.True(File.Exists(newFull));
            Assert.False(File.Exists(oldFull));

            await using var readBack = new CoveRevertJournal(db);
            var batch = await JournalPageReader.ReadWholeUndoTargetAsync(readBack);
            Assert.NotNull(batch);
            Assert.Equal(RenamerFileKind.Video, batch!.Kind);

            var entry = Assert.Single(batch.Rows);
            Assert.Equal(videoId, entry.EntityId);
            Assert.Equal(fileId, entry.FileId);
            Assert.NotEqual(entry.EntityId, entry.FileId);

            var port = new CoveRenamerDataPort(db);
            var undoBus = new CapturingEventBus();
            var result = await new UndoReplayer(port, undoBus).RevertAsync(batch, default);

            Assert.Equal(1, result.Undone);
            Assert.Empty(result.Failed);
            Assert.Empty(result.Skipped);

            Assert.True(File.Exists(oldFull), "file restored to old path");
            Assert.False(File.Exists(newFull), "new path gone after undo");
            Assert.Equal("video-bytes", File.ReadAllText(oldFull));

            var (basename, path) = await ExecutorTestSeed.ReadFileAsync(db, fileId);
            Assert.Equal("raw.mkv", basename);
            Assert.Equal(folderPath + "/raw.mkv", path);

            // The undo republishes the entity the row names, not its file.
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

    // Seeds one throwaway video, so the next seeded video's id runs one ahead of its file's id.
    private static async Task SeedDecoyVideoAsync(DbContext db)
    {
        db.Set<Video>().Add(new Video { Title = "decoy", Organized = true });
        await db.SaveChangesAsync();
    }
}
