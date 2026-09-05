using Cove.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Cove.Extensions.Shared.Testing;

/// <summary>
/// Stands up a real <c>CoveContext</c> for an integration tier over SQLite-in-memory, mirroring
/// Cove's own proven <c>AiCoreControllerTests</c> pattern.
///
/// It is HANDED BACK as <see cref="DbContext"/>, which is the type an extension is given at runtime
/// and the only one Renamer's own code names. What these tests need from Cove is not the type but
/// the CONFIGURED context: Cove's EF model, including the <c>(ParentFolderId, Basename)</c> unique
/// index, and Cove's <c>SaveChangesAsync</c> overrides, which derive every touched file's
/// <c>Path</c>. A hand-rolled context over <c>Cove.Core</c> entities would compile and run and would
/// carry neither, so a collision or path-derivation test on one would pass while the real host
/// failed.
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
    /// the save path.
    ///
    /// The open connection is what keeps the in-memory database alive for the context's lifetime, so the
    /// caller OWNS both returned disposables and MUST dispose them:
    /// <code>await db.DisposeAsync(); await conn.DisposeAsync();</code>
    /// </summary>
    public static async Task<(DbContext db, SqliteConnection conn)> CreateSqliteContextAsync()
    {
        var (db, connection) = CreateSqliteContextWithoutSchema();
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
    public static (DbContext db, SqliteConnection conn) CreateSqliteContextWithoutSchema()
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

        return (new CoveContext(options, principalAccessor: null), connection);
    }
}
