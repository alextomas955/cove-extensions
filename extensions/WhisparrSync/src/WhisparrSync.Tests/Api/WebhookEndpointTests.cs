using System.Net;
using System.Text.Json;
using Cove.Core.Auth;
using Cove.Plugins;
using Microsoft.AspNetCore.Http;
using WhisparrSync.Client;
using WhisparrSync.Options;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Webhook;
using Ext = global::WhisparrSync.WhisparrSync;

namespace WhisparrSync.Tests.Api;

/// <summary>
/// The connector-sourced /webhook-url read + host-persisting idempotent /register-webhook handler
/// (WEBHOOK-01/02/03) at the endpoint boundary. Injects a <see cref="FakePrincipalAccessor"/> (a Cove.Core
/// auth type) so it is a cove-referencing test (Compile-Removed on the bare-CI path). Proves: both routes
/// are 403-first for a read-only principal; the read reports the connector's URL + registered:true when the
/// connection exists and the derived default + registered:false when it does not (persisted WebhookHost, else
/// the request host); a down Whisparr degrades to a 200 registered:false (never a 500); and register persists
/// the resolved host while reporting registered:true idempotently.
/// </summary>
public sealed class WebhookEndpointTests
{
    private const string StoredBaseUrl = "http://stored.local:6969";
    private const string StoredApiKey = "STORED-KEY";
    private const string RequestHost = "http://cove.local";
    private const string PersistedHost = "http://host.docker.internal:5073";
    private const string ConnectionName = "Cove Whisparr Sync";
    private const string ConnectorUrl =
        "http://host.docker.internal:5073/api/extensions/com.alextomas955.whisparrsync/webhook?token=connector-token";

    private static Ext NewExtension(FakeStore store)
    {
        var ext = new Ext();
        ((IStatefulExtension)ext).SetStore(store);
        return ext;
    }

    private static WhisparrClient ClientReturning(string json)
        => new(new HttpClient(FakeHttpMessageHandler.Json(json)));

    private static async Task<FakeStore> StoreWith(string? webhookHost = null)
    {
        var host = webhookHost is null ? "" : $",\"WebhookHost\":\"{webhookHost}\"";
        var store = new FakeStore();
        await store.SetAsync(
            "options",
            $"{{\"BaseUrl\":\"{StoredBaseUrl}\",\"ApiKey\":\"{StoredApiKey}\",\"SelectedVersion\":\"v3\"{host}}}");
        return store;
    }

    private static string NotificationRow(int id, string name, string url) => JsonSerializer.Serialize(new
    {
        id,
        name,
        fields = new object[] { new { name = "url", value = url }, new { name = "method", value = 1 } },
    });

    private static int StatusOf(IResult result)
        => Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode ?? 0;

    private static Ext.WebhookUrlResponse ReadResponse(IResult result)
        => (Ext.WebhookUrlResponse)Assert.IsAssignableFrom<IValueHttpResult>(result).Value!;

    private static bool Registered(IResult result)
    {
        var value = Assert.IsAssignableFrom<IValueHttpResult>(result).Value!;
        return (bool)value.GetType().GetProperty("registered")!.GetValue(value)!;
    }

    private static FakePrincipalAccessor Configure()
        => FakePrincipalAccessor.WithPermissions(Permissions.ExtensionsConfigure);

    private static FakePrincipalAccessor ReadOnly()
        => FakePrincipalAccessor.WithPermissions(Permissions.ExtensionsRead);

    [Fact]
    public async Task WebhookUrl_WithReadOnly_Returns403()
    {
        var result = await NewExtension(await StoreWith()).WebhookUrlAsync(
            RequestHost, ClientReturning("[]"), ReadOnly(), default);
        Assert.Equal(403, StatusOf(result));
    }

    [Fact]
    public async Task RegisterWebhook_WithReadOnly_Returns403()
    {
        var result = await NewExtension(await StoreWith()).RegisterWebhookAsync(
            RequestHost, ClientReturning("{}"), ReadOnly(), default);
        Assert.Equal(403, StatusOf(result));
    }

