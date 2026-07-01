using Cove.Core.Events;
using Cove.Plugins;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Events;

/// <summary>
/// The auto-renamer hook must contain its own failures. The host dispatches these events
/// fire-and-forget and only logs an escaped exception generically, with no entity context, so a
/// handler that lets a failure bubble produces an opaque, repeating host-log error on every update.
/// The handler instead catches, records the failure with the entity context, and returns — so
/// the host-facing <c>OnEventAsync</c> completes normally even when the inner path throws.
/// </summary>
[Trait("Tier", "Integration")]
public sealed class AutoRenamerFailureContainmentTests
{
    /// <summary>
    /// An <see cref="IExtensionStore"/> whose every read throws — standing in for any inner failure
    /// (a transient store/DB error) on the auto-renamer path. The handler loads options from the store
    /// as its first step, so this reliably exercises the catch.
    /// </summary>
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

            var ext = new global::Renamer.Renamer();
            ((IStatefulExtension)ext).SetStore(new ThrowingStore()); // first store read throws.
            await ext.InitializeAsync(provider);

            // The host calls OnEventAsync; the inner option-load throws. The handler must swallow it
            // so the host's dispatch loop is not handed a context-free exception. No throw == pass.
            await ext.OnEventAsync(new ExtensionEvent("video.updated", "video", 1), default);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }
}
