using System.Collections.Concurrent;
using Cove.Plugins;

namespace Cove.Extensions.Shared.Testing;

/// <summary>
/// A thread-safe <see cref="IExtensionStore"/> fake for the concurrency proofs. The single-threaded
/// <see cref="FakeStore"/> is a bare <see cref="Dictionary{TKey,TValue}"/> with no locking, so using it in
/// a concurrency test would either throw (a Dictionary race) or silently lose writes — confounding
/// the proof. This variant backs every operation with a <see cref="ConcurrentDictionary{TKey,TValue}"/>
/// so the STORE is never the source of a race; any torn/lost row in a concurrency test then isolates
/// the caller's serialization under test, not the store. Same async signatures as <see cref="FakeStore"/>.
/// </summary>
public sealed class ConcurrentFakeStore : IExtensionStore
{
    private readonly ConcurrentDictionary<string, string> _d = new();

    public Task<string?> GetAsync(string key, CancellationToken ct = default)
        => Task.FromResult(_d.TryGetValue(key, out var v) ? v : null);

    public Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        _d[key] = value;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        _d.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    public Task<Dictionary<string, string>> GetAllAsync(CancellationToken ct = default)
        => Task.FromResult(new Dictionary<string, string>(_d));
}
