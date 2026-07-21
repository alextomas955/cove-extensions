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
/// inert on minimal-API extension endpoints, so the five scene push/search/bulk handlers
/// (<c>/scene-add</c>, <c>/scene-search</c>, <c>/scene-monitor</c>, <c>/bulk-add-missing</c>,
/// <c>/bulk-search-monitored</c>) enforce <c>extensions.configure</c> themselves and reach the stored
/// credentials only. These prove, for every route: the deny trio (null / read-only / no-configure → 403) and
/// the allow (configure proceeds); that a scene/entity with no resolvable StashDB identity is the handled
/// <c>NO_STASHDB_IDENTITY</c> outcome with NO outbound call; that a v2 instance returns
/// <c>VERSION_UNSUPPORTED</c> (400, never a 500 — scenes are v3-only); and — on the identity-
/// from-body bulk-search route — that the outbound call carries the STORED host + key (never a caller value,
/// the body carries none) and that the stored key never appears in the response. Mirrors
/// <see cref="MonitorEndpointAuthTests"/> / <see cref="SceneEndpointAuthTests"/>.
/// </summary>
public sealed class SceneActionEndpointAuthTests
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

    private static (WhisparrClient Client, FakeHttpMessageHandler Handler) ClientWithHandler(string json)
    {
        var handler = FakeHttpMessageHandler.Json(json);
        return (new WhisparrClient(new HttpClient(handler)), handler);
    }

    // A store with a stored host + key. version defaults to v3; pass "v2" to prove the graceful deferral.
    private static async Task<FakeStore> StoreWith(string baseUrl, string apiKey, string version = "v3")
    {
        var store = new FakeStore();
        await store.SetAsync(
            "options", $"{{\"BaseUrl\":\"{baseUrl}\",\"ApiKey\":\"{apiKey}\",\"SelectedVersion\":\"{version}\"}}");
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

    // Drives one of the five routes by name so the deny/allow/version matrices are a single [Theory] each.
    private static Task<IResult> Invoke(
        string route, Ext ext, WhisparrClient client, ICurrentPrincipalAccessor principal,
        Ext.RemoteIdInput[]? remotes = null)
        => route switch
        {
            "scene-add" => ext.SceneAddAsync(new Ext.SceneAddRequest(5), client, principal, default),
            "scene-search" => ext.SceneSearchAsync(new Ext.SceneSearchRequest(5), client, principal, default),
            "scene-monitor" => ext.SceneMonitorAsync(new Ext.SceneMonitorRequest(5, true), client, principal, default),
            "bulk-add-missing" => ext.BulkAddMissingAsync(new Ext.BulkAddMissingRequest("studio", 7), client, principal, default),
            "bulk-search-monitored" => ext.BulkSearchMonitoredAsync(
                new Ext.BulkSearchMonitoredRequest("studio", remotes ?? MatchingRemotes()), client, principal, default),
            _ => throw new ArgumentOutOfRangeException(nameof(route), route, "unknown route"),
        };

    public static TheoryData<string> AllRoutes() => new()
    {
        "scene-add", "scene-search", "scene-monitor", "bulk-add-missing", "bulk-search-monitored",
    };

    // ---- 403-first permission gate: every route requires extensions.configure ----

    [Theory]
    [MemberData(nameof(AllRoutes))]
    public async Task Route_WithoutConfigure_Returns403(string route)
    {
        var result = await Invoke(route, NewExtension(), ClientReturning("[]"), FakePrincipalAccessor.None());
        Assert.Equal(403, StatusOf(result));
    }

    [Theory]
    [MemberData(nameof(AllRoutes))]
    public async Task Route_WithReadOnly_Returns403(string route)
    {
        // EoP: a scene mutation reaches the stored key to call Whisparr, so extensions.read (a strictly
        // lower privilege) must NOT reach it — only extensions.configure.
        var result = await Invoke(
            route, NewExtension(), ClientReturning("[]"),
            FakePrincipalAccessor.WithPermissions(Permissions.ExtensionsRead));
        Assert.Equal(403, StatusOf(result));
    }

    [Theory]
    [MemberData(nameof(AllRoutes))]
    public async Task Route_NullPrincipal_Returns403(string route)
    {
        var result = await Invoke(route, NewExtension(), ClientReturning("[]"), FakePrincipalAccessor.NullPrincipal());
        Assert.Equal(403, StatusOf(result));
    }

    // ---- configure IS allowed (not forbidden) on every route ----

    [Theory]
    [MemberData(nameof(AllRoutes))]
    public async Task Route_WithConfigure_IsNotForbidden(string route)
    {
        var store = await StoreWith(StoredBaseUrl, StoredKey);
        var result = await Invoke(
            route, NewExtension(store), ClientReturning("[]"),
            FakePrincipalAccessor.WithPermissions(Permissions.ExtensionsConfigure));
        Assert.NotEqual(403, StatusOf(result));
    }

    // ---- v2 → VERSION_UNSUPPORTED (400, never a 500): the per-scene add + bulk-add-missing surface is v3-only ----
    // (bulk-search-monitored is NOT here — a v2 studio search-all GOes via the SITE episode search; see below.)

    [Theory]
    [InlineData("scene-add")]
    [InlineData("scene-search")]
    [InlineData("scene-monitor")]
    [InlineData("bulk-add-missing")]
    public async Task Route_V2Instance_ReturnsVersionUnsupported400(string route)
    {
        // The v2 guard fires BEFORE any scene resolution or outbound call, so it is a clean 400 for every route.
        var store = await StoreWith(StoredBaseUrl, StoredKey, version: "v2");
        var (client, handler) = ClientWithHandler("[]");

        var result = await Invoke(
            route, NewExtension(store), client, FakePrincipalAccessor.WithPermissions(Permissions.ExtensionsConfigure));

        Assert.Equal(400, StatusOf(result));
        Assert.Contains("VERSION_UNSUPPORTED", ResponseJson(result), StringComparison.Ordinal);
        Assert.Equal(0, handler.CallCount); // deferred before any Whisparr call
    }

    // bulk-search-monitored routes on v2, so it keys on the connected version's endpoint (ThePornDB): a StashDB-
    // only entity has no v2 identity and refuses cleanly with no wire call (VERSION-agnostic no-identity outcome).
    [Fact]
    public async Task BulkSearchMonitored_V2_StashDbOnlyEntity_ReturnsNoIdentity_AndMakesNoOutboundCall()
    {
        var store = await StoreWith(StoredBaseUrl, StoredKey, version: "v2");
        var (client, handler) = ClientWithHandler("[]");

        var result = await NewExtension(store).BulkSearchMonitoredAsync(
            new Ext.BulkSearchMonitoredRequest("studio", MatchingRemotes()), client,
            FakePrincipalAccessor.WithPermissions(Permissions.ExtensionsConfigure), default);

        Assert.Contains("NO_STASHDB_IDENTITY", ResponseJson(result), StringComparison.Ordinal);
        Assert.Equal(0, handler.CallCount);
    }

    // ---- unknown kind on the bulk routes is a clean 400 (never guessed), before any outbound call ----

    [Fact]
    public async Task BulkAddMissing_UnknownKind_Returns400()
    {
        var store = await StoreWith(StoredBaseUrl, StoredKey);
        var (client, handler) = ClientWithHandler("[]");

        var result = await NewExtension(store).BulkAddMissingAsync(
            new Ext.BulkAddMissingRequest("franchise", 7), client,
            FakePrincipalAccessor.WithPermissions(Permissions.ExtensionsConfigure), default);

        Assert.Equal(400, StatusOf(result));
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task BulkSearchMonitored_UnknownKind_Returns400()
    {
        var store = await StoreWith(StoredBaseUrl, StoredKey);
        var (client, handler) = ClientWithHandler("[]");

        var result = await NewExtension(store).BulkSearchMonitoredAsync(
            new Ext.BulkSearchMonitoredRequest("franchise", MatchingRemotes()), client,
            FakePrincipalAccessor.WithPermissions(Permissions.ExtensionsConfigure), default);

        Assert.Equal(400, StatusOf(result));
        Assert.Equal(0, handler.CallCount);
    }

    // ---- server-side identity: a per-scene route with no resolvable scene is NO_STASHDB_IDENTITY, no call ----

    [Theory]
    [InlineData("scene-add")]
    [InlineData("scene-search")]
    [InlineData("scene-monitor")]
    public async Task PerScene_NoResolvableStashId_ReturnsNoIdentity_AndMakesNoOutboundCall(string route)
    {
        // With no host DB scope the scene cannot be resolved (LoadVideoByIdSafeAsync degrades to null), so the
        // handler returns the handled NO_STASHDB_IDENTITY outcome BEFORE any Whisparr call — never a 500.
        var store = await StoreWith(StoredBaseUrl, StoredKey);
        var (client, handler) = ClientWithHandler("[]");

        var result = await Invoke(
            route, NewExtension(store), client, FakePrincipalAccessor.WithPermissions(Permissions.ExtensionsConfigure));

        Assert.NotEqual(403, StatusOf(result));
        Assert.Contains("NO_STASHDB_IDENTITY", ResponseJson(result), StringComparison.Ordinal);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task BulkSearchMonitored_NoMatchingStashDbEndpoint_ReturnsNoIdentity_AndMakesNoOutboundCall()
    {
        var store = await StoreWith(StoredBaseUrl, StoredKey);
        var (client, handler) = ClientWithHandler("[]");

        var result = await NewExtension(store).BulkSearchMonitoredAsync(
            new Ext.BulkSearchMonitoredRequest("studio", NonMatchingRemotes()), client,
            FakePrincipalAccessor.WithPermissions(Permissions.ExtensionsConfigure), default);

        Assert.Contains("NO_STASHDB_IDENTITY", ResponseJson(result), StringComparison.Ordinal);
        Assert.Equal(0, handler.CallCount); // rejected before any Whisparr call
    }

    // ---- the outbound call uses the STORED host + key, never a caller value ----

    [Fact]
    public async Task BulkSearchMonitored_OutboundCall_UsesTheStoredHostAndKey()
    {
        // The request body carries NO url/key (only kind + remoteIds), so the only creds available are the
        // stored ones. Prove the entity lookup targets the stored host and sends the stored X-Api-Key.
        var store = await StoreWith(StoredBaseUrl, StoredKey);
        var (client, handler) = ClientWithHandler("[]");

        await NewExtension(store).BulkSearchMonitoredAsync(
            new Ext.BulkSearchMonitoredRequest("studio", MatchingRemotes()), client,
            FakePrincipalAccessor.WithPermissions(Permissions.ExtensionsConfigure), default);

        Assert.True(handler.CallCount >= 1);
        Assert.StartsWith(StoredBaseUrl + "/", handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal(StoredKey, SentApiKey(handler));
    }

    // ---- no-echo: the API key is never echoed to the response ----

    [Fact]
    public async Task BulkSearchMonitored_ResponseNeverContainsTheApiKey()
    {
        const string secretKey = "SUPER-SECRET-KEY-9f3a";
        var store = await StoreWith(StoredBaseUrl, secretKey);
        var result = await NewExtension(store).BulkSearchMonitoredAsync(
            new Ext.BulkSearchMonitoredRequest("studio", MatchingRemotes()), ClientReturning("[]"),
            FakePrincipalAccessor.WithPermissions(Permissions.ExtensionsConfigure), default);

        Assert.DoesNotContain(secretKey, ResponseJson(result), StringComparison.Ordinal);
    }

    // ---- reflect-owned: the owned-scene import endpoint (both versions; only an unmanageable version defers) ----

    [Fact]
    public async Task ReflectOwned_WithReadOnly_Returns403()
    {
        // EoP: an owned-scene import reaches the stored key to call Whisparr, so extensions.read must NOT reach it.
        var result = await NewExtension().ReflectOwnedAsync(
            new Ext.ReflectOwnedRequest("studio", 7), ClientReturning("[]"),
            FakePrincipalAccessor.WithPermissions(Permissions.ExtensionsRead), default);
        Assert.Equal(403, StatusOf(result));
    }

    [Fact]
    public async Task ReflectOwned_V3Instance_WithConfigure_IsNotForbidden()
    {
        // v3 supports owned-import, so the endpoint proceeds (not forbidden, not VERSION_UNSUPPORTED).
        var store = await StoreWith(StoredBaseUrl, StoredKey);
        var result = await NewExtension(store).ReflectOwnedAsync(
            new Ext.ReflectOwnedRequest("studio", 7), ClientReturning("[]"),
            FakePrincipalAccessor.WithPermissions(Permissions.ExtensionsConfigure), default);

        Assert.NotEqual(403, StatusOf(result));
        Assert.DoesNotContain("VERSION_UNSUPPORTED", ResponseJson(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReflectOwned_V2Instance_WithConfigure_IsNotForbidden()
    {
        var store = await StoreWith(StoredBaseUrl, StoredKey, version: "v2");
        var result = await NewExtension(store).ReflectOwnedAsync(
            new Ext.ReflectOwnedRequest("studio", 7), ClientReturning("[]"),
            FakePrincipalAccessor.WithPermissions(Permissions.ExtensionsConfigure), default);

        Assert.NotEqual(403, StatusOf(result));
    }
}
