using Cove.Plugins;
using Renamer.Planner;

namespace Renamer.Execution;

// Moves an existing installation's stored undo journal into the journal table once, then deletes both
// legacy keys. The deletion is the idempotency marker: a second run finds nothing and does nothing.
//
// It reads through RevertLog's tolerant parsers, so this one-shot path over somebody else's data has
// one implementation of the legacy format.
public static class JournalBlobMigration
{
    // How many stored lines are parsed, and inserted, before the next slice is looked at. The stored
    // value is unbounded input and the tolerant reader has to hold it whole to parse it, so slicing the
    // insert keeps a second structure of that size from existing beside it.
    public const int LinesPerChunk = 500;

    // Runs the one-shot migration and returns how many rows it moved. nowUtc stamps a batch that
    // carries no timestamp of its own.
    //
    // Both keys are deleted whatever happens, including on a parse or insert failure. The host serves
    // every value an extension owns as one payload, so an oversized leftover makes every settings read
    // for this extension fail, in a state that survives uninstall and reinstall.
    public static async Task<int> RunAsync(
        IExtensionStore store, IRevertJournal journal, DateTime nowUtc, CancellationToken ct = default)
    {
        try
        {
            // The stamp says whether the stored journal may be parsed. One written under a shape this
            // code does not read is discarded unread, because a pre-cap value is the thing not to load.
            if (await store.GetAsync(RevertLog.SchemaKey, ct) != RevertLog.CurrentSchema)
            {
                return 0;
            }

            var blob = await store.GetAsync(RevertLog.Key, ct);
            if (string.IsNullOrEmpty(blob))
            {
                return 0;
            }

            var lines = RevertLog.SplitLines(blob);
            if (RevertLog.LocateLastOpenBatch(lines) is not { } located)
            {
                return 0;
            }

            string runId = string.IsNullOrEmpty(located.RunId)
                ? "legacy-" + Guid.NewGuid().ToString("N")
                : located.RunId;

            int moved = 0;
            for (int start = located.RowStart; start < located.RowEnd; start += LinesPerChunk)
            {
                int end = Math.Min(start + LinesPerChunk, located.RowEnd);
                var rows = RevertLog.ParseRows(lines, start, end, located.Headerless);
                if (rows.Count == 0)
                {
                    continue;
                }

                // Opened on the first slice that yields a row, so a value with nothing readable in it
                // leaves no empty batch claiming an undo it cannot deliver.
                if (moved == 0)
                {
                    // A migrated blob held one run, so the run is the operation it belongs to.
                    await journal.BeginBatchAsync(
                        runId, runId, located.Kind, OpenedAt(located, nowUtc), ct);
                }

                foreach (var row in rows)
                {
                    await journal.AppendAsync(
                        new RevertRow(runId, Seq: 0, row.EntityId, row.FileId, row.OldPath, ""), ct);
                }

                moved += rows.Count;
            }

            return moved;
        }
        finally
        {
            await store.DeleteAsync(RevertLog.Key, ct);
            await store.DeleteAsync(RevertLog.SchemaKey, ct);
        }
    }

    // The header's own timestamp wherever there is one: a pending undo keeps its real age against the
    // retention window, and restamping it now would extend a batch that should already have expired.
    // The pre-header stored shape carried no timestamp, and an unknown age is given the full window.
    // Treating it as expired would delete a live undo at the next batch open with nothing to say so.
    private static DateTime OpenedAt(RevertLog.LegacyBatch located, DateTime nowUtc) =>
        located.WrittenAtUtcTicks > 0 && located.WrittenAtUtcTicks <= DateTime.MaxValue.Ticks
            ? new DateTime(located.WrittenAtUtcTicks, DateTimeKind.Utc)
            : nowUtc;
}
