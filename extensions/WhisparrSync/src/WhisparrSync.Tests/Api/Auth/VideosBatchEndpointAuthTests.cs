using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using WhisparrSync.Adapters;
using WhisparrSync.Client;
using WhisparrSync.Contracts;
using WhisparrSync.Library;
using WhisparrSync.Options;
using WhisparrSync.Push;
using WhisparrSync.Tests.TestSupport;
using static WhisparrSync.Tests.TestSupport.EndpointTestSupport;
using Ext = global::WhisparrSync.WhisparrSync;

namespace WhisparrSync.Tests.Api.Auth;

/// <summary>
/// Security-critical: the <c>/videos-batch</c> handler enforces
/// <c>extensions.configure</c> itself (the host filter is inert on minimal-API), caps the selection BEFORE any
/// per-item work (fan-out containment), rejects an unknown op, is v3-only, and reaches the stored creds only.
/// The ROUTED tier proves the deny trio / allow / unknown-op-400 / oversized-400 / v2-400 / no-scope-all-skipped
/// matrix with no host DB scope. The CORE tier drives the extracted <see cref="Ext.VideosBatchCoreAsync"/> with a
/// seeded <see cref="FakeCoveLibraryPort"/> + a fake-HTTP <see cref="V3Adapter"/> to prove server-side scene
/// resolution, mixed-selection skip counting, the per-op grab boundary (add/monitor/unmonitor/exclude issue NO
/// MoviesSearch command — monitor's add leg carries <c>searchForMovie:false</c> and its flip is a PUT;
/// search/search-upgrades DO grab), stored-creds usage, and that the key is never echoed.
/// </summary>
[Trait("Tier", "L2")]
public sealed class VideosBatchEndpointAuthTests
{
    private const string StoredBaseUrl = "http://stored.local:6969";
    private const string StoredKey = "STORED-KEY";

    private static (WhisparrClient Client, FakeHttpMessageHandler Handler) ClientWithSequence(
        params Func<HttpResponseMessage>[] steps)
    {
        var handler = FakeHttpMessageHandler.Sequence(steps);
        return (new WhisparrClient(new HttpClient(handler)), handler);
    }

    private static Func<HttpResponseMessage> Ok(string body)
        => FakeHttpMessageHandler.Respond(HttpStatusCode.OK, "application/json", body);

    private static Func<HttpResponseMessage> Created(string body)
        => FakeHttpMessageHandler.Respond(HttpStatusCode.Created, "application/json", body);

    private static string RootFolderList => JsonSerializer.Serialize(new[]
    {
        new { id = 1, path = "/data/media", accessible = true, freeSpace = 1L },
    });

    private static string TagListWithOrigin => JsonSerializer.Serialize(new[]
    {
        new { id = 5, label = AddContextResolver.OriginTagLabel },
    });

    private static string ProfileList => JsonSerializer.Serialize(new[]
    {
        new { id = 4, name = "HD-1080p" },
    });

    private static string MovieRow(int id, string stashId, bool monitored) => JsonSerializer.Serialize(new
    {
        id,
        foreignId = stashId,
        stashId,
        title = "A Scene",
        monitored,
        hasFile = false,
        itemType = "scene",
        qualityProfileId = 4,
        rootFolderPath = "/data/media",
        tags = new[] { 5 },
    });

    private static WhisparrOptions OptionsV3(string baseUrl, string apiKey)
        => new() { BaseUrl = baseUrl, ApiKey = apiKey, SelectedVersion = "v3", AllowQualityUpgrades = true };

    private static CoveVideo Video(int coveId, string stashId)
        => new(coveId, $"Scene {coveId}", new DateOnly(2021, 1, 1), [stashId], [], [], []);

    // Whether any captured outbound request is a grab (a MoviesSearch command or an interactive release grab).
    private static bool IssuedGrab(FakeHttpMessageHandler handler)
        => handler.Requests.Any(r =>
            r.Url.Contains("/api/v3/command", StringComparison.Ordinal)
            || r.Url.Contains("/api/v3/release", StringComparison.Ordinal));

    [Fact]
    public async Task VideosBatch_UnknownOp_Returns400_BeforeAnyOutboundCall()
    {
        var store = await StoreWith(StoredBaseUrl, StoredKey);
        var (client, handler) = ClientWithHandler("[]");

        var result = await NewExtension(store).VideosBatchAsync(
            new VideosBatchRequest("obliterate", [1, 2]), client, default);

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
            new VideosBatchRequest("add", oversized), client, default);

