using WhisparrSync.Connection;
using WhisparrSync.Contracts;

namespace WhisparrSync.Tests.TestSupport;

/// <summary>
/// An <see cref="IWhisparrConnectionTester"/> that records the ARGUMENTS of every test asked of it and
/// answers with a result the caller chose.
/// </summary>
/// <remarks>
/// It stands in for the one seam an outbound request originates from, so a path that reaches no call
/// here made no request. A call counter would say a request was avoided while saying nothing about
/// which address the one that was made went to.
/// </remarks>
internal sealed class RecordingConnectionTester(ConnectionTestView answer) : IWhisparrConnectionTester
{
    /// <summary>Every test this tester was asked for, in order.</summary>
    public List<(string? Address, string? ApiKey)> Calls { get; } = [];

    public Task<ConnectionTestView> TestAsync(string? address, string? apiKey, CancellationToken ct)
    {
        Calls.Add((address, apiKey));
        return Task.FromResult(answer);
    }

    /// <summary>An instance this product manages answered on <paramref name="version"/>.</summary>
    public static RecordingConnectionTester Connected(string version)
        => new(new ConnectionTestView(
            ConnectionFailureKind.Connected, WhisparrGeneration.V3, version, "master", true, null, null, null));

    /// <summary>Something answered and turned the key down.</summary>
    public static RecordingConnectionTester KeyRejected()
        => new(new ConnectionTestView(
            ConnectionFailureKind.KeyRejected, null, null, null, null, null, null, null));

    /// <summary>Nothing answered.</summary>
    public static RecordingConnectionTester Unreachable()
        => new(new ConnectionTestView(
            ConnectionFailureKind.Unreachable, null, null, null, null, null, null, null));
}
