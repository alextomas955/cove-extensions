using System.Net;
using System.Text;

namespace WhisparrSync.Tests.TestSupport;

/// <summary>
/// A programmable <see cref="HttpMessageHandler"/> that returns caller-configured responses — a fixed
/// status/<c>Content-Type</c>/body, or an ordered <see cref="Sequence(Func{HttpResponseMessage}[])"/> of
/// steps (each of which may throw to simulate a transient transport fault) — so the transport-only
/// <c>WhisparrClient</c> and its error classification + bounded-retry are testable with no live Whisparr.
/// It is the outbound analogue of Renamer's boundary fakes. Captures the last outbound request (for URL +
/// <c>X-Api-Key</c> header assertions) and counts calls (to prove GET retries and POST does not).
/// </summary>
internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly IReadOnlyList<Func<HttpResponseMessage>> _steps;
    private int _index;

    /// <summary>The last request this handler saw, for URL + header assertions. Null until a call is made.</summary>
    public HttpRequestMessage? LastRequest { get; private set; }

    /// <summary>How many times the handler has been invoked — a retry probe.</summary>
    public int CallCount { get; private set; }

    private FakeHttpMessageHandler(IReadOnlyList<Func<HttpResponseMessage>> steps) => _steps = steps;

    public FakeHttpMessageHandler(HttpStatusCode status, string contentType, string body)
        : this([() => Build(status, contentType, body)])
    {
    }

    /// <summary>Convenience factory: a 200 response carrying <paramref name="json"/> as application/json.</summary>
    public static FakeHttpMessageHandler Json(string json)
        => new(HttpStatusCode.OK, "application/json", json);

    /// <summary>Convenience factory: a response with only a status code and an empty JSON body.</summary>
    public static FakeHttpMessageHandler Status(HttpStatusCode status)
        => new(status, "application/json", "{}");

    /// <summary>Convenience factory: a <c>text/html</c> body (a reverse-proxy landing page / 502).</summary>
    public static FakeHttpMessageHandler Html(HttpStatusCode status, string body = "<html><body>502 Bad Gateway</body></html>")
        => new(status, "text/html", body);

    /// <summary>
    /// An ordered set of response steps: call N runs step N; once exhausted the last step repeats. A step
    /// built with <see cref="Throw"/> raises its exception to simulate a transient transport fault.
    /// </summary>
    public static FakeHttpMessageHandler Sequence(params Func<HttpResponseMessage>[] steps)
        => new(steps);

    /// <summary>A step that returns a configured response.</summary>
    public static Func<HttpResponseMessage> Respond(HttpStatusCode status, string contentType, string body)
        => () => Build(status, contentType, body);

    /// <summary>A step that throws — simulates connection refused / a timeout the client must classify or retry.</summary>
    public static Func<HttpResponseMessage> Throw(Exception ex)
        => () => throw ex;

    private static HttpResponseMessage Build(HttpStatusCode status, string contentType, string body)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, contentType) };

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        CallCount++;
        var step = _steps[Math.Min(_index, _steps.Count - 1)];
        _index++;

        // A throwing step throws synchronously here; HttpClient captures it into the returned task at its
        // await boundary, so the client's try/catch classifies it exactly as a real transport fault would.
        return Task.FromResult(step());
    }
}
