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
    /// <remarks>
    /// Throws once the registry is disposed. A gateway is a container singleton, so a request still
    /// in flight when the container tears one down would otherwise register against a cleared cache
    /// and leave the provider it built undisposed.
    /// </remarks>
    public ServiceProvider Reach(TTarget target)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var registration = _registrations.GetOrAdd(target, key => new Registration(key, register));
        registration.ReachedAt = Interlocked.Increment(ref _reachCount);
        var provider = registration.Provider;
        DiscardBeyondCap(target);
        return provider;
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
            registration.Discard();
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

            discarded.Discard();
        }
    }

    // A concurrent first reach of one target runs the add factory twice and keeps one result, so what
    // the factory builds holds a provider it has not created yet: the result the dictionary drops
    // never creates one, and the client factory, its expiry timers and its handler pool are built
    // only for the entry that was kept.
    private sealed class Registration(TTarget target, Func<TTarget, ServiceProvider> register)
    {
        private readonly Lazy<ServiceProvider> _provider = new(
            () => register(target), LazyThreadSafetyMode.ExecutionAndPublication);

        public long ReachedAt { get; set; }

        public ServiceProvider Provider => _provider.Value;

        public void Discard()
        {
            if (_provider.IsValueCreated)
            {
                _provider.Value.Dispose();
            }
        }
    }
}
