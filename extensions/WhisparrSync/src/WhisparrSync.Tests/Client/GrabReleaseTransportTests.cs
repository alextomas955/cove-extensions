using System.Net;
using System.Text.Json;
using WhisparrSync.Client;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.Client;

/// <summary>
/// The grab transport contract: the interactive grab POSTs to <c>/api/v3/release</c> with the
/// <c>X-Api-Key</c> header, is single-shot (a grab is never blind-retried, so a transient fault can never
/// double-grab), and a 2xx classifies Ok. Also pins the enriched <see cref="WhisparrRelease"/> deserialize
/// the interactive picker needs (quality name, size, indexer, seeders, age, guid, indexerId) — a partial row
/// still binds. Mirrors <see cref="MovieWriteTransportTests"/>'s fake-handler shape.
/// </summary>
public sealed class GrabReleaseTransportTests
{
    private const string BaseUrl = "http://localhost:6969";
    private const string ApiKey = "test-api-key";

    private static WhisparrClient ClientFor(FakeHttpMessageHandler handler) => new(new HttpClient(handler));

    // ---- grab-release POST (single-shot) ----

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.Created)]
    public async Task Grab_release_2xx_targets_release_endpoint_with_api_key(HttpStatusCode status)
    {
        var handler = new FakeHttpMessageHandler(status, "application/json", "{}");

        var result = await ClientFor(handler).GrabReleaseAsync(
            BaseUrl, ApiKey, "{\"guid\":\"indexer-1\",\"indexerId\":3}", CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.True(result.Value);
        Assert.Equal($"{BaseUrl}/api/v3/release", handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Post, handler.LastRequest.Method);
        Assert.Equal(ApiKey, Assert.Single(handler.LastRequest.Headers.GetValues("X-Api-Key")));
        Assert.Contains("\"guid\":\"indexer-1\"", handler.LastRequestBody);
        Assert.Contains("\"indexerId\":3", handler.LastRequestBody);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Grab_release_is_never_blind_retried_on_transient_fault()
    {
        var handler = FakeHttpMessageHandler.Sequence(
            FakeHttpMessageHandler.Throw(new HttpRequestException("connection reset")),
            FakeHttpMessageHandler.Respond(HttpStatusCode.OK, "application/json", "{}"));

        var result = await ClientFor(handler).GrabReleaseAsync(BaseUrl, ApiKey, "{}", CancellationToken.None);

        Assert.Equal(WhisparrResultState.Unreachable, result.State);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Grab_release_401_classifies_as_bad_key()
    {
        var result = await ClientFor(FakeHttpMessageHandler.Status(HttpStatusCode.Unauthorized))
            .GrabReleaseAsync(BaseUrl, ApiKey, "{}", CancellationToken.None);

        Assert.Equal(WhisparrResultState.BadKey, result.State);
    }

    // ---- enriched release rows the interactive picker renders + grabs ----

    [Fact]
    public async Task Release_get_deserializes_the_enriched_display_and_grab_fields()
    {
        var body = JsonSerializer.Serialize(new[]
        {
            new
            {
                guid = "indexer-abc",
                title = "Scene.2021.2160p.WEB-DL",
                quality = new { quality = new { name = "WEB-DL 2160p" } },
                size = 8_589_934_592L,
                indexer = "My Indexer",
                indexerId = 3,
                seeders = 42,
                age = 5,
            },
        });

        var result = await ClientFor(FakeHttpMessageHandler.Json(body))
            .GetReleasesAsync(BaseUrl, ApiKey, 42, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        var row = Assert.Single(result.Value!);
        Assert.Equal("indexer-abc", row.Guid);
        Assert.Equal("WEB-DL 2160p", row.Quality!.Quality!.Name);
        Assert.Equal(8_589_934_592L, row.Size);
        Assert.Equal("My Indexer", row.Indexer);
        Assert.Equal(3, row.IndexerId);
        Assert.Equal(42, row.Seeders);
        Assert.Equal(5, row.Age);
    }

    [Fact]
    public async Task Release_get_partial_row_still_binds_with_null_enriched_fields()
    {
        // A row carrying only guid+title (an older/partial indexer response) must still bind, with every
        // new display/grab field degrading to null rather than throwing.
        var body = JsonSerializer.Serialize(new[]
        {
            new { guid = "indexer-x", title = "Scene.2021.1080p" },
        });

        var result = await ClientFor(FakeHttpMessageHandler.Json(body))
            .GetReleasesAsync(BaseUrl, ApiKey, 7, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        var row = Assert.Single(result.Value!);
        Assert.Equal("indexer-x", row.Guid);
        Assert.Null(row.Quality);
        Assert.Null(row.Size);
        Assert.Null(row.Indexer);
        Assert.Null(row.IndexerId);
        Assert.Null(row.Seeders);
        Assert.Null(row.Age);
    }
}
