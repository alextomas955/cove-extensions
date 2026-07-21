using System.Text.Json;
using Cove.Core.Auth;
using Cove.Plugins;
using Microsoft.AspNetCore.Http;
using WhisparrSync.Adapters;
using WhisparrSync.Client;
using WhisparrSync.Library;
using WhisparrSync.Options;
using WhisparrSync.Tests.TestSupport;
using Ext = global::WhisparrSync.WhisparrSync;

namespace WhisparrSync.Tests.Api;

/// <summary>
/// Security-critical: the <c>/videos-batch</c> handler enforces
/// <c>extensions.configure</c> itself (the host filter is inert on minimal-API), caps the selection BEFORE any
/// per-item work (fan-out containment), rejects an unknown op, is v3-only, and reaches the stored creds only.
/// The ROUTED tier proves the deny trio / allow / unknown-op-400 / oversized-400 / v2-400 / no-scope-all-skipped
/// matrix with no host DB scope. The CORE tier drives the extracted <see cref="Ext.VideosBatchCoreAsync"/> with a
/// seeded <see cref="FakeCoveLibraryPort"/> + a fake-HTTP <see cref="V3Adapter"/> to prove server-side scene
/// resolution, mixed-selection skip counting, the per-op grab boundary (add/exclude/un-exclude issue NO
/// MoviesSearch command; search/search-upgrades DO), stored-creds usage, and that the key is never echoed.
/// </summary>
public sealed class VideosBatchEndpointAuthTests
{
    private const string StoredBaseUrl = "http://stored.local:6969";
    private const string StoredKey = "STORED-KEY";

    private static Ext NewExtension(FakeStore? store = null)
    {
        var ext = new Ext();
        ((IStatefulExtension)ext).SetStore(store ?? new FakeStore());
        return ext;
    }

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

    private static WhisparrOptions OptionsV3(string baseUrl, string apiKey)
        => new() { BaseUrl = baseUrl, ApiKey = apiKey, SelectedVersion = "v3", AllowQualityUpgrades = true };

    private static CoveVideo Video(int coveId, string stashId)
        => new(coveId, $"Scene {coveId}", new DateOnly(2021, 1, 1), [stashId], [], [], []);

    private static int StatusOf(IResult result)
        => Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode ?? 0;

    private static string ResponseJson(IResult result)
        => JsonSerializer.Serialize(Assert.IsAssignableFrom<IValueHttpResult>(result).Value);

    // Whether any captured outbound request is a grab (a MoviesSearch command or an interactive release grab).
    private static bool IssuedGrab(FakeHttpMessageHandler handler)
        => handler.Requests.Any(r =>
            r.Url.Contains("/api/v3/command", StringComparison.Ordinal)
            || r.Url.Contains("/api/v3/release", StringComparison.Ordinal));

    private static string SentApiKey(FakeHttpMessageHandler handler)
        => handler.LastRequest!.Headers.TryGetValues("X-Api-Key", out var values) ? string.Concat(values!) : string.Empty;

    // ---- ROUTED tier ----

    [Theory]
    [InlineData("add")]
    [InlineData("search")]
    [InlineData("exclude")]
    public async Task VideosBatch_WithoutConfigure_Returns403(string op)
    {
        var (client, _) = ClientWithHandler("[]");
        var result = await NewExtension().VideosBatchAsync(
            new Ext.VideosBatchRequest(op, [1, 2]), client, FakePrincipalAccessor.None(), default);
        Assert.Equal(403, StatusOf(result));
    }

    [Fact]
    public async Task VideosBatch_WithReadOnly_Returns403()
    {
        var (client, _) = ClientWithHandler("[]");
        var result = await NewExtension().VideosBatchAsync(
            new Ext.VideosBatchRequest("add", [1]), client,
            FakePrincipalAccessor.WithPermissions(Permissions.ExtensionsRead), default);
        Assert.Equal(403, StatusOf(result));
    }

    [Fact]
    public async Task VideosBatch_NullPrincipal_Returns403()
    {
        var (client, _) = ClientWithHandler("[]");
        var result = await NewExtension().VideosBatchAsync(
            new Ext.VideosBatchRequest("add", [1]), client, FakePrincipalAccessor.NullPrincipal(), default);
        Assert.Equal(403, StatusOf(result));
    }

