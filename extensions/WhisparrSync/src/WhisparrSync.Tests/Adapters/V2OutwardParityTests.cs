using System.Globalization;
using System.Net;
using WhisparrSync.Adapters;
using WhisparrSync.Client;
using WhisparrSync.Contracts;
using WhisparrSync.Options;
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
[Trait("Tier", "L0")]
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

        var result = await adapter.SetStudioMonitorAsync(
            BaseUrl, ApiKey, AbsentTpdb, monitored: true,
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

        var result = await adapter.SetStudioMonitorAsync(
            BaseUrl, ApiKey, AbsentTpdb, monitored: true,
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

        var result = await adapter.SetStudioMonitorAsync(
            BaseUrl, ApiKey, AbsentTpdb, monitored: true,
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

        var result = await adapter.SetStudioMonitorAsync(
            BaseUrl, ApiKey, AbsentTpdb, monitored: true,
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

        var result = await adapter.SetStudioMonitorAsync(
            BaseUrl, ApiKey, AbsentTpdb, monitored: true,
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

        var result = await adapter.GetStudioStatusAsync(
            BaseUrl, ApiKey, AddedTpdb, CancellationToken.None);

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

        var result = await adapter.ListStudioAttributedIdsAsync(
            BaseUrl, ApiKey, AddedTpdb, monitoredOnly: true, CancellationToken.None);

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

    // GO 5 (register — the monitor-independent site-add): an absent site is registered PRESENT yet inert —
    // monitored:false, monitor/monitorNewItems "none", searchForMissingEpisodes:false, origin-tagged — with no
    // flip, no episode cascade, and no grab command. This is the primitive the monitor-OFF bulk sync needs.
    [Fact]
    public async Task Register_V2_AbsentSite_AddsNonGrabbing_NoFlipNoCommand()
    {
        var (adapter, handler) = AdapterOn(FakeHttpMessageHandler.Sequence(
            Respond(HttpStatusCode.OK, V2Fixtures.SeriesArray),             // GET /series -> 3417 absent
            Respond(HttpStatusCode.OK, V2Fixtures.SeriesLookup),            // lookup tpdb:3417
            Respond(HttpStatusCode.Created, V2Fixtures.SeriesAddResponse))); // POST /series (created id 3, monitored:false)

        var result = await adapter.RegisterStudioAsync(
            BaseUrl, ApiKey, AbsentTpdb,
            rootFolderPath: "/config/media", qualityProfileId: 1, OriginTag, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.True(result.Value!.Added);
        Assert.False(result.Value.Monitored);

        var add = Assert.Single(handler.Requests, r => r.Method == HttpMethod.Post && r.Url.EndsWith("/api/v3/series", StringComparison.Ordinal));
        Assert.Contains("\"monitored\":false", add.Body);
        Assert.Contains("\"monitor\":\"none\"", add.Body);
        Assert.Contains("\"monitorNewItems\":\"none\"", add.Body);
        Assert.Contains("\"searchForMissingEpisodes\":false", add.Body);
        Assert.Contains("\"tags\":[1]", add.Body);

        // Register never flips (no PUT), never cascades episode monitors, and never grabs (no command).
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Put);
        Assert.DoesNotContain(handler.Requests, r => r.Url.EndsWith("/api/v3/episode/monitor", StringComparison.Ordinal));
        Assert.DoesNotContain(handler.Requests, r => r.Url.Contains("/api/v3/command", StringComparison.Ordinal));
    }

    // GO 5 (register idempotency spine): a duplicate add (400 SeriesExistsValidator) is success, resolved by
    // re-read — never a second POST /series.
    [Fact]
    public async Task Register_V2_DuplicateAdd_IsIdempotentSuccess_SinglePost()
    {
        const string addedTushy = """
            [ { "id": 5, "tvdbId": 3417, "title": "Tushy", "titleSlug": "tushy", "path": "/config/media/Tushy", "monitored": false, "tags": [] } ]
            """;
        var (adapter, handler) = AdapterOn(FakeHttpMessageHandler.Sequence(
            Respond(HttpStatusCode.OK, V2Fixtures.SeriesArray),
            Respond(HttpStatusCode.OK, V2Fixtures.SeriesLookup),
            Respond(HttpStatusCode.BadRequest, V2Fixtures.SeriesExistsError),
            Respond(HttpStatusCode.OK, addedTushy)));

        var result = await adapter.RegisterStudioAsync(
            BaseUrl, ApiKey, AbsentTpdb,
            rootFolderPath: "/config/media", qualityProfileId: 1, OriginTag, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.False(result.Value!.Added);
        Assert.Single(handler.Requests, r => r.Method == HttpMethod.Post && r.Url.EndsWith("/api/v3/series", StringComparison.Ordinal));
        Assert.DoesNotContain(handler.Requests, r => r.Url.Contains("/api/v3/command", StringComparison.Ordinal));
    }

    // GO 5 (register no-op): an already-present site is a success with NO create — only the single GET /series
    // ran (no lookup, no POST, no PUT, no command), and it reports the site's existing monitored state.
    [Fact]
    public async Task Register_V2_AlreadyPresent_IsNoOpSuccess_OnlyGet()
    {
        var (adapter, handler) = AdapterOn(FakeHttpMessageHandler.Sequence(
            Respond(HttpStatusCode.OK, V2Fixtures.SeriesArray)));

        var result = await adapter.RegisterStudioAsync(
            BaseUrl, ApiKey, AddedTpdb, // 3372 seeded present + monitored:true
            rootFolderPath: "/config/media", qualityProfileId: 1, OriginTag, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.False(result.Value!.Added);
        Assert.True(result.Value.Monitored);

        var only = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, only.Method);
        Assert.EndsWith("/api/v3/series", only.Url, StringComparison.Ordinal);
    }

    // GO 6 (discovery — read-only catalogue enumeration): a Cove studio's v2 catalogue is its SITE's episodes
    // projected as uniform "scenes" (GET /series + /episode + /episodefile), keyed on TPDB with no StashDB id,
    // and it issues NO grab command — enumeration is a pure read.
    [Fact]
    public async Task Discovery_V2_Studio_EnumeratesEpisodesAsUniformScenes_NoCommand()
    {
        var (adapter, handler) = AdapterOn(FakeHttpMessageHandler.Sequence(
            Respond(HttpStatusCode.OK, V2Fixtures.SeriesArray),
            Respond(HttpStatusCode.OK, V2Fixtures.EpisodesSeries1),
            Respond(HttpStatusCode.OK, V2Fixtures.EpisodeFilesSeries1)));

        var result = await ((IWhisparrEntityCatalogue)adapter).ListEntityMoviesAsync(
            BaseUrl, ApiKey, EntityKind.Studio, AddedTpdb, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.Equal(EntityCatalogueState.Known, result.Value!.State);
        var scenes = result.Value.Movies;
        Assert.Equal(3, scenes.Length);
        // Uniform "scene" shape: no StashDB id, the TPDB scene id in ForeignId, the v2scene marker, the site
        // title stamped for the entity display name.
        Assert.All(scenes, s => Assert.Null(s.StashId));
        Assert.All(scenes, s => Assert.Equal("v2scene", s.ItemType));
        Assert.All(scenes, s => Assert.Equal("Vixen", s.StudioTitle));
        Assert.Contains(scenes, s => s.ForeignId == "1010276");
        Assert.DoesNotContain(handler.Requests, r => r.Url.Contains("/api/v3/command", StringComparison.Ordinal));
    }

    // GO 6 (discovery — unknown site): a studio Whisparr does not know is the EntityUnknown answer (a handled
    // Ok, never an error), reading ONLY the series list — no episode fetch, no grab command. The walk already
    // knew the site had not resolved; it used to report that as an empty catalogue.
    [Fact]
    public async Task Discovery_V2_AbsentSite_IsEntityUnknown_OnlySeriesRead()
    {
        var (adapter, handler) = AdapterOn(FakeHttpMessageHandler.Json(V2Fixtures.SeriesArray));

        var result = await ((IWhisparrEntityCatalogue)adapter).ListEntityMoviesAsync(
            BaseUrl, ApiKey, EntityKind.Studio, AbsentTpdb, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.Equal(EntityCatalogueState.EntityUnknown, result.Value!.State);
        Assert.Empty(result.Value.Movies);
        var only = Assert.Single(handler.Requests);
        Assert.EndsWith("/api/v3/series", only.Url, StringComparison.Ordinal);
    }

    // GO 6 (discovery — performer defer): v2 has no performer entity, so a performer discovery enumerates
    // nothing cleanly — ZERO outbound wire calls against a handler primed to answer, positive proof it
    // short-circuited before the transport. It answers NotEnumerableOnThisVersion rather than an empty
    // catalogue, so a caller can tell "there is nothing here" from "this generation cannot be asked".
    [Fact]
    public async Task Discovery_V2_Performer_DefersNotEnumerable_NoWireCall()
    {
        var (adapter, handler) = AdapterOn(FakeHttpMessageHandler.Json(V2Fixtures.SeriesArray));

        var result = await ((IWhisparrEntityCatalogue)adapter).ListEntityMoviesAsync(
            BaseUrl, ApiKey, EntityKind.Performer, "83401", CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.Equal(EntityCatalogueState.NotEnumerableOnThisVersion, result.Value!.State);
        Assert.Empty(result.Value.Movies);
        Assert.Empty(handler.Requests);
    }

    // GO 6 (discovery search — the SOLE v2 grab): resolve a site scene's episode id, then search it — exactly one
    // POST /command carrying EpisodeSearch over that episode id, and nothing grab-capable on any other path. The
    // catalogue leg exercised here is an adapter seam with NO production caller: the shipped v2 discovery
    // catalogue is the direct ThePornDB read, and the action index keys off that. The seam is retained because a
    // Whisparr-sourced v2 per-scene status or a Whisparr-sourced v2 search has no other surface to read from. The
    // SearchScenesAsync leg is the shipped one, and this is the v2 half of "Search is the only discovery action
    // that grabs".
    [Fact]
    public async Task Discovery_V2_SearchResolvedEpisode_IssuesEpisodeSearch_SoleCommand()
    {
        var (adapter, handler) = AdapterOn(FakeHttpMessageHandler.Sequence(
            Respond(HttpStatusCode.OK, V2Fixtures.SeriesArray),
            Respond(HttpStatusCode.OK, V2Fixtures.EpisodesSeries1),
            Respond(HttpStatusCode.OK, V2Fixtures.EpisodeFilesSeries1),
            Respond(HttpStatusCode.OK, V2Fixtures.EpisodeSearchCommandResponse)));

        var catalogue = await ((IWhisparrEntityCatalogue)adapter).ListEntityMoviesAsync(
            BaseUrl, ApiKey, EntityKind.Studio, AddedTpdb, CancellationToken.None);
        Assert.Equal(WhisparrResultState.Ok, catalogue.State);
        var episodeId = catalogue.Value!.Movies[0].Id;
        Assert.NotEqual(0, episodeId); // a real episode id backs the grab (a synthesized Id 0 would be searched:false)

        var result = await adapter.SearchScenesAsync(BaseUrl, ApiKey, [episodeId], CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        var command = Assert.Single(
            handler.Requests, r => r.Method == HttpMethod.Post && r.Url.EndsWith("/api/v3/command", StringComparison.Ordinal));
        Assert.Contains("\"EpisodeSearch\"", command.Body);
        Assert.Contains(episodeId.ToString(CultureInfo.InvariantCulture), command.Body, StringComparison.Ordinal);
    }

    // Discovery is a SHARED v2+v3 role (unlike the v3-only roles below): v2 DOES implement it, so a studio's v2
    // catalogue path exists by construction.
    [Fact]
    public void V2_implements_the_discovery_source_role()
        => Assert.Contains(typeof(IWhisparrEntityCatalogue), typeof(V2Adapter).GetInterfaces());

    // v2 per-scene UNMONITOR defers before any wire call for the SAME structural reason a per-scene add does: a v2
    // adapter never implements IWhisparrScenePush (the un-path is SceneActions.SetSceneMonitorAsync, which defers
    // VersionMismatch before the transport on a version lacking that role). Discovery unmonitor is v3-only.
    [Fact]
    public void V2_discovery_unmonitor_has_no_scene_push_role_to_call()
        => Assert.DoesNotContain(typeof(IWhisparrScenePush), typeof(V2Adapter).GetInterfaces());

    // GO 7 (configuration completeness): the required-option predicate is generation-NEUTRAL. Both generations
    // need the same address and key, so there is nothing to branch on — and a predicate that answered differently
    // per generation would refuse a working setup on one of them.
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void ConfigCompleteness_IsIdenticalAcrossBothStoredGenerations(int blankFields)
    {
        var v3 = new WhisparrOptions
        {
            SelectedVersion = "v3",
            BaseUrl = blankFields > 0 ? "" : BaseUrl,
            ApiKey = ApiKey,
        };
        var v2 = v3 with { SelectedVersion = "v2" };

        Assert.Equal(
            ConfigCompletenessGuard.MissingRequiredOptions(v3),
            ConfigCompletenessGuard.MissingRequiredOptions(v2));
    }

    // === DEFER — capability-specific, each with a real reason; now a STRUCTURAL refusal ===
    //
    // The former "VersionMismatch + zero wire calls" refusals are now a type-system fact: a v2 adapter never
    // implements any of the 5 v3-only role interfaces, so a v3-only verb has NO METHOD to call on a v2 instance
    // — the wire-free defer is guaranteed by construction, not by a runtime probe or a per-method early-return.
    // Each capability's real reason is preserved in the comment; the orchestration-level deferral (SceneActions /
    // EntityMonitor returning VersionMismatch before any wire work) is proven by their own tests.

    // v2 has NO performer entity (performers are embedded episode.actors[] metadata), so monitor / status /
    // attributed-ids / register a performer all structurally defer.
    [Fact]
    public void V2_does_not_implement_the_performer_monitor_role()
        => Assert.DoesNotContain(typeof(IWhisparrPerformerMonitor), typeof(V2Adapter).GetInterfaces());

    // v2 has no per-scene (episode) add — no POST /episode — so per-scene add and monitor structurally defer.
    [Fact]
    public void V2_does_not_implement_the_scene_push_role()
        => Assert.DoesNotContain(typeof(IWhisparrScenePush), typeof(V2Adapter).GetInterfaces());

    // v2 (Sonarr) has one sole grab verb (the episode search) and no cutoff-upgrade / interactive-release
    // endpoint, so upgrade-search / releases-read / interactive-grab all structurally defer.
    [Fact]
    public void V2_does_not_implement_the_release_grab_role()
        => Assert.DoesNotContain(typeof(IWhisparrReleaseGrab), typeof(V2Adapter).GetInterfaces());

    // v2's /importlistexclusion rows are TPDB-keyed and cannot correlate to a Cove scene without a StashDB id, so
    // the exclusion read/add/remove surface structurally defers.
    [Fact]
    public void V2_does_not_implement_the_exclusions_role()
        => Assert.DoesNotContain(typeof(IWhisparrExclusions), typeof(V2Adapter).GetInterfaces());

    // v2's Sonarr-shaped /config/* uses divergent field names, so the file-settings editor structurally defers.
    [Fact]
    public void V2_does_not_implement_the_file_settings_role()
        => Assert.DoesNotContain(typeof(IWhisparrFileSettings), typeof(V2Adapter).GetInterfaces());
}
