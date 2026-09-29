using System.Data.Common;
using Cove.Core.Events;
using Cove.Data;
using Cove.Plugins;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Renamer.Options;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Events;

public sealed class AutoRenamerFailureContainmentTests
{
    // The auto-renamer's own error event, raised when the hook swallows an exception.
    private const int AutoRenamerErrorEventId = 1022;

    private sealed class ThrowingStore : IExtensionStore
    {
        public Task<string?> GetAsync(string key, CancellationToken ct = default)
            => throw new InvalidOperationException("store unavailable");

        public Task SetAsync(string key, string value, CancellationToken ct = default)
            => throw new InvalidOperationException("store unavailable");

        public Task DeleteAsync(string key, CancellationToken ct = default)
            => throw new InvalidOperationException("store unavailable");

        public Task<Dictionary<string, string>> GetAllAsync(CancellationToken ct = default)
            => throw new InvalidOperationException("store unavailable");
    }

    [Fact]
    public async Task InnerPathThrows_HandlerCatches_LogsOnce_DoesNotPropagateToHost()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            var log = new CapturingLogger<global::Renamer.Renamer>();
            var services = new ServiceCollection();
            services.AddSingleton<DbContext>(db);
            services.AddSingleton<IEventBus>(new CapturingEventBus());
            services.AddSingleton<ILogger<global::Renamer.Renamer>>(log);
            var provider = services.BuildServiceProvider();

            var ext = RenamerFixture.Create();
            ((IStatefulExtension)ext).SetStore(new ThrowingStore());
            await ext.InitializeAsync(provider);

            // The load's own store failures are logged too, and are not what this case observes.
            log.Entries.Clear();

            // The inner option load throws. The host's dispatch loop would be handed an exception
            // naming no item, so the handler records the entity and swallows it.
            var thrown = await Record.ExceptionAsync(
                () => ext.OnEventAsync(new ExtensionEvent("video.updated", "video", 1), default));
            Assert.Null(thrown);

            var entry = Assert.Single(log.Entries);
            Assert.Equal((LogLevel.Error, AutoRenamerErrorEventId), (entry.Level, entry.EventId));
            Assert.Equal("store unavailable", entry.Error?.Message);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    // Cancels the hook's token on the first command after the undo batch is opened, which is the first
    // read the executor makes. By then the hook has claimed the entity's next update event as its own.
    private sealed class CancelAfterBatchOpens(CancellationTokenSource cts) : DbCommandInterceptor
    {
        private bool _opened;

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (_opened)
            {
                cts.Cancel();
            }

            if (command.CommandText.Contains("INSERT INTO \"renamer_revert_batches\"", StringComparison.Ordinal))
            {
                _opened = true;
            }

            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    [Fact]
    public async Task CancelledMidRename_Rethrows_LogsNoFailure_AndTheNextEditIsHonoured()
    {
        using var dir = new TempDir();
        using var cts = new CancellationTokenSource();
        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        await using var db = new CoveContext(
            new DbContextOptionsBuilder<CoveContext>()
                .UseSqlite(conn)
                .ReplaceService<IModelCacheKeyFactory, CoveModelCacheKeyFactory>()
                .AddInterceptors(new CancelAfterBatchOpens(cts))
                .Options,
            principalAccessor: null);
        await db.Database.EnsureCreatedAsync();

        string folderPath = dir.Root.Replace('\\', '/');
        var (_, videoId, fileId) = await ExecutorTestSeed.SeedVideoAsync(db, folderPath, "raw.mkv", "My Film");
        File.WriteAllText(Path.Combine(dir.Root, "raw.mkv"), "bytes");

        var log = new CapturingLogger<global::Renamer.Renamer>();
        var services = new ServiceCollection();
        services.AddSingleton<DbContext>(db);
        services.AddSingleton<IEventBus>(new CapturingEventBus());
        services.AddSingleton<ILogger<global::Renamer.Renamer>>(log);
        var store = new FakeStore();
        await new OptionsStore(store).SaveAsync(
            new RenamerOptions { AutoRenamerOnUpdate = true, FilenameTemplate = "$title" });
        var ext = RenamerFixture.Create();
        ((IStatefulExtension)ext).SetStore(store);
        await ext.InitializeAsync(services.BuildServiceProvider());

        // Host shutdown reaches the hook as a cancelled token, which is not a failed rename.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ext.OnEventAsync(new ExtensionEvent("video.updated", "video", videoId), cts.Token));

        Assert.True(cts.IsCancellationRequested);
        Assert.True(File.Exists(Path.Combine(dir.Root, "raw.mkv")));
        Assert.DoesNotContain(log.Entries, e => e.EventId == AutoRenamerErrorEventId);

        // A claim left behind would swallow this genuine edit as the hook's own save.
        await ext.OnEventAsync(new ExtensionEvent("video.updated", "video", videoId), default);

        Assert.True(File.Exists(Path.Combine(dir.Root, "My Film.mkv")),
            "the edit after a cancelled run was swallowed - the cancelled run left its claim behind");
        var (basename, _) = await ExecutorTestSeed.ReadFileAsync(db, fileId);
        Assert.Equal("My Film.mkv", basename);
    }
}
