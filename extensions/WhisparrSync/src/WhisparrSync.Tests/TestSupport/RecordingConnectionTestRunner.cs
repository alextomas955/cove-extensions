using WhisparrSync.Connection;
using WhisparrSync.Contracts;

namespace WhisparrSync.Tests.TestSupport;

/// <summary>
/// An <see cref="IConnectionTestRunner"/> that records the ARGUMENTS of every test asked of it.
/// </summary>
/// <remarks>
/// A route's deny path has to be shown to have done NOTHING, not merely to have answered 403, and
/// nothing is what an empty argument log says.
/// </remarks>
internal sealed class RecordingConnectionTestRunner : IConnectionTestRunner
{
    /// <summary>Every transient test asked for, in order.</summary>
    public List<(string? Address, string? ApiKey)> Transient { get; } = [];

    /// <summary>How many stored tests were asked for.</summary>
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
