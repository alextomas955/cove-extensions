using Cove.Core.Entities;
using Cove.Core.Events;
using Cove.Plugins;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Renamer.Execution;
using Renamer.Options;
using Renamer.Tests.Execution;
using Renamer.Tests.TestSupport;

using OperationJournalBudget = Renamer.Renamer.OperationJournalBudget;

namespace Renamer.Tests.Jobs;

/// <summary>
/// A whole-library rename walked a chunk at a time: every entity still renames, each chunk opens its
/// own batch under one operation id, and a chunk is planned against what the chunks before it did.
/// </summary>
/// <remarks>
/// Driven at a chunk size of one or two so the multi-chunk paths cost a handful of rows rather than a
/// library-sized fixture. The shared-cache SQLite database is the same one the parallel-batch tier
/// uses, because each worker resolves its own context.
/// </remarks>
[Collection(SubstDriveScope.CollectionName)]
public sealed class ChunkedRenameTests
{
    // Nothing is denied here: this suite's subject is what the chunk walk does, not who may reach it.
    private static readonly global::Renamer.AllowedIds AllowAll = (_, ids, _) => Task.FromResult(ids);

    private static async Task<global::Renamer.Renamer> BuildAsync(
        SharedCacheSqlite shared, RenamerOptions options, params string[] libraryPaths)
    {
        var services = new ServiceCollection();
        services.AddScoped<DbContext>(_ => shared.NewContext());
        services.AddLibraryPaths(libraryPaths);
        services.AddSingleton<IEventBus>(new CapturingEventBus());
        var provider = services.BuildServiceProvider();

        var ext = RenamerFixture.Create();
        var store = new ConcurrentFakeStore();
        await new OptionsStore(store).SaveAsync(options);
        ((IStatefulExtension)ext).SetStore(store);
        await ext.InitializeAsync(provider);
        return ext;
    }

    /// <summary>
    /// Seeds <paramref name="count"/> single-file videos in one folder, titled "Film i" over
    /// "raw i.mkv", with real bytes on disk. Their entity ids ascend with i, which is the order the
    /// walk pages them in.
    /// </summary>
    private static async Task SeedVideosAsync(DbContext db, string dirRoot, int count)
    {
        string folderPath = dirRoot.Replace('\\', '/');
        var (folderId, _, _) = await ExecutorTestSeed.SeedVideoAsync(db, folderPath, "raw 0.mkv", "Film 0");
        File.WriteAllText(Path.Combine(dirRoot, "raw 0.mkv"), "bytes-0");

        for (int i = 1; i < count; i++)
        {
            var video = new Video { Title = $"Film {i}", Organized = true };
            db.Set<Video>().Add(video);
            await db.SaveChangesAsync();
            await ExecutorTestSeed.SeedAdditionalFileAsync(db, folderId, video.Id, $"raw {i}.mkv");
            File.WriteAllText(Path.Combine(dirRoot, $"raw {i}.mkv"), $"bytes-{i}");
        }
    }

    [Fact]
    public async Task AKindWalkedInChunks_RenamesEveryEntity_AndOpensOneBatchPerChunkUnderOneOperation()
    {
        using var dir = new TempDir();
        var shared = await SharedCacheSqlite.CreateAsync();
        try
        {
            const int entities = 5;
            await using (var seedDb = shared.NewContext())
            {
                await SeedVideosAsync(seedDb, dir.Root, entities);
            }

            var options = new RenamerOptions { FilenameTemplate = "$title" };
            var ext = await BuildAsync(shared, options);
            var progress = new FakeJobProgress();

            await ext.RunRenamerKindAsync(
                RenamerFileKind.Video, entities, new OperationJournalBudget("op"), options, AllowAll, progress,
                default, chunkEntities: 2);

            for (int i = 0; i < entities; i++)
            {
                Assert.True(File.Exists(Path.Combine(dir.Root, $"Film {i}.mkv")), $"Film {i}.mkv missing");
                Assert.False(File.Exists(Path.Combine(dir.Root, $"raw {i}.mkv")), $"raw {i}.mkv left behind");
            }

            await using var readDb = shared.NewContext();
            var batches = await readDb.Set<RevertBatchEntity>().AsNoTracking().ToListAsync();

            // Three pages of two, two and one. Every batch belongs to the one operation, so the whole
            // walk is a single undo.
            Assert.Equal(3, batches.Count);
            Assert.All(batches, b => Assert.Equal("op", b.OperationId));
            Assert.Equal(entities, batches.Sum(b => b.OriginalCount));
            Assert.Equal(1d, progress.LastPercent);
        }
        finally
        {
            await shared.DisposeAsync();
        }
    }

