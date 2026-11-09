using Cove.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Renamer.Execution;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Execution.Journal;

public sealed class RevertJournalRetentionTests
{
    private static readonly DateTime Opened = new(2026, 8, 3, 10, 0, 0, DateTimeKind.Utc);

    // Just past the window: everything opened at Opened is expired at this moment.
    private static DateTime JustOutside => Opened + CoveRevertJournal.RetentionWindow + TimeSpan.FromHours(1);

    // Just inside the window: everything opened at Opened still survives.
    private static DateTime JustInside => Opened + CoveRevertJournal.RetentionWindow - TimeSpan.FromHours(1);

    [Theory]
    [MemberData(nameof(MomentsTheNextBatchOpens))]
    public async Task TheOlderBatchIsPurgedOnlyWhenTheNextBatchOpensOutsideTheWindow(
        DateTime now, int survivingBatches, int survivingRows)
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        await using var _ = db;
        await using var __ = conn;

        await SeedBatchAsync(db, "run-old", Opened, rows: 3);

        // Opening the next batch is the only trigger - no timer, no scheduler, no second call site.
        await new CoveRevertJournal(db).BeginBatchAsync("run-new", "run-new", RenamerFileKind.Video, now);

        Assert.Equal(survivingBatches, (await BatchRunIdsAsync(db, "run-old")).Count);
        Assert.Equal(survivingRows, (await RowRunIdsAsync(db, "run-old")).Count);

        // The batch that did the purging is untouched by it.
        Assert.Single(await BatchRunIdsAsync(db, "run-new"));
    }

    public static TheoryData<DateTime, int, int> MomentsTheNextBatchOpens() => new()
    {
        // Past the window: the older batch goes whole, its batch row and all of its rows.
        { JustOutside, 0, 0 },

        // One hour inside the window: opening a batch must not replace the stored journal, or one
        // background auto-rename would destroy the undo record of a deliberate 500-file run.
        { JustInside, 1, 3 },
    };

    [Fact]
    public async Task APartiallyRestoredBatchOutsideTheWindow_LosesItsRemainingRowsAndItsBatchRow()
    {
        // Half a batch surviving would make a later undo silently partial, with nothing to say so - so
        // the purge keys on the batch, and a batch that is already half spent goes with the same sweep.
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        await using var _ = db;
        await using var __ = conn;

        var journal = await SeedBatchAsync(db, "run-old", Opened, rows: 4);
        await journal.DeleteRowAsync("run-old", seq: 1, unrestorable: false);
        await journal.DeleteRowAsync("run-old", seq: 2, unrestorable: true);
        Assert.Equal(2, (await RowRunIdsAsync(db, "run-old")).Count);

        await new CoveRevertJournal(db).BeginBatchAsync("run-new", "run-new", RenamerFileKind.Video, JustOutside);

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
        Assert.Null(await journal.ReadUndoTargetAsync());
    }

    [Fact]
    public async Task OnePurge_RemovesOnlyTheBatchOpenedBeforeTheCutoff()
    {
        // Two batches, one either side of the same cutoff, purged in one call: the survivor proves the
        // purge selects by each batch's own open timestamp rather than sweeping everything it finds.
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        await using var _ = db;
        await using var __ = conn;

        var now = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        await SeedBatchAsync(db, "run-expired", now - CoveRevertJournal.RetentionWindow - TimeSpan.FromMinutes(1), rows: 2);
        await SeedBatchAsync(db, "run-live", now - CoveRevertJournal.RetentionWindow + TimeSpan.FromMinutes(1), rows: 2);

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

            counter.ReaderCount = 0;
            counter.NonQueryTexts.Clear();
            await new CoveRevertJournal(db).PurgeExpiredAsync(JustOutside);

            Assert.Empty(await RowRunIdsAsync(db, "run-old"));
            Assert.InRange(counter.ReaderCount + counter.NonQueryTexts.Count, 1, 8);
        }
        finally
        {
            await connection.DisposeAsync();
        }
    }

    // Seeds a finished run: the journal that wrote the batch is disposed, which is what puts its
    // buffered rows in the database. The returned journal is a fresh one over the same context, as a
    // later undo or purge would have.
    private static async Task<CoveRevertJournal> SeedBatchAsync(
        DbContext db, string runId, DateTime openedAt, int rows)
    {
        await using (var writing = new CoveRevertJournal(db))
        {
            await writing.BeginBatchAsync(runId, runId, RenamerFileKind.Video, openedAt);

            for (int i = 1; i <= rows; i++)
            {
                await writing.AppendAsync(
                    new RevertRow(runId, Seq: 0, EntityId: 100 + i, FileId: 200 + i, $"/media/{runId}/{i}.mkv", ""));
            }
        }

        return new CoveRevertJournal(db);
    }

    // Read the tables directly rather than through the port: what the purge must leave behind is a
    // storage fact, and the port's own readers only ever answer about the newest batch.
    private static Task<List<string>> BatchRunIdsAsync(DbContext db, string runId) =>
        db.Set<RevertBatchEntity>().AsNoTracking().Where(b => b.RunId == runId).Select(b => b.RunId).ToListAsync();

    private static Task<List<long>> RowRunIdsAsync(DbContext db, string runId) =>
        db.Set<RevertRowEntity>().AsNoTracking().Where(r => r.RunId == runId).Select(r => r.Seq).ToListAsync();
}
