using Cove.Core.Entities;
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
/// CR-01 / CR-02 regression locks for the two-phase parallel batch. CR-01: many parallel workers
/// routing MULTIPLE items to the SAME not-yet-created destination folder must end with EXACTLY ONE
/// <see cref="Folder"/> row for that path — never a duplicate row (silent disk/DB divergence) and
/// never an unhandled throw. The fix pre-creates every distinct destination folder ONCE in the
/// sequential PHASE A and hands the resolved id to each worker, so the parallel PHASE B never does a
/// check-then-act create on a shared <see cref="Folder"/> row. CR-02: a duplicate <c>OldFullPath</c>
/// across acting units must not make the PHASE B lookup throw and abort the whole batch after the
/// RevertLog header is open.
/// </summary>
[Trait("Tier", "Integration")]
public sealed class ParallelFolderCreationTests
{
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
    public async Task ParallelBatch_ManyItemsToSameNewFolder_CreatesExactlyOneFolderRow()
    {
        using var dir = new TempDir();
        var shared = await SharedCacheSqlite.CreateAsync();
        try
        {
            const int k = 12;
            string sourceFolderFwd = dir.Root.Replace('\\', '/');
            // A NOT-YET-CREATED destination folder under the same volume, shared by EVERY routed item.
            // Same volume => the partition runs the workers UNBOUNDED, maximizing the create race window.
            string destRootFwd = sourceFolderFwd;
            string destFolderFwd = destRootFwd + "/sorted";

            await using var seedDb = shared.NewContext();
            var (folderId, firstVideo, _) =
                await ExecutorTestSeed.SeedVideoAsync(seedDb, sourceFolderFwd, "raw 0.mkv", "Film 0");
            var ids = new List<int> { firstVideo };
            File.WriteAllText(Path.Combine(dir.Root, "raw 0.mkv"), "bytes-0");
            for (int i = 1; i < k; i++)
            {
                var video = new Video { Title = $"Film {i}", Organized = true };
                seedDb.Set<Video>().Add(video);
                await seedDb.SaveChangesAsync();
                await ExecutorTestSeed.SeedAdditionalFileAsync(seedDb, folderId, video.Id, $"raw {i}.mkv");
                ids.Add(video.Id);
                File.WriteAllText(Path.Combine(dir.Root, $"raw {i}.mkv"), $"bytes-{i}");
            }

            // Route every item from the one source folder into the SAME new "sorted" subfolder under the
            // (allowed) temp root, via an exact source-path rule + a constant folder template. Every
            // acting item therefore has the identical TargetFolderPath = "<root>/sorted", which does not
            // yet exist in the DB — the exact CR-01 trigger.
            var options = new RenamerOptions
            {
                FilenameTemplate = "$title",
                FolderTemplate = "sorted",
                AllowedRoots = [destRootFwd],
                PathDestinations =
                    [new PathDestinationRule { Pattern = sourceFolderFwd, Dest = destRootFwd, IsRegex = false }],
            };
            var (ext, store, _) = await BuildAsync(shared, options);
            var progress = new FakeJobProgress();

            await ext.RunRenamerBatchAsync(RenamerJob.Encode("video", ids), progress, default);

            // EXACTLY ONE Folder row for the shared destination path — no duplicate rows from a racing
            // check-then-act create across parallel workers.
            await using var verifyDb = shared.NewContext();
            int folderRows = await verifyDb.Set<Folder>()
                .AsNoTracking()
                .CountAsync(f => f.Path == destFolderFwd);
            Assert.Equal(1, folderRows);

            // Every file landed in the one routed folder on disk and the batch completed.
            for (int i = 0; i < k; i++)
            {
                Assert.True(File.Exists(Path.Combine(dir.Root, "sorted", $"Film {i}.mkv")),
                    $"Film {i}.mkv missing from the routed folder");
            }
            Assert.Equal(1d, progress.LastPercent);

            // The RevertLog recorded one row per moved file under one batch (no torn/lost append).
            var batch = await new RevertLog(store).ReadLastOpenBatchAsync();
            Assert.NotNull(batch);
            Assert.Equal(k, batch!.Entries.Count);
        }
        finally
        {
            await shared.DisposeAsync();
        }
    }

    [Fact]
    public async Task InPlaceRenamer_StillWorks_NoNewDestinationFolder()
    {
        using var dir = new TempDir();
        var shared = await SharedCacheSqlite.CreateAsync();
        try
        {
            string folderFwd = dir.Root.Replace('\\', '/');
            await using var seedDb = shared.NewContext();
            await ExecutorTestSeed.SeedVideoAsync(seedDb, folderFwd, "raw.mkv", "My Film");
            File.WriteAllText(Path.Combine(dir.Root, "raw.mkv"), "bytes");
            int videoId = await seedDb.Set<Video>().AsNoTracking().Select(v => v.Id).FirstAsync();

            // No routing, no folder template => an in-place renamer (same parent folder, no new folder).
            var (ext, _, _) = await BuildAsync(shared, new RenamerOptions { FilenameTemplate = "$title" });
            var progress = new FakeJobProgress();

            await ext.RunRenamerBatchAsync(RenamerJob.Encode("video", [videoId]), progress, default);

            Assert.True(File.Exists(Path.Combine(dir.Root, "My Film.mkv")));
            Assert.False(File.Exists(Path.Combine(dir.Root, "raw.mkv")));

            // No spurious second Folder row was created for the in-place case (only the seeded one).
            await using var verifyDb = shared.NewContext();
            int folderRows = await verifyDb.Set<Folder>().AsNoTracking().CountAsync();
            Assert.Equal(1, folderRows);
            Assert.Equal(1d, progress.LastPercent);
        }
        finally
        {
            await shared.DisposeAsync();
        }
    }

    [Fact]
    public async Task DuplicateOldFullPath_DoesNotAbortBatch()
    {
        using var dir = new TempDir();
        var shared = await SharedCacheSqlite.CreateAsync();
        try
        {
            // CR-02: the same entity id listed twice in one batch (a caller / host re-enqueue passing a
            // duplicate id — Decode does NOT dedupe) plans the SAME file twice, producing two acting
            // units with the IDENTICAL OldFullPath. PHASE B used to build its move→unit lookup with
            // ToDictionary, which throws ArgumentException on the duplicate key and aborts the WHOLE
            // batch AFTER the RevertLog header was opened (violating classify-not-throw and masking the
            // prior undoable batch from /undo). The defensive group/keep-first build must tolerate the
            // duplicate: the batch must complete (final 1.0) with no unhandled throw.
            string folderFwd = dir.Root.Replace('\\', '/');
            await using var seedDb = shared.NewContext();
            var (_, videoId, _) = await ExecutorTestSeed.SeedVideoAsync(seedDb, folderFwd, "raw.mkv", "My Film");
            File.WriteAllText(Path.Combine(dir.Root, "raw.mkv"), "bytes");

            var (ext, _, _) = await BuildAsync(shared, new RenamerOptions { FilenameTemplate = "$title" });
            var progress = new FakeJobProgress();

            // The duplicate id => two acting units with the same OldFullPath. Must NOT throw; completes.
            await ext.RunRenamerBatchAsync(RenamerJob.Encode("video", [videoId, videoId]), progress, default);

            Assert.Equal(1d, progress.LastPercent);
            Assert.True(File.Exists(Path.Combine(dir.Root, "My Film.mkv")));
        }
        finally
        {
            await shared.DisposeAsync();
        }
    }
}
