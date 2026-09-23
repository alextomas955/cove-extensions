using Cove.Plugins;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Renamer.Options;

namespace Renamer.Tests.TestSupport;

// Builds an initialized extension whose every scope resolves the one seeded context the test also
// reads, so a test asserts on the rows the extension wrote. Only single-scope paths may use it: a
// path that opens one scope per worker would race on a DbContext that is not thread-safe.
internal static class ExtensionHarness
{
    internal static Task<(global::Renamer.Renamer Extension, FakeStore Store)> CreateWithSharedContextAsync(
        DbContext db, RenamerOptions options, params string[] libraryPaths)
        => CreateWithSharedContextAsync(db, options, new CapturingEventBus(), libraryPaths);

    internal static async Task<(global::Renamer.Renamer Extension, FakeStore Store)> CreateWithSharedContextAsync(
        DbContext db, RenamerOptions options, Cove.Core.Events.IEventBus bus, params string[] libraryPaths)
    {
        ArgumentNullException.ThrowIfNull(options);

        var services = new ServiceCollection();
        services.AddSingleton<DbContext>(db);
        services.AddSingleton(bus);
        services.AddLibraryPaths(libraryPaths);

        var store = new FakeStore();
        await new OptionsStore(store).SaveAsync(options);

        var extension = RenamerFixture.Create();
        ((IStatefulExtension)extension).SetStore(store);
        await extension.InitializeAsync(services.BuildServiceProvider());
        return (extension, store);
    }
}
