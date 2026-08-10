using Cove.Extensions.Shared;
using Cove.Plugins;
using Renamer.Planner;

namespace Renamer.Execution;

/// <summary>
/// The revert log: an append-only record of every successful renamer, persisted via Cove's
/// <see cref="IExtensionStore"/> (mirroring <c>OptionsStore</c>: a single appended blob under one
/// key, newline-delimited), so the log survives the process. Rows are also held in memory on this
/// instance — exposed via <see cref="Rows"/> so a caller/test can read the run's log without going
/// back to the store.
///
/// ON-DISK FORMAT. The blob is a newline-delimited list of two line shapes:
/// <list type="bullet">
/// <item>A <em>batch header</em> begins with <c>#</c> and records the run-level
/// <c>runId</c>, a server-written UTC-ticks timestamp, the run's <see cref="RenamerFileKind"/>, and a
/// lifecycle marker (the batch is either still replayable or already spent). The kind is single per
/// run (a batch loops many ids of ONE entity type), so it lives on the header.</item>
/// <item>A <em>data row</em> is <c>entityId|fileId|old</c>. The entityId is the PARENT entity id
/// (e.g. the Video id) that the forward executor published its reindex event for; it VARIES per item
/// within a run (a batch spans N entities), so it lives on the row, never the header. The fileId is
/// the physical file row; entityId and fileId differ in the normal case. The file's CURRENT path is
/// absent because Cove's database is authoritative for it (see <see cref="UndoReplayer"/>).</item>
/// </list>
/// The leading <c>#</c> cannot begin an integer entityId, so headers and rows are unambiguous.
///
/// SIZE. The host serves every value an extension owns as a single payload, so an unbounded blob here
/// breaks every settings read for the extension. Bounded to <see cref="MaxJournalledFiles"/> rows of
/// ONE batch; a run over that cap is not journalled at all (<see cref="SuppressAsync"/>).
///
/// TOLERANT READ. Both parsers read a PREFIX of a line, so a row carrying extra trailing fields still
/// yields its entry, and a blob with no header at all is one implicit, still-replayable
/// <see cref="RenamerFileKind.Video"/> batch with each entry's EntityId set to its FileId.
///
/// PARSING is defensive: a header or data line with missing/short fields or a non-integer
/// entityId/fileId is skipped, never thrown (mirrors <c>RenamerJob.Decode</c>).
///
/// Takes the <see cref="IExtensionStore"/> directly (not <c>FullExtensionBase.Store</c>) so it is
/// unit-testable host-free against a <c>FakeStore</c>.
/// </summary>
public sealed class RevertLog : SingleWriterBlobStore
{
    /// <summary>The store key the appended, newline-delimited blob lives under.</summary>
    public const string Key = "revertlog";

    /// <summary>The store key holding the stamp that decides whether <see cref="Key"/> needs discarding.</summary>
    /// <remarks>
    /// A SEPARATE key on purpose. A journal written before the row cap can be hundreds of megabytes,
    /// and reading it is the failure being cleared — so the discard decision cannot be carried by the
    /// journal's own content. This value is a few bytes and is always safe to read.
    /// </remarks>
    public const string SchemaKey = "journal-schema";

    /// <summary>The stamp a journal written by this version carries.</summary>
    /// <remarks>Bump when a stored journal from an earlier version must be discarded rather than parsed.</remarks>
    public const string CurrentSchema = "2";

    /// <summary>The hard cap on how many FILES one batch may journal.</summary>
    /// <remarks>
    /// FILES, not entities: the request-level <c>MaxEntityIdsPerRequest</c> bounds a selection to 1000
    /// ENTITIES, and one entity can hold many files (a live fixture held 151 on one), so an entity cap
    /// bounds nothing here. A whole-library run takes no id array and is bounded by nothing at all.
    /// </remarks>
    public const int MaxJournalledFiles = 5000;

