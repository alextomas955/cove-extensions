using Cove.Core.Events;
using Cove.Plugins;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Renamer.Execution;
using Renamer.Jobs;
using Renamer.Options;
using Renamer.Tests.Execution;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Concurrency;

/// <summary>
/// Parallel-batch correctness under the two-phase rewrite (SPACE-04). Proves: every acting item
/// renames and the shared RevertLog blob holds exactly one well-formed row per success (no torn/lost
/// append under real parallel workers); a per-item fault is an isolated skip while the rest succeed
/// and the batch still reports the final <c>1.0</c> (classify-not-throw under parallelism); a
/// same-volume-only batch runs despite a tiny free-space probe (same-volume is excluded from the
/// free-space sum); and an in-flight free-space drop skips a cross-volume item gracefully. Cove
/// disables EF thread-safety checks, so every assertion is on observable outcomes (files, DB rows,
/// the parsed RevertLog blob) — never on an EF exception. The store is a thread-safe
/// <see cref="ConcurrentFakeStore"/> so it is not a confounder.
/// </summary>
[Trait("Tier", "Integration")]
public sealed class ParallelBatchTests
{
    /// <summary>Wires the extension over a SCOPED DbContext factory so each worker gets its OWN context over the shared DB.</summary>
    private static async Task<(global::Renamer.Renamer ext, ConcurrentFakeStore store, CapturingEventBus bus)>
        BuildAsync(SharedCacheSqlite shared, RenamerOptions options)
    {
        var services = new ServiceCollection();
        services.AddScoped<DbContext>(_ => shared.NewContext());
        var bus = new CapturingEventBus();
        services.AddSingleton<IEventBus>(bus);
        var provider = services.BuildServiceProvider();

        var ext = new global::Renamer.Renamer();
        var store = new ConcurrentFakeStore();
        await new OptionsStore(store).SaveAsync(options);
        ((IStatefulExtension)ext).SetStore(store);
        await ext.InitializeAsync(provider);
        return (ext, store, bus);
    }

    [Fact]
    public async Task ParallelBatch_AllItemsRenamed_RevertLogRowsEqualSuccesses()
    {
        using var dir = new TempDir();
        var shared = await SharedCacheSqlite.CreateAsync();
        try
        {
            const int k = 8;
            string folderPath = dir.Root.Replace('\\', '/');
            await using var seedDb = shared.NewContext();

            var (folderId, firstVideo, _) =
                await ExecutorTestSeed.SeedVideoAsync(seedDb, folderPath, "raw 0.mkv", "Film 0");
            var ids = new List<int> { firstVideo };
            File.WriteAllText(Path.Combine(dir.Root, "raw 0.mkv"), "bytes-0");
            for (int i = 1; i < k; i++)
            {
                var video = new Cove.Core.Entities.Video { Title = $"Film {i}", Organized = true };
                seedDb.Set<Cove.Core.Entities.Video>().Add(video);
                await seedDb.SaveChangesAsync();
                await ExecutorTestSeed.SeedAdditionalFileAsync(seedDb, folderId, video.Id, $"raw {i}.mkv");
                ids.Add(video.Id);
                File.WriteAllText(Path.Combine(dir.Root, $"raw {i}.mkv"), $"bytes-{i}");
            }

            var (ext, store, _) = await BuildAsync(shared, new RenamerOptions { FilenameTemplate = "$title" });
            var progress = new FakeJobProgress();

            await ext.RunRenamerBatchAsync(RenamerJob.Encode("video", ids), progress, default);

            // All K renamed on disk.
            for (int i = 0; i < k; i++)
            {
                Assert.True(File.Exists(Path.Combine(dir.Root, $"Film {i}.mkv")), $"Film {i}.mkv missing");
                Assert.False(File.Exists(Path.Combine(dir.Root, $"raw {i}.mkv")), $"raw {i}.mkv lingered");
            }

            // The shared RevertLog blob (read fresh from the store) holds exactly K well-formed rows.
            var log = new RevertLog(store);
            var batch = await log.ReadLastOpenBatchAsync();
            Assert.NotNull(batch);
            Assert.Equal(k, batch!.Entries.Count);
            Assert.Equal(k, batch.Entries.Select(e => e.FileId).Distinct().Count());
            Assert.All(batch.Entries, e =>
            {
                Assert.NotEqual(0, e.FileId);
                Assert.False(string.IsNullOrEmpty(e.OldPath));
                Assert.False(string.IsNullOrEmpty(e.NewPath));
            });

            Assert.Equal(1d, progress.LastPercent);
        }
        finally
        {
            await shared.DisposeAsync();
        }
    }

