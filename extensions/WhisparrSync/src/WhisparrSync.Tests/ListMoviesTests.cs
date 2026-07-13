using System.Net;
using System.Text.Json;
using WhisparrSync.Client;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests;

/// <summary>
/// The MATCH-01 data-source contract for <c>WhisparrClient.ListMoviesAsync</c> (GET /api/v3/movie): a
/// live-shaped movie array deserializes into typed <c>WhisparrMovie</c> records, and every Phase-1
/// classify-not-throw guard is inherited unchanged (HTML/502 → NotWhisparr, 401 → BadKey). The full set
/// is returned unpaged (issue #218); the stashId index is built client-side downstream, so there is no
/// server-side stashId query to assert here.
/// </summary>
public sealed class ListMoviesTests
{
    private const string BaseUrl = "http://localhost:6969";
    private const string ApiKey = "test-api-key";

    private static WhisparrClient ClientFor(FakeHttpMessageHandler handler) => new(new HttpClient(handler));

    [Fact]
    public async Task ListMovies_DeserializesMovieArray()
    {
        var body = JsonSerializer.Serialize(new[]
        {
            new
            {
                id = 1,
                title = "X",
                year = 2020,
                stashId = "uuid-a",
                foreignId = "uuid-a",
                itemType = "scene",
                monitored = true,
                hasFile = true,
                movieFile = new { id = 9, path = "/data/media/x.mkv" },
            },
        });
        var result = await ClientFor(FakeHttpMessageHandler.Json(body)).ListMoviesAsync(BaseUrl, ApiKey, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        var movie = Assert.Single(result.Value!);
        Assert.Equal("uuid-a", movie.StashId);
        Assert.Equal("scene", movie.ItemType);
        Assert.Equal("/data/media/x.mkv", movie.MovieFile!.Path);
    }

    [Fact]
    public async Task ListMovies_EmptyArray()
    {
        // The live e2e instance shape: 0 movies, no StashDB source configured.
        var result = await ClientFor(FakeHttpMessageHandler.Json("[]")).ListMoviesAsync(BaseUrl, ApiKey, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.Empty(result.Value!);
    }

    [Fact]
    public async Task ListMovies_HtmlBodyClassifiesNotWhisparr()
    {
        var result = await ClientFor(FakeHttpMessageHandler.Html(HttpStatusCode.BadGateway)).ListMoviesAsync(BaseUrl, ApiKey, CancellationToken.None);

        Assert.Equal(WhisparrResultState.NotWhisparr, result.State);
    }

    [Fact]
    public async Task ListMovies_UnauthorizedClassifiesBadKey()
    {
        var result = await ClientFor(FakeHttpMessageHandler.Status(HttpStatusCode.Unauthorized)).ListMoviesAsync(BaseUrl, ApiKey, CancellationToken.None);

        Assert.Equal(WhisparrResultState.BadKey, result.State);
    }

    [Fact]
    public async Task ListMovies_TargetsMovieEndpoint()
    {
        var handler = FakeHttpMessageHandler.Json("[]");
        await ClientFor(handler).ListMoviesAsync(BaseUrl, ApiKey, CancellationToken.None);

        Assert.EndsWith("/api/v3/movie", handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.True(handler.LastRequest.Headers.TryGetValues("X-Api-Key", out var values));
        Assert.Equal(ApiKey, Assert.Single(values!));
    }
}
