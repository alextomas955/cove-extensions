using System.Collections.Concurrent;
using System.Text.Json;
using Cove.Plugins;

namespace WhisparrSync.State;

/// <summary>
/// One audited auto-import attempt. <see cref="UtcTicks"/> is a server-written
/// <c>DateTime.UtcNow.Ticks</c> — NEVER a browser value (the log is read back later/elsewhere).
/// <see cref="Result"/> is the UI vocabulary <c>Imported</c> / <c>Skipped</c> / <c>Flagged</c>;
/// <see cref="Reason"/> distinguishes the flagged sub-cases (out-of-root rejection vs failed-ingest scan
/// fallback). <see cref="LedgerKey"/> is the <see cref="EventLedger.ImportKey"/> the attempt deduped on.
/// </summary>
internal readonly record struct ImportLogEntry(
    long UtcTicks,
    string Source,
    string? EventType,
    string Path,
    string? Kind,
    int? CoveEntityId,
    string Result,
    string? Reason,
    string LedgerKey);

/// <summary>
/// The audit journal: append-only entries persisted as a SINGLE JSON-array blob over one
/// <see cref="IExtensionStore"/> key. Structurally a clone of <c>MatchStateStore</c> — a process-wide
/// single-writer gate, a defensive parse that yields an empty array on a corrupt blob (never throws), and
/// a gated read-modify-write append. Takes <see cref="IExtensionStore"/> directly so it is unit-testable
/// host-free against a fake.
/// </summary>
internal sealed class ImportLog(IExtensionStore store)
{
    private const string Key = "importlog";

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> StoreGates = new();
    private readonly SemaphoreSlim _gate = StoreGates.GetOrAdd(Key, _ => new SemaphoreSlim(1, 1));

    /// <summary>Loads the whole audit journal in append order; empty on an absent key (first run) or a corrupt blob.</summary>
    public async Task<IReadOnlyList<ImportLogEntry>> LoadAllAsync(CancellationToken ct = default)
        => Parse(await store.GetAsync(Key, ct));

    /// <summary>Appends one audit entry (gated read-modify-write so concurrent appends cannot tear the blob).</summary>
    public async Task AppendAsync(ImportLogEntry entry, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var current = Parse(await store.GetAsync(Key, ct));
            var next = new ImportLogEntry[current.Length + 1];
            Array.Copy(current, next, current.Length);
            next[^1] = entry;
            await store.SetAsync(Key, JsonSerializer.Serialize(next, IngestJsonContext.Default.ImportLogEntryArray), ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static ImportLogEntry[] Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize(json, IngestJsonContext.Default.ImportLogEntryArray) ?? [];
        }
        catch (JsonException)
        {
            return []; // corrupt/hand-edited blob → empty journal, never throws
        }
    }
}
