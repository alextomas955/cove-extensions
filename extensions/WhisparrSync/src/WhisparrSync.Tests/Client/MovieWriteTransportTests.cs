using System.Net;
using System.Text.Json;
using WhisparrSync.Client;
using WhisparrSync.Tests.Monitor;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.Client;

/// <summary>
/// The executable classification contract for the v3 movie-write + command transport surface
/// (add, search, monitor flip, idempotency). Mirrors
/// <see cref="MonitorTransportTests"/>: a programmable <see cref="FakeHttpMessageHandler"/> drives every
/// status/body so no live Whisparr is needed, and the outbound URL + method + <c>X-Api-Key</c> are
/// asserted via <see cref="FakeHttpMessageHandler.LastRequest"/>, single-shot POST via
/// <see cref="FakeHttpMessageHandler.CallCount"/>. These are transport-shape tests — they assert the
/// wire (URL + method + classification), not the domain body (the adapter owns the body; tested separately).
/// </summary>
public sealed class MovieWriteTransportTests
{
    private const string BaseUrl = "http://localhost:6969";
    private const string ApiKey = "test-api-key";
    private const string StashId = "157c9e0d-5f8e-446a-b1c5-dddf3cb5b2d1";

    private static WhisparrClient ClientFor(FakeHttpMessageHandler handler) => new(new HttpClient(handler));

    // ---- movie GET by stashId (query param) ----

