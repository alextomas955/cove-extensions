using System.Text.Json;
using Cove.Core.Auth;
using Cove.Plugins;
using Microsoft.AspNetCore.Http;
using WhisparrSync.Client;
using WhisparrSync.Tests.TestSupport;
using Ext = global::WhisparrSync.WhisparrSync;

namespace WhisparrSync.Tests.Api;

/// <summary>
/// Security-critical: the host's <c>[RequiresPermission]</c> filter is
/// inert on minimal-API extension endpoints, so the read-only scene-status handlers
/// (<c>/scene-status-summary</c>, <c>/scene-detail</c>) enforce
/// <c>extensions.configure</c> themselves and reach the stored credentials only. These prove the deny/allow
/// pair for both routes, that an outbound status read carries the STORED host + key (never a caller
/// value — the body carries none), that the API key is never echoed in the response, and that a scene with
/// no resolvable StashDB identity is the handled <c>NO_STASHDB_IDENTITY</c> outcome with NO outbound call.
/// Mirrors <see cref="MonitorEndpointAuthTests"/>.
/// </summary>
public sealed class SceneEndpointAuthTests
{
    private const string StoredBaseUrl = "http://stored.local:6969";
    private const string StoredKey = "STORED-KEY";

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

    // ---- 403-first permission gate: every route requires extensions.configure ----

    [Fact]
    public async Task Summary_WithoutConfigure_Returns403()
    {
        var result = await NewExtension().SceneStatusSummaryAsync(
            ClientReturning("[]"), FakePrincipalAccessor.None(), default);
        Assert.Equal(403, StatusOf(result));
    }

    [Fact]
    public async Task Summary_WithReadOnly_Returns403()
    {
        // EoP: a status read reaches the stored key to call Whisparr, so extensions.read (a strictly
        // lower privilege) must NOT reach it — only extensions.configure.
        var result = await NewExtension().SceneStatusSummaryAsync(
            ClientReturning("[]"), FakePrincipalAccessor.WithPermissions(Permissions.ExtensionsRead), default);
        Assert.Equal(403, StatusOf(result));
    }

    [Fact]
    public async Task Summary_NullPrincipal_Returns403()
    {
        var result = await NewExtension().SceneStatusSummaryAsync(
            ClientReturning("[]"), FakePrincipalAccessor.NullPrincipal(), default);
        Assert.Equal(403, StatusOf(result));
    }

    [Fact]
    public async Task Detail_WithoutConfigure_Returns403()
    {
        var result = await NewExtension().SceneDetailAsync(
            new Ext.SceneDetailRequest(5), ClientReturning("[]"), FakePrincipalAccessor.None(), default);
        Assert.Equal(403, StatusOf(result));
    }

    [Fact]
    public async Task Detail_WithReadOnly_Returns403()
    {
        var result = await NewExtension().SceneDetailAsync(
            new Ext.SceneDetailRequest(5), ClientReturning("[]"),
            FakePrincipalAccessor.WithPermissions(Permissions.ExtensionsRead), default);
        Assert.Equal(403, StatusOf(result));
    }

    // ---- configure IS allowed (not forbidden) on every route ----

    [Fact]
    public async Task Summary_WithConfigure_IsNotForbidden()
    {
        var store = await StoreWith(StoredBaseUrl, StoredKey);
        var result = await NewExtension(store).SceneStatusSummaryAsync(
            ClientReturning("[]"), FakePrincipalAccessor.WithPermissions(Permissions.ExtensionsConfigure), default);
        Assert.NotEqual(403, StatusOf(result));
    }

    [Fact]
    public async Task Detail_WithConfigure_IsNotForbidden()
    {
        var store = await StoreWith(StoredBaseUrl, StoredKey);
        var result = await NewExtension(store).SceneDetailAsync(
            new Ext.SceneDetailRequest(5), ClientReturning("[]"),
            FakePrincipalAccessor.WithPermissions(Permissions.ExtensionsConfigure), default);
        Assert.NotEqual(403, StatusOf(result));
    }

    // ---- the summary's outbound reads use the STORED host + key, never a caller value ----

    [Fact]
    public async Task Summary_OutboundCall_UsesTheStoredHostAndKey()
    {
        // The request has no body, so the only creds available are the stored ones. The summary reads the
        // movie set + exclusion set (two outbound GETs); prove they target the stored host with the stored key.
        var store = await StoreWith(StoredBaseUrl, StoredKey);
        var (client, handler) = ClientWithHandler("[]");

        await NewExtension(store).SceneStatusSummaryAsync(
            client, FakePrincipalAccessor.WithPermissions(Permissions.ExtensionsConfigure), default);

        Assert.Equal(2, handler.CallCount); // movie read + exclusion read
        Assert.All(handler.Requests, r => Assert.StartsWith(StoredBaseUrl + "/", r.Url));
        Assert.Equal(StoredKey, SentApiKey(handler));
    }

    // ---- the API key is never echoed to the response ----

    [Fact]
    public async Task Summary_ResponseNeverContainsTheApiKey()
    {
        const string secretKey = "SUPER-SECRET-KEY-9f3a";
        var store = await StoreWith(StoredBaseUrl, secretKey);
        var result = await NewExtension(store).SceneStatusSummaryAsync(
            ClientReturning("[]"), FakePrincipalAccessor.WithPermissions(Permissions.ExtensionsConfigure), default);

        Assert.DoesNotContain(secretKey, ResponseJson(result), StringComparison.Ordinal);
    }

    // ---- server-side identity resolution: a scene with no StashDB id is handled, and makes NO outbound call ----

    [Fact]
    public async Task Detail_NoResolvableStashId_ReturnsNoIdentity_AndMakesNoOutboundCall()
    {
        // With no host DB scope the scene cannot be resolved (LoadVideoByIdSafeAsync degrades to null), so the
        // handler returns the handled NO_STASHDB_IDENTITY outcome BEFORE any Whisparr call — never a 500.
        var store = await StoreWith(StoredBaseUrl, StoredKey);
        var (client, handler) = ClientWithHandler("[]");

        var result = await NewExtension(store).SceneDetailAsync(
            new Ext.SceneDetailRequest(5), client,
            FakePrincipalAccessor.WithPermissions(Permissions.ExtensionsConfigure), default);

        Assert.NotEqual(403, StatusOf(result));
        Assert.Contains("NO_STASHDB_IDENTITY", ResponseJson(result), StringComparison.Ordinal);
        Assert.Equal(0, handler.CallCount);
    }
}
