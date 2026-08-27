using Cove.Plugins;
using Renamer.Options;
using Renamer.Tests.Execution;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Events;

/// <summary>
/// What bounds the auto-renamer hook: it plans only the entity that was edited, and it opens no batch
/// at all when nothing would act. The hook fires once per metadata edit with no user confirm, so a
/// pass that acts on nothing must not reach the executor - no batch, no save, and therefore no
/// re-raised update event to re-enter on. The companion <c>AutoRenamerRoutingTests</c> proves a
/// matched rule still relocates.
/// </summary>
public sealed class AutoRenamerNoDribbleTests
{
    [Fact]
    public async Task NothingWouldAct_NoExecutorCall_NoReRaisedEvent()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string srcFolder = Path.Combine(dir.Root, "incoming");
            Directory.CreateDirectory(srcFolder);
            string srcPathFwd = srcFolder.Replace('\\', '/');

            // The file already carries the name the template renders, and the default destination names
            // neither a root nor a folder, so the plan is a no-op for every file.
            var (_, videoId, fileId) =
                await ExecutorTestSeed.SeedVideoAsync(db, srcPathFwd, "My Film.mkv", "My Film");
            File.WriteAllText(Path.Combine(srcFolder, "My Film.mkv"), "bytes");

            var options = new RenamerOptions
            {
                AutoRenamerOnUpdate = true,
                FilenameTemplate = "$title",
            };
            var (ext, bus, _) = await EventTestHarness.BuildAsync(db, options, srcPathFwd);

            await ext.OnEventAsync(new ExtensionEvent("video.updated", "video", videoId), default);

            Assert.True(File.Exists(Path.Combine(srcFolder, "My Film.mkv")));
            var (_, pathAfter) = await ExecutorTestSeed.ReadFileAsync(db, fileId);
            Assert.EndsWith("My Film.mkv", pathAfter.Replace('\\', '/'));

            // No acting item on the hook path → the executor was never invoked → no re-raised event.
            // (The re-entrancy guard short-circuited; the save→event→re-enter loop never started.)
            Assert.Empty(bus.Published);
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
            // The item matches NO explicit (tag/studio/path) rule, so it takes the DEFAULT destination.
            // The hook resolves destinations identically to /preview and the manual batch, so an
            // unmatched item is not a category the hook treats differently.
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
