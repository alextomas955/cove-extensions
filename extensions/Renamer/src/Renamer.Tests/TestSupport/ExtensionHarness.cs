using Cove.Data;
using Cove.Plugins;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Renamer.Options;

namespace Renamer.Tests.TestSupport;

/// <summary>
/// Builds an initialized extension over one already-seeded <c>CoveContext</c>, for the endpoint suites
/// that seed and assert through the same context the extension reads.
/// </summary>
internal static class ExtensionHarness
{
    /// <summary>
    /// Registers <paramref name="db"/> as a SINGLETON <c>DbContext</c>, so every scope the extension
    /// opens resolves that one seeded context, and seeds <paramref name="options"/> into its store.
    /// </summary>
    /// <remarks>
    /// Singleton is what makes the test's own seed/assert context and the extension's context the same
    /// object, which is how these suites read back rows the extension wrote. It is safe only because
    /// these paths are single-scope; a path that opens one scope per worker needs its own context each
    /// time, or the two race on a <c>DbContext</c> that is not thread-safe.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    internal static async Task<(global::Renamer.Renamer Extension, FakeStore Store)> CreateWithSharedContextAsync(
        CoveContext db, RenamerOptions options, params string[] libraryPaths)
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
