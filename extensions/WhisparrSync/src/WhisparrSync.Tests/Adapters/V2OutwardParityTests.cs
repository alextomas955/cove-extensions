using System.Globalization;
using System.Net;
using WhisparrSync.Adapters;
using WhisparrSync.Client;
using WhisparrSync.Monitor;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.Adapters;

/// <summary>
/// The v2 outward-capability verdict matrix. v2 now has a REAL outward path (reversing the v1.1
/// "0 GO / 9 DEFER"): a Cove studio maps to a v2 SITE (series) keyed on the TPDB id in Sonarr's <c>tvdbId</c>
/// slot, so add/monitor a site, read its status, enumerate its episode ids, and search its episodes all GO —
/// loop-safe and idempotent, exactly like v3. The deferrals that remain are capability-specific with real
/// reasons (a performer has no v2 entity; a scene has no per-scene add; upgrade/release/exclusion surfaces
/// have no correct v2 mapping) — not a blanket refuse.
/// </summary>
/// <remarks>
/// Two shapes of assertion live here. A GO capability asserts a real outbound flow over a fixture-primed
/// handler: the add is NON-grabbing (<c>searchForMissingEpisodes:false</c>), origin-tagged, and idempotent,
/// and the sole grab-capable verb (<c>POST /command</c>) is hit ONLY by an explicit search — never by an
/// add/monitor path. A DEFER capability asserts a clean refusal: the classified
/// <see cref="WhisparrResultState.VersionMismatch"/> ("v2") AND ZERO outbound wire calls (an empty
/// <see cref="FakeHttpMessageHandler.Requests"/> against a handler primed to answer 200 is positive proof the
/// method short-circuited before the transport — no v2 request, and no silent v3 request).
/// </remarks>
public sealed class V2OutwardParityTests
{
    private const string BaseUrl = "http://localhost:6970";
    private const string ApiKey = "test-api-key";

    // On v2 the outward id is a TPDB site id (the tvdbId slot), not a StashDB id. 3372 (Vixen) is added in
    // the seeded /series set; 3417 (Tushy) is not, so it exercises the add-then-flip.
    private const string AddedTpdb = "3372";
    private const string AbsentTpdb = "3417";

    private static readonly IReadOnlyList<int> OriginTag = [1];

    private static (V2Adapter Adapter, FakeHttpMessageHandler Handler) AdapterOn(FakeHttpMessageHandler handler)
        => (new V2Adapter(new WhisparrClient(new HttpClient(handler)), TimeSpan.Zero), handler);

    // The create-path verify read-back: the just-added site (id 3, the SeriesAddResponse id) now monitored:true,
    // so the create-path monitor verify passes on the first attempt (no re-PUT).
    private const string AddedSiteMonitored = """
        [ { "id": 3, "tvdbId": 3417, "title": "Tushy", "titleSlug": "tushy", "path": "/config/media/Tushy", "monitored": true, "tags": [] } ]
        """;

    private static Func<HttpResponseMessage> Respond(HttpStatusCode status, string body)
        => FakeHttpMessageHandler.Respond(status, "application/json", body);

    // A handler primed to answer 200 for ANY call: if a deferred method wrongly reached the wire (v2 or a
    // silent v3 fall-through) the request would be captured, so an empty Requests log is the refusal proof.
    private static (V2Adapter Adapter, FakeHttpMessageHandler Handler) Tripwire()
        => AdapterOn(FakeHttpMessageHandler.Json("{}"));

    private static void AssertCleanRefusal<T>(WhisparrResult<T> result, FakeHttpMessageHandler handler)
    {
        Assert.Equal(WhisparrResultState.VersionMismatch, result.State);
        Assert.Equal("v2", result.DetectedVersion);
        Assert.Empty(handler.Requests);
        Assert.Equal(0, handler.CallCount);
    }

    // === GO — a Cove studio -> v2 SITE (series), loop-safe and idempotent ===

