using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
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

    /// <summary>Whether <paramref name="key"/> has already been recorded — the check-before-ingest short-circuit (IMPT-03).</summary>
    public async Task<bool> SeenAsync(string key, CancellationToken ct = default)
        => Array.IndexOf(Parse(await store.GetAsync(Key, ct)), key) >= 0;

    /// <summary>
    /// The ONE cross-channel idempotency key: <c>SHA-256(downloadId | NormalizePath(path))</c> as lowercase
    /// hex. It keys ONLY on fields BOTH the webhook payload AND the <c>/api/v3/history</c> record reliably
    /// carry — <paramref name="downloadId"/> and the imported path — and deliberately OMITS <c>movieFileId</c>
    /// (the history record does not carry it), so a webhook-then-poll overlap of the same import derives an
    /// identical key. Never keyed on a volatile field (a timestamp). The 03-02 poll path calls this same helper.
    /// </summary>
    public static string ImportKey(string? downloadId, string path)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{downloadId}|{NormalizePath(path)}")));

    /// <summary>
    /// The single shared path-normalizer applied on BOTH sides before hashing so the key is byte-stable
    /// across delivery channels: separators unified to <c>/</c>, trailing separators trimmed, case-folded.
    /// </summary>
    public static string NormalizePath(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }

        return path.Replace('\\', '/').TrimEnd('/').ToLowerInvariant();
    }

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
