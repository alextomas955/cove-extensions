using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Cove.Plugins;

namespace Cove.Extensions.Shared;

/// <summary>
/// The writer gates <see cref="SingleWriterBlobStore{T}"/> serializes on, keyed on the store key.
/// </summary>
/// <remarks>
/// Non-generic on purpose, and NOT a static field on the generic base. A static field there exists once
/// per CLOSED generic type, so two journals over the same key with different element types would resolve
/// two independent maps and serialize against neither — the exact case the base class documents itself as
/// covering. Process-lifetime; the semaphores are never disposed.
/// </remarks>
internal static class BlobStoreGates
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new();

    internal static SemaphoreSlim For(string key) => Gates.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
}

/// <summary>
/// Opt-in base for a journal persisted as ONE blob under a single <see cref="IExtensionStore"/> key,
/// factoring the single-writer gate, the defensive JSON-array parse, and the <see cref="Compact"/>
/// retention hook every such journal hand-rolls.
/// </summary>
/// <remarks>
/// The subclass supplies the key: this is a base-class swap under the SAME key and shape, not a re-key.
/// The gate is a shared static map keyed on that key, so two instances over one key resolve the same
/// semaphore — serializing writers across separate instances, not just within one. Its default
/// <see cref="Compact"/> returns the blob unchanged, so a non-overriding subclass keeps its current
/// unbounded behavior.
/// </remarks>
/// <typeparam name="T">The array element the JSON blob (de)serializes to.</typeparam>
public abstract class SingleWriterBlobStore<T>
{

    private readonly IExtensionStore _store;
    private readonly string _key;
    private readonly SemaphoreSlim _gate;

    protected SingleWriterBlobStore(IExtensionStore store, string key)
    {
        _store = store;
        _key = key;
        _gate = BlobStoreGates.For(key);
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

    /// <summary>Gate-guarded variant of <see cref="RunExclusiveAsync(Func{Task},CancellationToken)"/> that returns a value.</summary>
    protected async Task<TResult> RunExclusiveAsync<TResult>(Func<Task<TResult>> action, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return await action();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Defensive JSON-array parse: an absent or corrupt blob is the empty array, never a throw.</summary>
    protected static T[] ParseArray(string? json, JsonTypeInfo<T[]> typeInfo)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize(json, typeInfo) ?? [];
        }
        catch (JsonException)
        {
            return []; // corrupt/hand-edited blob → empty, never throws
        }
    }

    /// <summary>Retention hook: the blob to persist in place of <paramref name="blob"/>. The default no-op keeps the full history.</summary>
    protected virtual string Compact(string? blob) => blob ?? string.Empty;
}
