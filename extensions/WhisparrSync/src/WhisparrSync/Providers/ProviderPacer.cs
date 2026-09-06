using System.Threading.RateLimiting;

namespace WhisparrSync.Providers;

/// <summary>Holds outbound provider requests to the rate the host is configured with.</summary>
/// <remarks>
/// One limiter shared by every request, so it has to outlive a request scope. Pacing held per scope
/// would let concurrent requests each spend a full allowance.
/// <para>
/// The limiter replenishes its whole allowance once a minute rather than trickling, which lets a
/// page's own reads proceed together and holds the minute's total to the configured number.
/// </para>
/// </remarks>
internal sealed class ProviderPacer : IDisposable
{
    // A queue rather than a refusal: a paced call waits its turn. The depth bounds how many callers
    // may wait at once, past which a caller is told immediately instead of queueing without limit.
    private const int MaxQueuedRequests = 256;

    private readonly Dictionary<int, FixedWindowRateLimiter> _limiters = [];
    private readonly Lock _gate = new();
    private bool _disposed;

    /// <summary>Waits until a request at <paramref name="maxRequestsPerMinute"/> may be sent.</summary>
    /// <remarks>
    /// A rate at or below zero paces nothing, the host having named no limit to honour.
    /// </remarks>
    internal async Task<bool> WaitForTurnAsync(int maxRequestsPerMinute, CancellationToken ct)
    {
        if (maxRequestsPerMinute <= 0)
        {
            return true;
        }

        using var lease = await LimiterFor(maxRequestsPerMinute)
            .AcquireAsync(1, ct)
            .ConfigureAwait(false);
        return lease.IsAcquired;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var limiter in _limiters.Values)
            {
                limiter.Dispose();
            }

            _limiters.Clear();
        }
    }

    // Keyed on the rate, because the configured number is read per request and a limiter built for
    // one rate would pace a changed one to the old number.
    private FixedWindowRateLimiter LimiterFor(int maxRequestsPerMinute)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_limiters.TryGetValue(maxRequestsPerMinute, out var existing))
            {
                return existing;
            }

            var created = new FixedWindowRateLimiter(
                new FixedWindowRateLimiterOptions
                {
                    PermitLimit = maxRequestsPerMinute,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = MaxQueuedRequests,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    AutoReplenishment = true,
                });

            _limiters[maxRequestsPerMinute] = created;
            return created;
        }
    }
}
