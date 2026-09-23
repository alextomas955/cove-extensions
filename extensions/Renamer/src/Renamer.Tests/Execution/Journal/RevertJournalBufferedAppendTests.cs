using Microsoft.EntityFrameworkCore;
using Renamer.Execution;

namespace Renamer.Tests.Execution.Journal;

public sealed class RevertJournalBufferedAppendTests
{
    private static readonly DateTime Opened = new(2026, 9, 19, 10, 0, 0, DateTimeKind.Utc);

    private static RevertRow Row(string runId, int i) =>
        new(runId, Seq: 0, EntityId: 100 + i, FileId: 200 + i, $"/media/old/{i}.mkv", "");

    private static async Task AppendAsync(CoveRevertJournal journal, string runId, int rows)
    {
        for (int i = 0; i < rows; i++)
        {
            await journal.AppendAsync(Row(runId, i));
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(CoveRevertJournal.AppendFlushEvery - 1)]
    [InlineData(CoveRevertJournal.AppendFlushEvery)]
    [InlineData(CoveRevertJournal.AppendFlushEvery + 1)]
    [InlineData((CoveRevertJournal.AppendFlushEvery * 2) + 7)]
    public async Task AFinishedRun_HasEveryRowAndATallyThatMatches(int rows)
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        await using var _ = db;
        await using var __ = conn;

        await using (var journal = new CoveRevertJournal(db))
        {
            await journal.BeginBatchAsync("run", "op", RenamerFileKind.Video, Opened);
            await AppendAsync(journal, "run", rows);
        }

        // Read back through the storage rather than the instance that wrote it: a buffered row that
        // never left the instance is exactly the loss under test.
        Assert.Equal(rows, await db.Set<RevertRowEntity>().AsNoTracking().CountAsync());

        var batch = await db.Set<RevertBatchEntity>().AsNoTracking().SingleAsync();
        Assert.Equal(rows, batch.OriginalCount);

        // Every row is addressable, so the buffer did not collapse two appends onto one sequence.
        var seqs = await db.Set<RevertRowEntity>().AsNoTracking().Select(r => r.Seq).ToListAsync();
        Assert.Equal(rows, seqs.Distinct().Count());
    }

    [Fact]
    public async Task ARunUnderTheFlushSize_HasWrittenNothingYet_AndWritesItAllOnDisposal()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        await using var _ = db;
        await using var __ = conn;

        var journal = new CoveRevertJournal(db);
        await journal.BeginBatchAsync("run", "op", RenamerFileKind.Video, Opened);
        await AppendAsync(journal, "run", CoveRevertJournal.AppendFlushEvery - 1);

        // The saving is what a round-trip per file costs, so a run under the flush size must not have
        // done any of it yet. This is also the crash window: these rows would be lost with the process.
        Assert.Equal(0, await db.Set<RevertRowEntity>().AsNoTracking().CountAsync());

        await journal.DisposeAsync();

        Assert.Equal(
            CoveRevertJournal.AppendFlushEvery - 1,
            await db.Set<RevertRowEntity>().AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task AReadOnTheWritingInstance_SeesWhatIsStillBuffered()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        await using var _ = db;
        await using var __ = conn;

        await using var journal = new CoveRevertJournal(db);
        await journal.BeginBatchAsync("run", "op", RenamerFileKind.Video, Opened);
        await AppendAsync(journal, "run", rows: 3);

        // The undo panel reads the journal that a just-finished auto-rename wrote, so a read has to
        // answer over the buffer rather than over what happens to have been written.
        var target = await journal.ReadUndoTargetAsync();

        Assert.NotNull(target);
        Assert.Equal(3, target!.Value.OriginalCount);
        Assert.Equal(3, target.Value.Remaining);
        Assert.Equal(3, (await journal.ReadBatchPageAsync("run", long.MaxValue, limit: 100)).Count);
    }
}