    /// <summary>True when a batch of <paramref name="fileCount"/> files is too large to journal.</summary>
    /// <remarks>The one definition of "over cap", read by both the preview and the batch core.</remarks>
    public static bool ExceedsCap(int fileCount) => fileCount > MaxJournalledFiles;

    // The field separator. Paths are forward-slash and never contain '|' on the platforms Cove runs.
    private const char FieldSep = '|';

    // Header line prefix + its lifecycle markers. A line beginning with this char is a batch header.
    private const char HeaderPrefix = '#';
    private const string HeaderTag = "#batch";
    private const string StatusOpen = "open";
    private const string StatusConsumed = "consumed";

    private readonly List<RevertEntry> _rows = [];

    // Latched by SuppressAsync. The instance is shared by every parallel worker's executor, so
    // latching it here makes "an over-cap batch writes no row" structural, not a caller's discipline.
    private bool _suppressed;

    public RevertLog(IExtensionStore store) : base(store, Key)
    {
    }

    /// <summary>One logged renamer row.</summary>
    /// <param name="EntityId">The PARENT entity id (e.g. Video id) the forward event was published for. Differs from <see cref="FileId"/> in the normal case.</param>
    /// <param name="FileId">The renamed physical file row's id.</param>
    /// <param name="OldPath">The path the file moved FROM (forward-slash).</param>
    public readonly record struct RevertEntry(int EntityId, int FileId, string OldPath);

    /// <summary>The data rows appended during this run, in append order (readable without the store).</summary>
    public IReadOnlyList<RevertEntry> Rows => _rows;

    /// <summary>A read-back batch: the run-level <see cref="Kind"/> (from the header) and its data rows, newest-first.</summary>
    /// <param name="Kind">The run's entity kind, read from the batch header.</param>
    /// <param name="Entries">The batch's data rows in REVERSE append order (so chained slots free correctly).</param>
    public sealed record RevertBatch(RenamerFileKind Kind, IReadOnlyList<RevertEntry> Entries);

    /// <summary>A lightweight summary of the most recent batch for the panel.</summary>
    /// <param name="RunId">The batch's run id ("" for a legacy blob).</param>
    /// <param name="Count">The batch's data-row count.</param>
    /// <param name="WrittenAtUtcTicks">The server-written UTC ticks when the batch opened (0 for a legacy blob).</param>
    /// <param name="Consumed">True iff the batch has already been spent.</param>
    public readonly record struct RevertBatchSummary(string RunId, int Count, long WrittenAtUtcTicks, bool Consumed);

    /// <summary>
    /// Opens a run by appending a batch header carrying <paramref name="runId"/>, a server-written
    /// UTC-ticks timestamp, <paramref name="kind"/>, and the still-replayable marker. The caller mints
    /// the runId (the job passes its run id); this method only records it. The timestamp is server
    /// time — NEVER a browser value (the summary is read later/elsewhere).
    /// </summary>
    public async Task BeginBatchAsync(string runId, RenamerFileKind kind, CancellationToken ct = default)
    {
        var line = $"{HeaderTag}{FieldSep}{runId}{FieldSep}{DateTime.UtcNow.Ticks}{FieldSep}{kind}{FieldSep}{StatusOpen}";
        // Held under the same gate as AppendAsync so every blob write is serialized. This runs once,
        // single-threaded, before any parallel append, so it is uncontended in practice.
        await RunExclusiveAsync(async () =>
        {
            // REPLACE, not append: the new header supersedes every earlier batch, and overwriting is
            // what makes the retained unit exactly ONE. Compacting instead could only drop up to the
            // second-newest, leaving two.
            await StoreBlobAsync(line, ct);
        }, ct);
    }

