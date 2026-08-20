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
    /// <param name="db">The seeded context, registered as the base <see cref="DbContext"/>.</param>
    /// <param name="options">Persisted into the store before the first event fires.</param>
    /// <param name="libraryRoots">
    /// Registered as Cove's configured library paths when any are named — the list a destination root
    /// is chosen from and re-checked against. Omitted, no <c>CoveConfiguration</c> is registered at all,
    /// which is the host state one of these tests deliberately exercises; see
    /// <see cref="TestSupport.Library.LibraryConfig"/> for the whole statement.
    /// </param>
    public static async Task<(global::Renamer.Renamer ext, CapturingEventBus bus, FakeStore store)> BuildAsync(
        CoveContext db, RenamerOptions options, params string[] libraryRoots)
    {
        var services = new ServiceCollection();
        services.AddSingleton<DbContext>(db);
        var bus = new CapturingEventBus();
        services.AddSingleton<IEventBus>(bus);
        if (libraryRoots.Length > 0)
        {
            services.AddSingleton(Library.LibraryConfig(libraryRoots));
        }

        var provider = services.BuildServiceProvider();

        var store = new FakeStore();
        await new OptionsStore(store).SaveAsync(options); // hook loads these on the first event.

        var ext = RenamerFixture.Create();
        ((IStatefulExtension)ext).SetStore(store);
        await ext.InitializeAsync(provider); // captures IServiceScopeFactory + IEventBus from DI.

        return (ext, bus, store);
    }
}
