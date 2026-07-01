using Cove.Plugins;

namespace Renamer.Tests;

/// <summary>
/// In-memory <see cref="IExtensionStore"/> fake (Dictionary-backed, async) so the
/// <c>OptionsStore</c> can be unit-tested without a running Cove host.
/// </summary>
internal sealed class FakeStore : IExtensionStore
{
    private readonly Dictionary<string, string> _d = new();

    public Task<string?> GetAsync(string key, CancellationToken ct = default)
        => Task.FromResult(_d.GetValueOrDefault(key));

    public Task SetAsync(string key, string value, CancellationToken ct = default)
    {
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
