using Cove.Plugins;
using Microsoft.EntityFrameworkCore;
using Renamer.Execution;
using Renamer.Options;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Events;

// Every save the hook makes raises another update event for the same entity, so a rename is only
// finished once the events it raised come back and act on nothing.
public sealed class AutoRenamerChainTests
{
    // Enough re-delivery rounds for a runaway to be unmistakable; a settled item needs one.
    private const int MaxGenerations = 12;

    // Delivers every event the hook raised, round by round, until a round raises nothing. Returns how
    // many rounds that took.
    private static async Task<int> DeliverUntilSettledAsync(
        global::Renamer.Renamer ext, CapturingEventBus bus, int videoId)
    {
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

        return generations;
    }

    private static Task<int> CountBatchesAsync(DbContext db) =>
        db.Set<RevertBatchEntity>().AsNoTracking().CountAsync();

    [Fact]
    public async Task FlagOn_NameDiffers_RenamesOnce_AndALaterEditOfTheRenamedItemOpensNoBatch()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string folderPath = dir.Root.Replace('\\', '/');
            var (_, videoId, fileId) =
                await ExecutorTestSeed.SeedVideoAsync(db, folderPath, "raw.mkv", "My Film");
            File.WriteAllText(Path.Combine(dir.Root, "raw.mkv"), "bytes");

            var options = new RenamerOptions { AutoRenamerOnUpdate = true, FilenameTemplate = "$title" };
            var bus = new CapturingEventBus();
            var (ext, _) = await ExtensionHarness.CreateWithSharedContextAsync(db, options, bus);

            await ext.OnEventAsync(new ExtensionEvent("video.updated", "video", videoId), default);

            Assert.True(File.Exists(Path.Combine(dir.Root, "My Film.mkv")));
            Assert.False(File.Exists(Path.Combine(dir.Root, "raw.mkv")));
            var (basenameAfterFirst, _) = await ExecutorTestSeed.ReadFileAsync(db, fileId);
            Assert.Equal("My Film.mkv", basenameAfterFirst);
            Assert.Single(bus.Published);

            Assert.Equal(1, await DeliverUntilSettledAsync(ext, bus, videoId));

            // The re-raised event was the hook's own save. This one is a genuine edit, planned against
            // a name that already matches, so the hook stops before opening an undo batch.
            await ext.OnEventAsync(new ExtensionEvent("video.updated", "video", videoId), default);

            var (basenameAfterEdit, _) = await ExecutorTestSeed.ReadFileAsync(db, fileId);
            Assert.Equal("My Film.mkv", basenameAfterEdit);
            Assert.Single(bus.Published);
            Assert.Equal(1, await CountBatchesAsync(db));
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

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
            var bus = new CapturingEventBus();
            var (ext, _) = await ExtensionHarness.CreateWithSharedContextAsync(db, options, bus, folderFwd);

            await ext.OnEventAsync(new ExtensionEvent("video.updated", "video", videoId), default);
            int generations = await DeliverUntilSettledAsync(ext, bus, videoId);

            // A further generation means the surplus file planned as a move onto its own path, so the
            // executor moved it to where it already was and saved, and the save re-raised the event.
            Assert.True(
                bus.Published.Count == 2 && generations == 1,
                $"the chain kept going: {bus.Published.Count} events across {generations} generations,"
                    + $" leaving {string.Join(", ", Directory.GetFiles(dir.Root).Select(Path.GetFileName).Order())}");

            // Transcribed from the arrangement. Asking the planner where a file belongs would produce an
            // expectation that agrees with the code under test however far the two drift.
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
            var bus = new CapturingEventBus();
            var (ext, _) = await ExtensionHarness.CreateWithSharedContextAsync(db, options, bus, folderFwd);

            await ext.OnEventAsync(new ExtensionEvent("video.updated", "video", videoId), default);
            int generations = await DeliverUntilSettledAsync(ext, bus, videoId);

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
