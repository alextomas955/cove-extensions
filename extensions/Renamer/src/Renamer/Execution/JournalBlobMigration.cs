using Cove.Plugins;

namespace Renamer.Execution;

// Moves an installation's stored undo journal, written by 0.3.0 and earlier, into the journal table
// once, then deletes both legacy keys. The deletion is the idempotency marker: a second run finds
// nothing and does nothing.
//
// A stamped blob is what 0.3.0 wrote: every batch it opened replaced the value with one header line,
// #batch|runId|ticks|kind|status, followed by at most 5000 entityId|fileId|oldPath rows. A malformed
// header or row is skipped, never thrown.
public static class JournalBlobMigration
{
    public const string Key = "revertlog";

    // The few-byte stamp that says whether Key may be parsed, read before the value itself.
    public const string SchemaKey = "journal-schema";

    public const string CurrentSchema = "2";

    private const char FieldSep = '|';

    // Runs the one-shot migration and returns how many rows it moved.
    //
    // Both keys are deleted whatever happens, including on a parse or insert failure. The host serves
    // every value an extension owns as one payload, so an oversized leftover makes every settings read
    // for this extension fail, in a state that survives uninstall and reinstall.
    public static async Task<int> RunAsync(
        IExtensionStore store, IRevertJournal journal, DateTime nowUtc, CancellationToken ct = default)
    {
        try
        {
            // An unstamped value predates the row cap and is discarded unread.
            if (await store.GetAsync(SchemaKey, ct) != CurrentSchema)
            {
                return 0;
            }

            var blob = await store.GetAsync(Key, ct);
            if (string.IsNullOrEmpty(blob))
            {
                return 0;
            }

            var lines = blob.Split('\n');
            if (!TryParseOpenHeader(lines[0], out var runId, out var kind, out var openedAt, nowUtc))
            {
                return 0;
            }

            var rows = lines.Skip(1).Select(ParseRow).OfType<RevertRow>().ToList();
            if (rows.Count == 0)
            {
                return 0;
            }

            // A migrated blob held one run, so the run is the operation it belongs to.
            await journal.BeginBatchAsync(runId, runId, kind, openedAt, ct);
            foreach (var row in rows)
            {
                await journal.AppendAsync(row with { RunId = runId }, ct);
            }

            return rows.Count;
        }
        finally
        {
            await store.DeleteAsync(Key, ct);
            await store.DeleteAsync(SchemaKey, ct);
        }
    }

    // True only for a header whose status is still "open"; a spent batch has nothing to undo. The
    // header's own timestamp is kept, so a pending undo keeps its real age against the retention window.
    // An unrecognised kind reads as Video.
    private static bool TryParseOpenHeader(
        string line, out string runId, out RenamerFileKind kind, out DateTime openedAt, DateTime nowUtc)
    {
        var parts = line.Split(FieldSep);
        runId = parts.Length > 1 ? parts[1] : "";
        kind = parts.Length > 3 && Enum.TryParse(parts[3], ignoreCase: true, out RenamerFileKind k)
            ? k
            : RenamerFileKind.Video;
        openedAt = parts.Length > 2 && long.TryParse(parts[2], out var ticks) && ticks > 0
            && ticks <= DateTime.MaxValue.Ticks
            ? new DateTime(ticks, DateTimeKind.Utc)
            : nowUtc;
        return parts.Length >= 5 && line[0] == '#' && runId.Length > 0 && parts[4] == "open";
    }

    // The path is the last field and may itself contain the separator, so everything past the second
    // separator is rejoined.
    private static RevertRow? ParseRow(string line)
    {
        var parts = line.Split(FieldSep);
        return parts.Length >= 3
            && int.TryParse(parts[0], out var entityId)
            && int.TryParse(parts[1], out var fileId)
            ? new RevertRow("", Seq: 0, entityId, fileId, string.Join(FieldSep, parts, 2, parts.Length - 2), "")
            : null;
    }
}
