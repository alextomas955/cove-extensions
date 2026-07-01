using Cove.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Renamer.Tests.TestSupport;

/// <summary>
/// Stands up a real <see cref="CoveContext"/> for the integration tier in two shapes. Mirrors
/// Cove's own proven patterns: <c>AiCoreControllerTests</c> (SQLite-in-memory) and
/// <c>CoveContextDerivedMetricsTests</c> (EF-InMemory).
///
/// WHY two providers: the EF-InMemory provider does NOT enforce the
/// <c>(ParentFolderId, Basename)</c> unique index and treats transactions as a silent no-op,
/// so any test asserting <em>collision-on-save throws</em> or <em>rollback</em> would
/// false-green on it. Those tests MUST use <see cref="CreateSqliteContextAsync"/> (relational,
/// <c>EnsureCreatedAsync</c> materializes the unique index + real transactions). Use
/// <see cref="CreateInMemoryContext"/> only for projection / <c>ComputeFilePaths</c> tests.
/// </summary>
internal static class CoveContextFactory
{
    /// <summary>
    /// Opens an in-memory SQLite connection and builds a <see cref="CoveContext"/> over it,
    /// then calls <c>EnsureCreatedAsync()</c> so the relational schema — including the
    /// <c>(ParentFolderId, Basename)</c> UNIQUE index and real transaction support — is
    /// materialized. A null principal accessor is fine in the save path (research A1).
    ///
    /// The caller OWNS both returned disposables and MUST dispose them (the open connection
    /// is what keeps the in-memory database alive for the context's lifetime):
    /// <code>await db.DisposeAsync(); await conn.DisposeAsync();</code>
    /// </summary>
    public static async Task<(CoveContext db, SqliteConnection conn)> CreateSqliteContextAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseSqlite(connection)
            .Options;

        var db = new CoveContext(options, principalAccessor: null);
        await db.Database.EnsureCreatedAsync();
        return (db, connection);
    }

    /// <summary>
    /// Builds a fast EF-InMemory <see cref="CoveContext"/> with a unique database name per call.
    /// <c>CoveContext.ComputeFilePaths</c> still runs on save under this provider (proven by
    /// Cove's own metric tests), so it is correct for Path-recompute/projection assertions —
    /// but NOT for constraint or transaction behavior (see class remarks). The caller disposes it.
    /// </summary>
    public static CoveContext CreateInMemoryContext()
    {
        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseInMemoryDatabase($"renamer-{Guid.NewGuid():N}")
            .Options;

        return new CoveContext(options, principalAccessor: null);
    }
}
