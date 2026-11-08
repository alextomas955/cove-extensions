using Microsoft.EntityFrameworkCore;
using Renamer.Execution;
using Renamer.Options;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Concurrency;

[Collection(SubstDriveScope.CollectionName)]
public sealed class ParallelBatchTests
{
    // Writes each source to disk unless skip names its index.
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

    // The bound that makes this execution path its own rather than the host's batch helper. Cove's
    // RunBatchAsync takes one worker count over a flat list; a cross-volume copy has to be bounded per
    // (source, destination) pair, because the limit protects a disk's queue and not the process. A
    // substitution that kept the work and lost the partitioning would still rename every file, so
    // nothing but a concurrency observation can catch it.
    //
    // Observed at the in-flight free-space probe, which each acting unit calls exactly once before its
    // copy: the probe is this run's own injected delegate, so the count is of units the executor
    // actually had in flight.
    [Fact]
    public async Task CrossVolumeCopies_RunNoMoreThanTheConfiguredPerPairConcurrency()
    {
        Assert.SkipUnless(SecondVolume.IsAvailable, SecondVolume.UnavailableReason);

        const int files = 6;
        const int allowed = 2;

        using var dir = new TempDir();
        using var drive = new SecondVolume();
        var shared = await SharedCacheSqlite.CreateAsync();
        try
        {
            string srcPathFwd = dir.Root.Replace('\\', '/');
            string destRootFwd = drive.Root.Replace('\\', '/');
            var ids = await SeedVideosAsync(shared, dir, files);

            var options = new RenamerOptions
            {
                FilenameTemplate = "$title",
                FolderTemplate = "Films",
                CrossVolumeConcurrency = allowed,
                FreeSpaceHeadroomBytes = 0,
                PathDestinations =
                [
                    new PathDestinationRule
                    {
                        Pattern = srcPathFwd, Dest = Dest.At(destRootFwd, "Films"), IsRegex = false,
                    },
                ],
            };
            var (ext, _) = await ExtensionHarness.CreateWithScopedContextsAsync(
                shared, options, [srcPathFwd, destRootFwd]);

            // A rendezvous of exactly `allowed` participants rather than a wait: units are held until
            // that many are in flight together, so the overlap is forced rather than hoped for, and a
            // scheduler that happened to serialize them would time out instead of passing.
            using var rendezvous = new Barrier(allowed);
            int calls = 0;
            int live = 0;
            int peak = 0;

            long Probe(string volume)
            {
                // The first call is the single-threaded aggregate check before any unit starts. It has
                // nobody to rendezvous with, and it cannot inflate the peak.
                if (Interlocked.Increment(ref calls) == 1)
                {
                    return 1L << 40;
                }

                InterlockedMax(ref peak, Interlocked.Increment(ref live));
                rendezvous.SignalAndWait(TimeSpan.FromSeconds(30));
                Interlocked.Decrement(ref live);
                return 1L << 40;
            }

            await ext.RunRenamerBatchAsync(
                RenamerFileKind.Video, [.. ids], new FakeJobProgress(), default, Probe);

            Assert.True(
                peak <= allowed,
                $"{peak} cross-volume copies were in flight at once, with {allowed} configured over "
                    + $"{files} files - the per-pair bound is not holding");
            // The rendezvous completing is what says the bound was reached rather than merely not
            // exceeded: a run that never had two copies in flight would have timed out above.
            Assert.Equal(allowed, peak);
        }
        finally
        {
            await shared.DisposeAsync();
        }
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen = Volatile.Read(ref target);
        while (value > seen)
        {
            int actual = Interlocked.CompareExchange(ref target, value, seen);
            if (actual == seen)
            {
                return;
            }

            seen = actual;
        }
    }
}
