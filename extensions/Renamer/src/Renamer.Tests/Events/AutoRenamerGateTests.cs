using Cove.Plugins;
using Renamer.Options;
using Renamer.Tests.Execution;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Events;

/// <summary>
/// The opt-in + gating half of auto-renamer: with the flag OFF (default) the hook does nothing, and
/// with the flag ON but the planner's require-fields gate excluding the item, it still does nothing
/// — no junk names on incomplete metadata.
/// </summary>
[Trait("Tier", "Integration")]
public sealed class AutoRenamerGateTests
{
    [Fact]
    public async Task FlagOff_FiringUpdated_PerformsNoRenamer_NoEvents()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string folderPath = dir.Root.Replace('\\', '/');
            // Name differs from the "$title" render, so ONLY the OFF flag can be why nothing happens.
            var (_, videoId, fileId) =
                await ExecutorTestSeed.SeedVideoAsync(db, folderPath, "raw.mkv", "My Film");
            File.WriteAllText(Path.Combine(dir.Root, "raw.mkv"), "bytes");

            // Default options: AutoRenamerOnUpdate is false.
            var (ext, bus, _) = await EventTestHarness.BuildAsync(db, new RenamerOptions());

            await ext.OnEventAsync(new ExtensionEvent("video.updated", "video", videoId), default);

            var (basename, _) = await ExecutorTestSeed.ReadFileAsync(db, fileId);
            Assert.Equal("raw.mkv", basename);                              // DB untouched
            Assert.True(File.Exists(Path.Combine(dir.Root, "raw.mkv")));     // disk untouched
            Assert.False(File.Exists(Path.Combine(dir.Root, "My Film.mkv")));
            Assert.Empty(bus.Published);                                     // no save → no event
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task FlagOn_ButRequireFieldsGateExcludes_PerformsNoRenamer()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string folderPath = dir.Root.Replace('\\', '/');
            // Empty title → the require-fields ["title"] gate excludes this item (SkipGated).
            var (_, videoId, fileId) =
                await ExecutorTestSeed.SeedVideoAsync(db, folderPath, "raw.mkv", title: "");
            File.WriteAllText(Path.Combine(dir.Root, "raw.mkv"), "bytes");

            // FilenameAsTitle forced off so the empty title is NOT rescued by the basename fallback
            // (which now defaults on) — this case proves the require-fields gate excludes the item.
            var options = new RenamerOptions
            {
                AutoRenamerOnUpdate = true,
                RequiredFields = ["title"],
                FilenameAsTitle = false,
            };
            var (ext, bus, _) = await EventTestHarness.BuildAsync(db, options);

            await ext.OnEventAsync(new ExtensionEvent("video.updated", "video", videoId), default);

            var (basename, _) = await ExecutorTestSeed.ReadFileAsync(db, fileId);
            Assert.Equal("raw.mkv", basename);                           // gated → unchanged
            Assert.True(File.Exists(Path.Combine(dir.Root, "raw.mkv")));
            Assert.Empty(bus.Published);                                 // no save → no event
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }
}
