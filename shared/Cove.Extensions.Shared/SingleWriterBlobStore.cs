using System.Collections.Concurrent;
using Cove.Plugins;

namespace Cove.Extensions.Shared;

/// <summary>
/// Opt-in base for a journal persisted as ONE blob under a single <see cref="IExtensionStore"/> key,
/// factoring the single-writer gate and the <see cref="Compact"/> retention hook every such journal
/// hand-rolls.
/// </summary>
/// <remarks>
/// The subclass supplies the key: this is a base-class swap under the SAME key and shape, not a re-key.
/// The gate is a shared static map keyed on that key, so two instances over one key resolve the same
/// semaphore — serializing writers across separate instances, not just within one. Its default
/// <see cref="Compact"/> returns the blob unchanged, so a non-overriding subclass keeps its current
/// unbounded behavior.
/// </remarks>
public abstract class SingleWriterBlobStore
{
    // Single-writer serialization keyed on the store key, process-lifetime (never disposed). Because the
    // map is shared and keyed on the key string, two instances over the same key resolve the SAME
    // semaphore — covering both the intra-process many-writer case and the two-instances-same-key case.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> StoreGates = new();

    private readonly IExtensionStore _store;
    private readonly string _key;
    private readonly SemaphoreSlim _gate;

    protected SingleWriterBlobStore(IExtensionStore store, string key)
    {
        _store = store;
        _key = key;
        _gate = StoreGates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
    }

    /// <summary>Reads the raw persisted blob under this journal's key (null/empty when the key is absent).</summary>
    protected Task<string?> LoadBlobAsync(CancellationToken ct = default) => _store.GetAsync(_key, ct);

    /// <summary>Overwrites the persisted blob under this journal's key.</summary>
    protected Task StoreBlobAsync(string blob, CancellationToken ct = default) => _store.SetAsync(_key, blob, ct);

    /// <summary>Runs <paramref name="action"/> under the single-writer gate (the whole read-modify-write, so it cannot tear).</summary>
    protected async Task RunExclusiveAsync(Func<Task> action, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await action();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Retention hook: the blob to persist in place of <paramref name="blob"/>. The default no-op keeps the full history.</summary>
    protected virtual string Compact(string? blob) => blob ?? string.Empty;
}
