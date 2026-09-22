using Microsoft.EntityFrameworkCore;
using Renamer.Execution;

namespace Renamer.Tests.TestSupport;

/// <summary>
/// Builds the journal schema on a SQLite test database.
/// </summary>
/// <remarks>
/// The first migration ships portable SQL and is executed here as the host executes it. The second
/// ships PostgreSQL syntax, because the host runs on PostgreSQL alone, and SQLite cannot parse its
/// <c>ADD COLUMN IF NOT EXISTS</c> - so the column is added here by an equivalent SQLite statement.
/// <para>
/// That makes this setup, not coverage: nothing in the SQLite suite executes the second migration's
/// shipped string, and a test here cannot report that it drifted. What the string does is verified
/// against a real PostgreSQL host.
/// </para>
/// </remarks>
internal static class SqliteJournalSchema
{
    private const string AddOperationIdSql =
        """
        ALTER TABLE renamer_revert_batches ADD COLUMN operation_id TEXT NOT NULL DEFAULT '';
        CREATE INDEX IF NOT EXISTS ix_renamer_revert_batches_operation
            ON renamer_revert_batches (operation_id);
        """;

    /// <summary>Creates the journal tables as they stand after every shipped migration.</summary>
    public static async Task CreateAsync(DbContext db)
    {
        await db.Database.ExecuteSqlRawAsync(RevertJournalSchema.Migration001UpSql);
        await AddOperationColumnAsync(db);
    }

    /// <summary>Adds the operation column and its index to a table the first migration created.</summary>
    public static Task AddOperationColumnAsync(DbContext db) =>
        db.Database.ExecuteSqlRawAsync(AddOperationIdSql);
}
