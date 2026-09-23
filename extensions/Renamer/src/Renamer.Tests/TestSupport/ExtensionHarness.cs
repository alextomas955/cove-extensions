using Cove.Plugins;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Renamer.Options;

namespace Renamer.Tests.TestSupport;

// Builds an initialized extension over one already-seeded CoveContext, for the endpoint suites that
// seed and assert through the same context the extension reads.
internal static class ExtensionHarness
{
    // Registers db as a singleton DbContext, so every scope the extension opens resolves that one
    // seeded context, and seeds options into its store. Singleton is what makes the test's own
    // seed/assert context and the extension's context the same object, which is how these suites
    // read back rows the extension wrote. It is safe only because these paths are single-scope; a
    // path that opens one scope per worker needs its own context each time, or the two race on a
    // DbContext that is not thread-safe. <exception cref="ArgumentNullException">options is
    // null.</exception>
    internal static async Task<(global::Renamer.Renamer Extension, FakeStore Store)> CreateWithSharedContextAsync(
        DbContext db, RenamerOptions options, params string[] libraryPaths)
    {
        ArgumentNullException.ThrowIfNull(options);

        var services = new ServiceCollection();
        services.AddSingleton<DbContext>(db);
        services.AddSingleton<Cove.Core.Events.IEventBus>(new CapturingEventBus());
        services.AddLibraryPaths(libraryPaths);

        var store = new FakeStore();
        await new OptionsStore(store).SaveAsync(options);

        var extension = RenamerFixture.Create();
        ((IStatefulExtension)extension).SetStore(store);
        await extension.InitializeAsync(services.BuildServiceProvider());
        return (extension, store);
    }
}
