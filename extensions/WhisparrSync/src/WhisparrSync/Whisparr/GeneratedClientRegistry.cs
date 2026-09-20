using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;

namespace WhisparrSync.Whisparr;

// One registry belongs to one gateway. A cap shared between the two generations would let one
// generation's traffic discard the other's.
internal sealed class GeneratedClientRegistry<TTarget>(Func<TTarget, ServiceProvider> register)
    : IDisposable
    where TTarget : notnull
{
    // Address-and-key pairs kept before the least recently reached is discarded. A person testing a
    // connection supplies a pair per attempt, so the set is not bounded by how many instances exist.
    internal const int MaxRegistrations = 8;

    private readonly ConcurrentDictionary<TTarget, Registration> _registrations = new();
    private long _reachCount;
    private bool _disposed;

    // Throws once disposed: a gateway is a container singleton, so a request still in flight at
    // teardown would otherwise register against a cleared cache and leak the provider it built.
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

    // The reached entry is excluded: discarding it while the calling thread is about to hand its
    // provider back would return a disposed provider. A concurrent discard can also empty the
    // candidate set between the count and the pick.
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

    // A concurrent first reach of one target runs the add factory twice and keeps one result, so the
    // factory builds a provider lazily: the result the dictionary drops never creates one.
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
