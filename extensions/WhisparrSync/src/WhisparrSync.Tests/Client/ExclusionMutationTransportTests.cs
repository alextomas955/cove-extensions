using System.Net;
using System.Text.Json;
using WhisparrSync.Client;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.Client;

/// <summary>
/// The exclusion mutating transport contract: the exclusion ADD POST targets
/// <c>/api/v3/exclusions</c> with the <c>X-Api-Key</c> header and classifies a duplicate (409 OR a 400
/// "exists" validation body) as the non-error <see cref="WhisparrResultState.Conflict"/> the caller treats
/// as an idempotent success; the exclusion REMOVE DELETE targets <c>/api/v3/exclusions/{id}</c> and treats a
/// 404 (already gone) as success; both are single-shot. Mirrors <see cref="MovieWriteTransportTests"/> — a
/// programmable <see cref="FakeHttpMessageHandler"/> drives every status/body with no live Whisparr, and the
/// outbound URL + method + header are asserted directly.
/// </summary>
public sealed class ExclusionMutationTransportTests
{
    private const string BaseUrl = "http://localhost:6969";
    private const string ApiKey = "test-api-key";
    private const string StashId = "3f2a1b4c-5d6e-4f70-8a9b-0c1d2e3f4a5b";

    private static WhisparrClient ClientFor(FakeHttpMessageHandler handler) => new(new HttpClient(handler));

    // ---- add-exclusion POST ----

    [Theory]
    [InlineData(HttpStatusCode.Created)]
    [InlineData(HttpStatusCode.OK)]
    public async Task Create_exclusion_2xx_targets_exclusions_endpoint_with_api_key(HttpStatusCode status)
    {
        var body = JsonSerializer.Serialize(new { id = 7, foreignId = StashId, movieTitle = "A Scene", movieYear = 2024 });
        var handler = new FakeHttpMessageHandler(status, "application/json", body);

        var result = await ClientFor(handler).CreateExclusionAsync(BaseUrl, ApiKey, "{\"foreignId\":\"x\"}", CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.Equal(7, result.Value!.Id);
        Assert.Equal($"{BaseUrl}/api/v3/exclusions", handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Post, handler.LastRequest.Method);
        Assert.Equal(ApiKey, Assert.Single(handler.LastRequest.Headers.GetValues("X-Api-Key")));
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Create_exclusion_409_classifies_as_conflict_and_posts_exactly_once()
    {
        var handler = FakeHttpMessageHandler.Status(HttpStatusCode.Conflict);

        var result = await ClientFor(handler).CreateExclusionAsync(BaseUrl, ApiKey, "{}", CancellationToken.None);

        Assert.Equal(WhisparrResultState.Conflict, result.State);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Create_exclusion_400_exists_body_classifies_as_conflict()
    {
        // Like the movie add, a duplicate exclusion can surface as an HTTP 400 validation body
        // rather than a 409 — the idempotency spine must read it as the same non-error Conflict.
        var errorBody = JsonSerializer.Serialize(new[]
        {
            new { propertyName = "ForeignId", errorMessage = "This exclusion already exists", errorCode = "ExclusionExistsValidator" },
        });
        var handler = new FakeHttpMessageHandler(HttpStatusCode.BadRequest, "application/json", errorBody);

        var result = await ClientFor(handler).CreateExclusionAsync(BaseUrl, ApiKey, "{}", CancellationToken.None);

        Assert.Equal(WhisparrResultState.Conflict, result.State);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Create_exclusion_genuine_bad_body_is_rejected_with_message()
    {
        var errorBody = JsonSerializer.Serialize(new[]
        {
            new { propertyName = "ForeignId", errorMessage = "'Foreign Id' must not be empty.", errorCode = "NotEmptyValidator" },
        });
        var handler = new FakeHttpMessageHandler(HttpStatusCode.BadRequest, "application/json", errorBody);

        var result = await ClientFor(handler).CreateExclusionAsync(BaseUrl, ApiKey, "{}", CancellationToken.None);

        // A non-2xx with a JSON error body surfaces Whisparr's own message as Rejected (reached-but-declined).
        Assert.Equal(WhisparrResultState.Rejected, result.State);
        Assert.Equal("'Foreign Id' must not be empty.", result.Reason);
    }

    [Fact]
    public async Task Create_exclusion_is_never_blind_retried_on_transient_fault()
    {
        var handler = FakeHttpMessageHandler.Sequence(
            FakeHttpMessageHandler.Throw(new HttpRequestException("connection reset")),
            FakeHttpMessageHandler.Respond(HttpStatusCode.Created, "application/json", "{\"id\":1}"));

        var result = await ClientFor(handler).CreateExclusionAsync(BaseUrl, ApiKey, "{}", CancellationToken.None);

        Assert.Equal(WhisparrResultState.Unreachable, result.State);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Create_exclusion_401_classifies_as_bad_key()
    {
        var result = await ClientFor(FakeHttpMessageHandler.Status(HttpStatusCode.Unauthorized))
            .CreateExclusionAsync(BaseUrl, ApiKey, "{}", CancellationToken.None);

        Assert.Equal(WhisparrResultState.BadKey, result.State);
    }

    // ---- remove-exclusion DELETE ----

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.NoContent)]
    public async Task Delete_exclusion_2xx_targets_id_path_with_api_key(HttpStatusCode status)
    {
        var handler = new FakeHttpMessageHandler(status, "application/json", "{}");

        var result = await ClientFor(handler).DeleteExclusionAsync(BaseUrl, ApiKey, 42, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.True(result.Value);
        Assert.Equal($"{BaseUrl}/api/v3/exclusions/42", handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Delete, handler.LastRequest.Method);
        Assert.Equal(ApiKey, Assert.Single(handler.LastRequest.Headers.GetValues("X-Api-Key")));
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Delete_exclusion_404_is_an_already_gone_success()
    {
        var handler = FakeHttpMessageHandler.Status(HttpStatusCode.NotFound);

        var result = await ClientFor(handler).DeleteExclusionAsync(BaseUrl, ApiKey, 42, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.True(result.Value);
    }

    [Fact]
    public async Task Delete_exclusion_bodiless_200_still_ok_not_notwhisparr()
    {
        // A DELETE can answer with an empty non-JSON body; the bodiless-success path must NOT mis-classify
        // that as NotWhisparr (the Content-Type guard is bypassed for the declared success codes).
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, "text/plain", "");

        var result = await ClientFor(handler).DeleteExclusionAsync(BaseUrl, ApiKey, 5, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
    }

    [Fact]
    public async Task Delete_exclusion_401_classifies_as_bad_key()
    {
        var result = await ClientFor(FakeHttpMessageHandler.Status(HttpStatusCode.Unauthorized))
            .DeleteExclusionAsync(BaseUrl, ApiKey, 42, CancellationToken.None);

        Assert.Equal(WhisparrResultState.BadKey, result.State);
    }

    [Fact]
    public async Task Delete_exclusion_is_never_blind_retried_on_transient_fault()
    {
        var handler = FakeHttpMessageHandler.Sequence(
            FakeHttpMessageHandler.Throw(new HttpRequestException("connection reset")),
            FakeHttpMessageHandler.Respond(HttpStatusCode.OK, "application/json", "{}"));

        var result = await ClientFor(handler).DeleteExclusionAsync(BaseUrl, ApiKey, 42, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Unreachable, result.State);
        Assert.Equal(1, handler.CallCount);
    }
}
