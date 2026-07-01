using Cove.Core.Events;
using Cove.Data;
using Cove.Plugins;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Renamer.Options;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Events;

/// <summary>
/// Shared wiring for the auto-renamer hook tests. Builds a <c>Renamer</c> extension with its captured
/// seams (<c>_scopeFactory</c>, <c>_eventBus</c>, <c>Store</c>) sourced from a DI provider that
/// registers the seeded <see cref="CoveContext"/> as the base <see cref="DbContext"/> (singleton, so
/// the per-event scope resolves the same seeded instance) and a <see cref="CapturingEventBus"/>.
/// Options are persisted into the same store the hook loads from BEFORE the event fires.
/// </summary>
internal static class EventTestHarness
{
    public static async Task<(global::Renamer.Renamer ext, CapturingEventBus bus, FakeStore store)> BuildAsync(
        CoveContext db, RenamerOptions options)
    {
        var services = new ServiceCollection();
        services.AddSingleton<DbContext>(db);
        var bus = new CapturingEventBus();
        services.AddSingleton<IEventBus>(bus);
        var provider = services.BuildServiceProvider();

        var store = new FakeStore();
        await new OptionsStore(store).SaveAsync(options); // hook loads these on the first event.

        var ext = new global::Renamer.Renamer();
        ((IStatefulExtension)ext).SetStore(store);
        await ext.InitializeAsync(provider); // captures IServiceScopeFactory + IEventBus from DI.

        return (ext, bus, store);
    }
}
