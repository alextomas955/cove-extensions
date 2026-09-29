using Cove.Plugins;
using Renamer.Options;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Events;

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
                // The pair that never settles. Each rule matches where the other one puts the file, and
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
            var bus = new CapturingEventBus();
            var (ext, _) = await ExtensionHarness.CreateWithSharedContextAsync(db, options, bus, libraryFwd, sortedFwd);

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

            // (2) The event that save re-raised. The plan is not empty here - the second rule would take
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

            // (3) A later genuine edit, not the re-raised one. It must be processed: the suppression is
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

    [Fact]
    public async Task FlagOn_RunRenamesNothing_ReleasesSuppression_SoTheNextEditIsHonoured()
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

            // Files at the destination names that Cove holds no row for. The planner's collision check
            // reads file rows, so it sees a free name and plans the move; the executor measures the disk
            // too and re-suffixes. A suffix format carrying no {n} renders the same name on every attempt,
            // so those two occupied names exhaust the loop and the item skips. Any per-item failure
            // reaches the same place - this is just the shortest one that needs no platform behaviour.
            string blocker = Path.Combine(sortedFolder, "My Film.mkv");
            string suffixedBlocker = Path.Combine(sortedFolder, "My Film (copy).mkv");
            File.WriteAllText(blocker, "someone else's bytes");
            File.WriteAllText(suffixedBlocker, "and someone else's again");

            var options = new RenamerOptions
            {
                AutoRenamerOnUpdate = true,
                FilenameTemplate = "$title",
                FolderTemplate = "",
                DuplicateSuffixFormat = " (copy)",
                PathDestinations =
                [
                    new PathDestinationRule
                    {
                        Pattern = libraryFwd, Dest = Dest.At(sortedFwd), IsRegex = false,
                    },
                ],
            };
            var bus = new CapturingEventBus();
            var (ext, _) = await ExtensionHarness.CreateWithSharedContextAsync(db, options, bus, libraryFwd, sortedFwd);

            string atLibrary = Path.Combine(libraryFolder, "raw.mkv");

            // (1) A genuine edit. The rule matches, so the plan acts and the executor is called - and the
            //     occupied destination sends every item to a skip, so nothing is saved and no event is
            //     raised for the suppression to consume.
            await ext.OnEventAsync(new ExtensionEvent("video.updated", "video", videoId), default);

            Assert.True(File.Exists(atLibrary), "the file left its folder even though the move was refused");
            Assert.Equal("someone else's bytes", File.ReadAllText(blocker));
            Assert.Equal("and someone else's again", File.ReadAllText(suffixedBlocker));
            var (_, pathAfterFirst) = await ExecutorTestSeed.ReadFileAsync(db, fileId);
            Assert.Equal($"{libraryFwd}/raw.mkv", pathAfterFirst.Replace('\\', '/'));
            Assert.Empty(bus.Published);

            // The destination is free from here on, so the only thing that can still stop the rename is a
            // suppression left armed by the run above.
            File.Delete(blocker);
            File.Delete(suffixedBlocker);

            // (2) A later genuine edit, and the assertion this test exists for.
            await ext.OnEventAsync(new ExtensionEvent("video.updated", "video", videoId), default);

            Assert.True(
                File.Exists(blocker),
                "a genuine later edit was swallowed - the run that renamed nothing left its self-save "
                    + "suppression armed, so the auto-renamer is muted for this item until something else "
                    + "raises an update event for it");
            Assert.False(File.Exists(atLibrary), "the rename left a copy behind at the source");
            var (_, pathAfterSecond) = await ExecutorTestSeed.ReadFileAsync(db, fileId);
            Assert.Equal($"{sortedFwd}/My Film.mkv", pathAfterSecond.Replace('\\', '/'));
            Assert.Single(bus.Published);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }
}
