using Cove.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace WhisparrSync.Tests.TestSupport;

/// <summary>
/// Stands up a real <see cref="CoveContext"/> over an in-memory SQLite connection for the
/// <c>CoveLibraryPort</c> integration tier — the same proven shape Renamer's own factory uses. The open
/// connection is what keeps the in-memory database alive for the context's lifetime, so the caller OWNS
/// both returned disposables and MUST dispose them:
/// <code>await db.DisposeAsync(); await conn.DisposeAsync();</code>
/// </summary>
internal static class CoveContextFactory
{
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
