using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cove.Extensions.Shared;
using Cove.Plugins;

namespace WhisparrSync.State;

/// <summary>
/// The idempotency journal: the set of processed-import keys, persisted as a SINGLE JSON-array
/// blob under one <see cref="IExtensionStore"/> key. Rides <see cref="SingleWriterBlobStore{T}"/> for the
/// process-wide single-writer gate, the defensive parse (a corrupt blob loads empty, never throws), and
/// the gated read-modify-write append.
/// </summary>
internal sealed class EventLedger(IExtensionStore store) : SingleWriterBlobStore<string>(store, Key)
{
    private const string Key = "eventledger";

    private const string HistPrefix = "hist:";

    /// <summary>
    /// The retained-<see cref="ImportKey"/> ceiling. The cross-channel dedup only needs to outlive the
    /// redelivery window of a single On-Import — one reconcile poll interval (15 min) plus the webhook-retry
    /// margin. 512 is a deterministic count well above a full history page (<c>ReconcileJob.PageSize</c> = 50),
    /// hence above any realistic in-flight import burst inside one window. An eviction older than the window
    /// costs only an idempotent re-import — the host unique <c>(ParentFolderId, Basename)</c> index makes a
    /// duplicate entity structurally impossible — never a lost still-queryable guarantee. <c>hist:</c>
    /// keys are exempt: they are bounded at the checkpoint by <see cref="PruneHistBelowAsync"/> instead.
    /// </summary>
    internal const int MaxImportKeys = 512;

    /// <summary>Whether <paramref name="key"/> has already been recorded — the check-before-ingest short-circuit.</summary>
    public async Task<bool> SeenAsync(string key, CancellationToken ct = default)
        => Array.IndexOf(Parse(await LoadBlobAsync(ct)), key) >= 0;

    /// <summary>
    /// Atomically claims <paramref name="key"/>: UNDER the write gate, checks membership and inserts the key
    /// in ONE critical section, returning <c>true</c> ONLY to the first caller. This closes the check→ingest→
    /// record TOCTOU that a separate ungated <see cref="SeenAsync"/> + later <see cref="RecordAsync"/> left
    /// open — a concurrent webhook + poll for the SAME import now has exactly one winner (which ingests) and
    /// every loser gets <c>false</c> and skips, so Cove never creates two entities for one import.
    /// The winner must <see cref="ReleaseAsync"/> the key if its ingest throws so a failed import stays retryable.
    /// </summary>
    public Task<bool> TryClaimAsync(string key, CancellationToken ct = default)
        => RunExclusiveAsync(async () =>
        {
            var current = Parse(await LoadBlobAsync(ct));
            if (Array.IndexOf(current, key) >= 0)
            {
                return false; // another channel already claimed (or recorded) this import — skip
            }

            var next = new string[current.Length + 1];
            Array.Copy(current, next, current.Length);
            next[^1] = key;
            await PersistAsync(next, ct);
            return true;
        }, ct);

    /// <summary>
    /// Releases a previously-claimed <paramref name="key"/> (gated) so a claim whose ingest failed with an
    /// UNEXPECTED fault is retryable on the next delivery/poll rather than permanently swallowed. A no-op if
    /// the key is absent (a classified Imported/Flagged outcome keeps the claim — the event is audited once).
    /// </summary>
    public Task ReleaseAsync(string key, CancellationToken ct = default)
        => RunExclusiveAsync(async () =>
        {
            var current = Parse(await LoadBlobAsync(ct));
            var index = Array.IndexOf(current, key);
            if (index < 0)
            {
                return;
            }

            var next = new string[current.Length - 1];
            Array.Copy(current, 0, next, 0, index);
            Array.Copy(current, index + 1, next, index, current.Length - index - 1);
            await PersistAsync(next, ct);
        }, ct);

    /// <summary>
    /// The ONE cross-channel idempotency key: <c>SHA-256(downloadId | NormalizePath(path))</c> as lowercase
    /// hex. It keys ONLY on fields BOTH the webhook payload AND the <c>/api/v3/history</c> record reliably
    /// carry — <paramref name="downloadId"/> and the imported path — and deliberately OMITS <c>movieFileId</c>
    /// (the history record does not carry it), so a webhook-then-poll overlap of the same import derives an
    /// identical key. Never keyed on a volatile field (a timestamp). The reconcile-poll path calls this same helper.
    /// </summary>
    public static string ImportKey(string? downloadId, string path)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{downloadId}|{NormalizePath(path)}")));

