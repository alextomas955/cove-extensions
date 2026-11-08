using Renamer.Execution;

namespace Renamer.Tests.TestSupport;

// Pages a whole batch out of an IRevertJournal into one list, for tests that assert over all of it
// at once. It lives here and deliberately not on the port: a production read that returned all of a
// batch would tie memory to the batch size, while the fixtures here are small and this call site
// can never reach production. It pages through the same public reads an undo uses, so a cursor bug
// shows up here too.
public static class JournalPageReader
{
    // A small default page size, so a helper call crosses a page boundary on ordinary fixtures.
    public const int TestPageSize = 64;

    // The newest batch of the operation an undo would act on, with every row it still holds, or
    // null when that operation has no batch with rows left. The undo target itself falls back to a
    // settled operation so its aggregate stays readable, which is a question about the aggregate
    // rather than about the rows.
    public static async Task<RevertBatch?> ReadWholeUndoTargetAsync(
        IRevertJournal journal, int pageSize = TestPageSize, CancellationToken ct = default)
    {
        var target = await journal.ReadUndoTargetAsync(ct);
        if (target is null)
        {
            return null;
        }

        var batch = await journal.ReadNextBatchAsync(
            target.Value.OperationId, IRevertJournal.FirstBatchTicks, IRevertJournal.FirstBatchRunId, ct);
        if (batch is null)
        {
            return null;
        }

        var rows = await ReadAllRowsAsync(journal, batch.Value.RunId, pageSize, ct);
        return rows.Count == 0 ? null : new RevertBatch(batch.Value.RunId, batch.Value.Kind, rows);
    }

    // Every row runId still holds, newest-first, across as many pages as it takes.
    public static async Task<IReadOnlyList<RevertRow>> ReadAllRowsAsync(
        IRevertJournal journal, string runId, int pageSize = TestPageSize, CancellationToken ct = default)
    {
        var all = new List<RevertRow>();
        long cursor = long.MaxValue;

        while (true)
        {
            var page = await journal.ReadBatchPageAsync(runId, cursor, pageSize, ct);
            if (page.Count == 0)
            {
                return all;
            }

            all.AddRange(page);
            cursor = page[^1].Seq;
        }
    }
}
