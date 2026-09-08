using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;

namespace WhisparrSync.Whisparr;

/// <summary>The generated-client registrations one generation's gateway holds.</summary>
/// <remarks>
/// One registry belongs to one gateway. Only one generation is ever reached for a given instance, so
/// a cap shared between the generations would let one generation's traffic discard the other's.
/// </remarks>
internal sealed class GeneratedClientRegistry<TTarget>(Func<TTarget, ServiceProvider> register)
    : IDisposable
    where TTarget : notnull
{
    /// <summary>How many address-and-key pairs are kept before the least recent is discarded.</summary>
    /// <remarks>
    /// A person testing a connection supplies a pair per attempt, so the set is not bounded by how
    /// many instances exist. The least recently reached entry is the one discarded, and the entry the
    /// running call reached is never a candidate.
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
        DiscardBeyondCap(target);
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

    // The reached entry is excluded: another thread reaching a further target can pass the cap while
    // this call is between marking its own entry and handing that entry's provider back, and
    // discarding it there would hand back a disposed provider. A concurrent discard can also empty
    // the candidate set between the count and the pick.
    private void DiscardBeyondCap(TTarget reached)
    {
        while (_registrations.Count > MaxRegistrations)
        {
            var oldest = _registrations
                .Where(entry => !EqualityComparer<TTarget>.Default.Equals(entry.Key, reached))
                .MinBy(entry => entry.Value.ReachedAt);

            if (oldest.Value is null || !_registrations.TryRemove(oldest.Key, out var discarded))
            {
                return;
            }

            discarded.Provider.Dispose();
        }
    }

    private sealed class Registration(ServiceProvider provider)
    {
        public ServiceProvider Provider { get; } = provider;

        public long ReachedAt { get; set; }
    }
}
