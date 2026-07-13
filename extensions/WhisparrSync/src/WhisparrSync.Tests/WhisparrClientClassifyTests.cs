using System.Net;
using System.Text.Json;
using WhisparrSync.Client;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests;

/// <summary>
/// The full classify-not-throw contract for the transport-only <c>WhisparrClient</c> (CONN-02): every
/// failure class maps to a distinct <see cref="WhisparrResultState"/> with status + <c>Content-Type</c>
/// checked BEFORE any deserialize, idempotent GETs retry a bounded number of times on a transient
/// transport fault, and the non-idempotent webhook POST is never blind-retried.
/// </summary>
public sealed class WhisparrClientClassifyTests
{
    private const string BaseUrl = "http://localhost:6969";
    private const string ApiKey = "test-api-key";

    private static WhisparrClient ClientFor(FakeHttpMessageHandler handler) => new(new HttpClient(handler));

    [Fact]
    public async Task Ok_json_status_classifies_as_ok_with_payload()
    {
        var body = JsonSerializer.Serialize(new { version = "3.3.4.808", instanceName = "My Whisparr" });
        var result = await ClientFor(FakeHttpMessageHandler.Json(body)).GetStatusAsync(BaseUrl, ApiKey, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.Equal("3.3.4.808", result.Value!.Version);
        Assert.Equal("My Whisparr", result.Value.InstanceName);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Unauthorized_or_forbidden_classifies_as_bad_key(HttpStatusCode status)
    {
        var result = await ClientFor(FakeHttpMessageHandler.Status(status)).GetStatusAsync(BaseUrl, ApiKey, CancellationToken.None);

        Assert.Equal(WhisparrResultState.BadKey, result.State);
    }

    [Fact]
    public async Task Connection_refused_classifies_as_unreachable()
    {
        var handler = FakeHttpMessageHandler.Sequence(FakeHttpMessageHandler.Throw(new HttpRequestException("connection refused")));
        var result = await ClientFor(handler).GetStatusAsync(BaseUrl, ApiKey, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Unreachable, result.State);
        Assert.Equal("connection refused", result.Reason);
    }

    [Fact]
    public async Task Timeout_classifies_as_unreachable()
    {
        // A TaskCanceledException from the transport (the linked per-call timeout token firing) while the
        // caller's own token is NOT cancelled → the client reports "timeout", not a caller cancellation.
        var handler = FakeHttpMessageHandler.Sequence(FakeHttpMessageHandler.Throw(new TaskCanceledException()));
        var result = await ClientFor(handler).GetStatusAsync(BaseUrl, ApiKey, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Unreachable, result.State);
        Assert.Equal("timeout", result.Reason);
    }

    [Fact]
    public async Task Html_body_classifies_as_not_whisparr_without_deserializing()
    {
        // A reverse-proxy landing page: 200 but text/html. Must classify as NotWhisparr, never crash on a
        // JSON parse of the HTML.
        var result = await ClientFor(FakeHttpMessageHandler.Html(HttpStatusCode.OK)).GetStatusAsync(BaseUrl, ApiKey, CancellationToken.None);

        Assert.Equal(WhisparrResultState.NotWhisparr, result.State);
    }

    [Fact]
    public async Task Bad_gateway_html_classifies_as_not_whisparr()
    {
        var result = await ClientFor(FakeHttpMessageHandler.Html(HttpStatusCode.BadGateway)).GetStatusAsync(BaseUrl, ApiKey, CancellationToken.None);

        Assert.Equal(WhisparrResultState.NotWhisparr, result.State);
    }

    [Fact]
    public async Task Non_success_json_classifies_as_unreachable()
    {
        var result = await ClientFor(FakeHttpMessageHandler.Status(HttpStatusCode.InternalServerError)).GetStatusAsync(BaseUrl, ApiKey, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Unreachable, result.State);
    }

    [Fact]
    public async Task Malformed_json_body_classifies_as_not_whisparr()
    {
        // Claims application/json but the body is not parseable — classify rather than throw.
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, "application/json", "this is not json");
        var result = await ClientFor(handler).GetStatusAsync(BaseUrl, ApiKey, CancellationToken.None);

        Assert.Equal(WhisparrResultState.NotWhisparr, result.State);
    }

    [Fact]
    public async Task List_root_folders_deserializes_rows()
    {
        var body = JsonSerializer.Serialize(new[]
        {
            new { id = 1, path = "/data/media", accessible = true, freeSpace = (long?)5000 },
            new { id = 2, path = "/mnt/other", accessible = false, freeSpace = (long?)null },
        });
        var result = await ClientFor(FakeHttpMessageHandler.Json(body)).ListRootFoldersAsync(BaseUrl, ApiKey, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.Equal(2, result.Value!.Length);
        Assert.Equal("/data/media", result.Value[0].Path);
        Assert.True(result.Value[0].Accessible);
    }

    [Fact]
    public async Task List_quality_profiles_deserializes_rows()
    {
        var body = JsonSerializer.Serialize(new[]
        {
            new { id = 1, name = "Any" },
            new { id = 4, name = "HD-1080p" },
        });
        var result = await ClientFor(FakeHttpMessageHandler.Json(body)).ListQualityProfilesAsync(BaseUrl, ApiKey, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.Equal(2, result.Value!.Length);
        Assert.Equal("HD-1080p", result.Value[1].Name);
    }

    [Fact]
    public async Task Idempotent_get_retries_once_on_transient_fault()
    {
        var handler = FakeHttpMessageHandler.Sequence(
            FakeHttpMessageHandler.Throw(new HttpRequestException("connection reset")),
            FakeHttpMessageHandler.Respond(HttpStatusCode.OK, "application/json", "[{\"id\":1,\"name\":\"Any\"}]"));

        var result = await ClientFor(handler).ListQualityProfilesAsync(BaseUrl, ApiKey, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task Idempotent_get_retry_is_bounded()
    {
        var handler = FakeHttpMessageHandler.Sequence(FakeHttpMessageHandler.Throw(new HttpRequestException("down")));
        var result = await ClientFor(handler).GetStatusAsync(BaseUrl, ApiKey, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Unreachable, result.State);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task Webhook_post_is_never_blind_retried()
    {
        var handler = FakeHttpMessageHandler.Sequence(
            FakeHttpMessageHandler.Throw(new HttpRequestException("connection reset")),
            FakeHttpMessageHandler.Respond(HttpStatusCode.OK, "application/json", "{}"));

        var result = await ClientFor(handler).RegisterWebhookAsync(BaseUrl, ApiKey, "{}", CancellationToken.None);

        Assert.Equal(WhisparrResultState.Unreachable, result.State);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Webhook_post_targets_notification_endpoint_with_api_key()
    {
        var handler = FakeHttpMessageHandler.Json("{}");
        var result = await ClientFor(handler).RegisterWebhookAsync(BaseUrl, ApiKey, "{\"name\":\"x\"}", CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.Equal($"{BaseUrl}/api/v3/notification", handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Post, handler.LastRequest.Method);
        Assert.True(handler.LastRequest.Headers.TryGetValues("X-Api-Key", out var values));
        Assert.Equal(ApiKey, Assert.Single(values!));
    }

    [Fact]
    public async Task Empty_base_url_classifies_as_unreachable_without_calling_out()
    {
        // WR-02: an empty base URL yields a relative request URI (no BaseAddress). It must classify as
        // Unreachable at the transport edge — never escape as an unhandled 500 — and never hit the network.
        var handler = FakeHttpMessageHandler.Json("[]");
        var result = await ClientFor(handler).GetStatusAsync(string.Empty, ApiKey, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Unreachable, result.State);
        Assert.Equal("invalid url", result.Reason);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Empty_base_url_on_register_webhook_classifies_as_unreachable()
    {
        // WR-02's widened trigger: the register path uses the STORED base URL, which the UI can invoke
        // (Register button) before a Save while it is still empty. It must not 500.
        var handler = FakeHttpMessageHandler.Json("{}");
        var result = await ClientFor(handler).RegisterWebhookAsync(string.Empty, ApiKey, "{}", CancellationToken.None);

        Assert.Equal(WhisparrResultState.Unreachable, result.State);
        Assert.Equal("invalid url", result.Reason);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Non_http_scheme_classifies_as_unreachable()
    {
        // A non-http(s) base URL (e.g. file://) is rejected at the transport edge rather than dispatched.
        var handler = FakeHttpMessageHandler.Json("[]");
        var result = await ClientFor(handler).GetStatusAsync("file:///etc/passwd", ApiKey, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Unreachable, result.State);
        Assert.Equal("invalid url", result.Reason);
        Assert.Equal(0, handler.CallCount);
    }
}
