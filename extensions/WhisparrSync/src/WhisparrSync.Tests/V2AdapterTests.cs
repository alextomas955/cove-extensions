using System.Net;
using WhisparrSync.Adapters;
using WhisparrSync.Client;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests;

/// <summary>
/// The v2 client transport contract (VER-03): the three new Whisparr v2 GET methods
/// (<c>ListSeriesAsync</c>/<c>ListEpisodesAsync</c>/<c>ListEpisodeFilesAsync</c>) each target their exact
/// <c>/api/v3</c> endpoint (episode/episodefile carry the required <c>?seriesId=</c> query), carry the
/// <c>X-Api-Key</c> header, deserialize the live-shaped v2 fixtures, and inherit the Phase-1 classify-not-
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
/// The V2Adapter port contract (VER-03): the five connect-level methods are pure pass-throughs to the shared
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

    private static V2Adapter AdapterFor(FakeHttpMessageHandler handler) => new(new WhisparrClient(new HttpClient(handler)));

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

        // Pitfall 1 guard holds for EVERY synthesized row: never a StashDB-comparable identity.
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
}
