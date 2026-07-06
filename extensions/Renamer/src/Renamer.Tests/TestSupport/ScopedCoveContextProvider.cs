using Cove.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Renamer.Tests.TestSupport;

/// <summary>
/// Builds an <see cref="IServiceProvider"/> that registers the base <see cref="DbContext"/> as
/// <c>AddScoped</c> over a factory, so each <c>CreateAsyncScope()</c> resolves a DISTINCT
/// <see cref="CoveContext"/> instance (assertable by reference) — unlike the singleton registration
/// in the existing job tests, which hands every scope the same context and so cannot prove
/// per-worker isolation.
///
/// CONNECTION SHARING. All per-scope contexts are built over ONE shared in-memory
/// <see cref="SqliteConnection"/>. A SQLite <c>:memory:</c> database lives only as long as a
/// connection to it stays open, so a single kept-open connection backs one coherent database that
/// every scope reads and writes; each scope still gets its own context object over that one
/// connection. This gives both distinct-instance isolation (the Plan 02 proof) and a consistent DB
/// the parallel workers all observe. The schema (incl. the (ParentFolderId, Basename) unique index)
/// is materialized once via <c>EnsureCreatedAsync</c>.
///
/// The CALLER owns the returned disposable: disposing it disposes the service provider AND closes
/// the shared connection (which drops the in-memory database). Test-support only — never packaged.
/// </summary>
internal sealed class ScopedCoveContextProvider : IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;

    private ScopedCoveContextProvider(SqliteConnection connection, ServiceProvider provider)
    {
        _connection = connection;
        _provider = provider;
    }

    /// <summary>The provider to pass to <c>Renamer.InitializeAsync</c>; its scope factory yields per-scope contexts.</summary>
    public IServiceProvider Services => _provider;

    /// <summary>
    /// Opens one shared in-memory SQLite connection, materializes the schema once, and registers a
    /// scoped <see cref="DbContext"/> factory over that connection. Resolves <see cref="IServiceScopeFactory"/>
    /// for callers that want to open scopes directly.
    /// </summary>
    public static async Task<ScopedCoveContextProvider> CreateAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        // Materialize the relational schema once on the shared connection.
        var seedOptions = new DbContextOptionsBuilder<CoveContext>().UseSqlite(connection).Options;
        await using (var seed = new CoveContext(seedOptions, principalAccessor: null))
        {
            await seed.Database.EnsureCreatedAsync();
        }

        var services = new ServiceCollection();
        services.AddScoped<DbContext>(_ =>
        {
            var options = new DbContextOptionsBuilder<CoveContext>().UseSqlite(connection).Options;
            return new CoveContext(options, principalAccessor: null);
        });

        var provider = services.BuildServiceProvider();
        return new ScopedCoveContextProvider(connection, provider);
    }

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
