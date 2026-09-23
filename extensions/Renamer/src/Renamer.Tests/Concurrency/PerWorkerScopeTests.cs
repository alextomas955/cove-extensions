using System.Collections.Concurrent;
using Cove.Core.Events;
using Cove.Plugins;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Renamer.Tests.Execution;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Concurrency;

public sealed class PerWorkerScopeTests
{
    // Registers the base DbContext scoped so each CreateAsyncScope() yields a fresh, distinct
    // CoveContext (its own connection to the shared database), recording every constructed context
    // into constructed. The recorded references prove per-worker isolation while the workers still
    // observe one coherent DB.
    private static ServiceProvider BuildScopedProvider(
        SharedCacheSqlite shared, IEventBus bus, ConcurrentBag<DbContext> constructed)
    {
        var services = new ServiceCollection();
        services.AddScoped<DbContext>(_ =>
        {
            var ctx = shared.NewContext();
            constructed.Add(ctx);
            return ctx;
        });
        services.AddSingleton(bus);
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task PerWorkerScope_EachWorkerResolvesDistinctDbContextInstance()
    {
        using var dir = new TempDir();
        await using var shared = await SharedCacheSqlite.CreateAsync();
        await using var seedDb = shared.NewContext();

        // Seed N same-volume videos in one folder so the execution pass fans out N parallel workers under the
        // same-volume group, bounded by SameVolumeConcurrency.
        const int n = 6;
        string folderPath = dir.Root.Replace('\\', '/');
        var (folderId, firstVideo, _) =
            await ExecutorTestSeed.SeedVideoAsync(seedDb, folderPath, "raw 0.mkv", "Film 0");
        var ids = new List<int> { firstVideo };
        File.WriteAllText(Path.Combine(dir.Root, "raw 0.mkv"), "bytes-0");
        for (int i = 1; i < n; i++)
        {
            var video = new Cove.Core.Entities.Video { Title = $"Film {i}", Organized = true };
            seedDb.Set<Cove.Core.Entities.Video>().Add(video);
            await seedDb.SaveChangesAsync();
            await ExecutorTestSeed.SeedAdditionalFileAsync(seedDb, folderId, video.Id, $"raw {i}.mkv");
            ids.Add(video.Id);
            File.WriteAllText(Path.Combine(dir.Root, $"raw {i}.mkv"), $"bytes-{i}");
        }

        var constructed = new ConcurrentBag<DbContext>();
        var bus = new CapturingEventBus();
        var provider = BuildScopedProvider(shared, bus, constructed);

        var ext = RenamerFixture.Create();
        var store = new ConcurrentFakeStore();
        await new global::Renamer.Options.OptionsStore(store)
            .SaveAsync(new global::Renamer.Options.RenamerOptions { FilenameTemplate = "$title" });
        ((IStatefulExtension)ext).SetStore(store);
        await ext.InitializeAsync(provider);

        var progress = new FakeJobProgress();
        await ext.RunRenamerBatchAsync(RenamerFileKind.Video, ids, progress, default);

        // Structural proof: the planning pass opens one read scope; the execution pass opens one scope per acting unit.
        // Every resolve builds a new context, so what can fail is the count: fewer contexts than
        // workers means workers shared one.
        var distinct = new HashSet<DbContext>(constructed, ReferenceEqualityComparer.Instance);
        Assert.True(distinct.Count >= n + 1,
            $"expected at least {n + 1} distinct contexts (1 read scope + {n} workers), got {distinct.Count}");

        // The run still succeeded end-to-end (the isolation did not break the batch).
        Assert.Equal(1d, progress.LastPercent);
        for (int i = 0; i < n; i++)
        {
            Assert.True(File.Exists(Path.Combine(dir.Root, $"Film {i}.mkv")));
        }
    }
}
