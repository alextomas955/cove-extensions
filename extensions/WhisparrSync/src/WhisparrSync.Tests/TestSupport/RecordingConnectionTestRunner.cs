using WhisparrSync.Connection;
using WhisparrSync.Contracts;

namespace WhisparrSync.Tests.TestSupport;

// Records arguments, not counts: a route's deny path has to be shown to have made no call, not
// merely to have answered 403.
internal sealed class RecordingConnectionTestRunner : IConnectionTestRunner
{
    public List<(string? Address, string? ApiKey)> Transient { get; } = [];

    public int Stored { get; private set; }

    public Task<ConnectionTestView> TestTransientAsync(
        string? address, string? apiKey, CancellationToken ct)
    {
        Transient.Add((address, apiKey));
        return Task.FromResult(Answer);
    }

    public Task<ConnectionTestView> TestStoredAsync(CancellationToken ct)
    {
        Stored++;
        return Task.FromResult(Answer);
    }

    private static ConnectionTestView Answer { get; } = new(
        ConnectionFailureKind.Unreachable, null, null, null, null, null, null, null, null);
}
