using System.Net;
using System.Text.Json;
using Cove.Core.Auth;
using Cove.Plugins;
using Microsoft.AspNetCore.Http;
using WhisparrSync.Client;
using WhisparrSync.Tests.TestSupport;
using Ext = global::WhisparrSync.WhisparrSync;

namespace WhisparrSync.Tests.Api;

/// <summary>
/// The version-aware routing contract for the studio outward endpoints (<c>/monitor</c>,
/// <c>/monitor-status</c>, <c>/bulk-search-monitored</c>): a v2 connection whose entity carries a ThePornDB id
/// routes to the real v2 SITE adapter path (21-02), a v2 entity with no ThePornDB id refuses cleanly with the
/// handled no-identity outcome and ZERO wire calls, and a v3 connection is unchanged (resolves by StashDB id).
/// Complements <see cref="MonitorEndpointAuthTests"/> (the permission/redaction proofs).
/// </summary>
public sealed class MonitorEndpointTests
{
    private const string StoredBaseUrl = "http://stored.local:6969";
    private const string StoredKey = "STORED-KEY";
    private const string StashDbEndpoint = "https://stashdb.org/graphql";
    private const string TpdbEndpoint = "https://theporndb.net/graphql";
    private const string StashUuid = "157c9e0d-5f8e-446a-b1c5-dddf3cb5b2d1";

    // A ThePornDB site id present (3372 Vixen) / absent (3417 Tushy) in V2Fixtures.SeriesArray.
    private const string AddedTpdb = "3372";
    private const string AbsentTpdb = "3417";

    // The create-path verify read-back: the just-added site (id 3, the SeriesAddResponse id) now monitored:true,
    // so the create-path monitor verify passes on the first attempt (no re-PUT).
    private const string CreatedSiteMonitored = """
        [ { "id": 3, "tvdbId": 3417, "title": "Tushy", "titleSlug": "tushy", "path": "/config/media/Tushy", "monitored": true, "tags": [] } ]
        """;

    private static Ext NewExtension(FakeStore store)
    {
        var ext = new Ext();
        ((IStatefulExtension)ext).SetStore(store);
        return ext;
    }

    private static async Task<FakeStore> StoreWith(string version)
    {
        var store = new FakeStore();
        await store.SetAsync(
            "options",
            $"{{\"BaseUrl\":\"{StoredBaseUrl}\",\"ApiKey\":\"{StoredKey}\",\"SelectedVersion\":\"{version}\",\"RootFolderId\":2,\"QualityProfileId\":4}}");
        return store;
    }

    private static (WhisparrClient Client, FakeHttpMessageHandler Handler) ClientWith(params Func<HttpResponseMessage>[] responses)
    {
        var handler = FakeHttpMessageHandler.Sequence(responses);
        return (new WhisparrClient(new HttpClient(handler)), handler);
    }

    private static Func<HttpResponseMessage> Ok(string body)
        => FakeHttpMessageHandler.Respond(HttpStatusCode.OK, "application/json", body);

    private static Func<HttpResponseMessage> Created(string body)
        => FakeHttpMessageHandler.Respond(HttpStatusCode.Created, "application/json", body);

    private static Func<HttpResponseMessage> Accepted(string body)
        => FakeHttpMessageHandler.Respond(HttpStatusCode.Accepted, "application/json", body);

    private static int StatusOf(IResult result)
        => Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode ?? 200;

    private static string ResponseJson(IResult result)
        => JsonSerializer.Serialize(Assert.IsAssignableFrom<IValueHttpResult>(result).Value);

    private static string RootFolderList => JsonSerializer.Serialize(new[]
    {
        new { id = 2, path = "/data/media", accessible = true, freeSpace = 1L },
    });

    private static string TagListWithOrigin => JsonSerializer.Serialize(new[]
    {
        new { id = 5, label = "cove-sync" },
    });

    private static Ext.RemoteIdInput[] TpdbRemotes(string id) => [new(TpdbEndpoint, id)];
    private static Ext.RemoteIdInput[] StashRemotes(string id) => [new(StashDbEndpoint, id)];

    private static FakePrincipalAccessor Configure()
        => FakePrincipalAccessor.WithPermissions(Permissions.ExtensionsConfigure);

