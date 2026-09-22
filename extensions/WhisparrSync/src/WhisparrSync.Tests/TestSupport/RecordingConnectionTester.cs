using WhisparrSync.Connection;
using WhisparrSync.Contracts;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.TestSupport;

// The one seam an outbound request originates from, so a path that reaches no call here made no
// request. The recorded arguments also say which address a request that was made went to.
internal sealed class RecordingConnectionTester(ConnectionTestView answer) : IWhisparrConnectionTester
{
    public List<(string? Address, string? ApiKey)> Calls { get; } = [];

    public Task<ConnectionTestView> TestAsync(string? address, string? apiKey, CancellationToken ct)
    {
        Calls.Add((address, apiKey));
        return Task.FromResult(answer);
    }

    public static RecordingConnectionTester Connected(string version)
        => new(new ConnectionTestView(
            ConnectionFailureKind.Connected,
            WhisparrGeneration.V3,
            GenerationCapabilities.CapabilitiesOf(WhisparrGeneration.V3),
            version,
            "master",
            true,
            null,
            null,
            null));

    public static RecordingConnectionTester KeyRejected()
        => new(new ConnectionTestView(
            ConnectionFailureKind.KeyRejected, null, null, null, null, null, null, null, null));

    public static RecordingConnectionTester Unreachable()
        => new(new ConnectionTestView(
            ConnectionFailureKind.Unreachable, null, null, null, null, null, null, null, null));
}
