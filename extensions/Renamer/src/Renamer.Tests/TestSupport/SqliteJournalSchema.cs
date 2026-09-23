using Microsoft.EntityFrameworkCore;
using Renamer.Execution;

namespace Renamer.Tests.TestSupport;

// Builds the journal schema on a SQLite test database. The first migration ships portable SQL and
// is executed here as the host executes it. The second ships PostgreSQL syntax, because the host
// runs on PostgreSQL alone, and SQLite cannot parse its ADD COLUMN IF NOT EXISTS - so the column is
// added here by an equivalent SQLite statement. That makes this setup, not coverage: nothing in the
// SQLite suite executes the second migration's shipped string, and a test here cannot report that
// it drifted. What the string does is verified against a real PostgreSQL host.
internal static class SqliteJournalSchema
{
    private const string AddOperationIdSql =
        """
        ALTER TABLE renamer_revert_batches ADD COLUMN operation_id TEXT NOT NULL DEFAULT '';
        CREATE INDEX IF NOT EXISTS ix_renamer_revert_batches_operation
            ON renamer_revert_batches (operation_id);
        """;

    // Creates the journal tables as they stand after every shipped migration.
    public static async Task CreateAsync(DbContext db)
    {
        await db.Database.ExecuteSqlRawAsync(RevertJournalSchema.Migration001UpSql);
        await AddOperationColumnAsync(db);
    }

    // Adds the operation column and its index to a table the first migration created.
    public static Task AddOperationColumnAsync(DbContext db) =>
        db.Database.ExecuteSqlRawAsync(AddOperationIdSql);
}
