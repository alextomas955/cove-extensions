using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.TestSupport;

/// <summary>An outbound client whose every request, either generation's, reaches one handler.</summary>
/// <remarks>
/// Each generation's requests are composed by a generated client that stands up an <c>HttpClient</c>
/// of its own, so the handler under test is supplied to all three. A test that supplied it to one
/// would record part of the requests and assert on that part.
/// <para>
/// The bound on one attempt is read off the supplied client for the same reason, so a test that sets
/// its own is setting it for both.
/// </para>
/// </remarks>
internal static class TestWhisparrClient
{
    public static WhisparrClient Over(
        HttpClient http, HttpMessageHandler? handler = null, ILogger? log = null)
    {
        Func<HttpMessageHandler> primary =
            handler is null ? WhisparrClient.CreateHandler : () => handler;
        void Timeout(HttpClient client) => client.Timeout = http.Timeout;

        return new WhisparrClient(
            http,
            new Whisparr3Gateway(primary, Timeout),
            new Whisparr2Gateway(primary, Timeout),
            log ?? NullLogger.Instance);
    }

    public static WhisparrClient Over(HttpMessageHandler handler, ILogger? log = null)
    {
        var http = new HttpClient(handler);
        WhisparrClient.Configure(http);
        return Over(http, handler, log);
    }
}
