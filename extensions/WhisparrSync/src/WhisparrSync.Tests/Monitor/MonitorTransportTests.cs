using System.Net;
using System.Text.Json;
using WhisparrSync.Client;
using WhisparrSync.Tests.Client;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.Monitor;

/// <summary>
/// The executable classification contract for the v3 monitor transport surface (studio shapes,
/// performer 404/500-as-absent, 409-as-success + single-shot POST). Mirrors
/// <see cref="WhisparrClientClassifyTests"/>: a programmable <see cref="FakeHttpMessageHandler"/> drives
/// every status/body so no live Whisparr is needed, and outbound URL + method + <c>X-Api-Key</c> are
/// asserted via <see cref="FakeHttpMessageHandler.LastRequest"/>, single-shot POST via
/// <see cref="FakeHttpMessageHandler.CallCount"/>.
/// </summary>
public sealed class MonitorTransportTests
{
    private const string BaseUrl = "http://localhost:6969";
    private const string ApiKey = "test-api-key";
    private const string StashId = "157c9e0d-5f8e-446a-b1c5-dddf3cb5b2d1";

    private static WhisparrClient ClientFor(FakeHttpMessageHandler handler) => new(new HttpClient(handler));

    // ---- studio GET by stashId (query param) ----

    [Fact]
    public async Task Studio_get_with_one_row_classifies_as_ok_with_one_studio()
    {
        var body = JsonSerializer.Serialize(new[]
        {
            new { id = 1, foreignId = StashId, title = "IEnergy", monitored = false, qualityProfileId = 4, rootFolderPath = "/data/media", tags = Array.Empty<int>() },
        });
        var handler = FakeHttpMessageHandler.Json(body);

        var result = await ClientFor(handler).GetStudioByStashIdAsync(BaseUrl, ApiKey, StashId, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        var studio = Assert.Single(result.Value!);
        Assert.Equal(1, studio.Id);
        Assert.Equal(StashId, studio.ForeignId);
        Assert.False(studio.Monitored);
        Assert.Equal($"{BaseUrl}/api/v3/studio?stashId={StashId}", handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Get, handler.LastRequest.Method);
        Assert.Equal(ApiKey, Assert.Single(handler.LastRequest.Headers.GetValues("X-Api-Key")));
    }

    [Fact]
    public async Task Studio_get_with_empty_array_is_ok_and_not_added()
    {
        var result = await ClientFor(FakeHttpMessageHandler.Json("[]")).GetStudioByStashIdAsync(BaseUrl, ApiKey, StashId, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.Empty(result.Value!);
    }

    // ---- performer GET by stashId (path param); 404/500 = absent ----

    [Fact]
    public async Task Performer_get_200_classifies_as_ok_present()
    {
        var body = JsonSerializer.Serialize(new { id = 7, foreignId = StashId, fullName = "Miyu Aizawa", monitored = true, qualityProfileId = 4, rootFolderPath = "/data/media", tags = new[] { 3 } });
        var handler = FakeHttpMessageHandler.Json(body);

        var result = await ClientFor(handler).GetPerformerByStashIdAsync(BaseUrl, ApiKey, StashId, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.Equal(7, result.Value!.Id);
        Assert.Equal("Miyu Aizawa", result.Value.FullName);
        Assert.Equal($"{BaseUrl}/api/v3/performer/{StashId}", handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Get, handler.LastRequest.Method);
        Assert.Equal(ApiKey, Assert.Single(handler.LastRequest.Headers.GetValues("X-Api-Key")));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)] // older builds / stasharr's "HTTP 500 = not-found"
    [InlineData(HttpStatusCode.NotFound)] // this v3.3.x build answers 404 for a not-added performer
    public async Task Performer_get_500_or_404_classifies_as_absent_not_unreachable(HttpStatusCode status)
    {
        var result = await ClientFor(FakeHttpMessageHandler.Status(status)).GetPerformerByStashIdAsync(BaseUrl, ApiKey, StashId, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Absent, result.State);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task Performer_get_absent_via_problem_json_body_still_classifies_as_absent()
    {
        // The live v3 answers an absent performer with an RFC-9110 problem body carried as application/json;
        // the status-line-keyed absent check must classify it Absent, not attempt to bind it as a performer.
        var problem = JsonSerializer.Serialize(new { title = "Not Found", status = 404 });
        var handler = new FakeHttpMessageHandler(HttpStatusCode.NotFound, "application/json", problem);

        var result = await ClientFor(handler).GetPerformerByStashIdAsync(BaseUrl, ApiKey, StashId, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Absent, result.State);
    }

    [Fact]
    public async Task Performer_get_401_still_classifies_as_bad_key()
    {
        // The absent widening must not swallow an auth failure — 401 stays BadKey exactly as before.
        var result = await ClientFor(FakeHttpMessageHandler.Status(HttpStatusCode.Unauthorized)).GetPerformerByStashIdAsync(BaseUrl, ApiKey, StashId, CancellationToken.None);

        Assert.Equal(WhisparrResultState.BadKey, result.State);
    }

    // ---- create POST (single-shot; 409 = conflict success) ----

    [Theory]
    [InlineData(HttpStatusCode.Created)]
    [InlineData(HttpStatusCode.OK)]
    public async Task Create_studio_2xx_classifies_as_ok_with_created_row(HttpStatusCode status)
    {
        var body = JsonSerializer.Serialize(new { id = 42, foreignId = StashId, title = "IEnergy", monitored = false, qualityProfileId = 4, rootFolderPath = "/data/media", tags = Array.Empty<int>() });
        var handler = new FakeHttpMessageHandler(status, "application/json", body);

        var result = await ClientFor(handler).CreateStudioAsync(BaseUrl, ApiKey, "{\"foreignId\":\"x\"}", CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.Equal(42, result.Value!.Id);
        Assert.Equal($"{BaseUrl}/api/v3/studio", handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Post, handler.LastRequest.Method);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Create_studio_409_classifies_as_conflict_and_posts_exactly_once()
    {
        var handler = FakeHttpMessageHandler.Status(HttpStatusCode.Conflict);

        var result = await ClientFor(handler).CreateStudioAsync(BaseUrl, ApiKey, "{\"foreignId\":\"x\"}", CancellationToken.None);

        Assert.Equal(WhisparrResultState.Conflict, result.State);
        Assert.Equal(1, handler.CallCount); // single-shot, never blind-retried into a duplicate
    }

    [Fact]
    public async Task Create_performer_409_classifies_as_conflict()
    {
        var result = await ClientFor(FakeHttpMessageHandler.Status(HttpStatusCode.Conflict)).CreatePerformerAsync(BaseUrl, ApiKey, "{\"foreignId\":\"x\"}", CancellationToken.None);

        Assert.Equal(WhisparrResultState.Conflict, result.State);
    }

    [Fact]
    public async Task Create_studio_post_is_never_blind_retried_on_transient_fault()
    {
        // A transient transport fault on the create POST must NOT re-issue (a retried create could
        // duplicate). Mirrors the shipped webhook single-shot guarantee.
        var handler = FakeHttpMessageHandler.Sequence(
            FakeHttpMessageHandler.Throw(new HttpRequestException("connection reset")),
            FakeHttpMessageHandler.Respond(HttpStatusCode.Created, "application/json", "{\"id\":1}"));

        var result = await ClientFor(handler).CreateStudioAsync(BaseUrl, ApiKey, "{}", CancellationToken.None);

        Assert.Equal(WhisparrResultState.Unreachable, result.State);
        Assert.Equal(1, handler.CallCount);
    }

    // ---- PUT flip ----

    [Fact]
    public async Task Update_performer_puts_to_id_path_with_api_key()
    {
        var body = JsonSerializer.Serialize(new { id = 7, foreignId = StashId, fullName = "Miyu Aizawa", monitored = true, qualityProfileId = 4, rootFolderPath = "/data/media", tags = Array.Empty<int>() });
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, "application/json", body);

        var result = await ClientFor(handler).UpdatePerformerAsync(BaseUrl, ApiKey, 7, "{\"monitored\":true}", CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.True(result.Value!.Monitored);
        Assert.Equal($"{BaseUrl}/api/v3/performer/7", handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Put, handler.LastRequest.Method);
        Assert.Equal(ApiKey, Assert.Single(handler.LastRequest.Headers.GetValues("X-Api-Key")));
    }

    // ---- tag lookup / create ----

    [Fact]
    public async Task List_tags_deserializes_rows()
    {
        var body = JsonSerializer.Serialize(new[]
        {
            new { id = 1, label = "cove" },
            new { id = 2, label = "manual" },
        });
        var handler = FakeHttpMessageHandler.Json(body);

        var result = await ClientFor(handler).ListTagsAsync(BaseUrl, ApiKey, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.Equal(2, result.Value!.Length);
        Assert.Equal("cove", result.Value[0].Label);
        Assert.Equal($"{BaseUrl}/api/v3/tag", handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Get, handler.LastRequest.Method);
    }

    [Fact]
    public async Task Create_tag_201_returns_created_tag_with_api_key_on_expected_path()
    {
        var body = JsonSerializer.Serialize(new { id = 9, label = "cove" });
        var handler = new FakeHttpMessageHandler(HttpStatusCode.Created, "application/json", body);

        var result = await ClientFor(handler).CreateTagAsync(BaseUrl, ApiKey, "{\"label\":\"cove\"}", CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.Equal(9, result.Value!.Id);
        Assert.Equal("cove", result.Value.Label);
        Assert.Equal($"{BaseUrl}/api/v3/tag", handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Post, handler.LastRequest.Method);
        Assert.Equal(ApiKey, Assert.Single(handler.LastRequest.Headers.GetValues("X-Api-Key")));
        Assert.Equal(1, handler.CallCount);
    }
}
