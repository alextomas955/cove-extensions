using System.Collections.Concurrent;
using Cove.Core.Events;
using Cove.Data;
using Cove.Plugins;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Renamer.Jobs;
using Renamer.Tests.Execution;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Concurrency;

/// <summary>
/// The SPACE-04 structural isolation proof. The batch's PHASE B opens a per-worker
/// <c>CreateAsyncScope()</c> so no <see cref="DbContext"/> instance is shared across parallel
/// workers. Cove disables EF's thread-safety checks (<c>EnableThreadSafetyChecks(false)</c>), so a
/// shared-context bug does NOT throw — it corrupts silently. This proof is therefore STRUCTURAL: an
/// instrumented scoped factory records every <see cref="CoveContext"/> it constructs, and the test
/// asserts the set of contexts the workers resolved has exactly one DISTINCT instance per worker (by
/// reference). It never relies on an EF exception.
/// </summary>
[Trait("Tier", "Integration")]
public sealed class PerWorkerScopeTests
{
    /// <summary>
    /// Registers the base <see cref="DbContext"/> SCOPED so each <c>CreateAsyncScope()</c> yields a
    /// fresh, distinct <see cref="CoveContext"/> (its own connection to the shared database), recording
    /// every constructed context into <paramref name="constructed"/>. The recorded references prove
    /// per-worker isolation while the workers still observe one coherent DB.
    /// </summary>
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

        // Seed N same-volume videos in ONE folder so PHASE B fans out N parallel workers under the
        // unthrottled same-volume group.
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

        var ext = new global::Renamer.Renamer();
        var store = new ConcurrentFakeStore();
        await new global::Renamer.Options.OptionsStore(store)
            .SaveAsync(new global::Renamer.Options.RenamerOptions { FilenameTemplate = "$title" });
        ((IStatefulExtension)ext).SetStore(store);
        await ext.InitializeAsync(provider);

        var progress = new FakeJobProgress();
        await ext.RunRenamerBatchAsync(RenamerJob.Encode("video", ids), progress, default);

        // STRUCTURAL proof: PHASE A opens one read scope; PHASE B opens one scope per acting unit.
        // The distinct-instance count must be at least the worker count (n acting items), and every
        // recorded context is a distinct reference — NO instance was shared across workers.
        var distinct = new HashSet<DbContext>(constructed, ReferenceEqualityComparer.Instance);
        Assert.Equal(constructed.Count, distinct.Count); // all references distinct, by reference
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
