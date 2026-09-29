using Cove.Plugins;
using Microsoft.EntityFrameworkCore;
using Renamer.Execution;
using Renamer.Options;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Events;

public sealed class AutoRenamerGateTests
{
    [Fact]
    public async Task FlagOff_FiringUpdated_PerformsNoRename_NoEvents()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string folderPath = dir.Root.Replace('\\', '/');
            // The name differs from the "$title" render, so only the off flag can be why nothing happens.
            var (_, videoId, fileId) =
                await ExecutorTestSeed.SeedVideoAsync(db, folderPath, "raw.mkv", "My Film");
            File.WriteAllText(Path.Combine(dir.Root, "raw.mkv"), "bytes");

            var options = new RenamerOptions { AutoRenamerOnUpdate = false, FilenameTemplate = "$title" };
            var bus = new CapturingEventBus();
            var (ext, _) = await ExtensionHarness.CreateWithSharedContextAsync(db, options, bus);

            await ext.OnEventAsync(new ExtensionEvent("video.updated", "video", videoId), default);

            var (basename, _) = await ExecutorTestSeed.ReadFileAsync(db, fileId);
            Assert.Equal("raw.mkv", basename);
            Assert.True(File.Exists(Path.Combine(dir.Root, "raw.mkv")));
            Assert.False(File.Exists(Path.Combine(dir.Root, "My Film.mkv")));
            Assert.Empty(bus.Published);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task FlagOn_GatedItem_PerformsNoRename_AndOpensNoUndoBatch()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string folderPath = dir.Root.Replace('\\', '/');
            var (_, videoId, fileId) =
                await ExecutorTestSeed.SeedVideoAsync(db, folderPath, "raw.mkv", title: "");
            File.WriteAllText(Path.Combine(dir.Root, "raw.mkv"), "bytes");

            // The filename-as-title fallback would rescue the empty title, so it is off and the
            // require-fields gate skips the item.
            var options = new RenamerOptions
            {
                AutoRenamerOnUpdate = true,
                RequiredFields = ["title"],
                FilenameAsTitle = false,
            };
            var bus = new CapturingEventBus();
            var (ext, _) = await ExtensionHarness.CreateWithSharedContextAsync(db, options, bus);

            await ext.OnEventAsync(new ExtensionEvent("video.updated", "video", videoId), default);

            var (basename, _) = await ExecutorTestSeed.ReadFileAsync(db, fileId);
            Assert.Equal("raw.mkv", basename);
            Assert.True(File.Exists(Path.Combine(dir.Root, "raw.mkv")));
            Assert.Empty(bus.Published);

            // The executor would rename nothing here either, so the batch is what shows the hook
            // stopped before it.
            Assert.Equal(0, await db.Set<RevertBatchEntity>().AsNoTracking().CountAsync());
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }
}