    [Fact]
    public async Task ParallelBatch_OneItemFaults_OthersSucceed_BatchCompletes()
    {
        using var dir = new TempDir();
        var shared = await SharedCacheSqlite.CreateAsync();
        try
        {
            const int k = 6;
            const int faultIndex = 3; // this id's on-disk source is intentionally absent.
            string folderPath = dir.Root.Replace('\\', '/');
            await using var seedDb = shared.NewContext();

            var (folderId, firstVideo, _) =
                await ExecutorTestSeed.SeedVideoAsync(seedDb, folderPath, "raw 0.mkv", "Film 0");
            var ids = new List<int> { firstVideo };
            File.WriteAllText(Path.Combine(dir.Root, "raw 0.mkv"), "bytes-0");
            for (int i = 1; i < k; i++)
            {
                var video = new Cove.Core.Entities.Video { Title = $"Film {i}", Organized = true };
                seedDb.Set<Cove.Core.Entities.Video>().Add(video);
                await seedDb.SaveChangesAsync();
                await ExecutorTestSeed.SeedAdditionalFileAsync(seedDb, folderId, video.Id, $"raw {i}.mkv");
                ids.Add(video.Id);
                // Write the on-disk source for every id EXCEPT the fault one — its move will fail (no
                // source to move) and the executor classifies it as a skip without throwing.
                if (i != faultIndex)
                {
                    File.WriteAllText(Path.Combine(dir.Root, $"raw {i}.mkv"), $"bytes-{i}");
                }
            }

            var (ext, _, _) = await BuildAsync(shared, new RenamerOptions { FilenameTemplate = "$title" });
            var progress = new FakeJobProgress();

            await ext.RunRenamerBatchAsync(RenamerJob.Encode("video", ids), progress, default);

            // Every item whose source existed renamed; the faulting item did NOT (its target was never
            // created) and the batch still finished at 1.0 — one bad item never aborts the run.
            for (int i = 0; i < k; i++)
            {
                if (i == faultIndex)
                {
                    Assert.False(File.Exists(Path.Combine(dir.Root, $"Film {i}.mkv")),
                        "the faulting item must not have produced a renamed file");
                }
                else
                {
                    Assert.True(File.Exists(Path.Combine(dir.Root, $"Film {i}.mkv")), $"Film {i}.mkv missing");
                }
            }

            Assert.Equal(1d, progress.LastPercent);
        }
        finally
        {
            await shared.DisposeAsync();
        }
    }

    [Fact]
    public async Task SameVolumeBatch_NotThrottled_AndExcludedFromFreeSpace()
    {
        using var dir = new TempDir();
        var shared = await SharedCacheSqlite.CreateAsync();
        try
        {
            const int k = 5;
            string folderPath = dir.Root.Replace('\\', '/');
            await using var seedDb = shared.NewContext();

            var (folderId, firstVideo, _) =
                await ExecutorTestSeed.SeedVideoAsync(seedDb, folderPath, "raw 0.mkv", "Film 0");
            var ids = new List<int> { firstVideo };
            File.WriteAllText(Path.Combine(dir.Root, "raw 0.mkv"), "bytes-0");
            for (int i = 1; i < k; i++)
            {
                var video = new Cove.Core.Entities.Video { Title = $"Film {i}", Organized = true };
                seedDb.Set<Cove.Core.Entities.Video>().Add(video);
                await seedDb.SaveChangesAsync();
                await ExecutorTestSeed.SeedAdditionalFileAsync(seedDb, folderId, video.Id, $"raw {i}.mkv");
                ids.Add(video.Id);
                File.WriteAllText(Path.Combine(dir.Root, $"raw {i}.mkv"), $"bytes-{i}");
            }

            // CrossVolumeConcurrency = 1 would throttle a cross-volume group, but same-volume runs under
            // the unthrottled group regardless; the TINY probe (1 byte free everywhere) must NOT refuse
            // the batch because same-volume moves are excluded from the free-space sum (P8).
            var (ext, _, _) = await BuildAsync(shared,
                new RenamerOptions { FilenameTemplate = "$title", CrossVolumeConcurrency = 1 });
            var progress = new FakeJobProgress();

            await ext.RunRenamerBatchAsync(RenamerJob.Encode("video", ids), progress, default,
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
        if (!OperatingSystem.IsWindows())
        {
            return; // subst gives a distinct path root (NOT a real second drive) on Windows only.
        }

        using var dir = new TempDir();
        using var drive = new SubstDrive(); // a distinct path root that backs the same physical volume.
        var shared = await SharedCacheSqlite.CreateAsync();
        try
        {
            string srcFolder = Path.Combine(dir.Root, "incoming");
            Directory.CreateDirectory(srcFolder);
            string srcPathFwd = srcFolder.Replace('\\', '/');
            string destRootFwd = drive.Root.Replace('\\', '/'); // e.g. "P:/"

            await using var seedDb = shared.NewContext();
            var (_, videoId, _) = await ExecutorTestSeed.SeedVideoAsync(seedDb, srcPathFwd, "raw.mkv", "My Film");
            File.WriteAllText(Path.Combine(srcFolder, "raw.mkv"), "bytes");

            // Route the item across volumes (src on the temp drive → dest on the subst drive root), so
            // the partition classifies it cross-volume and the worker runs the in-flight Shortfall.
            var options = new RenamerOptions
            {
                FilenameTemplate = "$title",
                FolderTemplate = "Films",
                AllowedRoots = [srcPathFwd, destRootFwd],
                PathDestinations =
                    [new PathDestinationRule { Pattern = srcPathFwd, Dest = destRootFwd, IsRegex = false }],
                FreeSpaceHeadroomBytes = 0,
            };
            var (ext, _, _) = await BuildAsync(shared, options);

            // Stateful TOCTOU probe: the FIRST reading (PHASE A up-front check) reports ample free space
            // so the batch is accepted; the SECOND reading (PHASE B in-flight re-check, just before the
            // copy) reports near-zero, modelling a concurrent scanner that filled the destination. The
            // cross-volume item must then be skipped gracefully — never thrown, batch still completes.
            int calls = 0;
            long Probe(string vol) => Interlocked.Increment(ref calls) == 1 ? 1L << 40 : 1L;

            var progress = new FakeJobProgress();
            await ext.RunRenamerBatchAsync(RenamerJob.Encode("video", [videoId]), progress, default, Probe);

            // The in-flight drop skipped the move: the file stayed at its source and never landed on the
            // routed destination. The batch finished cleanly (no throw, final 1.0).
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
