using System.Net;
using WhisparrSync.Adapters;
using WhisparrSync.Client;
using WhisparrSync.Monitor;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.Adapters;

/// <summary>
/// The v2 client transport contract: the three new Whisparr v2 GET methods
/// (<c>ListSeriesAsync</c>/<c>ListEpisodesAsync</c>/<c>ListEpisodeFilesAsync</c>) each target their exact
/// <c>/api/v3</c> endpoint (episode/episodefile carry the required <c>?seriesId=</c> query), carry the
/// <c>X-Api-Key</c> header, deserialize the live-shaped v2 fixtures, and inherit the classify-not-
/// throw guards unchanged (HTML/502 → NotWhisparr, 401 → BadKey). These are transport-only — no v2 shape
/// knowledge lives in the client (that is the adapter's job — see the V2Adapter synth tests).
/// </summary>
public sealed class V2ClientTests
{
    private const string BaseUrl = "http://localhost:6970";
    private const string ApiKey = "test-api-key";

    private static WhisparrClient ClientFor(FakeHttpMessageHandler handler) => new(new HttpClient(handler));

    [Fact]
    public async Task ListSeries_DeserializesSeriesArray()
    {
        var result = await ClientFor(FakeHttpMessageHandler.Json(V2Fixtures.SeriesArray))
            .ListSeriesAsync(BaseUrl, ApiKey, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        var series = Assert.Single(result.Value!);
        Assert.Equal(1, series.Id);
        Assert.Equal(3372, series.TvdbId);
        Assert.Equal("Vixen", series.Title);
        Assert.Equal("/config/media/Vixen", series.Path);
    }

    [Fact]
    public async Task ListSeries_TargetsSeriesEndpoint_WithApiKey()
    {
        var handler = FakeHttpMessageHandler.Json(V2Fixtures.SeriesArray);
        await ClientFor(handler).ListSeriesAsync(BaseUrl, ApiKey, CancellationToken.None);

        Assert.EndsWith("/api/v3/series", handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.True(handler.LastRequest.Headers.TryGetValues("X-Api-Key", out var values));
        Assert.Equal(ApiKey, Assert.Single(values!));
    }

    [Fact]
    public async Task ListSeries_HtmlBodyClassifiesNotWhisparr()
    {
        var result = await ClientFor(FakeHttpMessageHandler.Html(HttpStatusCode.BadGateway))
            .ListSeriesAsync(BaseUrl, ApiKey, CancellationToken.None);

        Assert.Equal(WhisparrResultState.NotWhisparr, result.State);
    }

    [Fact]
    public async Task ListSeries_UnauthorizedClassifiesBadKey()
    {
        var result = await ClientFor(FakeHttpMessageHandler.Status(HttpStatusCode.Unauthorized))
            .ListSeriesAsync(BaseUrl, ApiKey, CancellationToken.None);

        Assert.Equal(WhisparrResultState.BadKey, result.State);
    }

    [Fact]
    public async Task ListEpisodes_DeserializesEpisodeArray()
    {
        var result = await ClientFor(FakeHttpMessageHandler.Json(V2Fixtures.EpisodesSeries1))
            .ListEpisodesAsync(BaseUrl, ApiKey, 1, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.Equal(3, result.Value!.Length);
        var first = result.Value![0];
        Assert.Equal(1, first.Id);
        Assert.Equal(1010276, first.TvdbId);
        Assert.Equal(0, first.EpisodeFileId);
        Assert.False(first.HasFile);
        Assert.Equal("Payment Extension", first.Title);
    }

    [Fact]
    public async Task ListEpisodes_TargetsEpisodeEndpoint_WithSeriesIdQuery_AndApiKey()
    {
        var handler = FakeHttpMessageHandler.Json(V2Fixtures.EpisodesSeries1);
        await ClientFor(handler).ListEpisodesAsync(BaseUrl, ApiKey, 1, CancellationToken.None);

        Assert.EndsWith("/api/v3/episode", handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Contains("seriesId=1", handler.LastRequest.RequestUri!.Query);
        Assert.True(handler.LastRequest.Headers.TryGetValues("X-Api-Key", out var values));
        Assert.Equal(ApiKey, Assert.Single(values!));
    }

    [Fact]
    public async Task ListEpisodes_UnauthorizedClassifiesBadKey()
    {
        var result = await ClientFor(FakeHttpMessageHandler.Status(HttpStatusCode.Unauthorized))
            .ListEpisodesAsync(BaseUrl, ApiKey, 1, CancellationToken.None);

        Assert.Equal(WhisparrResultState.BadKey, result.State);
    }

    [Fact]
    public async Task ListEpisodeFiles_DeserializesEpisodeFileArray()
    {
        var result = await ClientFor(FakeHttpMessageHandler.Json(V2Fixtures.EpisodeFilesSeries1))
            .ListEpisodeFilesAsync(BaseUrl, ApiKey, 1, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.Equal(2, result.Value!.Length);
        Assert.Equal(5001, result.Value![0].Id);
        Assert.Equal("/config/media/Vixen/second-scene.mkv", result.Value![0].Path);
    }

    [Fact]
    public async Task ListEpisodeFiles_TargetsEpisodeFileEndpoint_WithSeriesIdQuery_AndApiKey()
    {
        var handler = FakeHttpMessageHandler.Json(V2Fixtures.EmptyArray);
        await ClientFor(handler).ListEpisodeFilesAsync(BaseUrl, ApiKey, 1, CancellationToken.None);

        Assert.EndsWith("/api/v3/episodefile", handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Contains("seriesId=1", handler.LastRequest.RequestUri!.Query);
        Assert.True(handler.LastRequest.Headers.TryGetValues("X-Api-Key", out var values));
        Assert.Equal(ApiKey, Assert.Single(values!));
    }

    [Fact]
    public async Task ListEpisodeFiles_HtmlBodyClassifiesNotWhisparr()
    {
        var result = await ClientFor(FakeHttpMessageHandler.Html(HttpStatusCode.BadGateway))
            .ListEpisodeFilesAsync(BaseUrl, ApiKey, 1, CancellationToken.None);

        Assert.Equal(WhisparrResultState.NotWhisparr, result.State);
    }
}

/// <summary>
/// The V2Adapter port contract: the five connect-level methods are pure pass-throughs to the shared
/// <see cref="WhisparrClient"/> (proven by feeding a fake handler and asserting the adapter surfaces the same
/// result at the same endpoint), and <c>ListMoviesAsync</c> synthesizes the normalized <c>WhisparrMovie[]</c>
/// from <c>series → episode → episodefile</c> with the Pitfall-1 guard load-bearing: <c>StashId == null</c>
/// and <c>ItemType == "v2scene"</c> (never <c>"scene"</c>) so the StashDB matcher leg no-ops for v2, and
/// <c>MovieFile.Path</c> joined from the episodefile row. A non-Ok series read propagates without a partial synth.
/// </summary>
public sealed class V2AdapterTests
{
    private const string BaseUrl = "http://localhost:6970";
    private const string ApiKey = "test-api-key";

    private static V2Adapter AdapterFor(FakeHttpMessageHandler handler) => new(new WhisparrClient(new HttpClient(handler)), TimeSpan.Zero);

    private static Func<HttpResponseMessage> Json(string body)
        => FakeHttpMessageHandler.Respond(HttpStatusCode.OK, "application/json", body);

    // --- Delegation: the five connect-level methods pass through to the client unchanged ---

    [Fact]
    public async Task GetStatus_DelegatesToClient()
    {
        const string status = """{"version":"2.2.0.108","appName":"Whisparr","instanceName":"Whisparr","branch":"v2"}""";
        var handler = FakeHttpMessageHandler.Json(status);

        var result = await AdapterFor(handler).GetStatusAsync(BaseUrl, ApiKey, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.Equal("2.2.0.108", result.Value!.Version);
        Assert.EndsWith("/api/v3/system/status", handler.LastRequest!.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task ListRootFolders_DelegatesToClient()
    {
        var handler = FakeHttpMessageHandler.Json("""[{"id":1,"path":"/config/media","accessible":true,"freeSpace":123}]""");

        var result = await AdapterFor(handler).ListRootFoldersAsync(BaseUrl, ApiKey, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.Equal("/config/media", Assert.Single(result.Value!).Path);
        Assert.EndsWith("/api/v3/rootfolder", handler.LastRequest!.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task ListQualityProfiles_DelegatesToClient()
    {
        var handler = FakeHttpMessageHandler.Json("""[{"id":7,"name":"HD-1080p"}]""");

        var result = await AdapterFor(handler).ListQualityProfilesAsync(BaseUrl, ApiKey, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.Equal("HD-1080p", Assert.Single(result.Value!).Name);
        Assert.EndsWith("/api/v3/qualityprofile", handler.LastRequest!.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task ListHistory_DelegatesToClient()
    {
        var handler = FakeHttpMessageHandler.Json("""{"page":1,"pageSize":10,"totalRecords":0,"records":[]}""");

        var result = await AdapterFor(handler).ListHistoryAsync(BaseUrl, ApiKey, 1, 10, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.Equal(1, result.Value!.Page);
        Assert.StartsWith("/api/v3/history", handler.LastRequest!.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task RegisterWebhook_DelegatesToClient_PostsNotification()
    {
        var handler = FakeHttpMessageHandler.Json("{}");

        var result = await AdapterFor(handler)
            .RegisterWebhookAsync(BaseUrl, ApiKey, "http://cove/webhook?token=secret", CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.True(result.Value);
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.EndsWith("/api/v3/notification", handler.LastRequest.RequestUri!.AbsolutePath);
        Assert.Contains("X-Cove-Token", handler.LastRequestBody);
    }

    // --- The one substantive method: the series -> episode -> episodefile synth ---

    [Fact]
    public async Task ListMovies_SynthesizesScenes_StashIdNull_ItemTypeV2Scene_PathJoined()
    {
        // series -> episode?seriesId=1 -> episodefile?seriesId=1 (one series => three calls in order).
        var handler = FakeHttpMessageHandler.Sequence(
            Json(V2Fixtures.SeriesArray),
            Json(V2Fixtures.EpisodesSeries1),
            Json(V2Fixtures.EpisodeFilesSeries1));

        var result = await AdapterFor(handler).ListMoviesAsync(BaseUrl, ApiKey, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        var movies = result.Value!;
        Assert.Equal(3, movies.Length);

        // The TPDB-vs-StashDB guard holds for EVERY synthesized row: never a StashDB-comparable identity.
        Assert.All(movies, m =>
        {
            Assert.Null(m.StashId);
            Assert.Equal("v2scene", m.ItemType);
            Assert.NotEqual("scene", m.ItemType);
        });

        // Episode 1: undownloaded (episodeFileId=0, hasFile=false) => MovieFile null, year from releaseDate.
        var undownloaded = movies[0];
        Assert.Equal(1, undownloaded.Id);
        Assert.Equal("Payment Extension", undownloaded.Title);
        Assert.Equal(2016, undownloaded.Year);
        Assert.Equal("1010276", undownloaded.ForeignId); // TPDB scene id carried in a non-Stash field
        Assert.Null(undownloaded.MovieFile);
        Assert.False(undownloaded.HasFile);

        // Episode 2: downloaded => MovieFile.Path joined from episodefile id 5001.
        var downloaded = movies[1];
        Assert.Equal(2, downloaded.Id);
        Assert.Equal(2017, downloaded.Year);
        Assert.True(downloaded.HasFile);
        Assert.Equal(5001, downloaded.MovieFile!.Id);
        Assert.Equal("/config/media/Vixen/second-scene.mkv", downloaded.MovieFile.Path);
    }

    [Fact]
    public async Task ListMovies_NotOkSeries_PropagatesWithoutPartialSynth()
    {
        // The very first call (/series) is 401 => the whole synth surfaces BadKey, never a partial array.
        var result = await AdapterFor(FakeHttpMessageHandler.Status(HttpStatusCode.Unauthorized))
            .ListMoviesAsync(BaseUrl, ApiKey, CancellationToken.None);

        Assert.Equal(WhisparrResultState.BadKey, result.State);
        Assert.Null(result.Value);
    }

    // --- Outward: studio -> v2 SITE (series) monitor/status keyed on the TPDB id (the tvdbId slot) ---

    private static readonly IReadOnlyList<int> OriginTag = [1];

    // stashId on v2 carries the TPDB site id; the target 3417 (Tushy) is NOT in the seeded /series set (only
    // 3372), so the add-then-flip runs. seriesId 3372 (Vixen) IS added, so the status/idempotent paths use it.
    private const string AbsentTpdb = "3417";
    private const string AddedTpdb = "3372";

    private static Func<HttpResponseMessage> Respond(HttpStatusCode status, string body)
        => FakeHttpMessageHandler.Respond(status, "application/json", body);

    // A /series re-read row for the already-added Tushy site (shape mirrors the captured SeriesArray).
    private const string AddedTushySeries = """
        [ { "id": 5, "tvdbId": 3417, "title": "Tushy", "titleSlug": "tushy", "path": "/config/media/Tushy", "monitored": false, "tags": [] } ]
        """;

    // The create-path verify read-back: the just-created site (id 3, the SeriesAddResponse id) now monitored:true,
    // so the monitor verify passes on the first attempt (no re-PUT).
    private const string CreatedTushySeriesMonitored = """
        [ { "id": 3, "tvdbId": 3417, "title": "Tushy", "titleSlug": "tushy", "path": "/config/media/Tushy", "monitored": true, "tags": [] } ]
        """;

    [Fact]
    public async Task SetEntityMonitor_Studio_AbsentOn_AddsNonGrabbing_ThenFlips_NoSearchCommand()
    {
        var handler = FakeHttpMessageHandler.Sequence(
            Respond(HttpStatusCode.OK, V2Fixtures.SeriesArray),        // GET /series -> 3417 absent
            Respond(HttpStatusCode.OK, V2Fixtures.SeriesLookup),       // GET /series/lookup?term=tpdb:3417
            Respond(HttpStatusCode.Created, V2Fixtures.SeriesAddResponse),  // POST /series (201) monitored:false
            Respond(HttpStatusCode.Accepted, V2Fixtures.SeriesPutResponse), // PUT /series/3 (202) monitored:true
            Respond(HttpStatusCode.OK, CreatedTushySeriesMonitored));       // create-path verify read-back -> true

        var result = await AdapterFor(handler).SetEntityMonitorAsync(
            BaseUrl, ApiKey, EntityKind.Studio, AbsentTpdb, monitored: true,
            scope: MonitorScope.NewReleases, rootFolderPath: "/config/media", qualityProfileId: 1, OriginTag, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.True(result.Value!.Added);
        Assert.True(result.Value.Monitored);

        // Loop-safety: the add body registers the site WITHOUT grabbing and carries the origin tag; the sole
        // grab verb (POST /command) is never hit on a monitor path.
        var add = Assert.Single(handler.Requests, r => r.Method == HttpMethod.Post && r.Url.EndsWith("/api/v3/series", StringComparison.Ordinal));
        Assert.Contains("\"searchForMissingEpisodes\":false", add.Body);
        Assert.Contains("\"monitored\":false", add.Body);
        Assert.Contains("\"tags\":[1]", add.Body);
        Assert.DoesNotContain(handler.Requests, r => r.Url.Contains("/api/v3/command"));

        // The flip PUT sets monitored:true and is the last verb.
        var put = Assert.Single(handler.Requests, r => r.Method == HttpMethod.Put);
        Assert.Contains("\"monitored\":true", put.Body);
    }

    [Fact]
    public async Task SetEntityMonitor_Studio_DuplicateAdd_IsSuccess_NoDuplicate_NotAdded()
    {
        var handler = FakeHttpMessageHandler.Sequence(
            Respond(HttpStatusCode.OK, V2Fixtures.SeriesArray),         // GET /series -> 3417 absent
            Respond(HttpStatusCode.OK, V2Fixtures.SeriesLookup),        // GET /series/lookup
            Respond(HttpStatusCode.BadRequest, V2Fixtures.SeriesExistsError), // POST /series -> 400 SeriesExistsValidator = Conflict
            Respond(HttpStatusCode.OK, AddedTushySeries),               // GET /series re-read -> now present (id 5)
            Respond(HttpStatusCode.Accepted, V2Fixtures.SeriesPutResponse)); // PUT /series/5 flip

        var result = await AdapterFor(handler).SetEntityMonitorAsync(
            BaseUrl, ApiKey, EntityKind.Studio, AbsentTpdb, monitored: true,
            scope: MonitorScope.NewReleases, rootFolderPath: "/config/media", qualityProfileId: 1, OriginTag, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.False(result.Value!.Added); // a duplicate add resolves to the existing row, never "added" by us
        Assert.True(result.Value.Monitored);

        // Exactly one POST /series — the conflict is resolved by re-read, never a second create.
        Assert.Single(handler.Requests, r => r.Method == HttpMethod.Post && r.Url.EndsWith("/api/v3/series", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetEntityStatus_Studio_CountsGrabbedOfTotalFromEpisodes()
    {
        var handler = FakeHttpMessageHandler.Sequence(
            Respond(HttpStatusCode.OK, V2Fixtures.SeriesArray),   // GET /series -> Vixen 3372 (id 1, monitored)
            Respond(HttpStatusCode.OK, V2Fixtures.EpisodesSeries1)); // GET /episode?seriesId=1 -> 3 eps, 2 hasFile

        var result = await AdapterFor(handler).GetEntityStatusAsync(
            BaseUrl, ApiKey, EntityKind.Studio, AddedTpdb, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.True(result.Value!.Added);
        Assert.True(result.Value.Monitored);
        Assert.Equal(2, result.Value.ScenesPresent);
        Assert.Equal(3, result.Value.ScenesTotal);
    }

    [Fact]
    public async Task GetEntityStatus_Studio_AbsentSite_IsAddedFalse_ZeroOfZero()
    {
        var handler = FakeHttpMessageHandler.Json(V2Fixtures.SeriesArray); // GET /series -> 3417 not present

        var result = await AdapterFor(handler).GetEntityStatusAsync(
            BaseUrl, ApiKey, EntityKind.Studio, AbsentTpdb, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.False(result.Value!.Added);
        Assert.Equal(0, result.Value.ScenesTotal);
    }

    [Fact]
    public async Task SetEntityMonitor_Performer_DefersCleanly_NoWireCall()
    {
        var handler = FakeHttpMessageHandler.Json("{}");

        var result = await AdapterFor(handler).SetEntityMonitorAsync(
            BaseUrl, ApiKey, EntityKind.Performer, AbsentTpdb, monitored: true,
            scope: MonitorScope.NewReleases, rootFolderPath: "/config/media", qualityProfileId: 1, OriginTag, CancellationToken.None);

        // v2 has NO performer entity (performers are embedded episode.actors metadata) — nothing to add/flip.
        Assert.Equal(WhisparrResultState.VersionMismatch, result.State);
        Assert.Equal("v2", result.DetectedVersion);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task GetEntityStatus_Performer_DefersCleanly_NoWireCall()
    {
        var handler = FakeHttpMessageHandler.Json("{}");

        var result = await AdapterFor(handler).GetEntityStatusAsync(
            BaseUrl, ApiKey, EntityKind.Performer, AbsentTpdb, CancellationToken.None);

        Assert.Equal(WhisparrResultState.VersionMismatch, result.State);
        Assert.Empty(handler.Requests);
    }

    // --- Outward: attributed episode ids (search-all input) + the episode search (the sole grab verb) ---

    [Fact]
    public async Task ListAttributedMovieIds_Studio_MonitoredOnly_ReturnsMonitoredEpisodeIds()
    {
        var handler = FakeHttpMessageHandler.Sequence(
            Respond(HttpStatusCode.OK, V2Fixtures.SeriesArray),      // GET /series -> Vixen 3372 (id 1)
            Respond(HttpStatusCode.OK, V2Fixtures.EpisodesSeries1)); // eps 1,2 monitored; ep 3 not

        var result = await AdapterFor(handler).ListAttributedMovieIdsAsync(
            BaseUrl, ApiKey, EntityKind.Studio, AddedTpdb, monitoredOnly: true, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.Equal([1, 2], result.Value!);
    }

    [Fact]
    public async Task ListAttributedMovieIds_Studio_All_ReturnsEveryEpisodeId()
    {
        var handler = FakeHttpMessageHandler.Sequence(
            Respond(HttpStatusCode.OK, V2Fixtures.SeriesArray),
            Respond(HttpStatusCode.OK, V2Fixtures.EpisodesSeries1));

        var result = await AdapterFor(handler).ListAttributedMovieIdsAsync(
            BaseUrl, ApiKey, EntityKind.Studio, AddedTpdb, monitoredOnly: false, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.Equal([1, 2, 3], result.Value!);
    }

    [Fact]
    public async Task ListAttributedMovieIds_Studio_AbsentSite_ReturnsEmpty()
    {
        var handler = FakeHttpMessageHandler.Json(V2Fixtures.SeriesArray); // 3417 not present

        var result = await AdapterFor(handler).ListAttributedMovieIdsAsync(
            BaseUrl, ApiKey, EntityKind.Studio, AbsentTpdb, monitoredOnly: false, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.Empty(result.Value!);
    }

    [Fact]
    public async Task ListAttributedMovieIds_Performer_DefersCleanly_NoWireCall()
    {
        var handler = FakeHttpMessageHandler.Json("{}");

        var result = await AdapterFor(handler).ListAttributedMovieIdsAsync(
            BaseUrl, ApiKey, EntityKind.Performer, AbsentTpdb, monitoredOnly: false, CancellationToken.None);

        Assert.Equal(WhisparrResultState.VersionMismatch, result.State);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task SearchScenes_PostsEpisodeSearchCommand_OverTheIds()
    {
        var handler = FakeHttpMessageHandler.Json(V2Fixtures.EpisodeSearchCommandResponse);

        var result = await AdapterFor(handler).SearchScenesAsync(BaseUrl, ApiKey, [101, 102], CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.Equal(2, result.Value!.Total);

        var command = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, command.Method);
        Assert.EndsWith("/api/v3/command", command.Url, StringComparison.Ordinal);
        Assert.Contains("\"EpisodeSearch\"", command.Body);
        Assert.Contains("\"episodeIds\":[101,102]", command.Body);
    }

    [Fact]
    public async Task SearchScenes_EmptyIds_IssuesNoCommand()
    {
        var handler = FakeHttpMessageHandler.Json(V2Fixtures.EpisodeSearchCommandResponse);

        var result = await AdapterFor(handler).SearchScenesAsync(BaseUrl, ApiKey, [], CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.Empty(handler.Requests);
    }

    // --- Scene-level members DEFER: v2 has no per-scene (episode) add and one sole grab verb ---

    [Fact]
    public async Task AddScene_V2_DefersCleanly_NoWireCall()
    {
        var handler = FakeHttpMessageHandler.Json("{}");

        var result = await AdapterFor(handler).AddSceneAsync(
            BaseUrl, ApiKey, AbsentTpdb, title: "A Scene", monitored: true, searchForMovie: false,
            rootFolderPath: "/config/media", qualityProfileId: 1, OriginTag, CancellationToken.None);

        Assert.Equal(WhisparrResultState.VersionMismatch, result.State);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task SetSceneMonitor_V2_DefersCleanly_NoWireCall()
    {
        var handler = FakeHttpMessageHandler.Json("{}");

        var result = await AdapterFor(handler).SetSceneMonitorAsync(
            BaseUrl, ApiKey, AbsentTpdb, title: "A Scene", monitored: true,
            rootFolderPath: "/config/media", qualityProfileId: 1, OriginTag, CancellationToken.None);

        Assert.Equal(WhisparrResultState.VersionMismatch, result.State);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task SearchForUpgrades_V2_DefersCleanly_NoWireCall()
    {
        var handler = FakeHttpMessageHandler.Json("{}");

        var result = await AdapterFor(handler).SearchForUpgradesAsync(BaseUrl, ApiKey, [1, 2], CancellationToken.None);

        Assert.Equal(WhisparrResultState.VersionMismatch, result.State);
        Assert.Empty(handler.Requests);
    }

    // --- Owned-scene import: targeted ManualImport attaches an existing file in place, no move/grab ---

    private const string OwnedFilePath = "/config/media/Vixen/second-scene.mkv";

    // A manualimport listing whose one row matches OwnedFilePath and carries a quality/languages object (which
    // must round-trip verbatim) plus a name-parse rejection the targeted import ignores.
    private const string ManualImportListing = """
        [
          {
            "path": "/config/media/Vixen/second-scene.mkv",
            "quality": { "quality": { "id": 7, "name": "WEB-DL 1080p" }, "revision": { "version": 1 } },
            "languages": [ { "id": 1, "name": "English" } ],
            "rejections": [ { "reason": "Invalid season or episode", "type": "permanent" } ]
          }
        ]
        """;

    // The episode re-read after the import: episode 2 now hasFile:true (the import linked the file).
    private const string EpisodeLinked = """
        [ { "id": 2, "seriesId": 1, "tvdbId": 1010277, "episodeFileId": 5001, "title": "Second Scene", "releaseDate": "2017-04-20", "hasFile": true, "monitored": true } ]
        """;

    [Fact]
    public async Task ImportOwnedScene_ManualImportInPlace_AttachesToEpisode_NoSearch()
    {
        var handler = FakeHttpMessageHandler.Sequence(
            Json(ManualImportListing),   // GET /manualimport?folder=... -> the owned file listed
            Json("{}"),                  // POST /command (ManualImport) accepted
            Json(EpisodeLinked));        // GET /episode?seriesId=1 re-read -> hasFile:true

        var scene = new WhisparrMovie(
            Id: 2, Title: "Second Scene", Year: 2017, StashId: null, ForeignId: "1010277", ItemType: "v2scene",
            Monitored: true, HasFile: false, MovieFile: null, SeriesId: 1);
        var result = await AdapterFor(handler).ImportOwnedSceneAsync(
            BaseUrl, ApiKey, scene, OwnedFilePath, OwnedImportMode.InPlaceAdopt, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.True(result.Value);

        // The listing GET carries the file's parent folder and filterExistingFiles=false (so a known file lists).
        var list = Assert.Single(handler.Requests, r => r.Url.Contains("/api/v3/manualimport", StringComparison.Ordinal));
        Assert.Contains("folder=", list.Url, StringComparison.Ordinal);
        Assert.Contains("filterExistingFiles=false", list.Url, StringComparison.Ordinal);

        // The ManualImport command targets the explicit episode and carries the listed quality VERBATIM; it is a
        // ManualImport, NEVER a search verb (loop-safety: an owned import reuses the file, never grabs).
        var command = Assert.Single(handler.Requests, r => r.Url.EndsWith("/api/v3/command", StringComparison.Ordinal));
        Assert.Contains("\"name\":\"ManualImport\"", command.Body);
        Assert.Contains("\"seriesId\":1", command.Body);
        Assert.Contains("\"episodeIds\":[2]", command.Body);
        Assert.Contains("WEB-DL 1080p", command.Body);          // the listed quality object round-tripped
        Assert.DoesNotContain("Search", command.Body!);          // never EpisodeSearch/MoviesSearch/SeriesSearch
    }

    [Fact]
    public async Task ImportOwnedScene_FileNotListed_IsUnreachable_NoImportCommand()
    {
        // The listing does not contain the owned file (e.g. Whisparr cannot see it) -> a clear Unreachable, and
        // NO ManualImport command is issued (there is nothing safe to import).
        var handler = FakeHttpMessageHandler.Json("[]");

        var scene = new WhisparrMovie(
            Id: 2, Title: "Second Scene", Year: 2017, StashId: null, ForeignId: "1010277", ItemType: "v2scene",
            Monitored: true, HasFile: false, MovieFile: null, SeriesId: 1);
        var result = await AdapterFor(handler).ImportOwnedSceneAsync(
            BaseUrl, ApiKey, scene, OwnedFilePath, OwnedImportMode.InPlaceAdopt, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Unreachable, result.State);
        Assert.DoesNotContain(handler.Requests, r => r.Url.Contains("/api/v3/command", StringComparison.Ordinal));
    }
}
