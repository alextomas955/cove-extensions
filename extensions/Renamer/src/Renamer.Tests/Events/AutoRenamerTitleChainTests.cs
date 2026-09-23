using Cove.Plugins;
using Renamer.Options;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Events;

public sealed class AutoRenamerTitleChainTests
{
    // Enough re-delivery rounds for a runaway to be unmistakable; a settled item needs one.
    private const int MaxGenerations = 12;

    [Fact]
    public async Task FlagOn_TitlelessMultiFileItem_RenamesOnce_ThenTheChainStops()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string folderFwd = dir.Root.Replace('\\', '/');
            var (folderId, videoId, mkvFileId) = await ExecutorTestSeed.SeedVideoAsync(
                db, folderFwd, "raw clip.mkv", title: null!,
                date: new DateOnly(2021, 3, 14), height: 2160, width: 3840);
            int mp4FileId = await ExecutorTestSeed.SeedAdditionalFileAsync(
                db, folderId, videoId, "raw clip.mp4", height: 2160, width: 3840);

            File.WriteAllText(Path.Combine(dir.Root, "raw clip.mkv"), "one");
            File.WriteAllText(Path.Combine(dir.Root, "raw clip.mp4"), "two");

            var options = new RenamerOptions
            {
                AutoRenamerOnUpdate = true,
                // More than a bare $title: with nothing but the title the derivation equals the stem it
                // came from, so nothing acts and the chain is unobservable for the wrong reason.
                FilenameTemplate = "{$date - }$title{ [$resolution]}",
                FilenameAsTitle = true,
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

            // Two events for the two files, and the round that delivered them raised nothing further.
            Assert.True(
                bus.Published.Count == 2 && generations == 1,
                $"the chain kept going: {bus.Published.Count} events across {generations} generations,"
                    + $" leaving {string.Join(", ", Directory.GetFiles(dir.Root).Select(Path.GetFileName).Order())}");

            Assert.True(File.Exists(Path.Combine(dir.Root, "2021-03-14 - raw clip [4K].mkv")));
            Assert.True(File.Exists(Path.Combine(dir.Root, "2021-03-14 - raw clip [4K].mp4")));
            Assert.False(File.Exists(Path.Combine(dir.Root, "raw clip.mkv")));
            Assert.False(File.Exists(Path.Combine(dir.Root, "raw clip.mp4")));

            var (mkvBasename, _) = await ExecutorTestSeed.ReadFileAsync(db, mkvFileId);
            var (mp4Basename, _) = await ExecutorTestSeed.ReadFileAsync(db, mp4FileId);
            Assert.Equal("2021-03-14 - raw clip [4K].mkv", mkvBasename);
            Assert.Equal("2021-03-14 - raw clip [4K].mp4", mp4Basename);

            // The title the rename derived is now stored, which is why the second round found nothing.
            Assert.Equal("raw clip", await ExecutorTestSeed.ReadVideoTitleAsync(db, videoId));
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }
}
