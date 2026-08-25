using Cove.Plugins;
using Renamer.Options;
using Renamer.Tests.Execution;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Events;

/// <summary>
/// The auto-renamer hook must not act on the update event its own save raised.
/// </summary>
/// <remarks>
/// The plan-is-empty guard breaks the loop only where the plan CONVERGES: rename, re-enter, find
/// nothing left to do, stop. Two routing rules whose destinations are each other's patterns never
/// converge - each pass matches, acts and re-raises - so no per-pass check can stop it, and because
/// one entity can hold several files a pass can raise more events than the one that started it.
/// <para>
/// Every expected path below is written out from the arrangement by hand. Asking the resolver or the
/// planner where the file should be would produce an expectation that agrees with the code under test
/// however far the two drift, which is the one failure these assertions exist to catch.
/// </para>
/// </remarks>
[Trait("Tier", "L1")]
public sealed class AutoRenamerSelfSaveTests
{
    [Fact]
    public async Task FlagOn_NonConvergingRulePair_ActsOnce_AndNotOnItsOwnEvent()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            // Sibling folders under one temp root, so the same-volume atomic move path applies.
            string libraryFolder = Path.Combine(dir.Root, "library");
            string sortedFolder = Path.Combine(dir.Root, "sorted");
            Directory.CreateDirectory(libraryFolder);
            Directory.CreateDirectory(sortedFolder);

            string libraryFwd = libraryFolder.Replace('\\', '/');
            string sortedFwd = sortedFolder.Replace('\\', '/');

            var (_, videoId, fileId) =
                await ExecutorTestSeed.SeedVideoAsync(db, libraryFwd, "raw.mkv", "My Film");
            File.WriteAllText(Path.Combine(libraryFolder, "raw.mkv"), "bytes");

            var options = new RenamerOptions
            {
                AutoRenamerOnUpdate = true,
                FilenameTemplate = "$title",
                // Empty on purpose: a rendered subfolder would deepen the path until neither pattern
                // matched, which converges and hides the case under test.
                FolderTemplate = "",
                AllowedRoots = [libraryFwd, sortedFwd],
                // The pair that never settles. Each rule matches where the OTHER one puts the file, and
                // both are explicitly matched rules rather than the default relocate the hook excludes,
                // so every pass acts.
                PathDestinations =
                [
                    new PathDestinationRule { Pattern = libraryFwd, Dest = Dest.At(sortedFwd), IsRegex = false },
                    new PathDestinationRule { Pattern = sortedFwd, Dest = Dest.At(libraryFwd), IsRegex = false },
                ],
            };
            // Both roots are declared as Cove library paths: a destination names a root chosen from that list,
            // so a root the host does not have is a stated skip rather than a move.
            var (ext, bus, _) = await EventTestHarness.BuildAsync(db, options, libraryFwd, sortedFwd);

            // Both destinations, transcribed from the arrangement above and never computed.
            string atSorted = Path.Combine(sortedFolder, "My Film.mkv");
            string atLibrary = Path.Combine(libraryFolder, "My Film.mkv");

            // (1) One genuine edit, one hop: the first rule matches the file's folder and relocates it.
            await ext.OnEventAsync(new ExtensionEvent("video.updated", "video", videoId), default);

            Assert.True(File.Exists(atSorted), $"the matched rule did not relocate the file to {atSorted}");
            Assert.False(File.Exists(Path.Combine(libraryFolder, "raw.mkv")));
            var (_, pathAfterFirst) = await ExecutorTestSeed.ReadFileAsync(db, fileId);
            Assert.Equal($"{sortedFwd}/My Film.mkv", pathAfterFirst.Replace('\\', '/'));
            Assert.Single(bus.Published);

            // (2) The event that save re-raised. The plan is NOT empty here - the second rule would take
            //     the file straight back - so nothing but a suppression scoped to this handler's own save
            //     can stop it.
            await ext.OnEventAsync(new ExtensionEvent("video.updated", "video", videoId), default);

            Assert.True(
                File.Exists(atSorted),
                "the file moved on the event its own save raised - the auto-renamer re-entered itself "
                    + "instead of ignoring an item it had just saved");
            Assert.False(File.Exists(atLibrary), $"the file bounced back to {atLibrary}");
            var (_, pathAfterReentry) = await ExecutorTestSeed.ReadFileAsync(db, fileId);
            Assert.Equal($"{sortedFwd}/My Film.mkv", pathAfterReentry.Replace('\\', '/'));
            Assert.True(
                bus.Published.Count == 1,
                "the re-entrant event saved and re-raised again, which is the runaway: "
                    + $"{bus.Published.Count} events published where one action happened");

            // (3) A LATER genuine edit, not the re-raised one. It must be processed: the suppression is
            //     scoped to the action that armed it, not a mode the handler stays in. Without this
            //     assertion a suppression that never released would pass (1) and (2) and mute the hook
            //     for this item permanently.
            await ext.OnEventAsync(new ExtensionEvent("video.updated", "video", videoId), default);

            Assert.True(
                File.Exists(atLibrary),
                "a genuine later edit was swallowed - the self-save suppression never released, so the "
                    + "auto-renamer is now permanently muted for this item");
            Assert.False(File.Exists(atSorted), "the later edit left a copy at the previous destination");
            var (_, pathAfterThird) = await ExecutorTestSeed.ReadFileAsync(db, fileId);
            Assert.Equal($"{libraryFwd}/My Film.mkv", pathAfterThird.Replace('\\', '/'));
            Assert.Equal(2, bus.Published.Count);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }
}
