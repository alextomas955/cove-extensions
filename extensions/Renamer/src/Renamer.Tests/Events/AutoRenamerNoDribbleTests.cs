using Cove.Plugins;
using Renamer.Options;
using Renamer.Tests.Execution;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Events;

/// <summary>
/// Defense-in-depth proof for the auto-renamer hook: the per-metadata-edit event path can NEVER
/// default-relocate an item, EVEN WHEN <see cref="RenamerOptions.EnableDefaultRelocate"/> is true.
/// The hook fires once per edit with no user confirm, so a default-relocate reaching the executor on
/// this path would let a single edit dribble-relocate the whole library one item at a time. The guard
/// excludes the Default-relocate category from the hook's "acting" set, so the executor is never
/// invoked for it — and this test asserts that holds with the flag flipped ON (proving the guard, not
/// merely the off flag). The companion <c>AutoRenamerRoutingTests</c> proves an EXPLICIT rule still
/// relocates.
/// </summary>
[Trait("Tier", "Integration")]
public sealed class AutoRenamerNoDribbleTests
{
    [Fact]
    public async Task GuardHolds_DefaultRelocateEnabled_UnmatchedItem_DoesNotRelocate()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            // The item sits in a source folder and matches NO explicit (tag/studio/path) rule, so the
            // resolver's only acting route is the GATED Default category. The default destination is a
            // real sibling under an allowed root — so absent the guard the item WOULD relocate to it.
            string srcFolder = Path.Combine(dir.Root, "incoming");
            string defaultRoot = Path.Combine(dir.Root, "overflow");
            Directory.CreateDirectory(srcFolder);

            string srcPathFwd = srcFolder.Replace('\\', '/');
            string defaultRootFwd = defaultRoot.Replace('\\', '/');

            var (_, videoId, fileId) =
                await ExecutorTestSeed.SeedVideoAsync(db, srcPathFwd, "raw.mkv", "My Film");
            File.WriteAllText(Path.Combine(srcFolder, "raw.mkv"), "bytes");

            // EnableDefaultRelocate = TRUE + a configured DefaultDestination under an allowed root, and
            // NO explicit rule for the item: the resolver would route it to the Default category. The
            // guard must still keep it in place (an in-place renamer is allowed) on the hook path.
            var options = new RenamerOptions
            {
                AutoRenamerOnUpdate = true,
                FilenameTemplate = "$title",
                DefaultDestination = defaultRootFwd,
                EnableDefaultRelocate = true,
                AllowedRoots = [srcPathFwd, defaultRootFwd],
            };
            var (ext, bus, _) = await EventTestHarness.BuildAsync(db, options);

            await ext.OnEventAsync(new ExtensionEvent("video.updated", "video", videoId), default);

            // The item was NOT relocated to the default destination: nothing exists under the default
            // root, and the file is still in its source folder. (Because the item's ONLY acting outcome
            // would have been the now-excluded default-relocate, the hook short-circuits before the
            // executor — so the file keeps its original name in place; no save occurs.)
            Assert.False(
                Directory.Exists(defaultRoot) && Directory.EnumerateFileSystemEntries(defaultRoot).Any(),
                "the unmatched item must NOT be relocated to the default destination");
            Assert.True(
                File.Exists(Path.Combine(srcFolder, "raw.mkv")),
                "the item should stay in its source folder, untouched by the hook");

            // DB path confirms no relocate: the recorded path stays under the source folder and never
            // contains the default-destination segment.
            var (_, pathAfter) = await ExecutorTestSeed.ReadFileAsync(db, fileId);
            string pathFwd = pathAfter.Replace('\\', '/');
            Assert.Contains("incoming", pathFwd);
            Assert.DoesNotContain("overflow", pathFwd);

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
}
