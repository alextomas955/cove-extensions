using Cove.Data;
using Cove.Plugins;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests;

// What a load does to the legacy per-file scan value an early build wrote to the store.
public sealed class InitializeAsyncTests
{
    // The catch that keeps a load completing when the legacy value cannot be deleted.
    private const int LegacyScanPurgeFailedEvent = 1053;

    private sealed class ThrowingDeleteStore : IExtensionStore
    {
        public Task<string?> GetAsync(string key, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task SetAsync(string key, string value, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteAsync(string key, CancellationToken ct = default)
            => throw new InvalidOperationException("store unavailable");
        public Task<Dictionary<string, string>> GetAllAsync(CancellationToken ct = default)
            => Task.FromResult(new Dictionary<string, string>());
    }

    private static async Task InitializeWithStoreAsync(IExtensionStore store)
    {
        // A database carrying the journal and nothing else: the extension refuses to load without a
        // readable journal, and these tests are about what load does to the store.
        await using var journalDb = await JournalOnlyDatabase.CreateAsync();
        var ext = RenamerFixture.Create();
        ((IStatefulExtension)ext).SetStore(store);
        await ext.InitializeAsync(journalDb.BuildProvider());
    }

    [Fact]
    public async Task InitializeAsync_WithALegacyScanValue_DeletesIt_WithoutEverReadingIt()
    {
        var store = new FakeStore();
        await store.SetAsync(global::Renamer.Renamer.LastScanResultKey, "[a legacy per-file array]");
        store.GetKeys.Clear();

        await InitializeWithStoreAsync(store);

        // Reading the value to decide whether to delete it is the one operation guaranteed to hurt: the
        // host's bulk read already fails on it, and its own delete materializes the row it removes.
        Assert.DoesNotContain(global::Renamer.Renamer.LastScanResultKey, store.GetKeys);
        Assert.Null(await store.GetAsync(global::Renamer.Renamer.LastScanResultKey));
    }

    [Fact]
    public async Task InitializeAsync_WithNoLegacyScanValue_CompletesAndWritesNothing()
    {
        var store = new FakeStore();
        int setsBefore = store.SetCallCount;

        await InitializeWithStoreAsync(store);

        Assert.Equal(setsBefore, store.SetCallCount);
        Assert.Null(await store.GetAsync(global::Renamer.Renamer.LastScanResultKey));
    }

    [Fact]
    public async Task InitializeAsync_LeavesAPreExistingScanSummaryUntouched()
    {
        var store = new FakeStore();
        await store.SetAsync(global::Renamer.Renamer.LastScanSummaryKey, "{\"schemaVersion\":1}");
        await store.SetAsync(global::Renamer.Renamer.LastScanResultKey, "[legacy]");

        await InitializeWithStoreAsync(store);

        Assert.Equal("{\"schemaVersion\":1}", await store.GetAsync(global::Renamer.Renamer.LastScanSummaryKey));
        Assert.Null(await store.GetAsync(global::Renamer.Renamer.LastScanResultKey));
    }

    [Fact]
    public async Task InitializeAsync_WhenTheDeleteThrows_CompletesAndLogsTheFailureOnce()
    {
        // A load that refuses to finish because the cleanup failed leaves the user strictly worse off.
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        await db.DisposeAsync();
        try
        {
            var log = new CapturingLogger<global::Renamer.Renamer>();
            var services = new ServiceCollection();
            services.AddScoped<DbContext>(_ => new CoveContext(
                new DbContextOptionsBuilder<CoveContext>().UseSqlite(conn).Options, principalAccessor: null));
            services.AddSingleton<Cove.Core.Events.IEventBus>(new CapturingEventBus());
            services.AddSingleton<ILogger<global::Renamer.Renamer>>(log);

            var ext = RenamerFixture.Create();
            ((IStatefulExtension)ext).SetStore(new ThrowingDeleteStore());
            await ext.InitializeAsync(services.BuildServiceProvider());

            Assert.Single(log.Entries, e => e.EventId == LegacyScanPurgeFailedEvent && e.Error is InvalidOperationException);
        }
        finally
        {
            await conn.DisposeAsync();
        }
    }
}
