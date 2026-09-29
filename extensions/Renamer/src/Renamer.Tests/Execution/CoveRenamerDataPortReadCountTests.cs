using Cove.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Renamer.Execution;
using Renamer.Planner;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Execution;

public sealed class CoveRenamerDataPortReadCountTests
{
    [Fact]
    public async Task LoadEntitiesAsync_IssuesCeilOverChunk_ReaderQueries_NotOnePerId()
    {
        // More ids than one chunk, so a per-id load and a per-chunk load give different reader counts.
        var interceptor = new CommandCountingInterceptor();
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        try
        {
            var options = new DbContextOptionsBuilder<CoveContext>()
                .UseSqlite(connection)
                .AddInterceptors(interceptor)
                .Options;
            await using var db = new CoveContext(options, principalAccessor: null);
            await db.Database.EnsureCreatedAsync();

            int n = IRenamerDataPort.LoadChunkSize + 25;
            var ids = await ExecutorTestSeed.SeedVideosAsync(db, n, k => ($"media/{k}", $"c{k}.mkv", $"C{k}"));

            var port = new CoveRenamerDataPort(db);
            interceptor.ReaderCount = default;
            var loaded = await port.LoadEntitiesAsync(RenamerFileKind.Video, ids);

            Assert.Equal(n, loaded.Count);
            int expectedChunks = (n + IRenamerDataPort.LoadChunkSize - 1) / IRenamerDataPort.LoadChunkSize;
            // The video query is a split query: one reader for the roots and one for each collection it
            // includes (files, their captions, performers, tags). That count is set by the query's shape,
            // not by the population.
            const int readersPerChunk = 5;
            Assert.True(interceptor.ReaderCount <= expectedChunks * readersPerChunk,
                $"expected ~{expectedChunks} chunk queries, got {interceptor.ReaderCount} readers for {n} ids");
            Assert.True(interceptor.ReaderCount < n,
                $"batch load must issue fewer than N={n} reader queries; got {interceptor.ReaderCount}");
        }
        finally
        {
            await connection.DisposeAsync();
        }
    }
}
