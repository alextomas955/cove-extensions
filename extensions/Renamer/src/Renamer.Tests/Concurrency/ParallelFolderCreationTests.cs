using Cove.Core.Entities;
using Cove.Core.Events;
using Cove.Plugins;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Renamer.Execution;
using Renamer.Options;
using Renamer.Tests.Execution;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Concurrency;

/// <summary>
/// Regression locks for the parallel batch. Concurrent-folder lock: many parallel workers
/// routing multiple items to the same not-yet-created destination folder must end with exactly one
/// <see cref="Folder"/> row for that path - never a duplicate row (silent disk/DB divergence) and
/// never an unhandled throw. The fix pre-creates every distinct destination folder once in the
/// sequential planning pass and hands the resolved id to each worker, so the parallel execution pass never does a
/// check-then-act create on a shared <see cref="Folder"/> row. Duplicate-path lock: a duplicate <c>OldFullPath</c>
/// across acting units must not make the execution pass's lookup throw and abort the whole batch after the
/// journal batch is open.
/// </summary>
public sealed class ParallelFolderCreationTests
{
    private static async Task<(global::Renamer.Renamer ext, ConcurrentFakeStore store, CapturingEventBus bus)>
        BuildAsync(SharedCacheSqlite shared, RenamerOptions options, params string[] libraryPaths)
    {
        var services = new ServiceCollection();
        services.AddScoped<DbContext>(_ => shared.NewContext());
        services.AddLibraryPaths(libraryPaths);
        var bus = new CapturingEventBus();
        services.AddSingleton<IEventBus>(bus);
        var provider = services.BuildServiceProvider();

        var ext = RenamerFixture.Create();
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
            // A not-yet-created destination folder under the same volume, shared by every routed item.
            // Same volume => the partition runs the workers unbounded, maximizing the create race window.
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

            // Route every item from the one source folder into the same new "sorted" subfolder under the
            // (allowed) temp root, via an exact source-path rule + a constant folder template. Every
            // acting item therefore has the identical TargetFolderPath = "<root>/sorted", which does not
            // yet exist in the DB - the exact duplicate-folder race trigger.
            var options = new RenamerOptions
            {
                FilenameTemplate = "$title",
                FolderTemplate = "sorted",
                PathDestinations =
                [
                    new PathDestinationRule
                    {
                        Pattern = sourceFolderFwd, Dest = Dest.At(destRootFwd, "sorted"), IsRegex = false,
                    },
                ],
            };
            var (ext, _, _) = await BuildAsync(shared, options, destRootFwd);
            var progress = new FakeJobProgress();

            await ext.RunRenamerBatchAsync(RenamerFileKind.Video, ids, progress, default);

            // exactly one Folder row for the shared destination path - no duplicate rows from a racing
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

            // The journal recorded one row per moved file under one batch (no torn/lost append).
            await using var readDb = shared.NewContext();
            using var journal = new CoveRevertJournal(readDb);
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

            await ext.RunRenamerBatchAsync(RenamerFileKind.Video, [videoId], progress, default);

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
            // The same entity id listed twice in one batch (a caller / host re-enqueue passing a
            // duplicate id - Decode does not dedupe) plans the same file twice, producing two acting
            // units with the identical OldFullPath. The execution pass builds its move→unit lookup with
            // ToDictionary, which throws ArgumentException on the duplicate key and aborts the whole
            // batch after the journal batch was opened (violating classify-not-throw and masking the
            // prior undoable batch from /undo). The defensive group/keep-first build must tolerate the
            // duplicate: the batch must complete (final 1.0) with no unhandled throw.
            string folderFwd = dir.Root.Replace('\\', '/');
            await using var seedDb = shared.NewContext();
            var (_, videoId, _) = await ExecutorTestSeed.SeedVideoAsync(seedDb, folderFwd, "raw.mkv", "My Film");
            File.WriteAllText(Path.Combine(dir.Root, "raw.mkv"), "bytes");

            var (ext, _, _) = await BuildAsync(shared, new RenamerOptions { FilenameTemplate = "$title" });
            var progress = new FakeJobProgress();

            // The duplicate id => two acting units with the same OldFullPath. Must not throw; completes.
            await ext.RunRenamerBatchAsync(RenamerFileKind.Video, [videoId, videoId], progress, default);

            Assert.Equal(1d, progress.LastPercent);
            Assert.True(File.Exists(Path.Combine(dir.Root, "My Film.mkv")));
        }
        finally
        {
            await shared.DisposeAsync();
        }
    }
}
