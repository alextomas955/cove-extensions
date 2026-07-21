using System.Text.Json;
using Cove.Core.Auth;
using Cove.Plugins;
using Microsoft.AspNetCore.Http;
using WhisparrSync.Client;
using WhisparrSync.Tests.TestSupport;
using Ext = global::WhisparrSync.WhisparrSync;

namespace WhisparrSync.Tests.Api;

/// <summary>
/// Security-critical: the host's <c>[RequiresPermission]</c> filter is inert on
/// minimal-API extension endpoints, so the <c>/monitor</c> + <c>/monitor-status</c> handlers enforce
/// <c>extensions.configure</c> themselves and reach the stored credentials only. These prove the deny/allow
/// pair for both routes, that the outbound call carries the STORED host + key (never a caller value — the body
/// carries none), that the API key is never echoed in the response, and that an entity with no matching
/// StashDB endpoint is rejected server-side without any outbound call. Mirrors <see cref="EndpointAuthTests"/>.
/// </summary>
public sealed class MonitorEndpointAuthTests
{
    private const string StoredBaseUrl = "http://stored.local:6969";
    private const string StoredKey = "STORED-KEY";
    private const string StashDbEndpoint = "https://stashdb.org/graphql"; // the WhisparrOptions default
    private const string StashId = "157c9e0d-5f8e-446a-b1c5-dddf3cb5b2d1";

    private static Ext NewExtension(FakeStore? store = null)
    {
        var ext = new Ext();
        ((IStatefulExtension)ext).SetStore(store ?? new FakeStore());
        return ext;
    }

    private static WhisparrClient ClientReturning(string json)
        => new(new HttpClient(FakeHttpMessageHandler.Json(json)));

    // Exposes the handler so a test can assert the outbound request URL + X-Api-Key header and count calls.
    private static (WhisparrClient Client, FakeHttpMessageHandler Handler) ClientWithHandler(string json)
    {
        var handler = FakeHttpMessageHandler.Json(json);
        return (new WhisparrClient(new HttpClient(handler)), handler);
    }

    // A v3-configured store with a stored host + key (SelectedVersion v3, default StashDbEndpoint).
    private static async Task<FakeStore> StoreWith(string baseUrl, string apiKey)
    {
        var store = new FakeStore();
        await store.SetAsync(
            "options", $"{{\"BaseUrl\":\"{baseUrl}\",\"ApiKey\":\"{apiKey}\",\"SelectedVersion\":\"v3\"}}");
        return store;
    }

    private static string SentApiKey(FakeHttpMessageHandler handler)
        => handler.LastRequest!.Headers.TryGetValues("X-Api-Key", out var values)
            ? string.Concat(values!)
            : string.Empty;

    private static int StatusOf(IResult result)
        => Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode ?? 0;

    private static string ResponseJson(IResult result)
        => JsonSerializer.Serialize(Assert.IsAssignableFrom<IValueHttpResult>(result).Value);

    // A remote-id set whose StashDB endpoint matches the stored default, so the handler resolves a stashId.
    private static Ext.RemoteIdInput[] MatchingRemotes() => [new(StashDbEndpoint, StashId)];

    // A remote-id set whose only endpoint is a DIFFERENT metadata server — no StashDB identity to resolve.
    private static Ext.RemoteIdInput[] NonMatchingRemotes() => [new("https://theporndb.net/graphql", "some-id")];

    private static Ext.MonitorRequest MonitorOff() => new("studio", MatchingRemotes(), Monitored: false);
    private static Ext.MonitorStatusRequest StatusReq() => new("studio", MatchingRemotes());

    // ---- 403-first permission gate: both routes require extensions.configure ----

    [Fact]
    public async Task Monitor_WithoutConfigure_Returns403()
    {
        var result = await NewExtension().MonitorAsync(
            MonitorOff(), ClientReturning("[]"), FakePrincipalAccessor.None(), default);
        Assert.Equal(403, StatusOf(result));
    }

    [Fact]
    public async Task Monitor_WithReadOnly_Returns403()
    {
        // extensions.read is a strictly lower privilege that must not reach this mutation.
        var result = await NewExtension().MonitorAsync(
            MonitorOff(), ClientReturning("[]"),
            FakePrincipalAccessor.WithPermissions(Permissions.ExtensionsRead), default);
        Assert.Equal(403, StatusOf(result));
    }

    [Fact]
    public async Task MonitorStatus_WithoutConfigure_Returns403()
    {
        var result = await NewExtension().MonitorStatusAsync(
            StatusReq(), ClientReturning("[]"), FakePrincipalAccessor.None(), default);
        Assert.Equal(403, StatusOf(result));
    }

    [Fact]
    public async Task MonitorStatus_WithReadOnly_Returns403()
    {
        var result = await NewExtension().MonitorStatusAsync(
            StatusReq(), ClientReturning("[]"),
            FakePrincipalAccessor.WithPermissions(Permissions.ExtensionsRead), default);
        Assert.Equal(403, StatusOf(result));
    }

