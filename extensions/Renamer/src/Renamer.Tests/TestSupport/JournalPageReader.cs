using Renamer.Planner;

namespace Renamer.Tests.TestSupport;

/// <summary>
/// Pages a whole batch out of an <see cref="IRevertJournal"/> into one list, for tests that want to
/// assert over all of it at once.
/// </summary>
/// <remarks>
/// THIS is where materializing a whole batch lives now, and deliberately not on the port: a production
/// read that returned all of a batch would tie memory to whatever the cap on a batch is at the time,
/// while here the fixtures have a known, small size and a call site that can never reach production.
/// <para>
/// It pages through the same public reads an undo uses, so a cursor bug shows up here too rather than
/// being papered over by a shortcut.
/// </para>
/// </remarks>
public static class JournalPageReader
{
    /// <summary>A small default page size, so a helper call crosses a page boundary on ordinary fixtures.</summary>
    public const int TestPageSize = 64;

    /// <summary>
    /// The batch an undo would act on with every row it still holds, or null when no batch has any left.
    /// </summary>
    /// <remarks>
    /// Null-for-nothing-replayable is the shape assertions about "nothing left to offer" are written
    /// against: the undo target itself falls back to a settled batch so its aggregate stays readable,
    /// which is a question about the aggregate, not about the rows.
    /// </remarks>
    public static async Task<RevertBatch?> ReadWholeUndoTargetAsync(
        IRevertJournal journal, int pageSize = TestPageSize, CancellationToken ct = default)
    {
        var target = await journal.ReadUndoTargetAsync(ct);
        if (target is null)
        {
            return null;
        }

        var rows = await ReadAllRowsAsync(journal, target.Value.RunId, pageSize, ct);
        return rows.Count == 0 ? null : new RevertBatch(target.Value.RunId, target.Value.Kind, rows);
    }

    /// <summary>Every row <paramref name="runId"/> still holds, newest-first, across as many pages as it takes.</summary>
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
