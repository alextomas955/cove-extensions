using Cove.Plugins;
using Renamer.Options;
using Renamer.Tests.Execution;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Events;

/// <summary>
/// CR-01 regression for the auto-renamer hook: a matched routing rule must relocate the just-edited
/// item to its configured destination — the SAME on-disk outcome the manual batch and <c>/preview</c>
/// produce. Before the fix the hook called the empty-lookups overload, so auto-renamers silently never
/// relocated even when a matching destination rule was configured. ROUTE-04 default-relocate stays
/// gated (default off), so this proves only an explicitly-MATCHED rule relocates — not a dribble of
/// the whole library.
/// </summary>
[Trait("Tier", "Integration")]
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
                AllowedRoots = [srcPathFwd, destRootFwd],
                PathDestinations =
                    [new PathDestinationRule { Pattern = srcPathFwd, Dest = destRootFwd, IsRegex = false }],
            };
            var (ext, bus, _) = await EventTestHarness.BuildAsync(db, options);

            await ext.OnEventAsync(new ExtensionEvent("video.updated", "video", videoId), default);

            // The matched route relocated the file to destRoot/Films/My Film.mkv — NOT in place.
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
    public async Task FlagOn_UnmatchedItem_DefaultRelocateOff_StaysInPlace()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string srcFolder = dir.Root;
            string srcPathFwd = srcFolder.Replace('\\', '/');
            var (_, videoId, fileId) =
                await ExecutorTestSeed.SeedVideoAsync(db, srcPathFwd, "raw.mkv", "My Film");
            File.WriteAllText(Path.Combine(srcFolder, "raw.mkv"), "bytes");

            // No matching rule + default-relocate OFF → the item must NOT relocate; an in-place renamer
            // (My Film.mkv) is fine, but it stays under the source folder.
            var options = new RenamerOptions
            {
                AutoRenamerOnUpdate = true,
                FilenameTemplate = "$title",
                DefaultDestination = Path.Combine(dir.Root, "overflow").Replace('\\', '/'),
                EnableDefaultRelocate = false,
            };
            var (ext, _, _) = await EventTestHarness.BuildAsync(db, options);

            await ext.OnEventAsync(new ExtensionEvent("video.updated", "video", videoId), default);

            // Renamed in place, never relocated to the default destination (the gate held).
            Assert.True(File.Exists(Path.Combine(srcFolder, "My Film.mkv")));
            var (_, pathAfter) = await ExecutorTestSeed.ReadFileAsync(db, fileId);
            Assert.DoesNotContain("overflow", pathAfter.Replace('\\', '/'));
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }
}
