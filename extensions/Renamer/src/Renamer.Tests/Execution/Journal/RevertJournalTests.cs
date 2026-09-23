using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Renamer.Execution;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Execution.Journal;

public sealed class RevertJournalTests
{
    private static readonly DateTime Opened = new(2026, 8, 3, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task AppendedRows_ReadBackNewestFirst()
    {
        // Newest-first is a correctness requirement, not presentation: one run can rename A→B and then
        // B→C, so reversing in reverse-append order is what frees each slot before the next row needs it.
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        await using var _ = db;
        await using var __ = conn;
        var journal = await SeedBatchAsync(db, "run-1", 3);

        var batch = await JournalPageReader.ReadWholeUndoTargetAsync(journal);

        Assert.NotNull(batch);
        Assert.Equal("run-1", batch.RunId);
        Assert.Equal(RenamerFileKind.Video, batch.Kind);
        Assert.Equal([3L, 2L, 1L], batch.Rows.Select(r => r.Seq));
        Assert.Equal(["/media/old/3.mkv", "/media/old/2.mkv", "/media/old/1.mkv"], batch.Rows.Select(r => r.OldPath));
    }

    [Fact]
    public async Task OpeningBatches_LeavesNoneTracked_SoAWholeLibraryRunHoldsNoneOfThem()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        await using var _ = db;
        await using var __ = conn;
        await using var journal = new CoveRevertJournal(db);

        await journal.BeginBatchAsync("run-1", "op", RenamerFileKind.Video, Opened);
        await journal.BeginBatchAsync("run-2", "op", RenamerFileKind.Video, Opened);

        Assert.Empty(db.ChangeTracker.Entries<RevertBatchEntity>());
        Assert.Equal(2, await db.Set<RevertBatchEntity>().CountAsync());
    }

    [Fact]
    public async Task RetiringOneRow_LeavesTheOthersPending()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        await using var _ = db;
        await using var __ = conn;
        var journal = await SeedBatchAsync(db, "run-1", 3);

        await journal.DeleteRowAsync("run-1", seq: 2, unrestorable: false);

        var batch = await JournalPageReader.ReadWholeUndoTargetAsync(journal);
        Assert.NotNull(batch);
        Assert.Equal([3L, 1L], batch.Rows.Select(r => r.Seq));
    }

    [Fact]
    public async Task WhenEveryRowIsRetired_TheAggregateStillDescribesTheBatch()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        await using var _ = db;
        await using var __ = conn;
        var journal = await SeedBatchAsync(db, "run-1", 3);

        for (long seq = 1; seq <= 3; seq++)
        {
            await journal.DeleteRowAsync("run-1", seq, unrestorable: false);
        }

        // Nothing left to offer…
        Assert.Null(await JournalPageReader.ReadWholeUndoTargetAsync(journal));

        // …and yet the panel can still say what the run was: the aggregate outlives its rows.
        var summary = await journal.ReadUndoTargetAsync();
        Assert.NotNull(summary);
        Assert.Equal("run-1", summary.Value.OperationId);
        Assert.Equal(Opened.Ticks, summary.Value.OpenedAtUtcTicks);
        Assert.Equal(3, summary.Value.OriginalCount);
        Assert.Equal(3, summary.Value.RestoredCount);
        Assert.Equal(0, summary.Value.Remaining);
    }

    [Fact]
    public async Task TheFlagChoosesWhichCounterMoves_AndTheOriginalCountNeverDoes()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        await using var _ = db;
        await using var __ = conn;
        var journal = await SeedBatchAsync(db, "run-1", 3);

        await journal.DeleteRowAsync("run-1", seq: 1, unrestorable: false);
        await journal.DeleteRowAsync("run-1", seq: 2, unrestorable: true);

        var summary = await journal.ReadUndoTargetAsync();
        Assert.NotNull(summary);
        Assert.Equal(3, summary.Value.OriginalCount);
        Assert.Equal(1, summary.Value.RestoredCount);
        Assert.Equal(1, summary.Value.UnrestorableCount);
        Assert.Equal(1, summary.Value.Remaining);
    }

    [Fact]
    public async Task RetiringARowThatIsAlreadyGone_ChangesNothing()
    {
        // An undo can be retried, and a retry re-walks rows it may already have settled.
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        await using var _ = db;
        await using var __ = conn;
        var journal = await SeedBatchAsync(db, "run-1", 1);

        await journal.DeleteRowAsync("run-1", seq: 1, unrestorable: false);
        await journal.DeleteRowAsync("run-1", seq: 1, unrestorable: false);

        var summary = await journal.ReadUndoTargetAsync();
        Assert.NotNull(summary);
        Assert.Equal(1, summary.Value.RestoredCount);
        Assert.Equal(0, summary.Value.Remaining);
    }

    [Fact]
    public async Task TheNewestBatchIsTheOneOffered_EvenWhileAnOlderOneStillHasRows()
    {
        // The auto-renamer opens its own batch per metadata edit, so several batches with rows left is
        // the ordinary state, not an edge case.
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        await using var _ = db;
        await using var __ = conn;

        await SeedBatchAsync(db, "run-old", 2, Opened);
        var journal = await SeedBatchAsync(db, "run-new", 1, Opened.AddMinutes(5));

        var batch = await JournalPageReader.ReadWholeUndoTargetAsync(journal);
        Assert.NotNull(batch);
        Assert.Equal("run-new", batch.RunId);
        Assert.Single(batch.Rows);
    }

    [Fact]
    public async Task ThePurge_OverAnEmptyJournal_CompletesAndChangesNothing()
    {
        // It runs on every batch open, so the ordinary case is a journal with nothing expired in it -
        // and on a fresh install, nothing in it at all. That path must be a quiet no-op, not a throw.
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        await using var _ = db;
        await using var __ = conn;

        var journal = new CoveRevertJournal(db);

        await journal.PurgeExpiredAsync(Opened);

        Assert.Null(await journal.ReadUndoTargetAsync());
        Assert.Null(await JournalPageReader.ReadWholeUndoTargetAsync(journal));
    }

    private static async Task<CoveRevertJournal> SeedBatchAsync(
        DbContext db, string runId, int rows, DateTime? openedAt = null)
    {
        var journal = new CoveRevertJournal(db);
        await journal.BeginBatchAsync(runId, runId, RenamerFileKind.Video, openedAt ?? Opened);

        for (int i = 1; i <= rows; i++)
        {
            await journal.AppendAsync(
                new RevertRow(runId, Seq: 0, EntityId: 100 + i, FileId: 200 + i, $"/media/old/{i}.mkv", ""));
        }

        return journal;
    }
}
