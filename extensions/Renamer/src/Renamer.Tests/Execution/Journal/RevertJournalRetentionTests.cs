using System.Data.Common;
using Cove.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Renamer.Execution;
using Renamer.Planner;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Execution.Journal;

/// <summary>
/// The retention window: a batch older than <see cref="JournalRetention.Window"/> disappears WHOLE on
/// the next batch open, and one inside the window survives it.
/// </summary>
/// <remarks>
/// Time is driven through the moment the port already takes as a parameter, never the system clock, so
/// every case is deterministic and nothing waits. Driven through the real EF implementation because the
/// property under test — that no row of an expired batch is left behind — is a property of the storage.
/// </remarks>
[Trait("Tier", "L1")]
[Collection(CoveDataExtensionScope.CollectionName)]
public sealed class RevertJournalRetentionTests
{
    private static readonly DateTime Opened = new(2026, 8, 3, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>Just past the window: everything opened at <see cref="Opened"/> is expired at this moment.</summary>
    private static DateTime JustOutside => Opened + JournalRetention.Window + TimeSpan.FromHours(1);

    /// <summary>Just inside the window: everything opened at <see cref="Opened"/> still survives.</summary>
    private static DateTime JustInside => Opened + JournalRetention.Window - TimeSpan.FromHours(1);

    [Fact]
    public async Task ABatchOutsideTheWindow_GoesWholeWhenTheNextBatchOpens()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        await using var _ = db;
        await using var __ = conn;

        await SeedBatchAsync(db, "run-old", Opened, rows: 3);

        // Opening the next batch is the only trigger — no timer, no scheduler, no second call site.
        await new CoveRevertJournal(db).BeginBatchAsync("run-new", RenamerFileKind.Video, JustOutside);

        Assert.Empty(await BatchRunIdsAsync(db, "run-old"));
        Assert.Empty(await RowRunIdsAsync(db, "run-old"));

        // The batch that did the purging is untouched by it.
        Assert.Single(await BatchRunIdsAsync(db, "run-new"));
    }

    [Fact]
    public async Task ABatchOneHourInsideTheWindow_SurvivesTheNextBatchOpening()
    {
        // The live defect this closes: opening a batch used to REPLACE the stored journal, so one
        // background auto-rename destroyed the undo record of a deliberate 500-file run.
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        await using var _ = db;
        await using var __ = conn;

        await SeedBatchAsync(db, "run-old", Opened, rows: 3);

        await new CoveRevertJournal(db).BeginBatchAsync("run-new", RenamerFileKind.Video, JustInside);

        Assert.Single(await BatchRunIdsAsync(db, "run-old"));
        Assert.Equal(3, (await RowRunIdsAsync(db, "run-old")).Count);
    }

    [Fact]
    public async Task APartiallyRestoredBatchOutsideTheWindow_LosesItsRemainingRowsAndItsBatchRow()
    {
        // Half a batch surviving would make a later undo silently partial, with nothing to say so — so
        // the purge keys on the BATCH, and a batch that is already half spent goes with the same sweep.
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        await using var _ = db;
        await using var __ = conn;

        var journal = await SeedBatchAsync(db, "run-old", Opened, rows: 4);
        await journal.DeleteRowAsync("run-old", seq: 1, unrestorable: false);
        await journal.DeleteRowAsync("run-old", seq: 2, unrestorable: true);
        Assert.Equal(2, (await RowRunIdsAsync(db, "run-old")).Count);

        await new CoveRevertJournal(db).BeginBatchAsync("run-new", RenamerFileKind.Video, JustOutside);

        Assert.Empty(await RowRunIdsAsync(db, "run-old"));
        Assert.Empty(await BatchRunIdsAsync(db, "run-old"));
    }

    [Fact]
    public async Task TheNewestBatchIsNotExempt_ItExpiresLikeAnyOther()
    {
        // "Always keep the newest" was considered and declined: it is a second retention rule, and a
        // second rule is something that can disagree with the first.
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        await using var _ = db;
        await using var __ = conn;

        var journal = await SeedBatchAsync(db, "run-only", Opened, rows: 2);

        await journal.PurgeExpiredAsync(JustOutside);

        Assert.Empty(await BatchRunIdsAsync(db, "run-only"));
        Assert.Empty(await RowRunIdsAsync(db, "run-only"));
        Assert.Null(await journal.ReadLastBatchSummaryAsync());
    }

    [Fact]
    public async Task ABatchExpiresWhole_WhetherItHoldsOneRowOrManyRows()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        await using var _ = db;
        await using var __ = conn;

