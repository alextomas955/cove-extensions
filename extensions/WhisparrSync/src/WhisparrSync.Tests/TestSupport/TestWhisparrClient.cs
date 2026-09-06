using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.TestSupport;

/// <summary>An outbound client whose every request, either generation's, reaches one handler.</summary>
/// <remarks>
/// The newer generation's requests are composed by the generated client, which stands up an
/// <c>HttpClient</c> of its own, so the handler under test is supplied to both. A test that supplied
/// it to one would record half the requests and assert on that half.
/// <para>
/// The bound on one attempt is read off the supplied client for the same reason, so a test that sets
/// its own is setting it for both.
/// </para>
/// </remarks>
internal static class TestWhisparrClient
{
    public static WhisparrClient Over(
        HttpClient http, HttpMessageHandler? handler = null, ILogger? log = null)
        => new(
            http,
            new Whisparr3Gateway(
                handler is null ? WhisparrClient.CreateHandler : () => handler,
                client => client.Timeout = http.Timeout),
            log ?? NullLogger.Instance);

    public static WhisparrClient Over(HttpMessageHandler handler, ILogger? log = null)
    {
        var http = new HttpClient(handler);
        WhisparrClient.Configure(http);
        return Over(http, handler, log);
    }
}
