using System.Collections.Concurrent;
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
/// <item>A <em>data row</em> is <c>entityId|fileId|old|new</c>. The entityId is the PARENT entity id
/// (e.g. the Video id) that the forward executor published its reindex event for; it VARIES per item
/// within a run (a batch spans N entities), so it lives on the row, never the header. The fileId is
/// the physical file row; entityId and fileId differ in the normal case.</item>
/// </list>
/// The leading <c>#</c> cannot begin an integer entityId, so headers and rows are unambiguous.
///
/// BACKWARD-READ. A legacy blob written before this format (flat <c>fileId|old|new</c> rows with no
/// header) still parses: all such orphan rows are treated as one implicit, still-replayable
/// <see cref="RenamerFileKind.Video"/> batch, with each entry's EntityId set to its FileId as the
/// documented best-effort fallback. Fresh writes always carry the entityId + a header kind.
///
/// PARSING is defensive: a header or data line with missing/short fields or a non-integer
/// entityId/fileId is skipped, never thrown (mirrors <c>RenamerJob.Decode</c>).
///
/// Takes the <see cref="IExtensionStore"/> directly (not <c>FullExtensionBase.Store</c>) so it is
/// unit-testable host-free against a <c>FakeStore</c>.
/// </summary>
public sealed class RevertLog
{
    /// <summary>The store key the appended, newline-delimited blob lives under.</summary>
    public const string Key = "revertlog";

    // The field separator. Paths are forward-slash and never contain '|' on the platforms Cove runs.
    private const char FieldSep = '|';

    // Header line prefix + its lifecycle markers. A line beginning with this char is a batch header.
    private const char HeaderPrefix = '#';
    private const string HeaderTag = "#batch";
    private const string StatusOpen = "open";
    private const string StatusConsumed = "consumed";

    // SINGLE-WRITER SERIALIZATION. The persisted blob is read-modify-write (GetAsync → concat →
    // SetAsync) and the in-memory row list is a plain List, so concurrent appends would tear the
    // blob (a dropped/interleaved line) and race the List. A batch may run many renames in parallel,
    // and two batch jobs can run at once (the job is enqueued non-exclusively), so appends must
    // serialize BOTH within one instance AND across instances writing the SAME store key.
    //
    // The gate is keyed on the store Key. Because every instance shares one Key, a single shared
    // SemaphoreSlim already serializes every append process-wide — covering both the intra-batch and
    // the inter-job (separate-instance, same-key) cases. Keying on Key (rather than a single global
    // lock) keeps this correct if a future Key ever varies per store. The semaphore is process-
    // lifetime by design and is intentionally never disposed; RevertLog stays non-IDisposable.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> StoreGates = new();

    private readonly IExtensionStore _store;
    private readonly List<RevertEntry> _rows = [];
    private readonly SemaphoreSlim _gate = StoreGates.GetOrAdd(Key, _ => new SemaphoreSlim(1, 1));

    public RevertLog(IExtensionStore store) => _store = store;

