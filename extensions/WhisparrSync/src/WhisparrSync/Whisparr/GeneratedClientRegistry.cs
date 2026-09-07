using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;

namespace WhisparrSync.Whisparr;

/// <summary>The generated-client registrations one generation's gateway holds.</summary>
/// <remarks>
/// One registry per gateway rather than one shared between them. Only one generation is ever reached
/// for a given instance, so the live count is the same either way, and a shared cap would let one
/// generation's traffic discard the other's.
/// </remarks>
internal sealed class GeneratedClientRegistry<TTarget>(Func<TTarget, ServiceProvider> register)
    : IDisposable
    where TTarget : notnull
{
    /// <summary>How many address-and-key pairs are kept before the least recent is discarded.</summary>
    /// <remarks>
    /// A person testing a connection supplies a pair per attempt, so the set is not bounded by how
    /// many instances exist. The least recently reached entry is the one discarded, so a discarded
    /// registration is idle rather than one a request is running against.
    /// </remarks>
    internal const int MaxRegistrations = 8;

    private readonly ConcurrentDictionary<TTarget, Registration> _registrations = new();
    private long _reachCount;
    private bool _disposed;

    /// <summary>The provider for the instance <paramref name="target"/> names.</summary>
    public ServiceProvider Reach(TTarget target)
    {
        var registration = _registrations.GetOrAdd(target, key => new Registration(register(key)));
        registration.ReachedAt = Interlocked.Increment(ref _reachCount);
        DiscardBeyondCap();
        return registration.Provider;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var registration in _registrations.Values)
        {
            registration.Provider.Dispose();
        }

        _registrations.Clear();
    }

    // The least recently reached entry, which a running request is not holding: reaching one is what
    // marks it, and every call marks its own before this runs.
    private void DiscardBeyondCap()
    {
        while (_registrations.Count > MaxRegistrations)
        {
            var oldest = _registrations
                .OrderBy(entry => entry.Value.ReachedAt)
                .First();

            if (_registrations.TryRemove(oldest.Key, out var discarded))
            {
                discarded.Provider.Dispose();
            }
        }
    }

    private sealed class Registration(ServiceProvider provider)
    {
        public ServiceProvider Provider { get; } = provider;

        public long ReachedAt { get; set; }
    }
}
