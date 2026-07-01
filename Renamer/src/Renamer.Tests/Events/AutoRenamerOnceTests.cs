using Cove.Plugins;
using Renamer.Options;
using Renamer.Tests.Execution;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Events;

/// <summary>
/// The "executes once" half of the re-entrancy story: with auto-renamer ON and a name that
/// differs, the first event renamers the file once (disk + DB); a SECOND event — standing in for the
/// re-raised <c>video.updated</c> the executor's save produces — finds an all-NoOp plan and leaves
/// the terminal state stable with no further churn and no new published event.
/// </summary>
[Trait("Tier", "Integration")]
public sealed class AutoRenamerOnceTests
{
    [Fact]
    public async Task FlagOn_NameDiffers_RenamersOnce_ThenReentryIsStableNoOp()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string folderPath = dir.Root.Replace('\\', '/');
            var (_, videoId, fileId) =
                await ExecutorTestSeed.SeedVideoAsync(db, folderPath, "raw.mkv", "My Film");
            File.WriteAllText(Path.Combine(dir.Root, "raw.mkv"), "bytes");

            var options = new RenamerOptions
            {
                AutoRenamerOnUpdate = true,
                FilenameTemplate = "$title",
            };
            var (ext, bus, _) = await EventTestHarness.BuildAsync(db, options);

            // First event: renamers raw.mkv → My Film.mkv (one acting item ⇒ one publish).
            await ext.OnEventAsync(new ExtensionEvent("video.updated", "video", videoId), default);

            Assert.True(File.Exists(Path.Combine(dir.Root, "My Film.mkv")));
            Assert.False(File.Exists(Path.Combine(dir.Root, "raw.mkv")));
            var (basenameAfterFirst, _) = await ExecutorTestSeed.ReadFileAsync(db, fileId);
            Assert.Equal("My Film.mkv", basenameAfterFirst);
            Assert.Single(bus.Published);            // exactly one save → exactly one re-raise

            // Second event (the re-raised update): now all-NoOp → guard short-circuits, no churn.
            await ext.OnEventAsync(new ExtensionEvent("video.updated", "video", videoId), default);

            var (basenameAfterSecond, _) = await ExecutorTestSeed.ReadFileAsync(db, fileId);
            Assert.Equal("My Film.mkv", basenameAfterSecond);                 // stable terminal state
            Assert.True(File.Exists(Path.Combine(dir.Root, "My Film.mkv")));
            Assert.Single(bus.Published);            // no additional event from the re-entry
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }
}
