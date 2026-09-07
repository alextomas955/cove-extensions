using Cove.Plugins;

namespace Cove.Extensions.Shared.Testing;

/// <summary>
/// An <see cref="IExtensionStore"/> decorator that parks the FIRST read of one nominated key until the
/// test releases it, so a load-then-save caller can be held open across a concurrent writer's whole
/// read-modify-write. Every other call — including every later read of that key — delegates straight through.
/// </summary>
/// <remarks>
/// <para>
/// The parked read CAPTURES the stored value BEFORE it signals <see cref="Parked"/>, and returns that
/// captured value on release. The inversion is the point: a decorator that parks first and reads on release
/// hands the caller the value the concurrent writer just saved, which is a fresh read wearing a delay — the
/// caller then saves what it already knows and no staleness is ever exercised. A test built that way passes
/// against ungated and gated code alike and proves nothing.
/// </para>
/// <para>
/// Parking only the FIRST read keeps a second writer entering a gate from blocking behind the harness:
/// whichever reader arrives after the park runs at full speed.
/// </para>
/// </remarks>
public sealed class StalledReadStore(string parkedKey, IExtensionStore? inner = null) : IExtensionStore
{
    private readonly IExtensionStore _inner = inner ?? new ConcurrentFakeStore();
    private readonly TaskCompletionSource _parked = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _parkTaken;

    /// <summary>Completes once a read of the nominated key has captured its value and parked.</summary>
    public Task Parked => _parked.Task;

    /// <summary>Lets the parked read return its captured value. Safe to call more than once.</summary>
    public void Release() => _released.TrySetResult();

    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        if (!string.Equals(key, parkedKey, StringComparison.Ordinal)
            || Interlocked.Exchange(ref _parkTaken, 1) == 1)
        {
            return await _inner.GetAsync(key, ct);
        }

        var captured = await _inner.GetAsync(key, ct);
        _parked.TrySetResult();
        await _released.Task;
        return captured;
    }

    public Task SetAsync(string key, string value, CancellationToken ct = default)
        => _inner.SetAsync(key, value, ct);

    public Task DeleteAsync(string key, CancellationToken ct = default) => _inner.DeleteAsync(key, ct);

    public Task<Dictionary<string, string>> GetAllAsync(CancellationToken ct = default)
        => _inner.GetAllAsync(ct);
}
