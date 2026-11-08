using System.Collections.Concurrent;
using Cove.Plugins;

namespace Cove.Extensions.Shared.Testing;

/// <summary>
/// A thread-safe <see cref="IExtensionStore"/> fake, so the store is never itself the source of a race
/// and a lost row isolates the caller's serialization. <see cref="FakeStore"/> is an unlocked
/// <see cref="Dictionary{TKey,TValue}"/>, which under concurrency throws or loses writes.
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