        Assert.Equal(400, StatusOf(result));
        Assert.Contains("TOO_MANY_IDS", ResponseJson(result), StringComparison.Ordinal);
        Assert.Equal(0, handler.CallCount);
    }

    [Theory]
    [InlineData("add")]
    [InlineData("monitor")]
    public async Task VideosBatch_V2Instance_ReturnsVersionUnsupported400(string op)
    {
        var store = await StoreWith(StoredBaseUrl, StoredKey, version: "v2");
        var (client, handler) = ClientWithHandler("[]");

        var result = await NewExtension(store).VideosBatchAsync(
            new VideosBatchRequest(op, [1, 2]), client, default);

        Assert.Equal(400, StatusOf(result));
        Assert.Contains("VERSION_UNSUPPORTED", ResponseJson(result), StringComparison.Ordinal);
        Assert.Equal(0, handler.CallCount);
    }

    [Theory]
    [InlineData("exclude")]
    [InlineData("unmonitor")]
    public async Task VideosBatch_NoDbScope_SkipsEveryId_WithNoOutboundCall(string op)
    {
        // With no host DB scope every id is unresolvable, so all are skipped before any Whisparr call.
        var store = await StoreWith(StoredBaseUrl, StoredKey);
        var (client, handler) = ClientWithHandler("[]");

        var result = await NewExtension(store).VideosBatchAsync(
            new VideosBatchRequest(op, [1, 2, 3]), client, default);
        var json = ResponseJson(result);
        Assert.Contains("\"total\":3", json, StringComparison.Ordinal);
        Assert.Contains("\"skipped\":3", json, StringComparison.Ordinal);
        Assert.Equal(0, handler.CallCount);
    }

    // ---- an incomplete stored configuration is refused IN THE HANDLER, before enqueue ----

    [Theory]
    [InlineData("add")]
    [InlineData("unmonitor")]
    public async Task VideosBatch_WithBlankStoredAddress_Refuses400_OnEveryOp(string op)
    {
        // An unset address is fatal for every op: with no host there is no request to make. The refusal lives
        // above BOTH the inline branch and the enqueue branch, so nothing outbound happens and no aggregate is
        // computed — the response is the refusal, not a batch result with every id skipped.
        var store = await StoreWith("", StoredKey);
        var (client, handler) = ClientWithHandler("[]");

        var result = await NewExtension(store).VideosBatchAsync(
            new VideosBatchRequest(op, [1, 2]), client, default);

        Assert.Equal(400, StatusOf(result));
        var json = ResponseJson(result);
        Assert.Contains("CONFIG_INCOMPLETE", json, StringComparison.Ordinal);
        Assert.Contains("baseUrl", json, StringComparison.Ordinal);
        Assert.Equal(0, handler.CallCount);
        Assert.DoesNotContain("\"Total\"", json, StringComparison.Ordinal);
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
            BatchOp.Exclude, [1, 2, 3, 4], client, adapter, library, OptionsV3(StoredBaseUrl, StoredKey),
            new WhisparrCapabilityPort(client), StoredBaseUrl, StoredKey, default);

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
            BatchOp.Search, [1, 2], client, adapter, library, OptionsV3(StoredBaseUrl, secretKey),
            new WhisparrCapabilityPort(client), StoredBaseUrl, secretKey, default);

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
            BatchOp.SearchUpgrades, [1], client, adapter, library, OptionsV3(StoredBaseUrl, StoredKey),
            new WhisparrCapabilityPort(client), StoredBaseUrl, StoredKey, default);

        Assert.True(result.IsOk);
        Assert.True(IssuedGrab(handler)); // search-for-upgrades may grab (AllowQualityUpgrades is on)
    }

    [Fact]
    public async Task Core_Monitor_NotAddedScene_RegistersNonGrabbing_ThenFlipsViaPut_WithNoCommand()
    {
        // The absent+ON spine: root + origin-tag resolve, GET by stashid (absent), a non-grabbing POST add,
        // then the PUT flip — never a /command (loop-safety LOCKED).
        var (client, handler) = ClientWithSequence(
            Ok(RootFolderList),
            Ok(TagListWithOrigin),
            Ok(ProfileList),
            Ok("[]"),
            Created(MovieRow(99, "uuid-a", monitored: false)),
            Ok(MovieRow(99, "uuid-a", monitored: true)));
        var adapter = new V3Adapter(client);
        var library = new FakeCoveLibraryPort();
        library.Seed(Video(1, "uuid-a"));

        var result = await Ext.VideosBatchCoreAsync(
            BatchOp.Monitor, [1], client, adapter, library, OptionsV3(StoredBaseUrl, StoredKey),
            new WhisparrCapabilityPort(client), StoredBaseUrl, StoredKey, default);

        Assert.True(result.IsOk);
        Assert.Equal("monitor", result.Value!.Op);
        Assert.Equal(1, result.Value.Succeeded);
        Assert.Equal(0, result.Value.Failed);

        var post = Assert.Single(handler.Requests, r => r.Method == HttpMethod.Post);
        Assert.Contains("\"searchForMovie\":false", post.Body); // the add leg registers, never grabs
        Assert.Single(handler.Requests, r => r.Method == HttpMethod.Put); // the flip is a PUT, not a command
        Assert.False(IssuedGrab(handler));
        Assert.Equal(StoredKey, SentApiKey(handler)); // the outbound calls carry the stored key
        Assert.All(handler.Requests, r => Assert.StartsWith(StoredBaseUrl + "/", r.Url)); // stored host only
    }

    [Fact]
    public async Task Core_Monitor_AlreadyMonitoredScene_IsIdempotentSuccess_WithNoAddAndNoGrab()
    {
        var (client, handler) = ClientWithSequence(
            Ok(RootFolderList),
            Ok(TagListWithOrigin),
            Ok(ProfileList),
            Ok($"[{MovieRow(7, "uuid-a", monitored: true)}]"),
            Ok(MovieRow(7, "uuid-a", monitored: true)));
        var adapter = new V3Adapter(client);
        var library = new FakeCoveLibraryPort();
        library.Seed(Video(1, "uuid-a"));

        var result = await Ext.VideosBatchCoreAsync(
            BatchOp.Monitor, [1], client, adapter, library, OptionsV3(StoredBaseUrl, StoredKey),
            new WhisparrCapabilityPort(client), StoredBaseUrl, StoredKey, default);

        Assert.True(result.IsOk);
        Assert.Equal(1, result.Value!.Succeeded); // re-monitoring a monitored scene is a success no-op
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Post); // present scene: no add
        Assert.False(IssuedGrab(handler));
    }

    [Fact]
    public async Task Core_Unmonitor_NotAddedScene_IsSkipped_WithNoAddNoPutNoGrab()
    {
        // Absent + OFF is "nothing to unmonitor, nothing to add" — a Skip, never an add or a false success.
        var (client, handler) = ClientWithSequence(Ok("[]"));
        var adapter = new V3Adapter(client);
        var library = new FakeCoveLibraryPort();
        library.Seed(Video(1, "uuid-a"));

        var result = await Ext.VideosBatchCoreAsync(
            BatchOp.Unmonitor, [1], client, adapter, library, OptionsV3(StoredBaseUrl, StoredKey),
            new WhisparrCapabilityPort(client), StoredBaseUrl, StoredKey, default);

        Assert.True(result.IsOk);
        Assert.Equal(0, result.Value!.Succeeded);
        Assert.Equal(1, result.Value.Skipped);
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Post);
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Put);
        var get = Assert.Single(handler.Requests); // only the movie-by-stashid probe went out
        Assert.Equal(HttpMethod.Get, get.Method);
        Assert.False(IssuedGrab(handler));
    }

    [Fact]
    public async Task Core_Unmonitor_PresentMonitoredScene_FlipsViaPut_WithNoGrab()
    {
        var (client, handler) = ClientWithSequence(
            Ok($"[{MovieRow(7, "uuid-a", monitored: true)}]"),
            Ok(MovieRow(7, "uuid-a", monitored: false)));
        var adapter = new V3Adapter(client);
        var library = new FakeCoveLibraryPort();
        library.Seed(Video(1, "uuid-a"));

        var result = await Ext.VideosBatchCoreAsync(
            BatchOp.Unmonitor, [1], client, adapter, library, OptionsV3(StoredBaseUrl, StoredKey),
            new WhisparrCapabilityPort(client), StoredBaseUrl, StoredKey, default);

        Assert.True(result.IsOk);
        Assert.Equal("unmonitor", result.Value!.Op);
        Assert.Equal(1, result.Value.Succeeded);
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Post); // unmonitor never adds
        Assert.Single(handler.Requests, r => r.Method == HttpMethod.Put);
        Assert.False(IssuedGrab(handler));
    }

    // Serializes the VideosBatchResult exactly as the endpoint would, for the no-echo assertion.
    private static IResult WrapOk(WhisparrResult<VideosBatchResult> result)
        => Results.Json(result.Value);
}
