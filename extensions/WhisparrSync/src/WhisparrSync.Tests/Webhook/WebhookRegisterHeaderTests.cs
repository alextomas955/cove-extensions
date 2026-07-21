using System.Text;
using System.Text.Json;
using Cove.Core.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using WhisparrSync.Adapters;
using WhisparrSync.Ingest;
using WhisparrSync.Options;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Webhook;

namespace WhisparrSync.Tests.Webhook;

/// <summary>
/// The auto-register header-token wiring: the v3 Notification payload
/// carries the shared secret in a <c>headers</c> entry keyed <c>X-Cove-Token</c>, which is exactly the
/// header the webhook receiver validates. So Whisparr's Test ping — which posts to the URL with the
/// configured headers — reaches the receiver authenticated and succeeds. These prove the payload carries
/// the header AND that presenting that header value passes the receiver's token check (while a wrong value
/// is still rejected).
/// </summary>
public sealed class WebhookRegisterHeaderTests
{
    private const string Secret = "s3cr3t-webhook-token_ABC-123";
    private const string CoveBase = "http://host.docker.internal:5073";

    private static string TokenHeaderValue(string payloadJson)
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
                    return header.GetProperty("value").GetString() ?? string.Empty;
                }
            }
        }

        return string.Empty;
    }

    [Fact]
    public void NotificationPayload_CarriesTheTokenAsTheXCoveTokenHeader()
    {
        var url = WebhookUrlBuilder.BuildUrl(CoveBase, Secret);

        var payload = V3Adapter.BuildNotificationPayload(url);

        Assert.Equal(Secret, TokenHeaderValue(payload));
        // The bare URL still carries ?token= for the copy-paste path — both channels authenticate.
        Assert.Contains("token=", payload);
    }

    [Fact]
    public async Task ReceiverAcceptsTheHeaderValueTheRegisterPayloadSends()
    {
        var url = WebhookUrlBuilder.BuildUrl(CoveBase, Secret);
        var headerValue = TokenHeaderValue(V3Adapter.BuildNotificationPayload(url));

        var store = new FakeStore();
        await new OptionsStore(store).SaveAsync(new WhisparrOptions { WebhookSecret = Secret });
        var receiver = NewReceiver(store);

        var accepted = await receiver.HandleAsync(TestPing(headerToken: headerValue), default);
        var rejected = await receiver.HandleAsync(TestPing(headerToken: "wrong-token"), default);

        Assert.Equal(200, StatusOf(accepted));
        Assert.Equal(401, StatusOf(rejected));
    }

    private static WebhookReceiver NewReceiver(FakeStore store)
    {
        var services = new ServiceCollection();
        services.AddScoped<IScanService>(_ => new FakeScanService());
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var coordinator = new IngestCoordinator(
            scopeFactory, _ => ValueTask.FromResult<IReadOnlyList<string>>(["/data/media"]));
        return new WebhookReceiver(store, coordinator);
    }

    private static DefaultHttpContext TestPing(string headerToken)
    {
        var http = new DefaultHttpContext();
        http.Request.Method = "POST";
        http.Request.ContentType = "application/json";
        http.Request.Headers["X-Cove-Token"] = headerToken;
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(WebhookPayloads.Test));
        return http;
    }

    private static int StatusOf(IResult result)
        => Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode ?? 200;
}
