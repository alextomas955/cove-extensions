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
    public Lease Reach(TTarget target)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        Lease? lease;
        while (true)
        {
            var registration = _registrations.GetOrAdd(
                target, key => new Registration(key, register));
            registration.ReachedAt = Interlocked.Increment(ref _reachCount);
            lease = registration.TryLease();
            if (lease is not null)
            {
                break;
            }

            // Discarded between the lookup and the lease. Only that exact entry is removed, so a
            // registration another thread has since added in its place is left alone.
            _registrations.TryRemove(
                new KeyValuePair<TTarget, Registration>(target, registration));
        }

        DiscardBeyondCap(target);
        return lease;
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

    // The reached entry is excluded so that traffic against one pair cannot discard the registration
    // it is itself building on. A concurrent discard can also empty the candidate set between the
    // count and the pick.
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

    // A request holds one of these for as long as it is sending. A registration discarded while a
    // lease is out stays alive until the lease is released, so a provider is never disposed under a
    // request that is using it.
    internal sealed class Lease(Registration registration, ServiceProvider provider) : IDisposable
    {
        private int _released;

        public ServiceProvider Provider { get; } = provider;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                registration.Release();
            }
        }
    }

    // A concurrent first reach of one target runs the add factory twice and keeps one result, so the
    // provider is built at the first lease: the registration the dictionary drops never builds one.
    internal sealed class Registration(TTarget target, Func<TTarget, ServiceProvider> register)
    {
        private readonly Lock _gate = new();
        private ServiceProvider? _provider;
        private int _leases;
        private bool _discarded;

        public long ReachedAt { get; set; }

        // Answers null once discarded, which tells the caller to reach again rather than lease a
        // registration that is on its way out.
        public Lease? TryLease()
        {
            lock (_gate)
            {
                if (_discarded)
                {
                    return null;
                }

                _provider ??= register(target);
                _leases++;
                return new Lease(this, _provider);
            }
        }

        public void Release()
        {
            ServiceProvider? finished;
            lock (_gate)
            {
                _leases--;
                finished = _discarded && _leases == 0 ? _provider : null;
                _provider = finished is null ? _provider : null;
            }

            finished?.Dispose();
        }

        public void Discard()
        {
            ServiceProvider? finished;
            lock (_gate)
            {
                _discarded = true;
                finished = _leases == 0 ? _provider : null;
                _provider = finished is null ? _provider : null;
            }

            finished?.Dispose();
        }
    }
}
