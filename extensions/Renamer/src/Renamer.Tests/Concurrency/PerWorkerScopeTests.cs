using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Renamer.Options;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Concurrency;

public sealed class PerWorkerScopeTests
{
    // Scopes a chunk opens whatever it renames: the journal, the planning read, the source-path claim
    // count and the destination-folder pass.
    private const int FixedChunkScopes = 4;

    [Fact]
    public async Task PerWorkerScope_EachWorkerResolvesDistinctDbContextInstance()
    {
        using var dir = new TempDir();
        await using var shared = await SharedCacheSqlite.CreateAsync();

        const int n = 6;
        string folderPath = dir.Root.Replace('\\', '/');
        IReadOnlyList<int> ids;
        await using (var seedDb = shared.NewContext())
        {
            ids = await ExecutorTestSeed.SeedVideosAsync(seedDb, n, i => (folderPath, $"raw {i}.mkv", $"Film {i}"));
        }
        for (int i = 0; i < n; i++)
        {
            File.WriteAllText(Path.Combine(dir.Root, $"raw {i}.mkv"), $"bytes-{i}");
        }

        var constructed = new ConcurrentBag<DbContext>();
        var (ext, _) = await ExtensionHarness.CreateWithScopedContextsAsync(
            shared, new RenamerOptions { FilenameTemplate = "$title" }, onContextCreated: constructed.Add);

        // The load opens scopes of its own, which would pad the count below.
        constructed.Clear();

        var progress = new FakeJobProgress();
        await ext.RunRenamerBatchAsync(RenamerFileKind.Video, ids, progress, default);

        var distinct = new HashSet<DbContext>(constructed, ReferenceEqualityComparer.Instance);
        Assert.True(distinct.Count >= FixedChunkScopes + n,
            $"expected a context per worker on top of the chunk's {FixedChunkScopes}, got {distinct.Count}");

        Assert.Equal(1d, progress.LastPercent);
        for (int i = 0; i < n; i++)
        {
            Assert.True(File.Exists(Path.Combine(dir.Root, $"Film {i}.mkv")));
        }
    }
}
