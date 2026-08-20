using Cove.Plugins;
using Renamer.Planner;

namespace Renamer.Execution;

/// <summary>
/// Moves an existing installation's stored undo journal into the journal table exactly once, then
/// deletes both legacy keys.
/// </summary>
/// <remarks>
/// ONE-WAY. The source keys are deleted, and that deletion IS the idempotency marker — a second run
/// finds nothing and does nothing. A separate "already migrated" flag was declined: a second marker is
/// a second thing that can disagree with the first, and the state it would describe is already visible.
/// <para>
/// It reads through <see cref="RevertLog"/>'s existing tolerant parsers rather than a reader of its
/// own. A one-shot path that runs on somebody else's data is the worst possible place to have two
/// implementations of one format.
/// </para>
/// </remarks>
public static class JournalBlobMigration
{
    /// <summary>How many stored lines are parsed, and inserted, before the next slice is looked at.</summary>
    /// <remarks>
    /// The stored value is unbounded input — an unbounded journal is the incident the row cap was added
    /// for — and the tolerant reader has to hold it whole to parse it at all. Slicing the insert is what
    /// stops a SECOND structure of that size existing beside it. 500 keeps the live slice trivially
    /// small while making even a full legacy journal a handful of passes rather than one huge one.
    /// </remarks>
    public const int LinesPerChunk = 500;

    /// <summary>Runs the one-shot migration and returns how many rows it moved.</summary>
    /// <param name="store">The host key-value store holding the two legacy keys.</param>
    /// <param name="journal">The journal the parsed rows are inserted through.</param>
    /// <param name="nowUtc">The moment a batch is stamped with when it carries no timestamp of its own.</param>
    /// <param name="ct">Cancellation.</param>
    /// <remarks>
    /// BOTH KEYS ARE DELETED WHATEVER HAPPENS, including on a parse or insert failure. Leaving them is
    /// not the safe fallback it looks like: the host serves every value an extension owns as one
    /// payload, so an oversized leftover makes every settings read for this extension fail — a state
    /// that survives uninstall and reinstall and has needed SQL to clear. A migration that gives up and
    /// leaves the data behind chooses the worse of the two outcomes.
    /// </remarks>
    public static async Task<int> RunAsync(
        IExtensionStore store, IRevertJournal journal, DateTime nowUtc, CancellationToken ct = default)
    {
        try
        {
            // The stamp says whether the stored journal may be PARSED, which is what it has always
            // meant. One written under a shape this code does not read is discarded rather than guessed
            // at — and it is never read, because a pre-cap value is exactly the thing not to load.
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

                // Opened on the first slice that actually yields a row, so a value with nothing
                // readable in it leaves no empty batch behind claiming an undo it cannot deliver.
                if (moved == 0)
                {
                    await journal.BeginBatchAsync(runId, located.Kind, OpenedAt(located, nowUtc), ct);
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

    /// <summary>The moment the migrated batch is stamped with.</summary>
    /// <remarks>
    /// The header's OWN timestamp wherever there is one: a pending undo has to keep its real age against
    /// the retention window, and restamping it now would silently extend a batch that should already
    /// have expired. Where the stored form carried no timestamp — the pre-header shape did not — the age
    /// is unknown, and an unknown age is given the full window rather than none. Treating it as expired
    /// would delete a live undo on the next batch open with nothing anywhere to say so, which is exactly
    /// the silent loss this journal exists to prevent.
    /// </remarks>
    private static DateTime OpenedAt(RevertLog.LegacyBatch located, DateTime nowUtc) =>
        located.WrittenAtUtcTicks > 0 && located.WrittenAtUtcTicks <= DateTime.MaxValue.Ticks
            ? new DateTime(located.WrittenAtUtcTicks, DateTimeKind.Utc)
            : nowUtc;
}
