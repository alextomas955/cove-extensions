using System.Net;
using System.Text.Json;
using WhisparrSync.Adapters;
using WhisparrSync.Client;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.Adapters;

/// <summary>
/// The connector-sourced webhook find + idempotent register wire contract (WEBHOOK-01/02). Drives the
/// adapters directly over fake HTTP — no live Whisparr, no Cove type, so it stays in the bare-CI unit tier —
/// and asserts the request ordering (GET → POST create vs GET → PUT update, never a second POST), the
/// unique-name-400/409-as-success rule, the connector read, and the token-safety invariant (T-35-06): the
/// PUT body always re-mints the <c>X-Cove-Token</c>/url from the passed webhook URL, never the stored one.
/// </summary>
public sealed class WebhookConnectionTests
{
    private const string BaseUrl = "http://localhost:6969";
    private const string ApiKey = "test-api-key";
    private const string ConnectionName = "Cove Whisparr Sync";
    private const string Token = "secret-abc-123";
    private const string WebhookUrl =
        "http://host.docker.internal:5073/api/extensions/com.alextomas955.whisparrsync/webhook?token=" + Token;
    private const string StaleUrl = "http://localhost:5073/api/extensions/com.alextomas955.whisparrsync/webhook?token=STALE";

    private static V3Adapter V3(FakeHttpMessageHandler handler)
        => new(new WhisparrClient(new HttpClient(handler)), TimeSpan.Zero);

    private static V2Adapter V2(FakeHttpMessageHandler handler)
        => new(new WhisparrClient(new HttpClient(handler)), TimeSpan.Zero);

    private static Func<HttpResponseMessage> Ok(string body)
        => FakeHttpMessageHandler.Respond(HttpStatusCode.OK, "application/json", body);

    private static string NotificationRow(int id, string name, string url) => JsonSerializer.Serialize(new
    {
        id,
        name,
        fields = new object[] { new { name = "url", value = url }, new { name = "method", value = 1 } },
    });

    private static string UrlField(string payloadJson)
    {
        using var doc = JsonDocument.Parse(payloadJson);
        foreach (var field in doc.RootElement.GetProperty("fields").EnumerateArray())
        {
            if (field.GetProperty("name").GetString() == "url")
            {
                return field.GetProperty("value").GetString() ?? "";
            }
        }

        return "";
    }

    private static string TokenHeader(string payloadJson)
    {
        using var doc = JsonDocument.Parse(payloadJson);
        foreach (var field in doc.RootElement.GetProperty("fields").EnumerateArray())
        {
            if (field.GetProperty("name").GetString() != "headers")
            {
                continue;
            }

            foreach (var header in field.GetProperty("value").EnumerateArray())
            {
                if (header.GetProperty("key").GetString() == "X-Cove-Token")
                {
                    return header.GetProperty("value").GetString() ?? "";
                }
            }
        }

        return "";
    }

    [Fact]
    public async Task Register_V3_EmptyList_GetThenPost_ReportsOk()
    {
        var handler = FakeHttpMessageHandler.Sequence(
            Ok("[]"),                                            // GET /notification -> no connection yet
            Ok(NotificationRow(1, ConnectionName, WebhookUrl))); // POST /notification -> created

        var result = await V3(handler).RegisterWebhookAsync(BaseUrl, ApiKey, WebhookUrl, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.True(result.Value);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
        Assert.EndsWith("/api/v3/notification", handler.Requests[0].Url);
        Assert.Equal(HttpMethod.Post, handler.Requests[1].Method);
        Assert.EndsWith("/api/v3/notification", handler.Requests[1].Url);
    }

    [Fact]
    public async Task Register_V3_ExistingConnection_GetThenPut_ReMintsTokenNoSecondPost()
    {
        var handler = FakeHttpMessageHandler.Sequence(
            Ok($"[{NotificationRow(7, ConnectionName, StaleUrl)}]"), // GET -> existing row 7 holding a stale url/token
            Ok(NotificationRow(7, ConnectionName, WebhookUrl)));     // PUT /notification/7 -> updated

        var result = await V3(handler).RegisterWebhookAsync(BaseUrl, ApiKey, WebhookUrl, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.True(result.Value);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Post);

        var put = handler.Requests[1];
        Assert.Equal(HttpMethod.Put, put.Method);
        Assert.EndsWith("/api/v3/notification/7", put.Url);

        // Token safety (T-35-06): the PUT body re-mints from the PASSED url, never the stale stored one, and
        // carries the row id (Whisparr rejects a notification PUT without it).
        Assert.Equal(WebhookUrl, UrlField(put.Body!));
        Assert.Equal(Token, TokenHeader(put.Body!));
        Assert.DoesNotContain("STALE", put.Body);
        Assert.Contains("\"id\":7", put.Body);
    }

    [Fact]
    public async Task Register_V3_CreateUniqueNameConflict_ResolvesToOk()
    {
        var handler = FakeHttpMessageHandler.Sequence(
            Ok("[]"),                                            // GET -> empty, so the create path is taken
            FakeHttpMessageHandler.Respond(                      // POST -> Whisparr's unique-name 400
                HttpStatusCode.BadRequest, "application/json",
                "[{\"propertyName\":\"Name\",\"errorMessage\":\"Should be unique\"}]"));

        var result = await V3(handler).RegisterWebhookAsync(BaseUrl, ApiKey, WebhookUrl, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.True(result.Value);
    }

    [Fact]
    public async Task Find_V3_ConnectionPresent_ReturnsIdAndUrl()
    {
        var handler = FakeHttpMessageHandler.Json(
            $"[{NotificationRow(3, "Other Connector", "http://x/other")},{NotificationRow(9, ConnectionName, WebhookUrl)}]");

        var result = await V3(handler).FindWebhookConnectionAsync(BaseUrl, ApiKey, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.NotNull(result.Value);
        Assert.Equal(9, result.Value!.Id);
        Assert.Equal(WebhookUrl, result.Value.Url);
    }

    [Fact]
    public async Task Find_V3_EmptyList_ReturnsNull()
    {
        var result = await V3(FakeHttpMessageHandler.Json("[]"))
            .FindWebhookConnectionAsync(BaseUrl, ApiKey, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task Register_V2_ExistingConnection_GetThenPut_ReMintsToken()
    {
        var handler = FakeHttpMessageHandler.Sequence(
            Ok($"[{NotificationRow(4, ConnectionName, StaleUrl)}]"), // GET -> existing v2 connection
            Ok(NotificationRow(4, ConnectionName, WebhookUrl)));     // PUT /notification/4 -> updated

        var result = await V2(handler).RegisterWebhookAsync(BaseUrl, ApiKey, WebhookUrl, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.True(result.Value);

        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Post);
        var put = Assert.Single(handler.Requests, r => r.Method == HttpMethod.Put);
        // v2 reads/writes the SAME Sonarr-shaped /api/v3/notification endpoint as v3.
        Assert.EndsWith("/api/v3/notification/4", put.Url);
        Assert.Equal(Token, TokenHeader(put.Body!));
        Assert.DoesNotContain("STALE", put.Body);
    }
}
