using System.Collections.Concurrent;
using Cove.Plugins;
using Microsoft.EntityFrameworkCore;
using Renamer.Execution;
using Renamer.Options;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Concurrency;

[Collection(SubstDriveScope.CollectionName)]
public sealed class ParallelBatchTests
{
    // Seeds count single-file videos in one folder, titled "Film i" over "raw i.mkv", and writes each
    // source to disk unless skip names its index.
    private static async Task<IReadOnlyList<int>> SeedVideosAsync(
        SharedCacheSqlite shared, TempDir dir, int count, int? skip = null)
    {
        string folderPath = dir.Root.Replace('\\', '/');
        await using var seedDb = shared.NewContext();
        var ids = await ExecutorTestSeed.SeedVideosAsync(
            seedDb, count, i => (folderPath, $"raw {i}.mkv", $"Film {i}"));

        for (int i = 0; i < count; i++)
        {
            if (i != skip)
            {
                File.WriteAllText(Path.Combine(dir.Root, $"raw {i}.mkv"), $"bytes-{i}");
            }
        }

        return ids;
    }

    [Fact]
    public async Task ParallelBatch_AllItemsRenamed_JournalRowsEqualSuccesses()
    {
        using var dir = new TempDir();
        var shared = await SharedCacheSqlite.CreateAsync();
        try
        {
            const int k = 8;
            var ids = await SeedVideosAsync(shared, dir, k);

            var (ext, _) = await ExtensionHarness.CreateWithScopedContextsAsync(
                shared, new RenamerOptions { FilenameTemplate = "$title" });
            var progress = new FakeJobProgress();

            await ext.RunRenamerBatchAsync(RenamerFileKind.Video, ids, progress, default);

            for (int i = 0; i < k; i++)
            {
                Assert.True(File.Exists(Path.Combine(dir.Root, $"Film {i}.mkv")), $"Film {i}.mkv missing");
                Assert.False(File.Exists(Path.Combine(dir.Root, $"raw {i}.mkv")), $"raw {i}.mkv lingered");
            }

            await using var readDb = shared.NewContext();
            await using var journal = new CoveRevertJournal(readDb);
            var batch = await JournalPageReader.ReadWholeUndoTargetAsync(journal);
            Assert.NotNull(batch);
            Assert.Equal(k, batch!.Rows.Count);
            Assert.Equal(k, batch.Rows.Select(e => e.FileId).Distinct().Count());
            Assert.All(batch.Rows, e =>
            {
                Assert.NotEqual(0, e.FileId);
                Assert.False(string.IsNullOrEmpty(e.OldPath));
            });

            // Planning owns (0, 0.5] of the bar and execution the rest, so each band sees a report.
            Assert.Equal(1d, progress.LastPercent);
            Assert.Contains(progress.Reports, r => r.Percent is > 0d and <= 0.5d);
            Assert.Contains(progress.Reports, r => r.Percent is > 0.5d and < 1d);
            Assert.All(progress.Reports, r => Assert.InRange(r.Percent, 0d, 1d));
            var seq = progress.Reports.Select(r => r.Percent).ToList();
            Assert.Equal(seq.OrderBy(p => p).ToList(), seq);
        }
        finally
        {
            await shared.DisposeAsync();
        }
    }

    // Holds every execution-phase report for a moment, so the other workers finish and queue their
    // own reports behind it. Which queued report the host receives next is then up to the scheduler,
    // so a report carrying a count read before it queued can arrive after a larger one.
    private sealed class SlowExecutionReports : IJobProgress
    {
        public ConcurrentQueue<double> Percents { get; } = new();

