using System.Net;
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