    /// <summary>
    /// The single shared path-normalizer applied on BOTH sides before hashing so the key is byte-stable
    /// across delivery channels: separators unified to <c>/</c> and trailing separators trimmed, but
    /// CASE-SENSITIVE. The deployment target is Linux/Docker (a case-sensitive filesystem), where
    /// <c>/data/Media/A.mkv</c> and <c>/data/media/a.mkv</c> are DISTINCT files — case-folding here would
    /// (1) let the containment guard over-match a differently-cased root the admin never allow-listed
    /// (a security weakening) and (2) collide two distinct files onto one idempotency key, silently
    /// skipping the second import. Both Whisparr channels report the same path casing, so the cross-channel
    /// key still dedups without folding.
    /// </summary>
    public static string NormalizePath(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }

        return path.Replace('\\', '/').TrimEnd('/');
    }

    /// <summary>Records <paramref name="key"/> as processed (gated, idempotent — recording the same key twice is a no-op).</summary>
    public Task RecordAsync(string key, CancellationToken ct = default)
        => RunExclusiveAsync(async () =>
        {
            var current = Parse(await LoadBlobAsync(ct));
            if (Array.IndexOf(current, key) >= 0)
            {
                return;
            }

            var next = new string[current.Length + 1];
            Array.Copy(current, next, current.Length);
            next[^1] = key;
            await PersistAsync(next, ct);
        }, ct);

    /// <summary>
    /// Prunes every <c>hist:{id}</c> self-key at or below <paramref name="lastRecordId"/> (gated). Once the
    /// reconcile checkpoint advances past a record the poll walk stops before ever reaching it again
    /// (<c>ReconcileJob</c> breaks at <c>record.Id &lt;= checkpoint.LastRecordId</c>): that record's
    /// <c>hist:</c> self-key can never match on any later pass, making it dead weight. Called after the checkpoint
    /// advances, this is what bounds the ledger's dominant growth source. Cross-channel
    /// <see cref="ImportKey"/>s are left untouched (they guard the redelivery window and are capped by the
    /// window cap instead). A no-op when nothing is at/below the mark.
    /// </summary>
    public Task PruneHistBelowAsync(int lastRecordId, CancellationToken ct = default)
        => RunExclusiveAsync(async () =>
        {
            var current = Parse(await LoadBlobAsync(ct));
            var kept = new List<string>(current.Length);
            foreach (var key in current)
            {
                if (!IsHistKeyAtOrBelow(key, lastRecordId))
                {
                    kept.Add(key);
                }
            }

            if (kept.Count != current.Length)
            {
                await PersistAsync([.. kept], ct);
            }
        }, ct);

    private static bool IsHistKeyAtOrBelow(string key, int lastRecordId)
        => key.StartsWith(HistPrefix, StringComparison.Ordinal)
           && int.TryParse(key.AsSpan(HistPrefix.Length), out var id)
           && id <= lastRecordId;

    /// <summary>
    /// Caps retention to the newest <see cref="MaxImportKeys"/> non-<c>hist:</c> keys, evicting the oldest
    /// (the array is append-ordered, so oldest-first). Under the cap the blob is returned byte-for-byte
    /// unchanged (no churn, no re-serialization). <c>hist:</c> keys are never evicted here — they are bounded
    /// at the checkpoint by <see cref="PruneHistBelowAsync"/>.
    /// </summary>
    protected override string Compact(string? blob)
    {
        var keys = Parse(blob);
        var overflow = 0;
        foreach (var key in keys)
        {
            if (!key.StartsWith(HistPrefix, StringComparison.Ordinal))
            {
                overflow++;
            }
        }

        overflow -= MaxImportKeys;
        if (overflow <= 0)
        {
            return blob ?? string.Empty;
        }

        var kept = new List<string>(keys.Length - overflow);
        foreach (var key in keys)
        {
            if (overflow > 0 && !key.StartsWith(HistPrefix, StringComparison.Ordinal))
            {
                overflow--; // drop the oldest ImportKeys until back under the cap
                continue;
            }

            kept.Add(key);
        }

        return JsonSerializer.Serialize(kept.ToArray(), IngestJsonContext.Default.StringArray);
    }

    private static string[] Parse(string? json) => ParseArray(json, IngestJsonContext.Default.StringArray);

    // Routing every write through Compact enforces the ImportKey window cap on the one blob, under the gate.
    private Task PersistAsync(string[] keys, CancellationToken ct)
        => StoreBlobAsync(Compact(JsonSerializer.Serialize(keys, IngestJsonContext.Default.StringArray)), ct);
}