        public void Report(double percent, string? message = null)
        {
            Percents.Enqueue(percent);
            if (percent is > 0.5d and < 1d)
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(15));
            }
        }
    }

    // The interleaving is the scheduler's to choose, so one batch provokes it only some of the time.
    // Several batches make a pass on a regressing bar unlikely rather than impossible.
    [Fact]
    public async Task ParallelBatch_ReportsQueuedBehindASlowSink_NeverStepTheBarBackward()
    {
        for (int round = 0; round < 3; round++)
        {
            using var dir = new TempDir();
            await using var shared = await SharedCacheSqlite.CreateAsync();

            const int k = 32;
            var ids = await SeedVideosAsync(shared, dir, k);

            var (ext, _) = await ExtensionHarness.CreateWithScopedContextsAsync(
                shared, new RenamerOptions { FilenameTemplate = "$title", SameVolumeConcurrency = k });
            var progress = new SlowExecutionReports();

            await ext.RunRenamerBatchAsync(RenamerFileKind.Video, ids, progress, default);

            var seq = progress.Percents.ToList();
            Assert.Equal(seq.OrderBy(p => p).ToList(), seq);
            Assert.Equal(1d, seq[^1]);
        }
    }

    [Fact]
    public async Task ParallelBatch_OneItemMissingItsSource_OthersRename_BatchCompletes()
    {
        using var dir = new TempDir();
        var shared = await SharedCacheSqlite.CreateAsync();
        try
        {
            const int k = 6;
            const int missing = 3;
            var ids = await SeedVideosAsync(shared, dir, k, skip: missing);

            var (ext, _) = await ExtensionHarness.CreateWithScopedContextsAsync(
                shared, new RenamerOptions { FilenameTemplate = "$title" });
            var progress = new FakeJobProgress();

            await ext.RunRenamerBatchAsync(RenamerFileKind.Video, ids, progress, default);

            for (int i = 0; i < k; i++)
            {
                Assert.Equal(i != missing, File.Exists(Path.Combine(dir.Root, $"Film {i}.mkv")));
            }

            await using var readDb = shared.NewContext();
            var missingRow = await readDb.Set<Cove.Core.Entities.VideoFile>().AsNoTracking()
                .SingleAsync(f => f.VideoId == ids[missing]);
            Assert.Equal($"raw {missing}.mkv", missingRow.Basename);
            Assert.Equal(1d, progress.LastPercent);
        }
        finally
        {
            await shared.DisposeAsync();
        }
    }

    [Fact]
    public async Task SameVolumeBatch_IsExcludedFromTheFreeSpaceRefusal()
    {
        using var dir = new TempDir();
        var shared = await SharedCacheSqlite.CreateAsync();
        try
        {
            const int k = 5;
            var ids = await SeedVideosAsync(shared, dir, k);

            // One byte free everywhere is short of the default headroom, so counting these moves would
            // refuse the batch.
            var (ext, _) = await ExtensionHarness.CreateWithScopedContextsAsync(
                shared, new RenamerOptions { FilenameTemplate = "$title" });
            var progress = new FakeJobProgress();

            await ext.RunRenamerBatchAsync(RenamerFileKind.Video, ids, progress, default,
                freeSpaceProbe: _ => 1L);

            for (int i = 0; i < k; i++)
            {
                Assert.True(File.Exists(Path.Combine(dir.Root, $"Film {i}.mkv")), $"Film {i}.mkv missing");
            }
            Assert.Equal(1d, progress.LastPercent);
        }
        finally
        {
            await shared.DisposeAsync();
        }
    }

    [Fact]
    public async Task InFlightFreeSpaceDrop_SkipsCrossVolumeItemGracefully()
    {
        Assert.SkipUnless(SecondVolume.IsAvailable, SecondVolume.UnavailableReason);

        using var dir = new TempDir();
        using var drive = new SecondVolume();
        var shared = await SharedCacheSqlite.CreateAsync();
        try
        {
            string srcFolder = Path.Combine(dir.Root, "incoming");
            Directory.CreateDirectory(srcFolder);
            string srcPathFwd = srcFolder.Replace('\\', '/');
            string destRootFwd = drive.Root.Replace('\\', '/');

            await using var seedDb = shared.NewContext();
            var (_, videoId, fileId) = await ExecutorTestSeed.SeedVideoAsync(seedDb, srcPathFwd, "raw.mkv", "My Film");
            File.WriteAllText(Path.Combine(srcFolder, "raw.mkv"), "bytes");

            // The guard measures the recorded size, and a seeded row records zero, which no probe
            // value is short of.
            var fileRow = await seedDb.Set<Cove.Core.Entities.VideoFile>().FirstAsync(f => f.Id == fileId);
            fileRow.Size = 4096;
            await seedDb.SaveChangesAsync();

            var options = new RenamerOptions
            {
                FilenameTemplate = "$title",
                FolderTemplate = "Films",
                PathDestinations =
                [
                    new PathDestinationRule
                    {
                        Pattern = srcPathFwd, Dest = Dest.At(destRootFwd, "Films"), IsRegex = false,
                    },
                ],
                FreeSpaceHeadroomBytes = 0,
            };
            var (ext, _) = await ExtensionHarness.CreateWithScopedContextsAsync(
                shared, options, [srcPathFwd, destRootFwd]);

            // Ample room at the up-front check, none at the re-check just before the copy.
            int calls = 0;
            long Probe(string vol) => Interlocked.Increment(ref calls) == 1 ? 1L << 40 : 1L;

            var progress = new FakeJobProgress();
            await ext.RunRenamerBatchAsync(RenamerFileKind.Video, [videoId], progress, default, Probe);

            Assert.Equal(2, calls);
            Assert.True(File.Exists(Path.Combine(srcFolder, "raw.mkv")),
                "the source must stay put when the in-flight free-space check skips the move");
            Assert.False(File.Exists(Path.Combine(drive.Root, "Films", "My Film.mkv")),
                "no file must land on the destination after an in-flight free-space skip");
            Assert.Equal(1d, progress.LastPercent);
        }
        finally
        {
            await shared.DisposeAsync();
        }
    }
}
