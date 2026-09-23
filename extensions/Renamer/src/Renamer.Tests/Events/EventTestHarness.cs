using Cove.Core.Events;
using Cove.Plugins;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Renamer.Options;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Events;

internal static class EventTestHarness
{
    public static async Task<(global::Renamer.Renamer ext, CapturingEventBus bus, FakeStore store)> BuildAsync(
        DbContext db, RenamerOptions options, params string[] libraryPaths)
    {
        var services = new ServiceCollection();
        services.AddSingleton<DbContext>(db);
        services.AddLibraryPaths(libraryPaths);
        var bus = new CapturingEventBus();
        services.AddSingleton<IEventBus>(bus);
        var provider = services.BuildServiceProvider();

        var store = new FakeStore();
        await new OptionsStore(store).SaveAsync(options); // hook loads these on the first event.

        var ext = RenamerFixture.Create();
        ((IStatefulExtension)ext).SetStore(store);
        await ext.InitializeAsync(provider); // captures IServiceScopeFactory + IEventBus from DI.

        return (ext, bus, store);
    }
}