    [Fact]
    public async Task WebhookUrl_ConnectionPresent_ReturnsConnectorUrlAndRegisteredTrue()
    {
        var client = ClientReturning($"[{NotificationRow(9, ConnectionName, ConnectorUrl)}]");
        var result = await NewExtension(await StoreWith()).WebhookUrlAsync(
            RequestHost, client, Configure(), default);

        var response = ReadResponse(result);
        Assert.True(response.Registered);
        Assert.Equal(ConnectorUrl, response.Url);
    }

    [Fact]
    public async Task WebhookUrl_EmptyList_ReturnsDerivedFromPersistedHostAndRegisteredFalse()
    {
        var client = ClientReturning("[]");
        var result = await NewExtension(await StoreWith(webhookHost: PersistedHost)).WebhookUrlAsync(
            RequestHost, client, Configure(), default);

        var response = ReadResponse(result);
        Assert.False(response.Registered);
        // The derived default falls back to the PERSISTED host (WEBHOOK-03), not the request host.
        Assert.StartsWith(PersistedHost + WebhookUrlBuilder.WebhookPath + "?token=", response.Url);
    }

    [Fact]
    public async Task WebhookUrl_EmptyList_NoPersistedHost_DerivesFromRequestHost()
    {
        var client = ClientReturning("[]");
        var result = await NewExtension(await StoreWith()).WebhookUrlAsync(
            RequestHost, client, Configure(), default);

        var response = ReadResponse(result);
        Assert.False(response.Registered);
        Assert.StartsWith(RequestHost + WebhookUrlBuilder.WebhookPath + "?token=", response.Url);
    }

    [Fact]
    public async Task WebhookUrl_WhisparrDown_Returns200RegisteredFalse_NoThrow()
    {
        // A transport fault on the notification list must degrade to the derived default + registered:false,
        // never a 500 — a down Whisparr cannot block the settings page load.
        var handler = FakeHttpMessageHandler.Sequence(
            FakeHttpMessageHandler.Throw(new HttpRequestException("connection refused")));
        var client = new WhisparrClient(new HttpClient(handler));

        var result = await NewExtension(await StoreWith(webhookHost: PersistedHost)).WebhookUrlAsync(
            RequestHost, client, Configure(), default);

        // No throw and no error result — the handler returns the success JSON response (a JsonHttpResult
        // leaves StatusCode unset, HTTP-rendered as 200), never a 500.
        Assert.NotEqual(500, StatusOf(result));
        var response = ReadResponse(result);
        Assert.False(response.Registered);
        Assert.StartsWith(PersistedHost + WebhookUrlBuilder.WebhookPath + "?token=", response.Url);
    }

    [Fact]
    public async Task RegisterWebhook_ExistingConnection_ReturnsRegisteredTrue_AndPersistsHost()
    {
        var handler = FakeHttpMessageHandler.Sequence(
            FakeHttpMessageHandler.Respond(
                HttpStatusCode.OK, "application/json", $"[{NotificationRow(7, ConnectionName, ConnectorUrl)}]"),
            FakeHttpMessageHandler.Respond(
                HttpStatusCode.OK, "application/json", NotificationRow(7, ConnectionName, ConnectorUrl)));
        var client = new WhisparrClient(new HttpClient(handler));
        var store = await StoreWith();
        var ext = NewExtension(store);

        var result = await ext.RegisterWebhookAsync(RequestHost, client, Configure(), default);
        Assert.NotEqual(403, StatusOf(result));
        Assert.True(Registered(result));

        // WEBHOOK-03: the resolved origin (the request host here — no override) survives to WhisparrOptions.
        var loaded = await ext.GetOptionsAsync(ReadOnly(), default);
        var view = (OptionsView)Assert.IsAssignableFrom<IValueHttpResult>(loaded).Value!;
        Assert.Equal(RequestHost, view.WebhookHost);
    }
}
