using Cove.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Renamer.Tests.TestSupport;

/// <summary>
/// A named, shared-cache in-memory SQLite database for the parallel-batch concurrency proofs. A bare
/// <c>Data Source=:memory:</c> database is private to its ONE connection, so per-worker scopes that
/// each open their own context over one shared connection serialize onto a single SQLite connection
/// and throw "database is locked" the moment two workers query at once. A named
/// <c>Mode=Memory;Cache=Shared</c> database instead lets EACH context open its OWN connection to the
/// SAME database — the production shape (every scope gets its own pooled connection) — so the workers
/// run genuinely in parallel. One kept-open keep-alive connection holds the database alive for the
/// fixture's lifetime; a per-connection <c>busy_timeout</c> makes a writer that briefly contends wait
/// rather than fail. Test-support only — never packaged.
/// </summary>
internal sealed class SharedCacheSqlite : IAsyncDisposable
{
    private readonly SqliteConnection _keepAlive;

    /// <summary>The connection string every context opens its own connection from (shared-cache, named).</summary>
    public string ConnectionString { get; }

    private SharedCacheSqlite(SqliteConnection keepAlive, string connectionString)
    {
        _keepAlive = keepAlive;
        ConnectionString = connectionString;
    }

    /// <summary>Opens a fresh connection to the shared database with a generous busy-timeout.</summary>
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

    /// <summary>Builds a <see cref="CoveContext"/> over its OWN connection to the shared database.</summary>
    public CoveContext NewContext()
    {
        var options = new DbContextOptionsBuilder<CoveContext>().UseSqlite(OpenConnection()).Options;
        return new CoveContext(options, principalAccessor: null);
    }

    /// <summary>Names a fresh shared-cache database, opens the keep-alive connection, and materializes the schema once.</summary>
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
