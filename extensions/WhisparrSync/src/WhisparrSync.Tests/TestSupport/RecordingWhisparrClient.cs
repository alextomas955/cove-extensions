using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.TestSupport;

/// <summary>
/// An <see cref="IWhisparrClient"/> that records the ARGUMENTS of every request asked of it and
/// answers with a response the caller chose.
/// </summary>
/// <remarks>
/// It stands in for the one seam every outbound request leaves through, so a path that reaches no
/// call here contacted the instance not at all. The arguments are what is recorded rather than a
/// count: a count answers whether a request was made, and the question a refusal has to answer is
/// what would have been sent.
/// <para>
/// No network and no timing behaviour, so an empty log is a fact about the path under test.
/// </para>
/// </remarks>
internal sealed class RecordingWhisparrClient(WhisparrResponse answer) : IWhisparrClient
{
    private const string JsonContentType = "application/json; charset=utf-8";

    /// <summary>Every request this client was asked for, in order.</summary>
    public List<(Uri BaseAddress, string ApiKey)> Calls { get; } = [];

    public Task<WhisparrResponse> ReadStatusAsync(Uri baseAddress, string apiKey, CancellationToken ct)
    {
        Calls.Add((baseAddress, apiKey));
        return Task.FromResult(answer);
    }

    /// <summary>A client answering with the status document <paramref name="fixtureFileName"/> holds.</summary>
    public static RecordingWhisparrClient Reporting(string fixtureFileName)
        => new(new WhisparrResponse(200, JsonContentType, ProbeFixtures.Read(fixtureFileName)));
}
