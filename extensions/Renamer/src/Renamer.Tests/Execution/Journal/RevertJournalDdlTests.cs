using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Renamer.Execution;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Execution.Journal;

public sealed class RevertJournalDdlTests
{
    [Fact]
    public async Task TheShippedMigration_CreatesBothTablesAndTheIndex()
    {
        var (db, conn) = CoveContextFactory.CreateSqliteContextWithoutSchema();
        await using var _ = db;
        await using var __ = conn;

        Assert.Empty(await ObjectNamesAsync(conn));

        await db.Database.ExecuteSqlRawAsync(RevertJournalSchema.Migration001UpSql);

        var created = await ObjectNamesAsync(conn);
        Assert.Contains("renamer_revert_batches", created);
        Assert.Contains("renamer_revert_rows", created);
        Assert.Contains("ix_renamer_revert_rows_run", created);
    }

    [Fact]
    public async Task TheShippedMigration_RunAgainstItsOwnResult_Succeeds()
    {
        // The table can outlive its receipt - an uninstall removes the extension's directory and
        // nothing removes the receipt, while a restored database can carry the tables with none. A
        // second application therefore has to be harmless, and its failure would only be a host log
        // line, which is the silence these cases exist to close.
        var (db, conn) = CoveContextFactory.CreateSqliteContextWithoutSchema();
        await using var _ = db;
        await using var __ = conn;

        await db.Database.ExecuteSqlRawAsync(RevertJournalSchema.Migration001UpSql);
        await db.Database.ExecuteSqlRawAsync(RevertJournalSchema.Migration001UpSql);

        var after = await ObjectNamesAsync(conn);
        Assert.Contains("renamer_revert_batches", after);
        Assert.Contains("renamer_revert_rows", after);
    }

    [Fact]
    public async Task ARowWrittenThroughTheRealEntity_RoundTripsOverTheMigratedSchema()
    {
        var (db, conn) = CoveContextFactory.CreateSqliteContextWithoutSchema();
        await using var _ = db;
        await using var __ = conn;

        await ApplyShippedMigrationsAsync(db);

        db.Set<RevertRowEntity>().Add(new RevertRowEntity
        {
            RunId = "run-1",
            Seq = 1,
            EntityId = 7,
            FileId = 11,
            OldPath = "/media/old/clip.mkv",
            SidecarsJson = "",
        });
        await db.SaveChangesAsync();

        var read = await db.Set<RevertRowEntity>().AsNoTracking().SingleAsync();
        Assert.Equal("run-1", read.RunId);
        Assert.Equal(1, read.Seq);
        Assert.Equal(7, read.EntityId);
        Assert.Equal(11, read.FileId);
        Assert.Equal("/media/old/clip.mkv", read.OldPath);
        Assert.Equal("", read.SidecarsJson);
    }

    [Fact]
    public async Task TheDefaultedColumns_TakeTheirValuesFromTheSchema()
    {
        // Written through raw SQL that names only the required columns, so what comes back is the
        // schema's defaults rather than values the entity supplied - the two are indistinguishable
        // when the entity writes every column.
        var (db, conn) = CoveContextFactory.CreateSqliteContextWithoutSchema();
        await using var _ = db;
        await using var __ = conn;

        await ApplyShippedMigrationsAsync(db);
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO renamer_revert_batches (run_id, opened_at_utc_ticks, kind) VALUES ('run-1', 42, 'Video')");
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO renamer_revert_rows (run_id, seq, entity_id, file_id, old_path) "
                + "VALUES ('run-1', 1, 7, 11, '/media/old/clip.mkv')");

        var batch = await db.Set<RevertBatchEntity>().AsNoTracking().SingleAsync();
        Assert.Equal(0, batch.OriginalCount);
        Assert.Equal(0, batch.RestoredCount);
        Assert.Equal(0, batch.UnrestorableCount);
        Assert.Equal("", batch.OperationId);

        Assert.Equal("", (await db.Set<RevertRowEntity>().AsNoTracking().SingleAsync()).SidecarsJson);
    }

    [Fact]
    public async Task TheOperationColumn_LandsBesideAnExistingBatch_AndLeavesItWithNoOperation()
    {
        var (db, conn) = CoveContextFactory.CreateSqliteContextWithoutSchema();
        await using var _ = db;
        await using var __ = conn;

        await db.Database.ExecuteSqlRawAsync(RevertJournalSchema.Migration001UpSql);
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO renamer_revert_batches (run_id, opened_at_utc_ticks, kind) VALUES ('legacy', 42, 'Video')");

        await SqliteJournalSchema.AddOperationColumnAsync(db);

        Assert.Contains("ix_renamer_revert_batches_operation", await ObjectNamesAsync(conn));

        // A batch written before the column existed carries no operation of its own. Readers resolve
        // that to the batch's own run id, so it stays undoable as an operation of one.
        var batch = await db.Set<RevertBatchEntity>().AsNoTracking().SingleAsync();
        Assert.Equal("", batch.OperationId);
    }

    private static Task ApplyShippedMigrationsAsync(DbContext db) => SqliteJournalSchema.CreateAsync(db);

    private static async Task<List<string>> ObjectNamesAsync(SqliteConnection conn)
    {
        await using var command = conn.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE name NOT LIKE 'sqlite_%'";

        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }
}
