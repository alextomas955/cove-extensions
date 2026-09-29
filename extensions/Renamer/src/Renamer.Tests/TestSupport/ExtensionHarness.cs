using Cove.Core.Auth;
using Cove.Plugins;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Renamer.Options;

namespace Renamer.Tests.TestSupport;

// Builds an initialized extension with options saved to its store.
internal static class ExtensionHarness
{
    // Every scope resolves the one seeded context the test also reads, so a test asserts on the rows
    // the extension wrote. Only single-scope paths may use it: a path that opens one scope per worker
    // would race on a DbContext that is not thread-safe.
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

    // Every scope resolves a context of its own over its own connection to the shared database, the
    // production shape, so the parallel workers of a batch genuinely run at once. The interceptor is
    // attached to every context, because the work a caller counts is spread over those scopes.
    internal static async Task<(global::Renamer.Renamer Extension, CapturingEventBus Bus)> CreateWithScopedContextsAsync(
        SharedCacheSqlite shared,
        RenamerOptions options,
        string[]? libraryPaths = null,
        IInterceptor? interceptor = null,
        IAuthorizationService? authorization = null,
        Action<DbContext>? onContextCreated = null)
    {
        ArgumentNullException.ThrowIfNull(shared);

        var services = new ServiceCollection();
        services.AddScoped<DbContext>(_ =>
        {
            var context = shared.NewContext(interceptor);
            onContextCreated?.Invoke(context);
            return context;
        });
        services.AddLibraryPaths(libraryPaths ?? []);
        var bus = new CapturingEventBus();
        services.AddSingleton<Cove.Core.Events.IEventBus>(bus);
        if (authorization is not null)
        {
            services.AddSingleton(authorization);
        }

        var store = new ConcurrentFakeStore();
        await new OptionsStore(store).SaveAsync(options);

        var extension = RenamerFixture.Create();
        ((IStatefulExtension)extension).SetStore(store);
        await extension.InitializeAsync(services.BuildServiceProvider());
        return (extension, bus);
    }
}
