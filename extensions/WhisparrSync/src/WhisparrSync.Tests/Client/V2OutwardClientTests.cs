using System.Net;
using WhisparrSync.Client;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.Client;

/// <summary>
/// The executable classification contract for the Whisparr v2 (Sonarr-based) outward transport surface —
/// site (series) lookup / add / monitor-flip / search — driven by a <see cref="FakeHttpMessageHandler"/>
/// primed with the LIVE-captured <see cref="V2Fixtures"/> blobs. The v2 body field names came from the
/// running 2.2.0.108 instance (never guessed), and these tests replay those verbatim bytes, so a wrong
/// field name fails to bind → the test goes red rather than passing. Transport-shape tests only: the wire
/// (URL + method + classification + single-shot) is asserted, not the domain body (the adapter owns the body).
/// </summary>
public sealed class V2OutwardClientTests
{
    private const string BaseUrl = "http://localhost:6970";
    private const string ApiKey = "test-api-key";

    private static WhisparrClient ClientFor(FakeHttpMessageHandler handler) => new(new HttpClient(handler));

    // ---- series lookup (GET; idempotent) ----

    [Fact]
    public async Task Series_lookup_binds_the_captured_tpdb_keyed_site_row()
    {
        var handler = FakeHttpMessageHandler.Json(V2Fixtures.SeriesLookup);

        var result = await ClientFor(handler).LookupSeriesAsync(BaseUrl, ApiKey, "tpdb:3417", CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        var site = Assert.Single(result.Value!);
        // The v2 site identity is tvdbId (a TPDB site id), NOT a StashDB id — a wrong field name binds 0 here.
        Assert.Equal(3417, site.TvdbId);
        Assert.Equal("Tushy", site.Title);
        Assert.True(site.Monitored);
        Assert.Equal("all", site.MonitorNewItems);
        Assert.Equal($"{BaseUrl}/api/v3/series/lookup?term=tpdb%3A3417", handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Get, handler.LastRequest.Method);
        Assert.Equal(ApiKey, Assert.Single(handler.LastRequest.Headers.GetValues("X-Api-Key")));
    }

    [Fact]
    public async Task Series_lookup_empty_array_is_ok_and_no_match()
    {
        var result = await ClientFor(FakeHttpMessageHandler.Json("[]")).LookupSeriesAsync(BaseUrl, ApiKey, "tpdb:999", CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.Empty(result.Value!);
    }

    [Fact]
    public async Task Series_lookup_401_classifies_as_bad_key()
    {
        var result = await ClientFor(FakeHttpMessageHandler.Status(HttpStatusCode.Unauthorized)).LookupSeriesAsync(BaseUrl, ApiKey, "tpdb:3417", CancellationToken.None);

        Assert.Equal(WhisparrResultState.BadKey, result.State);
    }

    [Fact]
    public async Task Series_lookup_non_json_body_classifies_as_not_whisparr()
    {
        var result = await ClientFor(FakeHttpMessageHandler.Html(HttpStatusCode.OK)).LookupSeriesAsync(BaseUrl, ApiKey, "tpdb:3417", CancellationToken.None);

        Assert.Equal(WhisparrResultState.NotWhisparr, result.State);
    }

    // ---- create series POST (single-shot; already-added = conflict success) ----

    [Theory]
    [InlineData(HttpStatusCode.Created)]
    [InlineData(HttpStatusCode.OK)]
    public async Task Create_series_2xx_binds_the_created_non_grabbing_row(HttpStatusCode status)
    {
        var handler = new FakeHttpMessageHandler(status, "application/json", V2Fixtures.SeriesAddResponse);

        var result = await ClientFor(handler).CreateSeriesAsync(BaseUrl, ApiKey, "{\"tvdbId\":3417}", CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.Equal(3, result.Value!.Id);
        Assert.Equal(3417, result.Value.TvdbId);
        // A non-grab add lands monitored:false at the series level (addOptions.monitor:none) — the flip is a separate PUT.
        Assert.False(result.Value.Monitored);
        Assert.Equal($"{BaseUrl}/api/v3/series", handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Post, handler.LastRequest.Method);
        Assert.Equal(ApiKey, Assert.Single(handler.LastRequest.Headers.GetValues("X-Api-Key")));
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Create_series_400_series_exists_body_classifies_as_conflict()
    {
        // v2 signals a DUPLICATE add with an HTTP 400 SeriesExistsValidator body — NOT a 409 (the v2 analogue
        // of v3's MovieExistsValidator). The idempotency spine must classify it Conflict, or a re-add reads Unreachable.
        var handler = new FakeHttpMessageHandler(HttpStatusCode.BadRequest, "application/json", V2Fixtures.SeriesExistsError);

        var result = await ClientFor(handler).CreateSeriesAsync(BaseUrl, ApiKey, "{\"tvdbId\":3417}", CancellationToken.None);

        Assert.Equal(WhisparrResultState.Conflict, result.State);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Create_series_409_classifies_as_conflict_and_posts_exactly_once()
    {
        var handler = FakeHttpMessageHandler.Status(HttpStatusCode.Conflict);

        var result = await ClientFor(handler).CreateSeriesAsync(BaseUrl, ApiKey, "{\"tvdbId\":3417}", CancellationToken.None);

        Assert.Equal(WhisparrResultState.Conflict, result.State);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Create_series_400_genuine_bad_body_is_rejected_with_message_not_conflict()
    {
        var errorBody = """[{"propertyName":"Title","errorMessage":"'Title' must not be empty.","errorCode":"NotEmptyValidator"}]""";
        var handler = new FakeHttpMessageHandler(HttpStatusCode.BadRequest, "application/json", errorBody);

        var result = await ClientFor(handler).CreateSeriesAsync(BaseUrl, ApiKey, "{\"tvdbId\":3417}", CancellationToken.None);

        // A genuine bad body is NOT swallowed as Conflict — it surfaces Whisparr's own message as Rejected.
        Assert.Equal(WhisparrResultState.Rejected, result.State);
        Assert.Equal("'Title' must not be empty.", result.Reason);
    }

    [Fact]
    public async Task Create_series_post_is_never_blind_retried_on_transient_fault()
    {
        var handler = FakeHttpMessageHandler.Sequence(
            FakeHttpMessageHandler.Throw(new HttpRequestException("connection reset")),
            FakeHttpMessageHandler.Respond(HttpStatusCode.Created, "application/json", V2Fixtures.SeriesAddResponse));

        var result = await ClientFor(handler).CreateSeriesAsync(BaseUrl, ApiKey, "{}", CancellationToken.None);

        Assert.Equal(WhisparrResultState.Unreachable, result.State);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Create_series_401_classifies_as_bad_key()
    {
        var result = await ClientFor(FakeHttpMessageHandler.Status(HttpStatusCode.Unauthorized)).CreateSeriesAsync(BaseUrl, ApiKey, "{}", CancellationToken.None);

        Assert.Equal(WhisparrResultState.BadKey, result.State);
    }

    // ---- monitor-flip PUT (v2 answers 202 Accepted) ----

    [Fact]
    public async Task Update_series_puts_to_id_path_and_binds_flipped_row()
    {
        // v2's PUT /series/{id} answers HTTP 202 (Accepted), not 200 — both are 2xx and classify Ok.
        var handler = new FakeHttpMessageHandler(HttpStatusCode.Accepted, "application/json", V2Fixtures.SeriesPutResponse);

        var result = await ClientFor(handler).UpdateSeriesAsync(BaseUrl, ApiKey, 3, "{\"monitored\":true}", CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.True(result.Value!.Monitored);
        Assert.Equal($"{BaseUrl}/api/v3/series/3", handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Put, handler.LastRequest.Method);
        Assert.Equal(ApiKey, Assert.Single(handler.LastRequest.Headers.GetValues("X-Api-Key")));
        Assert.Equal(1, handler.CallCount);
    }

    // ---- search command (reuses the shared single-shot /command transport) ----

    [Fact]
    public async Task Episode_search_command_2xx_is_ok_on_command_path_single_shot()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.Created, "application/json", V2Fixtures.EpisodeSearchCommandResponse);

        var result = await ClientFor(handler).SendCommandAsync(BaseUrl, ApiKey, "{\"name\":\"EpisodeSearch\",\"episodeIds\":[1255]}", CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.True(result.Value);
        Assert.Equal($"{BaseUrl}/api/v3/command", handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Post, handler.LastRequest.Method);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Series_search_command_2xx_is_ok()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.Created, "application/json", V2Fixtures.SeriesSearchCommandResponse);

        var result = await ClientFor(handler).SendCommandAsync(BaseUrl, ApiKey, "{\"name\":\"SeriesSearch\",\"seriesId\":3}", CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.True(result.Value);
    }
}
