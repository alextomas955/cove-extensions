using Cove.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Renamer.Execution;
using Renamer.Options;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Concurrency;

public sealed class ParallelFolderCreationTests
{
    [Fact]
    public async Task ParallelBatch_ManyItemsToSameNewFolder_AllLandInTheOneNewFolder()
    {
        using var dir = new TempDir();
        var shared = await SharedCacheSqlite.CreateAsync();
        try
        {
            const int k = 12;
            string sourceFolderFwd = dir.Root.Replace('\\', '/');
            string destFolderFwd = sourceFolderFwd + "/sorted";

            IReadOnlyList<int> ids;
            await using (var seedDb = shared.NewContext())
            {
                ids = await ExecutorTestSeed.SeedVideosAsync(
                    seedDb, k, i => (sourceFolderFwd, $"raw {i}.mkv", $"Film {i}"));
            }
            for (int i = 0; i < k; i++)
            {
                File.WriteAllText(Path.Combine(dir.Root, $"raw {i}.mkv"), $"bytes-{i}");
            }

            // Every item targets one folder the database does not hold yet, and same-volume workers run
            // unbounded. Cove's unique index on the folder path turns a racing check-then-act create into
            // a failed save, so a race shows as a file that did not land, not as a second folder row.
            var options = new RenamerOptions
            {
                FilenameTemplate = "$title",
                FolderTemplate = "sorted",
                PathDestinations =
                [
                    new PathDestinationRule
                    {
                        Pattern = sourceFolderFwd, Dest = Dest.At(sourceFolderFwd, "sorted"), IsRegex = false,
                    },
                ],
            };
            var (ext, _) = await ExtensionHarness.CreateWithScopedContextsAsync(shared, options, [sourceFolderFwd]);
            var progress = new FakeJobProgress();

            await ext.RunRenamerBatchAsync(RenamerFileKind.Video, ids, progress, default);

            for (int i = 0; i < k; i++)
            {
                Assert.True(File.Exists(Path.Combine(dir.Root, "sorted", $"Film {i}.mkv")),
                    $"Film {i}.mkv missing from the routed folder");
            }
            Assert.Equal(1d, progress.LastPercent);

            await using var verifyDb = shared.NewContext();
            Assert.Equal(1, await verifyDb.Set<Folder>().AsNoTracking().CountAsync(f => f.Path == destFolderFwd));

            await using var journal = new CoveRevertJournal(verifyDb);
            var batch = await JournalPageReader.ReadWholeUndoTargetAsync(journal);
            Assert.NotNull(batch);
            Assert.Equal(k, batch!.Rows.Count);
        }
        finally
        {
            await shared.DisposeAsync();
        }
    }

    [Fact]
    public async Task InPlaceRename_StillWorks_NoNewDestinationFolder()
    {
        using var dir = new TempDir();
        var shared = await SharedCacheSqlite.CreateAsync();
        try
        {
            string folderFwd = dir.Root.Replace('\\', '/');
            int videoId;
            await using (var seedDb = shared.NewContext())
            {
                (_, videoId, _) = await ExecutorTestSeed.SeedVideoAsync(seedDb, folderFwd, "raw.mkv", "My Film");
            }
            File.WriteAllText(Path.Combine(dir.Root, "raw.mkv"), "bytes");

            var (ext, _) = await ExtensionHarness.CreateWithScopedContextsAsync(
                shared, new RenamerOptions { FilenameTemplate = "$title" });
            var progress = new FakeJobProgress();

            await ext.RunRenamerBatchAsync(RenamerFileKind.Video, [videoId], progress, default);

            Assert.True(File.Exists(Path.Combine(dir.Root, "My Film.mkv")));
            Assert.False(File.Exists(Path.Combine(dir.Root, "raw.mkv")));

            await using var verifyDb = shared.NewContext();
            Assert.Equal(1, await verifyDb.Set<Folder>().AsNoTracking().CountAsync());
            Assert.Equal(1d, progress.LastPercent);
        }
        finally
        {
            await shared.DisposeAsync();
        }
    }
}
