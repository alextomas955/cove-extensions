using Cove.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Renamer.Tests.TestSupport;

// A named, shared-cache in-memory SQLite database for the parallel-batch concurrency proofs. A bare
// Data Source=:memory: database is private to its one connection, so per-worker scopes that each
// open their own context over one shared connection serialize onto a single SQLite connection and
// throw "database is locked" the moment two workers query at once. A named Mode=Memory;Cache=Shared
// database instead lets each context open its own connection to the same database - the production
// shape (every scope gets its own pooled connection) - so the workers run genuinely in parallel.
// One kept-open keep-alive connection holds the database alive for the fixture's lifetime; a
// per-connection busy_timeout makes a writer that briefly contends wait rather than fail.
// Test-support only - never packaged.
internal sealed class SharedCacheSqlite : IAsyncDisposable
{
    private readonly SqliteConnection _keepAlive;

    // The connection string every context opens its own connection from (shared-cache, named).
    public string ConnectionString { get; }

    private SharedCacheSqlite(SqliteConnection keepAlive, string connectionString)
    {
        _keepAlive = keepAlive;
        ConnectionString = connectionString;
    }

    // Opens a fresh connection to the shared database with a generous busy-timeout.
    public SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection(ConnectionString);
        conn.Open();
        using (var pragma = conn.CreateCommand())
        {
            pragma.CommandText = "PRAGMA busy_timeout = 5000;";
            pragma.ExecuteNonQuery();
        }
        return conn;
    }

    // Builds a CoveContext over its own connection to the shared database. interceptor lets a
    // caller count what the context executes. Every context a run resolves has to carry the same
    // one, because the work being counted is spread over the per-worker scopes.
    public DbContext NewContext(IInterceptor? interceptor = null)
    {
        var builder = new DbContextOptionsBuilder<CoveContext>().UseSqlite(OpenConnection());
        if (interceptor is not null)
        {
            builder = builder.AddInterceptors(interceptor);
        }

        return new CoveContext(builder.Options, principalAccessor: null);
    }

    // Names a fresh shared-cache database, opens the keep-alive connection, and materializes the
    // schema once.
    public static async Task<SharedCacheSqlite> CreateAsync()
    {
        string name = "renamer-concurrency-" + Guid.NewGuid().ToString("N");
        string cs = $"Data Source={name};Mode=Memory;Cache=Shared";

        var keepAlive = new SqliteConnection(cs);
        await keepAlive.OpenAsync();

        var seedOptions = new DbContextOptionsBuilder<CoveContext>().UseSqlite(keepAlive).Options;
        await using (var seed = new CoveContext(seedOptions, principalAccessor: null))
        {
            await seed.Database.EnsureCreatedAsync();
        }

        return new SharedCacheSqlite(keepAlive, cs);
    }

    public async ValueTask DisposeAsync() => await _keepAlive.DisposeAsync();
}
