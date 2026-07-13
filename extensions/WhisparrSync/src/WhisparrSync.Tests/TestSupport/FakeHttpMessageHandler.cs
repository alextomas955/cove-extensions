using System.Net;
using System.Text;

namespace WhisparrSync.Tests.TestSupport;

/// <summary>
/// A programmable <see cref="HttpMessageHandler"/> that returns a caller-configured status code,
/// <c>Content-Type</c>, and body so the transport-only <c>WhisparrClient</c> and its error
/// classification are testable with no live Whisparr — the outbound analogue of Renamer's
/// boundary fakes. Captures the last outbound request so a test can assert the target URL and the
/// attached <c>X-Api-Key</c> header.
/// </summary>
internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly HttpStatusCode _status;
    private readonly string _contentType;
    private readonly string _body;

    /// <summary>The last request this handler saw, for URL + header assertions. Null until a call is made.</summary>
    public HttpRequestMessage? LastRequest { get; private set; }

    public FakeHttpMessageHandler(HttpStatusCode status, string contentType, string body)
    {
        _status = status;
        _contentType = contentType;
        _body = body;
    }

    /// <summary>Convenience factory: a 200 response carrying <paramref name="json"/> as application/json.</summary>
    public static FakeHttpMessageHandler Json(string json)
        => new(HttpStatusCode.OK, "application/json", json);

    /// <summary>Convenience factory: a response with only a status code and an empty JSON body.</summary>
    public static FakeHttpMessageHandler Status(HttpStatusCode status)
        => new(status, "application/json", "{}");

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        var response = new HttpResponseMessage(_status)
        {
            Content = new StringContent(_body, Encoding.UTF8, _contentType),
        };
        return Task.FromResult(response);
    }
}