    /// <summary>
    /// Refuses to journal this run: clears the stored blob and makes every later
    /// <see cref="AppendAsync"/> on this instance a no-op.
    /// </summary>
    /// <remarks>
    /// Skipping only the header would be worse than doing nothing: a headerless blob reads back as one
    /// implicit still-open batch with each entity id taken for a file id. Clearing rather than leaving
    /// the earlier batch matters too — it would otherwise be offered as "the last rename" after a
    /// larger one has already moved those files.
    /// </remarks>
    public async Task SuppressAsync(CancellationToken ct = default)
    {
        await RunExclusiveAsync(async () =>
        {
            _suppressed = true;
            _rows.Clear();
            await StoreBlobAsync("", ct);
        }, ct);
    }

    /// <summary>
    /// Appends an <c>entityId|fileId|old</c> row both in memory and to the persisted blob,
    /// associating it with the currently-open batch (the last header written), or does nothing when
    /// the run was refused by <see cref="SuppressAsync"/>. The blob is read-modify-write (a tiny KV
    /// value) to keep the store contract identical to <c>OptionsStore</c>.
    /// </summary>
    public async Task AppendAsync(int entityId, int fileId, string oldPath, CancellationToken ct = default)
    {
        var entry = new RevertEntry(entityId, fileId, oldPath);
        // ONE critical section over the WHOLE mutation: the in-memory List.Add AND the blob
        // read-modify-write. Holding both under the gate keeps the persisted blob untorn and the
        // _rows list race-free even when many workers (or two jobs over the same key) append at once.
        // The _suppressed read is inside it for the same reason — workers can already be in flight.
        await RunExclusiveAsync(async () =>
        {
            if (_suppressed)
            {
                return;
            }

            _rows.Add(entry);
            await AppendLineAsync(Format(entry), ct);
        }, ct);
    }

    /// <summary>
    /// Reads the LAST batch that is still replayable and returns its kind + data rows in REVERSE
    /// append order, or null when there is no such batch. A legacy blob (no headers) is read as one
    /// implicit replayable <see cref="RenamerFileKind.Video"/> batch with each entry's EntityId = FileId.
    /// </summary>
    public async Task<RevertBatch?> ReadLastOpenBatchAsync(CancellationToken ct = default)
    {
        var blob = await LoadBlobAsync(ct);
        if (string.IsNullOrEmpty(blob))
        {
            return null;
        }

        var lines = blob.Split('\n');

        // Find the LAST header that is still replayable; collect its data rows (up to the next header).
        int lastOpenHeader = -1;
        bool anyHeader = false;
        for (int i = 0; i < lines.Length; i++)
        {
            if (!IsHeader(lines[i]))
            {
                continue;
            }

            anyHeader = true;
            if (TryParseHeader(lines[i], out _, out _, out var status) && status == StatusOpen)
            {
                lastOpenHeader = i;
            }
        }

        if (!anyHeader)
        {
            // Legacy flat blob: every parseable orphan row is one implicit Video batch, EntityId=FileId.
            var legacy = ParseDataRows(lines, 0, lines.Length, legacy: true);
            if (legacy.Count == 0)
            {
                return null;
            }

            legacy.Reverse();
            return new RevertBatch(RenamerFileKind.Video, legacy);
        }

        if (lastOpenHeader < 0)
        {
            return null;  // headers exist but none are replayable
        }

        // Re-parse the chosen header for its kind; a parse failure here would mean the line stopped
        // being a valid header between the scan above and now — treat it as "no open batch".
        if (!TryParseHeader(lines[lastOpenHeader], out _, out var kind, out _))
        {
            return null;
        }

        // Rows run from just after this header to the next header (or end).
        int end = lines.Length;
        for (int i = lastOpenHeader + 1; i < lines.Length; i++)
        {
            if (IsHeader(lines[i])) { end = i; break; }
        }

        var rows = ParseDataRows(lines, lastOpenHeader + 1, end, legacy: false);
        rows.Reverse();  // newest-first
        return new RevertBatch(kind, rows);
    }

