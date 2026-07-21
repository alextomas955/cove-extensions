using System.Net;
using WhisparrSync.Client;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.Client;

/// <summary>
/// The command-id + status-read transport contract behind a Cove-owned targeted refresh:
/// <see cref="WhisparrClient.SendCommandForIdAsync"/> POSTs to <c>/api/v3/command</c> and returns the QUEUED
/// command id (so a caller can wait for an async refresh to land), and
/// <see cref="WhisparrClient.GetCommandAsync"/> reads that command's status by id from
/// <c>/api/v3/command/{id}</c>. The load-bearing invariants: the id is parsed out of the 2xx body, the body
/// posted is the caller's verbatim JSON (the transport adds no command-name of its own), and a failing POST
/// classifies non-Ok with no false id. Mirrors <see cref="GrabReleaseTransportTests"/>'s fake-handler shape;
/// no live Whisparr.
/// </summary>
public sealed class RefreshCommandTransportTests
{
    private const string BaseUrl = "http://localhost:6969";
    private const string ApiKey = "test-api-key";

    private static WhisparrClient ClientFor(FakeHttpMessageHandler handler) => new(new HttpClient(handler));

    // ---- SendCommandForIdAsync: POST /api/v3/command → queued id ----

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.Created)]
    public async Task Send_command_for_id_2xx_returns_the_queued_id_from_the_command_endpoint(HttpStatusCode status)
    {
        var handler = new FakeHttpMessageHandler(status, "application/json", """{"id":42,"status":"queued"}""");

        var result = await ClientFor(handler).SendCommandForIdAsync(
            BaseUrl, ApiKey, """{"name":"RefreshStudios","studioIds":[1]}""", CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.Equal(42, result.Value);
        Assert.Equal($"{BaseUrl}/api/v3/command", handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Post, handler.LastRequest.Method);
        Assert.Equal(ApiKey, Assert.Single(handler.LastRequest.Headers.GetValues("X-Api-Key")));
    }

    [Fact]
    public async Task Send_command_for_id_posts_the_caller_body_verbatim()
    {
        // The transport holds NO command-name knowledge — the body sent is EXACTLY the caller's JSON.
        const string body = """{"name":"RefreshPerformers","performerIds":[7]}""";
        var handler = FakeHttpMessageHandler.Json("""{"id":9,"status":"queued"}""");

        await ClientFor(handler).SendCommandForIdAsync(BaseUrl, ApiKey, body, CancellationToken.None);

        Assert.Equal(body, handler.LastRequestBody);
    }

    [Fact]
    public async Task Send_command_for_id_is_single_shot_never_blind_retried()
    {
        var handler = FakeHttpMessageHandler.Sequence(
            FakeHttpMessageHandler.Throw(new HttpRequestException("connection reset")),
            FakeHttpMessageHandler.Respond(HttpStatusCode.OK, "application/json", """{"id":1,"status":"queued"}"""));

        var result = await ClientFor(handler).SendCommandForIdAsync(BaseUrl, ApiKey, "{}", CancellationToken.None);

        Assert.Equal(WhisparrResultState.Unreachable, result.State);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Send_command_for_id_non_2xx_classifies_non_ok_with_no_id()
    {
        var result = await ClientFor(FakeHttpMessageHandler.Status(HttpStatusCode.InternalServerError))
            .SendCommandForIdAsync(BaseUrl, ApiKey, """{"name":"RefreshStudios","studioIds":[1]}""", CancellationToken.None);

        Assert.NotEqual(WhisparrResultState.Ok, result.State);
        Assert.Equal(0, result.Value);
    }

    [Fact]
    public async Task Send_command_for_id_401_classifies_as_bad_key()
    {
        var result = await ClientFor(FakeHttpMessageHandler.Status(HttpStatusCode.Unauthorized))
            .SendCommandForIdAsync(BaseUrl, ApiKey, "{}", CancellationToken.None);

        Assert.Equal(WhisparrResultState.BadKey, result.State);
    }

    // ---- GetCommandAsync: GET /api/v3/command/{id} → status ----

    [Fact]
    public async Task Get_command_reads_status_by_id_from_the_command_id_path()
    {
        var handler = FakeHttpMessageHandler.Json("""{"id":42,"status":"completed"}""");

        var result = await ClientFor(handler).GetCommandAsync(BaseUrl, ApiKey, 42, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.Equal(42, result.Value!.Id);
        Assert.Equal("completed", result.Value.Status);
        Assert.Equal($"{BaseUrl}/api/v3/command/42", handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Get, handler.LastRequest.Method);
    }

    [Fact]
    public async Task Get_command_body_missing_status_binds_null_without_throwing()
    {
        var handler = FakeHttpMessageHandler.Json("""{"id":42}""");

        var result = await ClientFor(handler).GetCommandAsync(BaseUrl, ApiKey, 42, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.Equal(42, result.Value!.Id);
        Assert.Null(result.Value.Status);
    }

    [Fact]
    public async Task Get_command_401_classifies_as_bad_key()
    {
        var result = await ClientFor(FakeHttpMessageHandler.Status(HttpStatusCode.Unauthorized))
            .GetCommandAsync(BaseUrl, ApiKey, 42, CancellationToken.None);

        Assert.Equal(WhisparrResultState.BadKey, result.State);
    }
}
