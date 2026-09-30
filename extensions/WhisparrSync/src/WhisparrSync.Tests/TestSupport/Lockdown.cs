using WhisparrSync.Connection;

namespace WhisparrSync.Tests.TestSupport;

internal sealed class Lockdown(bool wouldLockDown) : IHostLockdownPort
{
    public Task<bool> WouldLockDownAsync(CancellationToken ct) => Task.FromResult(wouldLockDown);
}
