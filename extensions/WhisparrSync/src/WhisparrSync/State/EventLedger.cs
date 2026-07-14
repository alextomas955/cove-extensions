using System.Collections.Concurrent;
using System.Text.Json;
using Cove.Plugins;

namespace WhisparrSync.State;

/// <summary>
/// The IMPT-03 idempotency journal: the set of processed-import keys, persisted as a SINGLE JSON-array
/// blob under one <see cref="IExtensionStore"/> key. Structurally a clone of <c>MatchStateStore</c> — a
/// process-wide single-writer gate, a defensive parse that yields an empty set on a corrupt blob (never
/// throws), and a gated read-modify-write for every append. Takes <see cref="IExtensionStore"/> directly
/// so it is unit-testable host-free against a fake.
/// </summary>
internal sealed class EventLedger(IExtensionStore store)
{
    private const string Key = "eventledger";

    // Single-writer serialization keyed on the store Key, process-lifetime, exactly like MatchStateStore:
    // RecordAsync is a read-modify-write on one shared blob and must serialize process-wide.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> StoreGates = new();
    private readonly SemaphoreSlim _gate = StoreGates.GetOrAdd(Key, _ => new SemaphoreSlim(1, 1));

    /// <summary>Records <paramref name="key"/> as processed (gated, idempotent — recording the same key twice is a no-op).</summary>
    public async Task RecordAsync(string key, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var current = Parse(await store.GetAsync(Key, ct));
            if (Array.IndexOf(current, key) >= 0)
            {
                return;
            }

            var next = new string[current.Length + 1];
            Array.Copy(current, next, current.Length);
            next[^1] = key;
            await PersistAsync(next, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string[] Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize(json, IngestJsonContext.Default.StringArray) ?? [];
        }
        catch (JsonException)
        {
            return []; // corrupt/hand-edited blob → empty set, never throws (IMPT-03 resilience)
        }
    }

    private Task PersistAsync(string[] keys, CancellationToken ct)
        => store.SetAsync(Key, JsonSerializer.Serialize(keys, IngestJsonContext.Default.StringArray), ct);
}