    // ---- v2 + a ThePornDB id → routes to the real v2 SITE adapter path (add-then-flip), not a 400 ----

    [Fact]
    public async Task Monitor_V2WithTpdbId_RoutesToV2SiteAdapter_AddsThenFlips()
    {
        var store = await StoreWith("v2");
        var (client, handler) = ClientWith(
            Ok(RootFolderList),                                            // EntityMonitor resolves the root path
            Ok(TagListWithOrigin),                                         // EntityMonitor ensures the origin tag
            Ok(V2Fixtures.SeriesArray),                                    // GET /series -> 3417 absent
            Ok(V2Fixtures.SeriesLookup),                                   // GET /series/lookup?term=tpdb:3417
            Created(V2Fixtures.SeriesAddResponse),                         // POST /series (non-grabbing add)
            Accepted(V2Fixtures.SeriesPutResponse),                        // PUT /series/{id} (monitor flip)
            Ok(CreatedSiteMonitored));                                     // create-path verify read-back -> true

        var req = new Ext.MonitorRequest("studio", TpdbRemotes(AbsentTpdb), Monitored: true);
        var result = await NewExtension(store).MonitorAsync(req, client, Configure(), default);

        Assert.NotEqual(400, StatusOf(result));
        var json = ResponseJson(result);
        Assert.Contains("\"added\":true", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"monitored\":true", json, StringComparison.OrdinalIgnoreCase);

        // The v2 SITE verbs were hit — a real outward path, not the old blanket VERSION_UNSUPPORTED refusal.
        Assert.Contains(handler.Requests, r => r.Method == HttpMethod.Post && r.Url.EndsWith("/api/v3/series", StringComparison.Ordinal));
        Assert.Contains(handler.Requests, r => r.Method == HttpMethod.Put && r.Url.Contains("/api/v3/series/", StringComparison.Ordinal));
        // Loop-safety: routing chose the adapter but the add is search-free — only an explicit search grabs.
        Assert.DoesNotContain(handler.Requests, r => r.Url.Contains("/command", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task MonitorStatus_V2WithTpdbId_RoutesToV2SiteAdapter()
    {
        var store = await StoreWith("v2");
        var (client, handler) = ClientWith(
            Ok(V2Fixtures.SeriesArray),          // GET /series -> 3372 present (monitored)
            Ok(V2Fixtures.EpisodesSeries1));     // GET /episode?seriesId -> the site's episodes

        var req = new Ext.MonitorStatusRequest("studio", TpdbRemotes(AddedTpdb));
        var result = await NewExtension(store).MonitorStatusAsync(req, client, Configure(), default);

        Assert.NotEqual(400, StatusOf(result));
        Assert.Contains("\"added\":true", ResponseJson(result), StringComparison.OrdinalIgnoreCase);
        Assert.Contains(handler.Requests, r => r.Url.Contains("/api/v3/series", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BulkSearchMonitored_V2WithTpdbId_RoutesToEpisodeSearch()
    {
        var store = await StoreWith("v2");
        var (client, handler) = ClientWith(
            Ok(V2Fixtures.SeriesArray),                       // resolve the site by tpdb id
            Ok(V2Fixtures.EpisodesSeries1),                   // its episode ids (the search-all input)
            Created(V2Fixtures.EpisodeSearchCommandResponse));// POST /command EpisodeSearch (the sole grab verb)

        var req = new Ext.BulkSearchMonitoredRequest("studio", TpdbRemotes(AddedTpdb));
        var result = await NewExtension(store).BulkSearchMonitoredAsync(req, client, Configure(), default);

        Assert.NotEqual(400, StatusOf(result));
        Assert.Contains(handler.Requests, r => r.Url.Contains("/api/v3/command", StringComparison.Ordinal));
    }

    // ---- v2 + no ThePornDB id → handled no-identity outcome, ZERO wire calls ----

    [Fact]
    public async Task Monitor_V2WithoutTpdbId_RefusesCleanly_NoWireCall()
    {
        var store = await StoreWith("v2");
        var (client, handler) = ClientWith(Ok("[]"));

        // The entity carries only a StashDB id; the connected v2 instance keys on the ThePornDB endpoint.
        var req = new Ext.MonitorRequest("studio", StashRemotes(StashUuid), Monitored: true);
        var result = await NewExtension(store).MonitorAsync(req, client, Configure(), default);

        Assert.Contains("NO_STASHDB_IDENTITY", ResponseJson(result), StringComparison.Ordinal);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task MonitorStatus_V2WithoutTpdbId_RefusesCleanly_NoWireCall()
    {
        var store = await StoreWith("v2");
        var (client, handler) = ClientWith(Ok("[]"));

        var req = new Ext.MonitorStatusRequest("studio", StashRemotes(StashUuid));
        var result = await NewExtension(store).MonitorStatusAsync(req, client, Configure(), default);

        Assert.Contains("NO_STASHDB_IDENTITY", ResponseJson(result), StringComparison.Ordinal);
        Assert.Equal(0, handler.CallCount);
    }

    // ---- v2 performer: capability precedes identity — the kind has no v2 entity, id or not. An id-less
    //      performer hides (VERSION_UNSUPPORTED); it must never show the misleading NO_STASHDB_IDENTITY. Wire-free ----

    [Fact]
    public async Task MonitorStatus_V2Performer_NoTpdbId_IsVersionUnsupported_NotNoIdentity_NoWireCall()
    {
        var store = await StoreWith("v2");
        var (client, handler) = ClientWith(); // no responses queued — any wire call would fail the test

        var req = new Ext.MonitorStatusRequest("performer", []);
        var result = await NewExtension(store).MonitorStatusAsync(req, client, Configure(), default);

        Assert.Equal(400, StatusOf(result));
        Assert.Contains("VERSION_UNSUPPORTED", ResponseJson(result), StringComparison.Ordinal);
        Assert.DoesNotContain("NO_STASHDB_IDENTITY", ResponseJson(result), StringComparison.Ordinal);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Monitor_V2Performer_NoTpdbId_IsVersionUnsupported_NotNoIdentity_NoWireCall()
    {
        var store = await StoreWith("v2");
        var (client, handler) = ClientWith();

        var req = new Ext.MonitorRequest("performer", [], Monitored: true);
        var result = await NewExtension(store).MonitorAsync(req, client, Configure(), default);

        Assert.Equal(400, StatusOf(result));
        Assert.Contains("VERSION_UNSUPPORTED", ResponseJson(result), StringComparison.Ordinal);
        Assert.DoesNotContain("NO_STASHDB_IDENTITY", ResponseJson(result), StringComparison.Ordinal);
        Assert.Equal(0, handler.CallCount);
    }

    // ---- v3 unchanged: resolves by the StashDB id and drives the v3 studio path ----

    [Fact]
    public async Task Monitor_V3WithStashId_ResolvesByStashDb_Unchanged()
    {
        var store = await StoreWith("v3");
        var (client, handler) = ClientWith(Ok("[]")); // studio GET -> absent (monitor OFF is a clean single call)

        var req = new Ext.MonitorRequest("studio", StashRemotes(StashUuid), Monitored: false);
        var result = await NewExtension(store).MonitorAsync(req, client, Configure(), default);

        Assert.NotEqual(400, StatusOf(result));
        Assert.DoesNotContain("NO_STASHDB_IDENTITY", ResponseJson(result), StringComparison.Ordinal);
        // v3 resolves by the StashDB endpoint, so the studio GET is issued against the stored host.
        Assert.Contains(handler.Requests, r => r.Url.Contains("/api/v3/studio", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Monitor_V3WithOnlyTpdbId_IsNoIdentity_Unchanged()
    {
        var store = await StoreWith("v3");
        var (client, handler) = ClientWith(Ok("[]"));

        // On v3 the match key is the StashDB endpoint, so a ThePornDB-only entity has no v3 identity.
        var req = new Ext.MonitorRequest("studio", TpdbRemotes(AddedTpdb), Monitored: true);
        var result = await NewExtension(store).MonitorAsync(req, client, Configure(), default);

        Assert.Contains("NO_STASHDB_IDENTITY", ResponseJson(result), StringComparison.Ordinal);
        Assert.Equal(0, handler.CallCount);
    }
}