    // ---- configure IS allowed (not forbidden) ----

    [Fact]
    public async Task Monitor_WithConfigure_IsNotForbidden()
    {
        // Monitor OFF on an absent studio: GET studio -> [] -> Ok(added:false), a clean single-call path.
        var store = await StoreWith(StoredBaseUrl, StoredKey);
        var result = await NewExtension(store).MonitorAsync(
            MonitorOff(), ClientReturning("[]"),
            FakePrincipalAccessor.WithPermissions(Permissions.ExtensionsConfigure), default);
        Assert.NotEqual(403, StatusOf(result));
    }

    [Fact]
    public async Task MonitorStatus_WithConfigure_IsNotForbidden()
    {
        var store = await StoreWith(StoredBaseUrl, StoredKey);
        var result = await NewExtension(store).MonitorStatusAsync(
            StatusReq(), ClientReturning("[]"),
            FakePrincipalAccessor.WithPermissions(Permissions.ExtensionsConfigure), default);
        Assert.NotEqual(403, StatusOf(result));
    }

    // ---- the outbound call uses the STORED host + key, never a caller value ----

    [Fact]
    public async Task Monitor_OutboundCall_UsesTheStoredHostAndKey()
    {
        // The request body carries NO url/key, so the only creds available are the stored ones. Prove the
        // studio GET targets the stored host and sends the stored X-Api-Key (never blank, never a caller value).
        var store = await StoreWith(StoredBaseUrl, StoredKey);
        var (client, handler) = ClientWithHandler("[]");

        await NewExtension(store).MonitorAsync(
            MonitorOff(), client,
            FakePrincipalAccessor.WithPermissions(Permissions.ExtensionsConfigure), default);

        Assert.NotNull(handler.LastRequest);
        Assert.StartsWith(StoredBaseUrl + "/", handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal(StoredKey, SentApiKey(handler));
    }

    // ---- the API key is never echoed to the response ----

    [Fact]
    public async Task MonitorStatus_ResponseNeverContainsTheApiKey()
    {
        const string secretKey = "SUPER-SECRET-KEY-9f3a";
        var store = await StoreWith(StoredBaseUrl, secretKey);
        var result = await NewExtension(store).MonitorStatusAsync(
            StatusReq(), ClientReturning("[]"),
            FakePrincipalAccessor.WithPermissions(Permissions.ExtensionsConfigure), default);

        Assert.DoesNotContain(secretKey, ResponseJson(result), StringComparison.Ordinal);
    }

    // ---- server-side stashId resolution: no matching StashDB endpoint -> handled, and NO outbound call ----

    [Fact]
    public async Task Monitor_NoMatchingStashDbEndpoint_ReturnsNoIdentity_AndMakesNoOutboundCall()
    {
        var store = await StoreWith(StoredBaseUrl, StoredKey);
        var (client, handler) = ClientWithHandler("[]");

        var req = new Ext.MonitorRequest("studio", NonMatchingRemotes(), Monitored: true);
        var result = await NewExtension(store).MonitorAsync(
            req, client, FakePrincipalAccessor.WithPermissions(Permissions.ExtensionsConfigure), default);

        Assert.NotEqual(403, StatusOf(result));
        Assert.Contains("NO_STASHDB_IDENTITY", ResponseJson(result), StringComparison.Ordinal);
        Assert.Equal(0, handler.CallCount); // rejected before any Whisparr call
    }

    [Fact]
    public async Task MonitorStatus_NoMatchingStashDbEndpoint_ReturnsNoIdentity_AndMakesNoOutboundCall()
    {
        var store = await StoreWith(StoredBaseUrl, StoredKey);
        var (client, handler) = ClientWithHandler("[]");

        var req = new Ext.MonitorStatusRequest("studio", NonMatchingRemotes());
        var result = await NewExtension(store).MonitorStatusAsync(
            req, client, FakePrincipalAccessor.WithPermissions(Permissions.ExtensionsConfigure), default);

        Assert.Contains("NO_STASHDB_IDENTITY", ResponseJson(result), StringComparison.Ordinal);
        Assert.Equal(0, handler.CallCount);
    }

    // ---- an unknown kind is rejected with a 400 (never guessed) ----

    [Fact]
    public async Task Monitor_UnknownKind_Returns400()
    {
        var store = await StoreWith(StoredBaseUrl, StoredKey);
        var (client, handler) = ClientWithHandler("[]");

        var req = new Ext.MonitorRequest("franchise", MatchingRemotes(), Monitored: true);
        var result = await NewExtension(store).MonitorAsync(
            req, client, FakePrincipalAccessor.WithPermissions(Permissions.ExtensionsConfigure), default);

        Assert.Equal(400, StatusOf(result));
        Assert.Equal(0, handler.CallCount);
    }
}