    /// <summary>One logged renamer row.</summary>
    /// <param name="EntityId">The PARENT entity id (e.g. Video id) the forward event was published for. Differs from <see cref="FileId"/> in the normal case.</param>
    /// <param name="FileId">The renamed physical file row's id.</param>
    /// <param name="OldPath">The path the file moved FROM (forward-slash).</param>
    /// <param name="NewPath">The path the file moved TO (forward-slash).</param>
    public readonly record struct RevertEntry(int EntityId, int FileId, string OldPath, string NewPath);

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
        await _gate.WaitAsync(ct);
        try
        {
            // COMPACT-THEN-APPEND. Drop the now-dead history before the new header lands, so this run's
            // header and its subsequent AppendAsync rows concatenate onto the live tail — an append is
            // O(last open batch), not O(total history). keepTrailingConsumed is false here: a
            // most-recent CONSUMED batch loses its panel role to the header about to be written, so it is
            // dropped; still-OPEN batches (later undo targets) are always kept.
            var existing = await _store.GetAsync(Key, ct);
            var compacted = Compact(existing, keepTrailingConsumed: false);
            if (compacted != existing)
            {
                await _store.SetAsync(Key, compacted, ct);
            }

            await AppendLineAsync(line, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Appends an <c>entityId|fileId|old|new</c> row both in memory and to the persisted blob,
    /// associating it with the currently-open batch (the last header written). The blob is
    /// read-modify-write (a tiny KV value) to keep the store contract identical to <c>OptionsStore</c>.
    /// </summary>
    public async Task AppendAsync(int entityId, int fileId, string oldPath, string newPath, CancellationToken ct = default)
    {
        var entry = new RevertEntry(entityId, fileId, oldPath, newPath);
        // ONE critical section over the WHOLE mutation: the in-memory List.Add AND the blob
        // read-modify-write. Holding both under the gate keeps the persisted blob untorn and the
        // _rows list race-free even when many workers (or two jobs over the same key) append at once.
        await _gate.WaitAsync(ct);
        try
        {
            _rows.Add(entry);
            await AppendLineAsync(Format(entry), ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Reads the LAST batch that is still replayable and returns its kind + data rows in REVERSE
    /// append order, or null when there is no such batch. A legacy blob (no headers) is read as one
    /// implicit replayable <see cref="RenamerFileKind.Video"/> batch with each entry's EntityId = FileId.
    /// </summary>
    public async Task<RevertBatch?> ReadLastOpenBatchAsync(CancellationToken ct = default)
    {
        var blob = await _store.GetAsync(Key, ct);
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
        var blob = await _store.GetAsync(Key, ct);
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
        await _gate.WaitAsync(ct);
        try
        {
            var blob = await _store.GetAsync(Key, ct);
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
                // MARK-THEN-COMPACT. After flipping the header to consumed, drop it (and any now-dead
                // older batches) so the stored footprint shrinks on consume instead of growing over the
                // install's life.
                await _store.SetAsync(Key, Compact(string.Join("\n", lines), keepTrailingConsumed: true), ct);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    // ── persistence helper ────────────────────────────────────────────────────

    private async Task AppendLineAsync(string line, CancellationToken ct)
    {
        var existing = await _store.GetAsync(Key, ct);
        var updated = string.IsNullOrEmpty(existing) ? line : existing + "\n" + line;
        await _store.SetAsync(Key, updated, ct);
    }

    // ── compaction ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the compacted blob: the LIVE TAIL from the LAST still-open header onward, with every
    /// earlier line dropped. If no header is open, the result depends on
    /// <paramref name="keepTrailingConsumed"/> — true keeps the LAST (consumed) header onward, false
    /// drops the whole blob to "".
    /// </summary>
    /// <remarks>
    /// Two reads constrain what may be dropped. Undo reads only the LAST OPEN batch
    /// (<see cref="ReadLastOpenBatchAsync"/> + <see cref="MarkLastBatchConsumedAsync"/> operate on "the
    /// last replayable batch"), so keeping from the last open header preserves every still-open batch —
    /// each is a live recovery path (including an earlier open batch that becomes the target again once a
    /// newer batch is consumed, and an all-skipped batch /undo deliberately left open for retry) and is
    /// never dropped. The panel reads the LAST batch, open OR consumed
    /// (<see cref="ReadLastBatchSummaryAsync"/>), to show its outcome. That split is why the caller
    /// chooses via <paramref name="keepTrailingConsumed"/>: the consume path passes true so a
    /// just-consumed most-recent batch survives at rest (else the panel would blank — a user-facing
    /// regression); the begin path passes false because the fresh open header it is about to write takes
    /// over the panel role, so the now-superseded consumed batch is dropped rather than stranded ahead of
    /// the new live tail. Everything before the kept header satisfies neither read and is a pure audit
    /// trail safe to drop; that is what bounds the footprint. A legacy no-header blob is one implicit
    /// still-replayable batch and is returned unchanged (backward-read invariant). Surviving lines are
    /// preserved byte-for-byte (a contiguous suffix, no re-serialization), so a tolerated-but-malformed
    /// row inside the live batch round-trips exactly as the defensive parsers already handle it. Never
    /// throws.
    /// </remarks>
    private static string Compact(string? blob, bool keepTrailingConsumed)
    {
        if (string.IsNullOrEmpty(blob))
        {
            return "";
        }

        var lines = blob.Split('\n');

        int lastOpenHeader = -1;
        int lastHeader = -1;
        for (int i = 0; i < lines.Length; i++)
        {
            if (!IsHeader(lines[i]) || !TryParseHeader(lines[i], out _, out _, out var status))
            {
                continue;
            }

            lastHeader = i;
            if (status == StatusOpen)
            {
                lastOpenHeader = i;
            }
        }

        if (lastHeader < 0)
        {
            return blob;  // legacy flat blob — one implicit still-replayable batch, never reshaped
        }

        if (lastOpenHeader >= 0)
        {
            return string.Join("\n", lines[lastOpenHeader..]);  // undo's target — always survives
        }

        // No open batch. Keep the last (consumed) one only for the panel at rest; drop it when a fresh
        // header is about to supersede it.
        return keepTrailingConsumed ? string.Join("\n", lines[lastHeader..]) : "";
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
    /// the row form is <c>fileId|old|new</c> and EntityId is set to FileId; otherwise
    /// <c>entityId|fileId|old|new</c>. Header lines and malformed/short lines are skipped.
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
                if (parts.Length < 3)
                {
                    continue;
                }

                if (!int.TryParse(parts[0], out var fileId))
                {
                    continue;
                }

                rows.Add(new RevertEntry(fileId, fileId, parts[1], parts[2]));
            }
            else
            {
                if (parts.Length < 4)
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

                rows.Add(new RevertEntry(entityId, fileId, parts[2], parts[3]));
            }
        }
        return rows;
    }

    /// <summary>Serializes one entry to its <c>entityId|fileId|old|new</c> wire form.</summary>
    private static string Format(RevertEntry e) =>
        $"{e.EntityId}{FieldSep}{e.FileId}{FieldSep}{e.OldPath}{FieldSep}{e.NewPath}";
}
