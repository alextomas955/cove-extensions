using Cove.Plugins;
using Renamer.Options;
using Renamer.Tests.Execution;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Events;

/// <summary>
/// The self-save suppression must be released on every exit that saves nothing, not only on the two
/// that throw.
/// </summary>
/// <remarks>
/// The suppression is armed before the executor call because the host re-raises fire-and-forget. It is
/// consumed by the event that save raises — so a run that reaches the executor and renames nothing
/// raises no event, and the armed token waits for the user's next genuine edit instead. Every path in
/// here is written out from the arrangement by hand rather than asked of the planner, so an expectation
/// cannot agree with the code however far the two drift.
/// </remarks>
public sealed class AutoRenamerSelfSaveReleaseTests
{
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
            // reads file ROWS, so it sees a free name and plans the move; the executor measures the DISK
            // too and re-suffixes. A suffix format carrying no {n} renders the same name on every attempt,
            // so those two occupied names exhaust the loop and the item skips. Any per-item failure
            // reaches the same place — this is just the shortest one that needs no platform behaviour.
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
                AllowedRoots = [libraryFwd, sortedFwd],
                PathDestinations =
                [
                    new PathDestinationRule
                    {
                        Pattern = libraryFwd, Dest = Dest.At(sortedFwd), IsRegex = false,
                    },
                ],
            };
            var (ext, bus, _) = await EventTestHarness.BuildAsync(db, options, libraryFwd, sortedFwd);

            string atLibrary = Path.Combine(libraryFolder, "raw.mkv");

            // (1) A genuine edit. The rule matches, so the plan acts and the executor is called — and the
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