        await SeedBatchAsync(db, "run-one", Opened, rows: 1);
        await SeedBatchAsync(db, "run-many", Opened.AddMinutes(1), rows: 250);

        await new CoveRevertJournal(db).PurgeExpiredAsync(JustOutside);

        Assert.Empty(await RowRunIdsAsync(db, "run-one"));
        Assert.Empty(await RowRunIdsAsync(db, "run-many"));
        Assert.Empty(await BatchRunIdsAsync(db, "run-one"));
        Assert.Empty(await BatchRunIdsAsync(db, "run-many"));
    }

    [Fact]
    public async Task TheWindowIsMeasuredFromTheBatchOpenTimestamp_NotFromItsRows()
    {
        // Two batches, one either side of the same cutoff, purged in one call: the survivor proves the
        // purge selects by each batch's OWN open timestamp rather than sweeping everything it finds.
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        await using var _ = db;
        await using var __ = conn;

        var now = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        await SeedBatchAsync(db, "run-expired", now - JournalRetention.Window - TimeSpan.FromMinutes(1), rows: 2);
        await SeedBatchAsync(db, "run-live", now - JournalRetention.Window + TimeSpan.FromMinutes(1), rows: 2);

        await new CoveRevertJournal(db).PurgeExpiredAsync(now);

        Assert.Empty(await BatchRunIdsAsync(db, "run-expired"));
        Assert.Empty(await RowRunIdsAsync(db, "run-expired"));
        Assert.Single(await BatchRunIdsAsync(db, "run-live"));
        Assert.Equal(2, (await RowRunIdsAsync(db, "run-live")).Count);
    }

    [Fact]
    public async Task PurgingAManyRowBatch_IssuesABoundedNumberOfStatements_NotOnePerRow()
    {
        // Library size is unbounded input, so a batch can hold as many rows as the library has files.
        // A per-row delete would make the purge itself the O(library) failure the retention exists to
        // prevent, so the statement count is asserted, not just the outcome.
        const int rows = 300;
        var counter = new CommandCountingInterceptor();
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        try
        {
            var options = new DbContextOptionsBuilder<CoveContext>()
                .UseSqlite(connection)
                .AddInterceptors(counter)
                .ReplaceService<IModelCacheKeyFactory, CoveModelCacheKeyFactory>()
                .Options;
            await using var db = new CoveContext(options, principalAccessor: null);
            await db.Database.EnsureCreatedAsync();

            await SeedBatchAsync(db, "run-old", Opened, rows);

            counter.Executed = 0;
            await new CoveRevertJournal(db).PurgeExpiredAsync(JustOutside);

            Assert.Empty(await RowRunIdsAsync(db, "run-old"));
            Assert.InRange(counter.Executed, 1, 8);
        }
        finally
        {
            await connection.DisposeAsync();
        }
    }

    private static async Task<CoveRevertJournal> SeedBatchAsync(
        DbContext db, string runId, DateTime openedAt, int rows)
    {
        var journal = new CoveRevertJournal(db);
        await journal.BeginBatchAsync(runId, RenamerFileKind.Video, openedAt);

        for (int i = 1; i <= rows; i++)
        {
            await journal.AppendAsync(
                new RevertRow(runId, Seq: 0, EntityId: 100 + i, FileId: 200 + i, $"/media/{runId}/{i}.mkv", ""));
        }

        return journal;
    }

    // Read the tables directly rather than through the port: what the purge must leave behind is a
    // storage fact, and the port's own readers only ever answer about the NEWEST batch.
    private static Task<List<string>> BatchRunIdsAsync(DbContext db, string runId) =>
        db.Set<RevertBatchEntity>().AsNoTracking().Where(b => b.RunId == runId).Select(b => b.RunId).ToListAsync();

    private static Task<List<long>> RowRunIdsAsync(DbContext db, string runId) =>
        db.Set<RevertRowEntity>().AsNoTracking().Where(r => r.RunId == runId).Select(r => r.Seq).ToListAsync();

    /// <summary>Counts every executed command so "bounded, not one per row" is measured rather than assumed.</summary>
    private sealed class CommandCountingInterceptor : DbCommandInterceptor
    {
        public int Executed { get; set; }

        public override ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken ct = default)
        {
            Executed++;
            return ValueTask.FromResult(result);
        }

        public override DbDataReader ReaderExecuted(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
        {
            Executed++;
            return result;
        }

        public override ValueTask<int> NonQueryExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken ct = default)
        {
            Executed++;
            return ValueTask.FromResult(result);
        }

        public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
        {
            Executed++;
            return result;
        }
    }
}