    [Fact]
    public async Task Movie_get_with_one_row_classifies_as_ok_with_one_movie()
    {
        var body = JsonSerializer.Serialize(new[]
        {
            new { id = 11, title = "A Scene", year = 2024, stashId = StashId, foreignId = StashId, itemType = "scene", monitored = true, hasFile = false },
        });
        var handler = FakeHttpMessageHandler.Json(body);

        var result = await ClientFor(handler).GetMovieByStashIdAsync(BaseUrl, ApiKey, StashId, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        var movie = Assert.Single(result.Value!);
        Assert.Equal(11, movie.Id);
        Assert.Equal(StashId, movie.StashId);
        Assert.True(movie.Monitored);
        Assert.Equal($"{BaseUrl}/api/v3/movie?stashId={StashId}", handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Get, handler.LastRequest.Method);
        Assert.Equal(ApiKey, Assert.Single(handler.LastRequest.Headers.GetValues("X-Api-Key")));
    }

    [Fact]
    public async Task Movie_get_with_empty_array_is_ok_and_not_added()
    {
        var result = await ClientFor(FakeHttpMessageHandler.Json("[]")).GetMovieByStashIdAsync(BaseUrl, ApiKey, StashId, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.Empty(result.Value!);
    }

    [Fact]
    public async Task Movie_get_401_classifies_as_bad_key()
    {
        var result = await ClientFor(FakeHttpMessageHandler.Status(HttpStatusCode.Unauthorized)).GetMovieByStashIdAsync(BaseUrl, ApiKey, StashId, CancellationToken.None);

        Assert.Equal(WhisparrResultState.BadKey, result.State);
    }

    [Fact]
    public async Task Movie_get_non_json_body_classifies_as_not_whisparr()
    {
        var result = await ClientFor(FakeHttpMessageHandler.Html(HttpStatusCode.OK)).GetMovieByStashIdAsync(BaseUrl, ApiKey, StashId, CancellationToken.None);

        Assert.Equal(WhisparrResultState.NotWhisparr, result.State);
    }

    // ---- create movie POST (single-shot; 409 = conflict success) ----

    [Theory]
    [InlineData(HttpStatusCode.Created)]
    [InlineData(HttpStatusCode.OK)]
    public async Task Create_movie_2xx_classifies_as_ok_with_created_row(HttpStatusCode status)
    {
        var body = JsonSerializer.Serialize(new { id = 42, title = "A Scene", stashId = StashId, foreignId = StashId, itemType = "scene", monitored = true, hasFile = false });
        var handler = new FakeHttpMessageHandler(status, "application/json", body);

        var result = await ClientFor(handler).CreateMovieAsync(BaseUrl, ApiKey, "{\"foreignId\":\"x\"}", CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.Equal(42, result.Value!.Id);
        Assert.Equal($"{BaseUrl}/api/v3/movie", handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Post, handler.LastRequest.Method);
        Assert.Equal(ApiKey, Assert.Single(handler.LastRequest.Headers.GetValues("X-Api-Key")));
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Create_movie_409_classifies_as_conflict_and_posts_exactly_once()
    {
        // Idempotency spine: an already-present scene surfaces 409 the caller re-reads, never a
        // transport failure; and the create POST is single-shot, never blind-retried.
        var handler = FakeHttpMessageHandler.Status(HttpStatusCode.Conflict);

        var result = await ClientFor(handler).CreateMovieAsync(BaseUrl, ApiKey, "{\"foreignId\":\"x\"}", CancellationToken.None);

        Assert.Equal(WhisparrResultState.Conflict, result.State);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Create_movie_400_movie_exists_body_classifies_as_conflict()
    {
        // Whisparr Eros answers a DUPLICATE add with HTTP 400 whose validation body names
        // the MovieExistsValidator — NOT a 409. The idempotency spine must classify that as Conflict (success),
        // or a re-add mis-classifies as Unreachable. Body shaped like the real Servarr validation response.
        var errorBody = JsonSerializer.Serialize(new[]
        {
            new
            {
                propertyName = "ForeignId",
                errorMessage = "This movie has already been added",
                errorCode = "MovieExistsValidator",
            },
        });
        var handler = new FakeHttpMessageHandler(HttpStatusCode.BadRequest, "application/json", errorBody);

        var result = await ClientFor(handler).CreateMovieAsync(BaseUrl, ApiKey, "{\"foreignId\":\"x\"}", CancellationToken.None);

        Assert.Equal(WhisparrResultState.Conflict, result.State);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Create_movie_400_genuine_bad_body_is_rejected_with_message_not_conflict()
    {
        // A real bad-request (NOT the already-added case) must NOT be swallowed as Conflict — only the
        // MovieExists body is the idempotency signal. Every other 400 carrying a JSON error body is a
        // Rejected result surfacing Whisparr's OWN message (reached-but-declined), never a silent Conflict.
        var errorBody = JsonSerializer.Serialize(new[]
        {
            new { propertyName = "Title", errorMessage = "'Title' must not be empty.", errorCode = "NotEmptyValidator" },
        });
        var handler = new FakeHttpMessageHandler(HttpStatusCode.BadRequest, "application/json", errorBody);

        var result = await ClientFor(handler).CreateMovieAsync(BaseUrl, ApiKey, "{\"foreignId\":\"x\"}", CancellationToken.None);

        Assert.Equal(WhisparrResultState.Rejected, result.State);
        Assert.Equal("'Title' must not be empty.", result.Reason);
    }

    [Fact]
    public async Task Create_movie_post_is_never_blind_retried_on_transient_fault()
    {
        // A transient transport fault on the create POST must NOT re-issue (a retried create could
        // duplicate). Mirrors the shipped studio/webhook single-shot guarantee.
        var handler = FakeHttpMessageHandler.Sequence(
            FakeHttpMessageHandler.Throw(new HttpRequestException("connection reset")),
            FakeHttpMessageHandler.Respond(HttpStatusCode.Created, "application/json", "{\"id\":1}"));

        var result = await ClientFor(handler).CreateMovieAsync(BaseUrl, ApiKey, "{}", CancellationToken.None);

        Assert.Equal(WhisparrResultState.Unreachable, result.State);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Create_movie_401_classifies_as_bad_key()
    {
        var result = await ClientFor(FakeHttpMessageHandler.Status(HttpStatusCode.Unauthorized)).CreateMovieAsync(BaseUrl, ApiKey, "{}", CancellationToken.None);

        Assert.Equal(WhisparrResultState.BadKey, result.State);
    }

    // ---- PUT flip ----

    [Fact]
    public async Task Update_movie_puts_to_id_path_with_api_key()
    {
        var body = JsonSerializer.Serialize(new { id = 7, title = "A Scene", stashId = StashId, foreignId = StashId, itemType = "scene", monitored = true, hasFile = false });
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, "application/json", body);

        var result = await ClientFor(handler).UpdateMovieAsync(BaseUrl, ApiKey, 7, "{\"monitored\":true}", CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.True(result.Value!.Monitored);
        Assert.Equal($"{BaseUrl}/api/v3/movie/7", handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Put, handler.LastRequest.Method);
        Assert.Equal(ApiKey, Assert.Single(handler.LastRequest.Headers.GetValues("X-Api-Key")));
        Assert.Equal(1, handler.CallCount);
    }

    // ---- MoviesSearch command POST (single-shot) ----

    [Fact]
    public async Task Send_command_2xx_json_classifies_as_ok_on_command_path()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.Created, "application/json", "{\"id\":99,\"name\":\"MoviesSearch\"}");

        var result = await ClientFor(handler).SendCommandAsync(BaseUrl, ApiKey, "{\"name\":\"MoviesSearch\",\"movieIds\":[11]}", CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.True(result.Value);
        Assert.Equal($"{BaseUrl}/api/v3/command", handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Post, handler.LastRequest.Method);
        Assert.Equal(ApiKey, Assert.Single(handler.LastRequest.Headers.GetValues("X-Api-Key")));
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Send_command_is_never_blind_retried_on_transient_fault()
    {
        // A MoviesSearch is single-shot — a retried command must never fan out a second search.
        var handler = FakeHttpMessageHandler.Sequence(
            FakeHttpMessageHandler.Throw(new HttpRequestException("connection reset")),
            FakeHttpMessageHandler.Respond(HttpStatusCode.Created, "application/json", "{\"id\":1}"));

        var result = await ClientFor(handler).SendCommandAsync(BaseUrl, ApiKey, "{}", CancellationToken.None);

        Assert.Equal(WhisparrResultState.Unreachable, result.State);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Send_command_401_classifies_as_bad_key()
    {
        var result = await ClientFor(FakeHttpMessageHandler.Status(HttpStatusCode.Unauthorized)).SendCommandAsync(BaseUrl, ApiKey, "{}", CancellationToken.None);

        Assert.Equal(WhisparrResultState.BadKey, result.State);
    }
}
