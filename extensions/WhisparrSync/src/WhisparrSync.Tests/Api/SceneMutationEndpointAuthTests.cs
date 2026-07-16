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
/// inert on minimal-API extension endpoints, so the four exclusion / interactive-grab / upgrade
/// handlers (<c>/scene-exclusion</c>, <c>/scene-grab-release</c>, <c>/scene-releases-list</c>,
/// <c>/scene-search-upgrades</c>) enforce <c>extensions.configure</c> themselves and reach the stored
/// credentials only. These prove, for every route: the deny trio (null / read-only / no-configure → 403) and
/// the allow (configure proceeds); that a scene with no resolvable StashDB identity is the handled
/// <c>NO_STASHDB_IDENTITY</c> outcome with NO outbound call; that a v2 instance returns
/// <c>VERSION_UNSUPPORTED</c> (400, never a 500 — the exclusion/grab surface is v3-only); and
/// that the stored key never appears in the response. Mirrors <see cref="SceneActionEndpointAuthTests"/> —
/// the per-scene identity is resolved SERVER-SIDE from the Cove id (LoadVideoByIdSafeAsync), so with no host DB
/// scope the resolution degrades to the handled no-identity outcome before any Whisparr call.
/// </summary>
public sealed class SceneMutationEndpointAuthTests
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

    private static (WhisparrClient Client, FakeHttpMessageHandler Handler) ClientWithHandler(string json)
    {
        var handler = FakeHttpMessageHandler.Json(json);
        return (new WhisparrClient(new HttpClient(handler)), handler);
    }

    private static async Task<FakeStore> StoreWith(string baseUrl, string apiKey, string version = "v3")
    {
        var store = new FakeStore();
        await store.SetAsync(
            "options", $"{{\"BaseUrl\":\"{baseUrl}\",\"ApiKey\":\"{apiKey}\",\"SelectedVersion\":\"{version}\"}}");
        return store;
    }

    private static int StatusOf(IResult result)
        => Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode ?? 0;

    private static string ResponseJson(IResult result)
        => JsonSerializer.Serialize(Assert.IsAssignableFrom<IValueHttpResult>(result).Value);

    // Drives one of the four routes by name so the deny/allow/version matrices are a single [Theory] each.
    private static Task<IResult> Invoke(
        string route, Ext ext, WhisparrClient client, ICurrentPrincipalAccessor principal)
        => route switch
        {
            "scene-exclusion" => ext.SceneExclusionAsync(new Ext.SceneExclusionRequest(5, true), client, principal, default),
            "scene-grab-release" => ext.SceneGrabReleaseAsync(
                new Ext.SceneGrabReleaseRequest(5, "release-guid-abc", 3), client, principal, default),
            "scene-releases-list" => ext.SceneReleasesListAsync(new Ext.SceneReleasesRequest(5), client, principal, default),
            "scene-search-upgrades" => ext.SceneSearchUpgradesAsync(new Ext.SceneSearchRequest(5), client, principal, default),
            _ => throw new ArgumentOutOfRangeException(nameof(route), route, "unknown route"),
        };

    public static TheoryData<string> AllRoutes() => new()
    {
        "scene-exclusion", "scene-grab-release", "scene-releases-list", "scene-search-upgrades",
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
        // EoP: an exclusion/grab reaches the stored key to call Whisparr, so extensions.read (a strictly
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

    // ---- v2 → VERSION_UNSUPPORTED (400, never a 500): the exclusion/grab surface is v3-only ----

    [Theory]
    [MemberData(nameof(AllRoutes))]
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

    // ---- server-side identity: no resolvable scene is NO_STASHDB_IDENTITY, no outbound call ----

    [Theory]
    [MemberData(nameof(AllRoutes))]
    public async Task Route_NoResolvableStashId_ReturnsNoIdentity_AndMakesNoOutboundCall(string route)
    {
        // With no host DB scope the scene cannot be resolved (LoadVideoByIdSafeAsync degrades to null), so the
        // handler returns the handled NO_STASHDB_IDENTITY outcome BEFORE any Whisparr call — never a 500. This is
        // the same tier boundary the sibling scene suites test; the resolvable-scene wire path is proven by the
        // SceneActions / transport tests + the videos-batch core tests (which inject a seeded port).
        var store = await StoreWith(StoredBaseUrl, StoredKey);
        var (client, handler) = ClientWithHandler("[]");

        var result = await Invoke(
            route, NewExtension(store), client, FakePrincipalAccessor.WithPermissions(Permissions.ExtensionsConfigure));

        Assert.NotEqual(403, StatusOf(result));
        Assert.Contains("NO_STASHDB_IDENTITY", ResponseJson(result), StringComparison.Ordinal);
        Assert.Equal(0, handler.CallCount);
    }

    // ---- grab: a missing release guid is a clean 400 before any resolution / outbound call ----

    [Fact]
    public async Task GrabRelease_MissingGuid_Returns400_BeforeAnyOutboundCall()
    {
        var store = await StoreWith(StoredBaseUrl, StoredKey);
        var (client, handler) = ClientWithHandler("[]");

        var result = await NewExtension(store).SceneGrabReleaseAsync(
            new Ext.SceneGrabReleaseRequest(5, "   ", 3), client,
            FakePrincipalAccessor.WithPermissions(Permissions.ExtensionsConfigure), default);

        Assert.Equal(400, StatusOf(result));
        Assert.Contains("MISSING_RELEASE", ResponseJson(result), StringComparison.Ordinal);
        Assert.Equal(0, handler.CallCount);
    }

    // ---- no-echo: the stored API key is never echoed to any response ----

    [Theory]
    [MemberData(nameof(AllRoutes))]
    public async Task Route_ResponseNeverContainsTheApiKey(string route)
    {
        const string secretKey = "SUPER-SECRET-KEY-14b2";
        var store = await StoreWith(StoredBaseUrl, secretKey);
        var result = await Invoke(
            route, NewExtension(store), ClientReturning("[]"),
            FakePrincipalAccessor.WithPermissions(Permissions.ExtensionsConfigure));

        Assert.DoesNotContain(secretKey, ResponseJson(result), StringComparison.Ordinal);
    }
}
