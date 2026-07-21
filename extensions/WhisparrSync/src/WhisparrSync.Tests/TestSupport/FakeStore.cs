using Cove.Plugins;

namespace WhisparrSync.Tests.TestSupport;

/// <summary>
/// In-memory <see cref="IExtensionStore"/> fake (Dictionary-backed, async) so the options layer can
/// be unit-tested without a running Cove host.
/// </summary>
internal sealed class FakeStore : IExtensionStore
{
    private readonly Dictionary<string, string> _d = new();

    /// <summary>Number of <see cref="SetAsync"/> calls — lets a test prove a read-only path wrote nothing.</summary>
    public int SetCallCount { get; private set; }

    public Task<string?> GetAsync(string key, CancellationToken ct = default)
        => Task.FromResult(_d.GetValueOrDefault(key));

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