    /// <summary>
    /// Returns the most recent batch's <see cref="RevertBatchSummary"/> (its run id, data-row count,
    /// open timestamp, and whether it is spent), or null when the blob is empty. For a legacy blob the
    /// run id is "" and the timestamp is 0.
    /// </summary>
    public async Task<RevertBatchSummary?> ReadLastBatchSummaryAsync(CancellationToken ct = default)
    {
        var blob = await LoadBlobAsync(ct);
        if (string.IsNullOrEmpty(blob))
        {
            return null;
        }

        var lines = blob.Split('\n');

        // The most recent batch is the one opened by the LAST header (replayable OR spent).
        int lastHeader = -1;
        for (int i = lines.Length - 1; i >= 0; i--)
        {
            if (IsHeader(lines[i]) && TryParseHeader(lines[i], out _, out _, out _)) { lastHeader = i; break; }
        }

        if (lastHeader < 0)
        {
            // Legacy blob: count its parseable data rows.
            int count = ParseDataRows(lines, 0, lines.Length, legacy: true).Count;
            return count == 0 ? null : new RevertBatchSummary("", count, 0, Consumed: false);
        }

        // The scan above only set lastHeader on a line that parsed; if it somehow no longer parses,
        // fall back to the legacy/no-batch reading rather than emitting a default-valued summary.
        if (!TryParseHeader(lines[lastHeader], out var runId, out _, out var status))
        {
            int count = ParseDataRows(lines, 0, lines.Length, legacy: true).Count;
            return count == 0 ? null : new RevertBatchSummary("", count, 0, Consumed: false);
        }

        long ticks = ParseHeaderTicks(lines[lastHeader]);
        int end = lines.Length;
        for (int i = lastHeader + 1; i < lines.Length; i++)
        {
            if (IsHeader(lines[i])) { end = i; break; }
        }
        int rowCount = ParseDataRows(lines, lastHeader + 1, end, legacy: false).Count;
        return new RevertBatchSummary(runId, rowCount, ticks, status == StatusConsumed);
    }

    /// <summary>
    /// Marks the batch with run id <paramref name="runId"/> as spent (read-modify-write the blob,
    /// rewrite that header's lifecycle marker) so a subsequent <see cref="ReadLastOpenBatchAsync"/>
    /// skips it, then compacts the dead batches away. A no-op if the run id is not found.
    /// </summary>
    public async Task MarkLastBatchConsumedAsync(string runId, CancellationToken ct = default)
    {
        // Runs its whole body under the gate: the consume rewrite is a blob read-modify-write on the
        // shared store key, so it must serialize against concurrent same-key appends exactly as every
        // other write does (a batch can be consumed by /undo while a NEW job appends to the same blob) —
        // an ungated rewrite here could tear an interleaved append.
        await RunExclusiveAsync(async () =>
        {
            var blob = await LoadBlobAsync(ct);
            if (string.IsNullOrEmpty(blob))
            {
                return;
            }

            var lines = blob.Split('\n');
            bool changed = false;
            for (int i = 0; i < lines.Length; i++)
            {
                if (IsHeader(lines[i]) && TryParseHeader(lines[i], out var rid, out var kind, out var status)
                    && rid == runId && status != StatusConsumed)
                {
                    long ticks = ParseHeaderTicks(lines[i]);
                    lines[i] = $"{HeaderTag}{FieldSep}{rid}{FieldSep}{ticks}{FieldSep}{kind}{FieldSep}{StatusConsumed}";
                    changed = true;
                }
            }

            if (changed)
            {
                // Stored THROUGH the retention hook, not written directly: what the blob is allowed to
                // keep is decided in one place, so the consume path cannot drift from it.
                await StoreBlobAsync(Compact(string.Join("\n", lines)), ct);
            }
        }, ct);
    }

    // ── persistence helper ────────────────────────────────────────────────────

    private async Task AppendLineAsync(string line, CancellationToken ct)
    {
        var existing = await LoadBlobAsync(ct);
        var updated = string.IsNullOrEmpty(existing) ? line : existing + "\n" + line;
        await StoreBlobAsync(updated, ct);
    }

    // ── compaction ─────────────────────────────────────────────────────────────

