using Cove.Plugins;
using Renamer.Options;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Events;

public sealed class AutoRenamerCollisionChainTests
{
    // Enough re-delivery rounds for a runaway to be unmistakable; a settled item needs one.
    private const int MaxGenerations = 12;

    [Fact]
    public async Task FlagOn_TwoFilesRenderingOneTarget_ChainReachesAFixedPoint()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string folderFwd = dir.Root.Replace('\\', '/');
            var (folderId, videoId, firstId) =
                await ExecutorTestSeed.SeedVideoAsync(db, folderFwd, "raw.mkv", "My Film");
            int secondId = await ExecutorTestSeed.SeedAdditionalFileAsync(db, folderId, videoId, "extra.mkv");

            File.WriteAllText(Path.Combine(dir.Root, "raw.mkv"), "one");
            File.WriteAllText(Path.Combine(dir.Root, "extra.mkv"), "two");

            var options = new RenamerOptions
            {
                AutoRenamerOnUpdate = true,
                // A stored title, so the rendered name is stable across generations and the only thing
                // that can keep the chain alive is the collision between the two files.
                FilenameTemplate = "$title",
            };
            var (ext, bus, _) = await EventTestHarness.BuildAsync(db, options, folderFwd);

            // One genuine edit.
            await ext.OnEventAsync(new ExtensionEvent("video.updated", "video", videoId), default);

            int delivered = 0;
            int generations = 0;
            while (delivered < bus.Published.Count && generations < MaxGenerations)
            {
                generations++;
                int frontier = bus.Published.Count;
                for (; delivered < frontier; delivered++)
                {
                    await ext.OnEventAsync(new ExtensionEvent("video.updated", "video", videoId), default);
                }
            }

            // Two events for the two files, and the round that delivered them raised nothing further. A
            // further generation means the surplus file planned as a move onto its own path, so the
            // executor moved it to where it already was and saved, and the save re-raised the event.
            Assert.True(
                bus.Published.Count == 2 && generations == 1,
                $"the chain kept going: {bus.Published.Count} events across {generations} generations,"
                    + $" leaving {string.Join(", ", Directory.GetFiles(dir.Root).Select(Path.GetFileName).Order())}");

            // Both terminal names transcribed from the arrangement. Asking the planner where a file
            // belongs would produce an expectation that agrees with the code under test however far the
            // two drift.
            Assert.True(File.Exists(Path.Combine(dir.Root, "My Film.mkv")));
            Assert.True(File.Exists(Path.Combine(dir.Root, "My Film (1).mkv")));
            Assert.False(File.Exists(Path.Combine(dir.Root, "raw.mkv")));
            Assert.False(File.Exists(Path.Combine(dir.Root, "extra.mkv")));

            var (firstBasename, _) = await ExecutorTestSeed.ReadFileAsync(db, firstId);
            var (secondBasename, _) = await ExecutorTestSeed.ReadFileAsync(db, secondId);
            Assert.Equal("My Film.mkv", firstBasename);
            Assert.Equal("My Film (1).mkv", secondBasename);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }
}
