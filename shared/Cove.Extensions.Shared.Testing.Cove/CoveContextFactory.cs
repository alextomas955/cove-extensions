using Cove.Core.Auth;
using Cove.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Cove.Extensions.Shared.Testing;

/// <summary>
/// Stands up a real <see cref="CoveContext"/> for an integration tier over SQLite-in-memory, mirroring
/// Cove's own proven <c>AiCoreControllerTests</c> pattern.
///
/// WHY relational SQLite (not EF-InMemory): the EF-InMemory provider does NOT enforce the
/// <c>(ParentFolderId, Basename)</c> unique index and treats transactions as a silent no-op, so any test
/// asserting <em>collision-on-save throws</em> or <em>rollback</em> would false-green on it.
/// <see cref="CreateSqliteContextAsync"/> is relational (<c>EnsureCreatedAsync</c> materializes the unique
/// index + real transactions), so it is correct for both those tests and the projection assertions.
/// </summary>
internal static class CoveContextFactory
{
    /// <summary>
    /// Opens an in-memory SQLite connection and builds a <see cref="CoveContext"/> over it, then calls
    /// <c>EnsureCreatedAsync()</c> so the relational schema — including the <c>(ParentFolderId, Basename)</c>
    /// UNIQUE index and real transaction support — is materialized. A null principal accessor is fine in
    /// the save path, and is the default.
    ///
    /// The open connection is what keeps the in-memory database alive for the context's lifetime, so the
    /// caller OWNS both returned disposables and MUST dispose them:
    /// <code>await db.DisposeAsync(); await conn.DisposeAsync();</code>
    ///
    /// Pass <paramref name="principalAccessor"/> to build a context that really ENFORCES Cove's
    /// per-principal authorization query filters. A null accessor bypasses them, so a test whose
    /// subject is the elevation must supply a real one and set an under-privileged principal on it.
    /// </summary>
    public static async Task<(CoveContext db, SqliteConnection conn)> CreateSqliteContextAsync(
        ICurrentPrincipalAccessor? principalAccessor = null)
    {
        var (db, connection) = CreateSqliteContextWithoutSchema(principalAccessor);
        await db.Database.EnsureCreatedAsync();
        return (db, connection);
    }

    /// <summary>
    /// The same context over the same in-memory SQLite connection, but with NO schema materialized —
    /// the database is genuinely empty. For a test whose subject is a schema-creating statement: a
    /// context that already carries every table cannot tell a statement that creates one from a
    /// statement that does nothing.
    ///
    /// The caller OWNS both returned disposables, exactly as with <see cref="CreateSqliteContextAsync"/>.
    /// </summary>
    public static (CoveContext db, SqliteConnection conn) CreateSqliteContextWithoutSchema(
        ICurrentPrincipalAccessor? principalAccessor = null)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseSqlite(connection)
            // Production's own cache-key factory, copied from Cove.Data's AddCoveData. EF's DEFAULT key
            // ignores CoveContext.ModelGeneration, so whichever context is built first in the process
            // pins the model for every context after it — and a data extension registered later then
            // has its entity types missing from a model that is never rebuilt. The failure is a
            // "cannot create a DbSet for X" that depends on test ORDER, which xUnit's per-class
            // parallelism makes unreproducible. Keyed on the generation, a registration change rebuilds.
            .ReplaceService<IModelCacheKeyFactory, CoveModelCacheKeyFactory>()
            .Options;

        return (new CoveContext(options, principalAccessor), connection);
    }
}