    // GO 1: monitor a studio -> add the site NON-grabbing (origin-tagged), then flip; the sole grab verb
    // (POST /command) is never hit on the monitor path.
    [Fact]
    public async Task StudioMonitor_V2_AddsSiteNonGrabbing_ThenFlips_NoSearchCommand()
    {
        var (adapter, handler) = AdapterOn(FakeHttpMessageHandler.Sequence(
            Respond(HttpStatusCode.OK, V2Fixtures.SeriesArray),
            Respond(HttpStatusCode.OK, V2Fixtures.SeriesLookup),
            Respond(HttpStatusCode.Created, V2Fixtures.SeriesAddResponse),
            Respond(HttpStatusCode.Accepted, V2Fixtures.SeriesPutResponse),
            Respond(HttpStatusCode.OK, AddedSiteMonitored)));               // create-path verify read-back

        var result = await adapter.SetEntityMonitorAsync(
            BaseUrl, ApiKey, EntityKind.Studio, AbsentTpdb, monitored: true,
            scope: MonitorScope.NewReleases, rootFolderPath: "/config/media", qualityProfileId: 1, OriginTag, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.True(result.Value!.Added);
        Assert.True(result.Value.Monitored);

        var add = Assert.Single(handler.Requests, r => r.Method == HttpMethod.Post && r.Url.EndsWith("/api/v3/series", StringComparison.Ordinal));
        Assert.Contains("\"searchForMissingEpisodes\":false", add.Body);
        Assert.Contains("\"tags\":[1]", add.Body);
        Assert.DoesNotContain(handler.Requests, r => r.Url.Contains("/api/v3/command", StringComparison.Ordinal));
    }

    // GO 1 (idempotency spine): a duplicate add (400 SeriesExistsValidator) is success, resolved by re-read —
    // never a second POST /series.
    [Fact]
    public async Task StudioMonitor_V2_DuplicateAdd_IsIdempotentSuccess_NoDuplicate()
    {
        const string addedTushy = """
            [ { "id": 5, "tvdbId": 3417, "title": "Tushy", "titleSlug": "tushy", "path": "/config/media/Tushy", "monitored": false, "tags": [] } ]
            """;
        var (adapter, handler) = AdapterOn(FakeHttpMessageHandler.Sequence(
            Respond(HttpStatusCode.OK, V2Fixtures.SeriesArray),
            Respond(HttpStatusCode.OK, V2Fixtures.SeriesLookup),
            Respond(HttpStatusCode.BadRequest, V2Fixtures.SeriesExistsError),
            Respond(HttpStatusCode.OK, addedTushy),
            Respond(HttpStatusCode.Accepted, V2Fixtures.SeriesPutResponse)));

        var result = await adapter.SetEntityMonitorAsync(
            BaseUrl, ApiKey, EntityKind.Studio, AbsentTpdb, monitored: true,
            scope: MonitorScope.NewReleases, rootFolderPath: "/config/media", qualityProfileId: 1, OriginTag, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.False(result.Value!.Added);
        Assert.True(result.Value.Monitored);
        Assert.Single(handler.Requests, r => r.Method == HttpMethod.Post && r.Url.EndsWith("/api/v3/series", StringComparison.Ordinal));
    }

    // GO 1 (AllScenes scope): the add carries monitor:"all" (want every existing episode) and, after the flip,
    // a bulk episode-monitor toggle marks the site's episodes monitored — still search-free (no POST /command).
    [Fact]
    public async Task StudioMonitor_V2_AllScenes_AddsMonitorAll_ThenBulkMonitorsEpisodes_NoSearch()
    {
        var (adapter, handler) = AdapterOn(FakeHttpMessageHandler.Sequence(
            Respond(HttpStatusCode.OK, V2Fixtures.SeriesArray),           // GET /series -> 3417 absent
            Respond(HttpStatusCode.OK, V2Fixtures.SeriesLookup),          // lookup tpdb:3417
            Respond(HttpStatusCode.Created, V2Fixtures.SeriesAddResponse),// POST /series (created id 3)
            Respond(HttpStatusCode.Accepted, V2Fixtures.SeriesPutResponse), // PUT flip
            Respond(HttpStatusCode.OK, V2Fixtures.EpisodesSeries1),       // cascade: GET /episode?seriesId=3
            Respond(HttpStatusCode.Accepted, "{}"),                       // cascade: PUT /episode/monitor
            Respond(HttpStatusCode.OK, AddedSiteMonitored)));             // create-path verify read-back

        var result = await adapter.SetEntityMonitorAsync(
            BaseUrl, ApiKey, EntityKind.Studio, AbsentTpdb, monitored: true,
            scope: MonitorScope.AllScenes, rootFolderPath: "/config/media", qualityProfileId: 1, OriginTag, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.True(result.Value!.Monitored);

        var add = Assert.Single(handler.Requests, r => r.Method == HttpMethod.Post && r.Url.EndsWith("/api/v3/series", StringComparison.Ordinal));
        Assert.Contains("\"monitor\":\"all\"", add.Body);
        Assert.Contains("\"monitorNewItems\":\"all\"", add.Body);
        Assert.Contains("\"searchForMissingEpisodes\":false", add.Body);

        // The cascade fired: a bulk toggle over every episode id (1,2,3), monitored:true, and it never searches.
        var monitor = Assert.Single(handler.Requests, r => r.Method == HttpMethod.Put && r.Url.EndsWith("/api/v3/episode/monitor", StringComparison.Ordinal));
        Assert.Contains("\"episodeIds\":[1,2,3]", monitor.Body);
        Assert.Contains("\"monitored\":true", monitor.Body);
        Assert.DoesNotContain(handler.Requests, r => r.Url.Contains("/api/v3/command", StringComparison.Ordinal));
    }

    // GO 1 (NewReleases scope): the add carries monitor:"none" (back-catalogue left alone) with monitorNewItems
    // still "all"; no episode-monitor cascade runs.
    [Fact]
    public async Task StudioMonitor_V2_NewReleases_AddsMonitorNone_NoEpisodeMonitor()
    {
        var (adapter, handler) = AdapterOn(FakeHttpMessageHandler.Sequence(
            Respond(HttpStatusCode.OK, V2Fixtures.SeriesArray),
            Respond(HttpStatusCode.OK, V2Fixtures.SeriesLookup),
            Respond(HttpStatusCode.Created, V2Fixtures.SeriesAddResponse),
            Respond(HttpStatusCode.Accepted, V2Fixtures.SeriesPutResponse),
            Respond(HttpStatusCode.OK, AddedSiteMonitored)));               // create-path verify read-back

        var result = await adapter.SetEntityMonitorAsync(
            BaseUrl, ApiKey, EntityKind.Studio, AbsentTpdb, monitored: true,
            scope: MonitorScope.NewReleases, rootFolderPath: "/config/media", qualityProfileId: 1, OriginTag, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);

        var add = Assert.Single(handler.Requests, r => r.Method == HttpMethod.Post && r.Url.EndsWith("/api/v3/series", StringComparison.Ordinal));
        Assert.Contains("\"monitor\":\"none\"", add.Body);
        Assert.Contains("\"monitorNewItems\":\"all\"", add.Body);
        Assert.Contains("\"searchForMissingEpisodes\":false", add.Body);
        Assert.DoesNotContain(handler.Requests, r => r.Url.EndsWith("/api/v3/episode/monitor", StringComparison.Ordinal));
    }

    // GO 1 (loop-safety parity with v3): the v2 studio monitor-ON NewReleases path fires NO POST /command — no
    // RefreshSeries (the v3 refresh-on-monitor has NO v2 analog: Sonarr honors the add body's monitor:"none"
    // rather than hard-coding the back-catalogue monitored), no global refresh, and no episode search. The
    // back-catalogue lever is the add body's monitor:"none", not a refresh — so v2 gets no speculative
    // population step and cannot arm a grab on monitor. This is the documented no-refresh-on-v2 decision.
    [Fact]
    public async Task StudioMonitor_V2_NewReleases_IssuesNoCommand_NoRefreshNoSearch()
    {
        var (adapter, handler) = AdapterOn(FakeHttpMessageHandler.Sequence(
            Respond(HttpStatusCode.OK, V2Fixtures.SeriesArray),
            Respond(HttpStatusCode.OK, V2Fixtures.SeriesLookup),
            Respond(HttpStatusCode.Created, V2Fixtures.SeriesAddResponse),
            Respond(HttpStatusCode.Accepted, V2Fixtures.SeriesPutResponse),
            Respond(HttpStatusCode.OK, AddedSiteMonitored)));               // create-path verify read-back

        var result = await adapter.SetEntityMonitorAsync(
            BaseUrl, ApiKey, EntityKind.Studio, AbsentTpdb, monitored: true,
            scope: MonitorScope.NewReleases, rootFolderPath: "/config/media", qualityProfileId: 1, OriginTag, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.DoesNotContain(handler.Requests, r => r.Url.Contains("/api/v3/command", StringComparison.Ordinal));
        Assert.DoesNotContain(handler.Requests, r => r.Url.EndsWith("/api/v3/episode/monitor", StringComparison.Ordinal));
    }

    // GO 2: a studio's status is the added/monitored fact plus grabbed-of-total from the site's episodes.
    [Fact]
    public async Task StudioStatus_V2_ReportsAddedMonitored_AndGrabbedOfTotal()
    {
        var (adapter, handler) = AdapterOn(FakeHttpMessageHandler.Sequence(
            Respond(HttpStatusCode.OK, V2Fixtures.SeriesArray),
            Respond(HttpStatusCode.OK, V2Fixtures.EpisodesSeries1)));

        var result = await adapter.GetEntityStatusAsync(
            BaseUrl, ApiKey, EntityKind.Studio, AddedTpdb, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.True(result.Value!.Added);
        Assert.Equal(2, result.Value.ScenesPresent);
        Assert.Equal(3, result.Value.ScenesTotal);
        Assert.DoesNotContain(handler.Requests, r => r.Url.Contains("/api/v3/command", StringComparison.Ordinal));
    }

    // GO 3: the search-all input — the site's episode ids (the v2 attributed-id set), monitored-filtered.
    [Fact]
    public async Task AttributedIds_V2_EnumeratesTheSitesEpisodeIds()
    {
        var (adapter, handler) = AdapterOn(FakeHttpMessageHandler.Sequence(
            Respond(HttpStatusCode.OK, V2Fixtures.SeriesArray),
            Respond(HttpStatusCode.OK, V2Fixtures.EpisodesSeries1)));

        var result = await adapter.ListAttributedMovieIdsAsync(
            BaseUrl, ApiKey, EntityKind.Studio, AddedTpdb, monitoredOnly: true, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.Equal([1, 2], result.Value!);
        Assert.DoesNotContain(handler.Requests, r => r.Url.Contains("/api/v3/command", StringComparison.Ordinal));
    }

    // GO 4: the episode search is the ONE grab-capable v2 verb — it (and only it) posts POST /command.
    [Fact]
    public async Task EpisodeSearch_V2_IsTheSoleGrabVerb_PostsCommand()
    {
        var (adapter, handler) = AdapterOn(FakeHttpMessageHandler.Json(V2Fixtures.EpisodeSearchCommandResponse));

        var result = await adapter.SearchScenesAsync(BaseUrl, ApiKey, [101, 102], CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        var command = Assert.Single(handler.Requests);
        Assert.EndsWith("/api/v3/command", command.Url, StringComparison.Ordinal);
        Assert.Contains("\"EpisodeSearch\"", command.Body);
    }

    // GO 4 (loop-safety): an empty id set issues NO grab command.
    [Fact]
    public async Task EpisodeSearch_V2_EmptyIds_IssuesNoCommand()
    {
        var (adapter, handler) = AdapterOn(FakeHttpMessageHandler.Json(V2Fixtures.EpisodeSearchCommandResponse));

        var result = await adapter.SearchScenesAsync(BaseUrl, ApiKey, [], CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.Empty(handler.Requests);
    }

    // === DEFER — capability-specific, each with a real reason; a clean refusal with zero wire calls ===

    // v2 has NO performer entity: performers are embedded episode.actors[] metadata with no monitorable
    // resource, so there is nothing to add/flip (monitor) or count a status for.
    [Fact]
    public async Task PerformerMonitor_V2_DefersCleanly_NoWireCall()
    {
        var (adapter, handler) = Tripwire();

        var result = await adapter.SetEntityMonitorAsync(
            BaseUrl, ApiKey, EntityKind.Performer, AbsentTpdb, monitored: true,
            scope: MonitorScope.NewReleases, rootFolderPath: "/config/media", qualityProfileId: 1, OriginTag, CancellationToken.None);

        AssertCleanRefusal(result, handler);
    }

    [Fact]
    public async Task PerformerStatus_V2_DefersCleanly_NoWireCall()
    {
        var (adapter, handler) = Tripwire();

        var result = await adapter.GetEntityStatusAsync(
            BaseUrl, ApiKey, EntityKind.Performer, AbsentTpdb, CancellationToken.None);

        AssertCleanRefusal(result, handler);
    }

    [Fact]
    public async Task PerformerAttributedIds_V2_DefersCleanly_NoWireCall()
    {
        var (adapter, handler) = Tripwire();

        var result = await adapter.ListAttributedMovieIdsAsync(
            BaseUrl, ApiKey, EntityKind.Performer, AbsentTpdb, monitoredOnly: true, CancellationToken.None);

        AssertCleanRefusal(result, handler);
    }

    // v2 has no per-scene (episode) add — no POST /episode; a scene is acquired by adding its site and
    // searching the episode. So per-scene add, availability-register, and bulk-add-missing all defer.
    [Fact]
    public async Task SceneAdd_V2_DefersCleanly_NoWireCall()
    {
        var (adapter, handler) = Tripwire();

        var result = await adapter.AddSceneAsync(
            BaseUrl, ApiKey, AbsentTpdb, title: "A Scene", monitored: true, searchForMovie: false,
            rootFolderPath: "/config/media", qualityProfileId: 1, OriginTag, CancellationToken.None);

        AssertCleanRefusal(result, handler);
    }

    // The scene-monitor add-then-flip add leg has no v2 per-scene add (see SceneAdd), so it defers too.
    [Fact]
    public async Task SceneMonitor_V2_DefersCleanly_NoWireCall()
    {
        var (adapter, handler) = Tripwire();

        var result = await adapter.SetSceneMonitorAsync(
            BaseUrl, ApiKey, AbsentTpdb, title: "A Scene", monitored: true,
            rootFolderPath: "/config/media", qualityProfileId: 1, OriginTag, CancellationToken.None);

        AssertCleanRefusal(result, handler);
    }

    // v2 (Sonarr) has no cutoff-upgrade-only search variant; the sole grab verb is the episode search.
    [Fact]
    public async Task SearchForUpgrades_V2_DefersCleanly_NoWireCall()
    {
        var (adapter, handler) = Tripwire();

        var result = await adapter.SearchForUpgradesAsync(BaseUrl, ApiKey, [1, 2], CancellationToken.None);

        AssertCleanRefusal(result, handler);
    }

    // v2 releases are episode-keyed; the scene panel resolves by a StashDB id v2 rows lack, so a count from a
    // fuzzy-matched episode would be misleading — defers despite a live-200 endpoint.
    [Fact]
    public async Task ReleasesRead_V2_DefersCleanly_NoWireCall()
    {
        var (adapter, handler) = Tripwire();

        var result = await adapter.GetReleasesAsync(BaseUrl, ApiKey, movieId: 42, CancellationToken.None);

        AssertCleanRefusal(result, handler);
    }

    // v2's renamed /importlistexclusion is live-200 but its rows are TPDB-keyed and cannot correlate to a
    // Cove scene without a StashDB id — so the exclusion read/write surface defers.
    [Fact]
    public async Task ExclusionsRead_V2_DefersCleanly_NoWireCall()
    {
        var (adapter, handler) = Tripwire();

        var result = await adapter.ListExclusionsAsync(BaseUrl, ApiKey, CancellationToken.None);

        AssertCleanRefusal(result, handler);
    }

    [Fact]
    public async Task AddExclusion_V2_DefersCleanly_NoWireCall()
    {
        var (adapter, handler) = Tripwire();

        var result = await adapter.AddExclusionAsync(BaseUrl, ApiKey, AbsentTpdb, title: "A Scene", year: 2016, CancellationToken.None);

        AssertCleanRefusal(result, handler);
    }

    [Fact]
    public async Task RemoveExclusion_V2_DefersCleanly_NoWireCall()
    {
        var (adapter, handler) = Tripwire();

        var result = await adapter.RemoveExclusionAsync(BaseUrl, ApiKey, AbsentTpdb, CancellationToken.None);

        AssertCleanRefusal(result, handler);
    }

    // The interactive single-release grab has no v2 mapping under this identity model — defers, never grabs.
    [Fact]
    public async Task GrabRelease_V2_DefersCleanly_NoWireCall()
    {
        var (adapter, handler) = Tripwire();

        var result = await adapter.GrabReleaseAsync(BaseUrl, ApiKey, guid: "abc", indexerId: 1, movieId: 1, CancellationToken.None);

        AssertCleanRefusal(result, handler);
    }

    // A DEFER is genuinely wire-free even when the transport is primed to fault: the method short-circuits
    // before the classify-not-throw send loop, so a would-be transport error is impossible to observe.
    [Fact]
    public async Task DeferredMethod_NeverReachesTransport_EvenWhenHandlerFaults()
    {
        var handler = FakeHttpMessageHandler.Sequence(
            FakeHttpMessageHandler.Throw(new HttpRequestException("connection refused")));
        var adapter = new V2Adapter(new WhisparrClient(new HttpClient(handler)));

        var result = await adapter.AddSceneAsync(
            BaseUrl, ApiKey, AbsentTpdb, title: "A Scene", monitored: true, searchForMovie: false,
            rootFolderPath: "/config/media", qualityProfileId: 1, OriginTag, CancellationToken.None);

        AssertCleanRefusal(result, handler);
    }

    // === LIVE probe (SkippableFact) — no-ops without a reachable v2 ===

    // The end-to-end confirmation of the no-refresh-on-v2 decision against a REAL v2 instance: monitor a
    // THROWAWAY site under NewReleases and confirm the discovered back-catalogue is NOT left all-monitored (the
    // add body's monitor:"none" lever, not a refresh, keeps the episodes unmonitored — no flood). Gated on the
    // live env AND an explicit disposable-site TPDB id so the default CI run never mutates a live seed; SKIPS
    // WITH A REASON when either is absent or the site has no episodes yet. The API key is read from the env and
    // used only for the X-Api-Key header — never logged or asserted on.
    [SkippableFact]
    [Trait("Tier", "LiveE2E")]
    public async Task StudioMonitor_LiveV2_NewReleases_LeavesBackCatalogueUnmonitored_OrSkip()
    {
        var baseUrl = Env("WHISPARR_V2_E2E_URL", "WHISPARR_V2_URL");
        var apiKey = Env("WHISPARR_V2_E2E_KEY", "WHISPARR_V2_KEY");
        var disposableTpdb = Environment.GetEnvironmentVariable("WHISPARR_V2_E2E_DISPOSABLE_TPDB");

        Skip.If(
            string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(apiKey),
            "live v2 gate not set — export WHISPARR_V2_E2E_URL + WHISPARR_V2_E2E_KEY to run the live v2 probe");
        Skip.If(
            string.IsNullOrWhiteSpace(disposableTpdb),
            "no disposable site — export WHISPARR_V2_E2E_DISPOSABLE_TPDB=<throwaway site tpdb id> to run the mutating v2 NewReleases probe");

        var client = new WhisparrClient(new HttpClient());
        var adapter = new V2Adapter(client);

        var monitor = await adapter.SetEntityMonitorAsync(
            baseUrl!, apiKey!, EntityKind.Studio, disposableTpdb!, monitored: true,
            MonitorScope.NewReleases, rootFolderPath: "/config/media", qualityProfileId: 1, tagIds: [], CancellationToken.None);
        Assert.Equal(WhisparrResultState.Ok, monitor.State);

        var series = await client.ListSeriesAsync(baseUrl!, apiKey!, CancellationToken.None);
        Assert.Equal(WhisparrResultState.Ok, series.State);
        var site = Array.Find(
            series.Value!,
            s => s.TvdbId is { } id && id.ToString(CultureInfo.InvariantCulture) == disposableTpdb);
        Assert.NotNull(site);

        var episodes = await client.ListEpisodesAsync(baseUrl!, apiKey!, site!.Id, CancellationToken.None);
        Assert.Equal(WhisparrResultState.Ok, episodes.State);
        Skip.If(
            episodes.Value!.Length == 0,
            "the disposable site has no episodes yet (Whisparr fetches them asynchronously) — re-run once the catalogue populated");

        // NewReleases leaves the back-catalogue unmonitored, so NOT every episode is monitored. An all-monitored
        // set would be the mass-grab-armed flood this phase forbids.
        Assert.Contains(episodes.Value!, e => !e.Monitored);
    }

    // Reads a live env var by its primary name, falling back to an alias (mirrors V2LiveE2ETests.Env). The key
    // is returned for the X-Api-Key header only — never logged.
    private static string? Env(string primary, string alias)
    {
        var value = Environment.GetEnvironmentVariable(primary);
        return string.IsNullOrWhiteSpace(value) ? Environment.GetEnvironmentVariable(alias) : value;
    }
}