    /// <summary>Retention: the blob keeps the LAST batch only, from its header onward.</summary>
    /// <remarks>
    /// ONE BATCH is the retained unit, and with the per-batch row cap that is what gives the stored
    /// value a fixed ceiling; retaining every still-open batch made it a function of how many renames
    /// had gone un-undone. Nothing is narrowed by this — every undo read already targeted the last
    /// batch, so an older open batch was retained but unreachable. The last batch is kept even when
    /// CONSUMED because the panel reads it at rest to show the outcome of the rename just undone.
    /// <para>
    /// A pre-header blob is one implicit still-replayable batch and is returned unchanged. Surviving
    /// lines are a contiguous suffix, preserved byte-for-byte, so a tolerated-but-malformed row
    /// round-trips exactly as the defensive parsers already handle it. Never throws.
    /// </para>
    /// </remarks>
    protected override string Compact(string? blob)
    {
        if (string.IsNullOrEmpty(blob))
        {
            return "";
        }

        var lines = blob.Split('\n');

        int lastHeader = -1;
        for (int i = 0; i < lines.Length; i++)
        {
            if (IsHeader(lines[i]) && TryParseHeader(lines[i], out _, out _, out _))
            {
                lastHeader = i;
            }
        }

        return lastHeader < 0
            ? blob  // pre-header blob — one implicit still-replayable batch, never reshaped
            : string.Join("\n", lines[lastHeader..]);
    }

    // ── parsing (defensive — never throws on a bad line) ───────────────────────

    private static bool IsHeader(string line) => line.Length > 0 && line[0] == HeaderPrefix;

    /// <summary>Parses a <c>#batch|runId|ticks|kind|status</c> header. False (skip) if fewer than 5 fields. Unknown kind → Video.</summary>
    private static bool TryParseHeader(string line, out string runId, out RenamerFileKind kind, out string status)
    {
        runId = ""; kind = RenamerFileKind.Video; status = "";
        var parts = line.Split(FieldSep);
        if (parts.Length < 5)
        {
            return false;
        }

        runId = parts[1];
        if (!Enum.TryParse(parts[3], ignoreCase: true, out kind))
        {
            kind = RenamerFileKind.Video;
        }

        status = parts[4];
        return true;
    }

    private static long ParseHeaderTicks(string line)
    {
        var parts = line.Split(FieldSep);
        return parts.Length >= 3 && long.TryParse(parts[2], out var t) ? t : 0;
    }

    /// <summary>
    /// Parses the data rows in <c>lines[start..end)</c> (append order). When <paramref name="legacy"/>
    /// the row form is <c>fileId|old</c> and EntityId is set to FileId; otherwise
    /// <c>entityId|fileId|old</c>. Header lines and malformed/short lines are skipped.
    /// </summary>
    private static List<RevertEntry> ParseDataRows(string[] lines, int start, int end, bool legacy)
    {
        var rows = new List<RevertEntry>();
        for (int i = start; i < end; i++)
        {
            var line = lines[i];
            if (line.Length == 0 || IsHeader(line))
            {
                continue;
            }

            var parts = line.Split(FieldSep);

            if (legacy)
            {
                if (parts.Length < 2)
                {
                    continue;
                }

                if (!int.TryParse(parts[0], out var fileId))
                {
                    continue;
                }

                rows.Add(new RevertEntry(fileId, fileId, parts[1]));
            }
            else
            {
                if (parts.Length < 3)
                {
                    continue;
                }

                if (!int.TryParse(parts[0], out var entityId))
                {
                    continue;
                }

                if (!int.TryParse(parts[1], out var fileId))
                {
                    continue;
                }

                rows.Add(new RevertEntry(entityId, fileId, parts[2]));
            }
        }
        return rows;
    }

    /// <summary>Serializes one entry to its <c>entityId|fileId|old</c> wire form.</summary>
    private static string Format(RevertEntry e) =>
        $"{e.EntityId}{FieldSep}{e.FileId}{FieldSep}{e.OldPath}";
}
