using Cove.Plugins;
using Renamer.Options;
using Renamer.Tests.Execution;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Events;

/// <summary>
/// The load-bearing auto-renamer safety property: with auto-renamer ON but the file ALREADY named
/// exactly what the template renders, the plan is all-NoOp, so firing the handler performs ZERO
/// saves and the event bus records ZERO published events. Because the executor's save is the only
/// thing that re-raises <c>video.updated</c>, zero events proves the save→event→re-enter loop can
/// never start.
/// </summary>
[Trait("Tier", "Integration")]
public sealed class ReentrancyGuardTests
{
    [Fact]
    public async Task FlagOn_AlreadyCorrectName_PerformsZeroSaves_ZeroEvents()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string folderPath = dir.Root.Replace('\\', '/');
            // The basename already equals what "$title" renders → every file is NoOp.
            var (_, videoId, fileId) =
                await ExecutorTestSeed.SeedVideoAsync(db, folderPath, "My Film.mkv", "My Film");
            File.WriteAllText(Path.Combine(dir.Root, "My Film.mkv"), "bytes");

            var options = new RenamerOptions
            {
                AutoRenamerOnUpdate = true,
                FilenameTemplate = "$title",
            };
            var (ext, bus, _) = await EventTestHarness.BuildAsync(db, options);

            await ext.OnEventAsync(new ExtensionEvent("video.updated", "video", videoId), default);

            // ZERO published events ⇒ no executor save ran ⇒ no re-raised event ⇒ the loop is impossible.
            Assert.Empty(bus.Published);

            var (basename, _) = await ExecutorTestSeed.ReadFileAsync(db, fileId);
            Assert.Equal("My Film.mkv", basename);                           // unchanged
            Assert.True(File.Exists(Path.Combine(dir.Root, "My Film.mkv")));
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }
}