    [Fact]
    public async Task VideosBatch_UnknownOp_Returns400_BeforeAnyOutboundCall()
    {
        var store = await StoreWith(StoredBaseUrl, StoredKey);
        var (client, handler) = ClientWithHandler("[]");

        var result = await NewExtension(store).VideosBatchAsync(
            new Ext.VideosBatchRequest("obliterate", [1, 2]), client,
            FakePrincipalAccessor.WithPermissions(Permissions.ExtensionsConfigure), default);

        Assert.Equal(400, StatusOf(result));
        Assert.Contains("UNKNOWN_OP", ResponseJson(result), StringComparison.Ordinal);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task VideosBatch_OversizedIdList_Returns400_BeforeAnyPerItemWork()
    {
        // An unbounded selection is a fan-out risk, rejected before any DB read or outbound call.
        var store = await StoreWith(StoredBaseUrl, StoredKey);
        var (client, handler) = ClientWithHandler("[]");
        var oversized = Enumerable.Range(1, 1001).ToArray();

        var result = await NewExtension(store).VideosBatchAsync(
            new Ext.VideosBatchRequest("add", oversized), client,
            FakePrincipalAccessor.WithPermissions(Permissions.ExtensionsConfigure), default);

        Assert.Equal(400, StatusOf(result));
        Assert.Contains("TOO_MANY_IDS", ResponseJson(result), StringComparison.Ordinal);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task VideosBatch_V2Instance_ReturnsVersionUnsupported400()
    {
        var store = await StoreWith(StoredBaseUrl, StoredKey, version: "v2");
        var (client, handler) = ClientWithHandler("[]");

        var result = await NewExtension(store).VideosBatchAsync(
            new Ext.VideosBatchRequest("add", [1, 2]), client,
            FakePrincipalAccessor.WithPermissions(Permissions.ExtensionsConfigure), default);

        Assert.Equal(400, StatusOf(result));
        Assert.Contains("VERSION_UNSUPPORTED", ResponseJson(result), StringComparison.Ordinal);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task VideosBatch_NoDbScope_SkipsEveryId_WithNoOutboundCall()
    {
        // With no host DB scope every id is unresolvable, so all are skipped before any Whisparr call.
        var store = await StoreWith(StoredBaseUrl, StoredKey);
        var (client, handler) = ClientWithHandler("[]");

        var result = await NewExtension(store).VideosBatchAsync(
            new Ext.VideosBatchRequest("exclude", [1, 2, 3]), client,
            FakePrincipalAccessor.WithPermissions(Permissions.ExtensionsConfigure), default);

        Assert.NotEqual(403, StatusOf(result));
        var json = ResponseJson(result);
        Assert.Contains("\"Total\":3", json, StringComparison.Ordinal);
        Assert.Contains("\"Skipped\":3", json, StringComparison.Ordinal);
        Assert.Equal(0, handler.CallCount);
    }

    // ---- CORE tier: server-side resolution + skip counting + per-op grab boundary ----

    [Fact]
    public async Task Core_Exclude_ResolvesScenesServerSide_SkipsUnresolvable_AndIssuesNoGrab()
    {
        var (client, handler) = ClientWithHandler("{}"); // any 2xx = an idempotent exclusion success
        var adapter = new V3Adapter(client);
        var library = new FakeCoveLibraryPort();
        library.Seed(Video(1, "uuid-a"), Video(2, "uuid-b")); // ids 3 (absent) + 4 (absent) are unresolvable

        var result = await Ext.VideosBatchCoreAsync(
            Ext.BatchOp.Exclude, [1, 2, 3, 4], client, adapter, library, OptionsV3(StoredBaseUrl, StoredKey),
            StoredBaseUrl, StoredKey, default);

        Assert.True(result.IsOk);
        Assert.Equal(4, result.Value!.Total);
        Assert.Equal(2, result.Value.Succeeded);
        Assert.Equal(2, result.Value.Skipped); // the two absent ids resolved to no scene → skipped, no call
        Assert.False(IssuedGrab(handler)); // an exclusion never searches (loop-safety LOCKED)
        Assert.All(handler.Requests, r => Assert.StartsWith(StoredBaseUrl + "/", r.Url)); // stored host only
    }

    [Fact]
    public async Task Core_Search_ResolvesMovieAndIssuesGrab_UsingStoredCreds_NeverEchoesKey()
    {
        const string secretKey = "SUPER-SECRET-KEY-14sb";
        // The movie set carries a scene-typed movie whose stashId matches the seeded scene, so it resolves.
        const string movies = """[{"id":42,"title":"Scene A","year":2021,"stashId":"uuid-a","foreignId":"uuid-a","itemType":"scene","monitored":true,"hasFile":false}]""";
        var (client, handler) = ClientWithHandler(movies);
        var adapter = new V3Adapter(client);
        var library = new FakeCoveLibraryPort();
        library.Seed(Video(1, "uuid-a"), Video(2, "uuid-zzz")); // id 2's scene has no matching movie → skipped

        var result = await Ext.VideosBatchCoreAsync(
            Ext.BatchOp.Search, [1, 2], client, adapter, library, OptionsV3(StoredBaseUrl, secretKey),
            StoredBaseUrl, secretKey, default);

        Assert.True(result.IsOk);
        Assert.Equal(2, result.Value!.Total);
        Assert.Equal(1, result.Value.Skipped); // the not-added scene is skipped
        Assert.True(IssuedGrab(handler)); // search may grab (a MoviesSearch command was issued)
        Assert.Equal(secretKey, SentApiKey(handler)); // the outbound call carries the stored key
        Assert.DoesNotContain(secretKey, ResponseJson(WrapOk(result)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Core_SearchUpgrades_IssuesGrab()
    {
        const string movies = """[{"id":7,"title":"Scene A","year":2021,"stashId":"uuid-a","foreignId":"uuid-a","itemType":"scene","monitored":true,"hasFile":false}]""";
        var (client, handler) = ClientWithHandler(movies);
        var adapter = new V3Adapter(client);
        var library = new FakeCoveLibraryPort();
        library.Seed(Video(1, "uuid-a"));

        var result = await Ext.VideosBatchCoreAsync(
            Ext.BatchOp.SearchUpgrades, [1], client, adapter, library, OptionsV3(StoredBaseUrl, StoredKey),
            StoredBaseUrl, StoredKey, default);

        Assert.True(result.IsOk);
        Assert.True(IssuedGrab(handler)); // search-for-upgrades may grab (AllowQualityUpgrades is on)
    }

    // Serializes the VideosBatchResult exactly as the endpoint would, for the no-echo assertion.
    private static IResult WrapOk(WhisparrResult<Ext.VideosBatchResult> result)
        => Results.Json(result.Value);
}
