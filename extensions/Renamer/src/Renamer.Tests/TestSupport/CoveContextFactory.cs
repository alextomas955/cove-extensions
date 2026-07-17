using Cove.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Renamer.Tests.TestSupport;

/// <summary>
/// Stands up a real <see cref="CoveContext"/> for the integration tier over SQLite-in-memory, mirroring
/// Cove's own proven <c>AiCoreControllerTests</c> pattern.
///
/// WHY relational SQLite (not EF-InMemory): the EF-InMemory provider does NOT enforce the
/// <c>(ParentFolderId, Basename)</c> unique index and treats transactions as a silent no-op, so any test
/// asserting <em>collision-on-save throws</em> or <em>rollback</em> would false-green on it.
/// <see cref="CreateSqliteContextAsync"/> is relational (<c>EnsureCreatedAsync</c> materializes the unique
/// index + real transactions), so it is correct for both those tests and the projection /
/// <c>ComputeFilePaths</c> assertions.
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
}
