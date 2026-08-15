using System.Data;
using Cove.Core.Events;
using Cove.Data;
using Cove.Plugins;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Renamer.Options;

namespace Renamer.Tests.TestSupport;

/// <summary>
/// The store/DI/DbContext wiring that the endpoint, journal and job suites each used to hand-roll as
/// their own <c>BuildExtensionAsync</c>. Every path builds the instance through
/// <see cref="RenamerFixture.Create"/>, so the shipped-manifest rule is unchanged.
/// </summary>
/// <remarks>
/// The six hand-rolled copies were never six copies of one shape: they were two families, and each
/// family holds a divergence that some suite's proof depends on. Those divergences are why this type
/// has three entry points instead of one overload set with a flag — see each method's own contract.
/// Collapsing them would leave every suite green while silently changing what two of them prove.
/// </remarks>
internal static class ExtensionHarness
{
    /// <summary>
    /// Builds an extension wired to a <see cref="FakeStore"/> seeded with <paramref name="options"/>,
    /// and — deliberately — NEVER calls <c>InitializeAsync</c>.
    /// </summary>
    /// <remarks>
    /// Not initializing is the point, not an omission. Preview reads the options store but never the
    /// scope factory or the event bus, so "this path needs no Initialize" is part of what the preview
    /// suites assert. A harness that always initialized would keep them green while destroying that
    /// property. Use <see cref="CreateWithSharedContextAsync"/> for anything that resolves a scope.
    /// </remarks>
    /// <param name="options">Seeded through <c>OptionsStore</c> before the extension is returned.</param>
    /// <returns>The extension and the store behind it; the store is returned so a caller can read back
    /// what a supposedly read-only path wrote.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    internal static async Task<(global::Renamer.Renamer Extension, FakeStore Store)> CreateStoreOnlyAsync(
        RenamerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var store = new FakeStore();
        await new OptionsStore(store).SaveAsync(options);

        var extension = RenamerFixture.Create();
        ((IStatefulExtension)extension).SetStore(store);
        return (extension, store);
    }

    /// <summary>
    /// Builds an initialized extension over <paramref name="db"/> registered as a SINGLETON
    /// <c>DbContext</c>, so every scope the extension opens resolves that one seeded context.
    /// </summary>
    /// <remarks>
    /// Singleton is what makes the test's own seed/assert context and the extension's context the same
    /// object, which is how these suites read back rows the extension wrote. It is safe here only
    /// because these paths are single-scope; a path that opens one scope per worker must use
    /// <see cref="CreateWithScopedContextAsync"/> instead, for the reason stated there.
    /// </remarks>
    /// <param name="db">The seeded context, registered as the base <c>DbContext</c>.</param>
    /// <param name="bus">Captured during initialization. Defaults to a fresh
    /// <see cref="CapturingEventBus"/> when the caller does not assert on published events.</param>
    /// <param name="options">Seeded when supplied; when null the store is left empty for the caller to
    /// seed itself.</param>
    /// <exception cref="ArgumentNullException"><paramref name="db"/> is null.</exception>
    internal static async Task<(global::Renamer.Renamer Extension, FakeStore Store)> CreateWithSharedContextAsync(
        CoveContext db,
        IEventBus? bus = null,
        RenamerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(db);

        var services = new ServiceCollection();
        services.AddSingleton<DbContext>(db);
        services.AddSingleton(bus ?? new CapturingEventBus());

        return await BuildAsync(services, options);
    }

    /// <summary>
    /// Builds an initialized extension over a <c>DbContext</c> registered SCOPED across
    /// <paramref name="connection"/>, so each scope resolves a DISTINCT context over the SAME database.
    /// </summary>
    /// <remarks>
    /// Scoped is a harness requirement, not a preference. The batch opens one DI scope per worker; a
    /// singleton registration would hand every parallel worker the one seeded context, and a
    /// <c>DbContext</c> is not thread-safe, so concurrent workers on it throw or corrupt. Sharing the
    /// connection is what keeps the <c>:memory:</c> database alive and keeps rows the workers save
    /// visible to the test's read-backs. In production each scope draws its own pooled connection, so
    /// this shape exists only here — and flattening it to singleton would change what the concurrency
    /// tests exercise while leaving them green.
    /// </remarks>
    /// <param name="connection">An OPEN connection to the shared in-memory database. A closed
    /// connection means the database is already gone.</param>
    /// <param name="bus">Captured during initialization. Defaults to a fresh
    /// <see cref="CapturingEventBus"/>.</param>
    /// <param name="options">Seeded when supplied.</param>
    /// <exception cref="ArgumentNullException"><paramref name="connection"/> is null.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="connection"/> is not open.</exception>
    internal static async Task<(global::Renamer.Renamer Extension, FakeStore Store)> CreateWithScopedContextAsync(
        SqliteConnection connection,
        IEventBus? bus = null,
        RenamerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (connection.State != ConnectionState.Open)
        {
            throw new InvalidOperationException(
                $"the shared SQLite connection is {connection.State}, not Open — an in-memory database "
                    + "only lives as long as a connection to it, so every scope would resolve a context "
                    + "over a database that no longer exists.");
        }

        var services = new ServiceCollection();
        services.AddScoped<DbContext>(_ =>
        {
            var contextOptions = new DbContextOptionsBuilder<CoveContext>().UseSqlite(connection).Options;
            return new CoveContext(contextOptions, principalAccessor: null);
        });
        services.AddSingleton(bus ?? new CapturingEventBus());

        return await BuildAsync(services, options);
    }

    private static async Task<(global::Renamer.Renamer Extension, FakeStore Store)> BuildAsync(
        IServiceCollection services,
        RenamerOptions? options)
    {
        var store = new FakeStore();
        if (options is not null)
        {
            await new OptionsStore(store).SaveAsync(options);
        }

        var extension = RenamerFixture.Create();
        ((IStatefulExtension)extension).SetStore(store);
        await extension.InitializeAsync(services.BuildServiceProvider());
        return (extension, store);
    }
}
