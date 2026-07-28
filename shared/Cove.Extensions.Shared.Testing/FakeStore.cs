using Cove.Plugins;

namespace Cove.Extensions.Shared.Testing;

/// <summary>
/// In-memory <see cref="IExtensionStore"/> fake (Dictionary-backed, async) so an options/store layer
/// can be unit-tested without a running Cove host.
/// </summary>
/// <remarks>
/// <see cref="SetCallCount"/> is the superset addition (over the plain Dictionary variant): it lets a
/// test prove a read-only path wrote nothing. Not thread-safe — a concurrency proof must use
/// <see cref="ConcurrentFakeStore"/> instead, whose store is never itself the source of a race.
/// </remarks>
public sealed class FakeStore : IExtensionStore
{
    private readonly Dictionary<string, string> _d = new();

    /// <summary>Number of <see cref="SetAsync"/> calls — lets a test prove a read-only path wrote nothing.</summary>
    public int SetCallCount { get; private set; }

    /// <summary>
    /// Every key passed to <see cref="GetAsync"/>, in order — lets a test prove a key was NEVER read,
    /// which for a value that can be hundreds of megabytes is the difference between a safe cleanup and
    /// the one operation guaranteed to hurt.
    /// </summary>
    public List<string> GetKeys { get; } = [];

    public Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        GetKeys.Add(key);
        return Task.FromResult(_d.GetValueOrDefault(key));
    }

    public Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        SetCallCount++;
        _d[key] = value;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        _d.Remove(key);
        return Task.CompletedTask;
    }

    public Task<Dictionary<string, string>> GetAllAsync(CancellationToken ct = default)
        => Task.FromResult(new Dictionary<string, string>(_d));
}
