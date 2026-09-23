using Cove.Plugins;
using Renamer.Options;
using Renamer.Tests.Execution;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Events;

public sealed class AutoRenamerRoutingTests
{
    [Fact]
    public async Task FlagOn_MatchedSourcePathRule_RelocatesToRoutedDestination()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            // src and dest are sibling folders under one temp root → same volume, so the DiskMover
            // atomic File.Move path applies (no cross-volume mover needed in this slice).
            string srcFolder = Path.Combine(dir.Root, "incoming");
            string destRoot = Path.Combine(dir.Root, "sorted");
            Directory.CreateDirectory(srcFolder);

            string srcPathFwd = srcFolder.Replace('\\', '/');
            string destRootFwd = destRoot.Replace('\\', '/');

            var (_, videoId, fileId) =
                await ExecutorTestSeed.SeedVideoAsync(db, srcPathFwd, "raw.mkv", "My Film");
            File.WriteAllText(Path.Combine(srcFolder, "raw.mkv"), "bytes");

            var options = new RenamerOptions
            {
                AutoRenamerOnUpdate = true,
                FilenameTemplate = "$title",
                FolderTemplate = "Films",
                PathDestinations =
                    [new PathDestinationRule
                    {
                        Pattern = srcPathFwd, Dest = Dest.At(destRootFwd, "Films"), IsRegex = false,
                    }],
            };
            var (ext, bus, _) = await EventTestHarness.BuildAsync(
                db, options, srcPathFwd, destRootFwd);

            await ext.OnEventAsync(new ExtensionEvent("video.updated", "video", videoId), default);

            // The matched route relocated the file to destRoot/Films/My Film.mkv - not in place.
            string expected = Path.Combine(destRoot, "Films", "My Film.mkv");
            Assert.True(File.Exists(expected), $"expected routed file at {expected}");
            Assert.False(File.Exists(Path.Combine(srcFolder, "raw.mkv")));

            var (_, pathAfter) = await ExecutorTestSeed.ReadFileAsync(db, fileId);
            Assert.Contains("sorted/Films/My Film.mkv", pathAfter.Replace('\\', '/'));
            Assert.Single(bus.Published); // one acting move → one re-raised event
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task UnmatchedItem_TakesTheDefaultDestination_LikeThePreviewAndTheBatch()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            // The item matches no explicit (tag/studio/path) rule, so it takes the default destination.
            string srcFolder = Path.Combine(dir.Root, "incoming");
            string defaultRoot = Path.Combine(dir.Root, "overflow");
            Directory.CreateDirectory(srcFolder);

            string srcPathFwd = srcFolder.Replace('\\', '/');
            string defaultRootFwd = defaultRoot.Replace('\\', '/');

            var (_, videoId, fileId) =
                await ExecutorTestSeed.SeedVideoAsync(db, srcPathFwd, "raw.mkv", "My Film");
            File.WriteAllText(Path.Combine(srcFolder, "raw.mkv"), "bytes");

            var options = new RenamerOptions
            {
                AutoRenamerOnUpdate = true,
                FilenameTemplate = "$title",
                FolderRoot = defaultRootFwd,
            };
            var (ext, bus, _) = await EventTestHarness.BuildAsync(
                db, options, srcPathFwd, defaultRootFwd);

            await ext.OnEventAsync(new ExtensionEvent("video.updated", "video", videoId), default);

            Assert.True(File.Exists(Path.Combine(defaultRoot, "My Film.mkv")));
            Assert.False(File.Exists(Path.Combine(srcFolder, "raw.mkv")));

            var (_, pathAfter) = await ExecutorTestSeed.ReadFileAsync(db, fileId);
            Assert.Contains("overflow/My Film.mkv", pathAfter.Replace('\\', '/'));
            Assert.Single(bus.Published); // one acting move → one re-raised event
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }
}
