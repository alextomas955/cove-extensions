using Cove.Core.Events;
using Cove.Plugins;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Events;

public sealed class AutoRenamerFailureContainmentTests
{
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
    public async Task InnerPathThrows_HandlerCatches_DoesNotPropagateToHost()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            var services = new ServiceCollection();
            services.AddSingleton<DbContext>(db);
            services.AddSingleton<IEventBus>(new CapturingEventBus());
            var provider = services.BuildServiceProvider();

            var ext = RenamerFixture.Create();
            ((IStatefulExtension)ext).SetStore(new ThrowingStore()); // first store read throws.
            await ext.InitializeAsync(provider);

            // The host calls OnEventAsync; the inner option-load throws. The handler must swallow it
            // so the host's dispatch loop is not handed a context-free exception.
            var thrown = await Record.ExceptionAsync(
                () => ext.OnEventAsync(new ExtensionEvent("video.updated", "video", 1), default));
            Assert.Null(thrown);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }
}