    /// <summary>Cancels the run as soon as a report reaches <paramref name="threshold"/>.</summary>
    private sealed class CancelAtProgress(CancellationTokenSource cts, double threshold) : IJobProgress
    {
        public void Report(double percent, string? message = null)
        {
            if (percent >= threshold)
            {
                cts.Cancel();
            }
        }
    }

    [Fact]
    public async Task CancellationBetweenChunks_KeepsTheFinishedChunk_AndNeverRunsTheNextOne()
    {
        using var dir = new TempDir();
        var shared = await SharedCacheSqlite.CreateAsync();
        try
        {
            const int entities = 4;
            await using (var seedDb = shared.NewContext())
            {
                await SeedVideosAsync(seedDb, dir.Root, entities);
            }

            var options = new RenamerOptions { FilenameTemplate = "$title" };
            var ext = await BuildAsync(shared, options);
            using var cts = new CancellationTokenSource();

            // Half the bar is the end of the first chunk of two out of four.
            var progress = new CancelAtProgress(cts, 0.5);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ext.RunRenamerKindAsync(
                RenamerFileKind.Video, entities, new OperationJournalBudget("op"), options, AllowAll, progress,
                cts.Token, chunkEntities: 2));

            Assert.True(File.Exists(Path.Combine(dir.Root, "Film 0.mkv")));
            Assert.True(File.Exists(Path.Combine(dir.Root, "Film 1.mkv")));
            Assert.True(File.Exists(Path.Combine(dir.Root, "raw 2.mkv")), "the second chunk must not have run");
            Assert.True(File.Exists(Path.Combine(dir.Root, "raw 3.mkv")), "the second chunk must not have run");

            // What the cancelled run did finish is still undoable.
            await using var readDb = shared.NewContext();
            var batch = Assert.Single(await readDb.Set<RevertBatchEntity>().AsNoTracking().ToListAsync());
            Assert.Equal("op", batch.OperationId);
            Assert.Equal(2, batch.OriginalCount);
        }
        finally
        {
            await shared.DisposeAsync();
        }
    }

    [Fact]
    public async Task ADestinationTakenByAnEarlierChunk_IsSeenByTheNextOne()
    {
        using var dir = new TempDir();
        var shared = await SharedCacheSqlite.CreateAsync();
        try
        {
            string folderPath = dir.Root.Replace('\\', '/');
            await using (var seedDb = shared.NewContext())
            {
                // Two entities with the same title, so both plan onto the same destination basename.
                var (folderId, _, _) = await ExecutorTestSeed.SeedVideoAsync(seedDb, folderPath, "a.mkv", "Twin");
                var second = new Video { Title = "Twin", Organized = true };
                seedDb.Set<Video>().Add(second);
                await seedDb.SaveChangesAsync();
                await ExecutorTestSeed.SeedAdditionalFileAsync(seedDb, folderId, second.Id, "b.mkv");
            }

            File.WriteAllText(Path.Combine(dir.Root, "a.mkv"), "bytes-a");
            File.WriteAllText(Path.Combine(dir.Root, "b.mkv"), "bytes-b");

            var options = new RenamerOptions { FilenameTemplate = "$title" };
            var ext = await BuildAsync(shared, options);

            await ext.RunRenamerKindAsync(
                RenamerFileKind.Video, 2, new OperationJournalBudget("op"), options, AllowAll,
                new FakeJobProgress(), default, chunkEntities: 1);

            // The second chunk is planned after the first one has already taken "Twin.mkv", so it
            // suffixes rather than clobbering it, and neither source is left behind.
            Assert.True(File.Exists(Path.Combine(dir.Root, "Twin.mkv")));
            Assert.True(File.Exists(Path.Combine(dir.Root, "Twin (1).mkv")));
            Assert.False(File.Exists(Path.Combine(dir.Root, "a.mkv")));
            Assert.False(File.Exists(Path.Combine(dir.Root, "b.mkv")));
        }
        finally
        {
            await shared.DisposeAsync();
        }
    }

    [Fact]
    public async Task TwoRowsNamingOneSourcePath_AreRefusedEvenInSeparateChunks()
    {
        using var dir = new TempDir();
        var shared = await SharedCacheSqlite.CreateAsync();
        try
        {
            string root = dir.Root.Replace('\\', '/');
            Directory.CreateDirectory(Path.Combine(dir.Root, "sub"));
            File.WriteAllText(Path.Combine(dir.Root, "sub", "raw.mkv"), "bytes");

            await using (var seedDb = shared.NewContext())
            {
                // One row reaches the file through its own folder; the other reaches it through the
                // parent folder with the subfolder inside its basename. Two rows, one path on disk,
                // and the unique index on (parent folder, basename) does not see it.
                await ExecutorTestSeed.SeedVideoAsync(seedDb, $"{root}/sub", "raw.mkv", "First");
                await ExecutorTestSeed.SeedVideoAsync(seedDb, root, "sub/raw.mkv", "Second");
            }

            var options = new RenamerOptions { FilenameTemplate = "$title" };
            var ext = await BuildAsync(shared, options);
            var progress = new FakeJobProgress();

            await ext.RunRenamerKindAsync(
                RenamerFileKind.Video, 2, new OperationJournalBudget("op"), options, AllowAll, progress,
                default, chunkEntities: 1);

            Assert.True(File.Exists(Path.Combine(dir.Root, "sub", "raw.mkv")), "a contested source must not move");
            Assert.False(File.Exists(Path.Combine(dir.Root, "sub", "First.mkv")));
            Assert.False(File.Exists(Path.Combine(dir.Root, "sub", "Second.mkv")));

            await using var readDb = shared.NewContext();
            Assert.Empty(await readDb.Set<RevertBatchEntity>().AsNoTracking().ToListAsync());
            Assert.Contains("refused", progress.Reports[^1].Message, StringComparison.Ordinal);
        }
        finally
        {
            await shared.DisposeAsync();
        }
    }

    [Fact]
    public async Task AChunkThatDoesNotFit_StopsTheRun_AndLeavesTheEarlierChunkRenamedAndUndoable()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // subst gives a distinct path root (NOT a real second drive) on Windows only.
        }

        using var dir = new TempDir();
        using var drive = new SubstDrive();
        var shared = await SharedCacheSqlite.CreateAsync();
        try
        {
            string keepDir = Path.Combine(dir.Root, "keep");
            string moveDir = Path.Combine(dir.Root, "move");
            Directory.CreateDirectory(keepDir);
            Directory.CreateDirectory(moveDir);
            string keepFwd = keepDir.Replace('\\', '/');
            string moveFwd = moveDir.Replace('\\', '/');
            string destRootFwd = drive.Root.Replace('\\', '/');

            int moveFileId;
            await using (var seedDb = shared.NewContext())
            {
                await ExecutorTestSeed.SeedVideoAsync(seedDb, keepFwd, "raw-keep.mkv", "Kept");
                (_, _, moveFileId) = await ExecutorTestSeed.SeedVideoAsync(seedDb, moveFwd, "raw-move.mkv", "Moved");

                // The guard's need is the recorded size, and a seeded row defaults to zero, which no
                // probe value can ever be short of.
                var row = await seedDb.Set<VideoFile>().FirstAsync(f => f.Id == moveFileId);
                row.Size = 4096;
                await seedDb.SaveChangesAsync();
            }

            File.WriteAllText(Path.Combine(keepDir, "raw-keep.mkv"), "bytes");
            File.WriteAllText(Path.Combine(moveDir, "raw-move.mkv"), "bytes");

            var options = new RenamerOptions
            {
                FilenameTemplate = "$title",
                PathDestinations =
                [
                    new PathDestinationRule
                    {
                        Pattern = moveFwd, Dest = Dest.At(destRootFwd, "Films"), IsRegex = false,
                    },
                ],
                FreeSpaceHeadroomBytes = 0,
            };
            var ext = await BuildAsync(shared, options, keepFwd, moveFwd, destRootFwd);
            var progress = new FakeJobProgress();

            // Only the subst root is short, so the first chunk's in-place rename is unaffected and the
            // second chunk's cross-volume move cannot fit.
            long Probe(string vol) => vol.Length > 0 && vol[0] == drive.Root[0] ? 1L : 1L << 40;

            await ext.RunRenamerKindAsync(
                RenamerFileKind.Video, 2, new OperationJournalBudget("op"), options, AllowAll, progress,
                default, Probe, chunkEntities: 1);

            Assert.True(File.Exists(Path.Combine(keepDir, "Kept.mkv")), "the first chunk must stay renamed");
            Assert.True(File.Exists(Path.Combine(moveDir, "raw-move.mkv")), "a refused chunk must not move");
            Assert.False(File.Exists(Path.Combine(drive.Root, "Films", "Moved.mkv")));

            await using var readDb = shared.NewContext();
            var batch = Assert.Single(await readDb.Set<RevertBatchEntity>().AsNoTracking().ToListAsync());
            Assert.Equal(1, batch.OriginalCount);

            Assert.Equal(1d, progress.LastPercent);
            Assert.Contains("insufficient free space", progress.Reports[^1].Message, StringComparison.Ordinal);
        }
        finally
        {
            await shared.DisposeAsync();
        }
    }
}
