using System.Net;
using System.Text.Json;
using WhisparrSync.Adapters;
using WhisparrSync.Client;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.Client;

/// <summary>
/// The read-only exclusion-list transport contract: the exclusion list GET targets
/// <c>/api/v3/exclusions</c> with the <c>X-Api-Key</c> header (an empty array is a valid Ok, a 401 is
/// BadKey), the release GET targets <c>/api/v3/release?movieId=</c>, and both v2 adapter paths defer
/// gracefully (<see cref="WhisparrResultState.VersionMismatch"/>) with NO wire call — mirroring the
/// Studio/performer/tag transport test shape (fake handler, URL + header assertions).
/// </summary>
public sealed class ExclusionsTransportTests
{
    private const string BaseUrl = "http://localhost:6969";
    private const string ApiKey = "test-api-key";

    private static WhisparrClient ClientFor(FakeHttpMessageHandler handler) => new(new HttpClient(handler));

    // --- Exclusions transport (client) ---

    [Fact]
    public async Task GetExclusions_TargetsExclusionsEndpoint_WithApiKey()
    {
        var handler = FakeHttpMessageHandler.Json("[]");

        await ClientFor(handler).GetExclusionsAsync(BaseUrl, ApiKey, CancellationToken.None);

        Assert.Equal($"{BaseUrl}/api/v3/exclusions", handler.LastRequest!.RequestUri!.ToString());
        Assert.True(handler.LastRequest.Headers.TryGetValues("X-Api-Key", out var values));
        Assert.Equal(ApiKey, Assert.Single(values!));
    }

    [Fact]
    public async Task GetExclusions_EmptyArray_IsOk()
    {
        var result = await ClientFor(FakeHttpMessageHandler.Json("[]"))
            .GetExclusionsAsync(BaseUrl, ApiKey, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.Empty(result.Value!);
    }

    [Fact]
    public async Task GetExclusions_DeserializesForeignId()
    {
        var body = JsonSerializer.Serialize(new[]
        {
            new { id = 1, foreignId = "scene-uuid-a", movieTitle = "Excluded Scene", movieYear = 2021 },
        });

        var result = await ClientFor(FakeHttpMessageHandler.Json(body))
            .GetExclusionsAsync(BaseUrl, ApiKey, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        var row = Assert.Single(result.Value!);
        Assert.Equal(1, row.Id);
        Assert.Equal("scene-uuid-a", row.ForeignId);
        Assert.Equal("Excluded Scene", row.Title);
        Assert.Equal(2021, row.Year);
    }

    [Fact]
    public async Task GetExclusions_Unauthorized_ClassifiesBadKey()
    {
        var result = await ClientFor(FakeHttpMessageHandler.Status(HttpStatusCode.Unauthorized))
            .GetExclusionsAsync(BaseUrl, ApiKey, CancellationToken.None);

        Assert.Equal(WhisparrResultState.BadKey, result.State);
    }

    [Fact]
    public async Task GetExclusions_HtmlBody_ClassifiesNotWhisparr()
    {
        var result = await ClientFor(FakeHttpMessageHandler.Html(HttpStatusCode.BadGateway))
            .GetExclusionsAsync(BaseUrl, ApiKey, CancellationToken.None);

        Assert.Equal(WhisparrResultState.NotWhisparr, result.State);
    }

    // --- Releases transport (client) ---

    [Fact]
    public async Task GetReleases_TargetsReleaseEndpoint_WithMovieIdQuery_AndApiKey()
    {
        var handler = FakeHttpMessageHandler.Json("[]");

        await ClientFor(handler).GetReleasesAsync(BaseUrl, ApiKey, 42, CancellationToken.None);

        Assert.EndsWith("/api/v3/release", handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Contains("movieId=42", handler.LastRequest.RequestUri!.Query);
        Assert.True(handler.LastRequest.Headers.TryGetValues("X-Api-Key", out var values));
        Assert.Equal(ApiKey, Assert.Single(values!));
    }

    [Fact]
    public async Task GetReleases_EmptyArray_IsOk()
    {
        var result = await ClientFor(FakeHttpMessageHandler.Json("[]"))
            .GetReleasesAsync(BaseUrl, ApiKey, 42, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.Empty(result.Value!);
    }

    [Fact]
    public async Task GetReleases_DeserializesReleaseRows()
    {
        var body = JsonSerializer.Serialize(new[]
        {
            new { guid = "indexer-1", title = "Scene.2021.1080p.WEB-DL" },
            new { guid = "indexer-2", title = "Scene.2021.2160p.WEB-DL" },
        });

        var result = await ClientFor(FakeHttpMessageHandler.Json(body))
            .GetReleasesAsync(BaseUrl, ApiKey, 42, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.Equal(2, result.Value!.Length);
        Assert.Equal("indexer-1", result.Value![0].Guid);
    }

    // --- V3 adapter delegates to the client at the same endpoints ---

    [Fact]
    public async Task V3Adapter_ListExclusions_DelegatesToClient()
    {
        var handler = FakeHttpMessageHandler.Json("""[{"id":9,"foreignId":"uuid-x","movieTitle":"X","movieYear":2020}]""");
        var adapter = new V3Adapter(ClientFor(handler));

        var result = await adapter.ListExclusionsAsync(BaseUrl, ApiKey, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.Equal("uuid-x", Assert.Single(result.Value!).ForeignId);
        Assert.EndsWith("/api/v3/exclusions", handler.LastRequest!.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task V3Adapter_GetReleases_DelegatesToClient()
    {
        var handler = FakeHttpMessageHandler.Json("[]");
        var adapter = new V3Adapter(ClientFor(handler));

        var result = await adapter.GetReleasesAsync(BaseUrl, ApiKey, 7, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.EndsWith("/api/v3/release", handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Contains("movieId=7", handler.LastRequest.RequestUri!.Query);
    }

    // --- V2 adapter defers both, with NO wire call ---

    [Fact]
    public async Task V2Adapter_ListExclusions_DefersVersionMismatch_NoWireCall()
    {
        var handler = FakeHttpMessageHandler.Json("[]");
        var adapter = new V2Adapter(ClientFor(handler));

        var result = await adapter.ListExclusionsAsync(BaseUrl, ApiKey, CancellationToken.None);

        Assert.Equal(WhisparrResultState.VersionMismatch, result.State);
        Assert.Equal("v2", result.DetectedVersion);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task V2Adapter_GetReleases_DefersVersionMismatch_NoWireCall()
    {
        var handler = FakeHttpMessageHandler.Json("[]");
        var adapter = new V2Adapter(ClientFor(handler));

        var result = await adapter.GetReleasesAsync(BaseUrl, ApiKey, 7, CancellationToken.None);

        Assert.Equal(WhisparrResultState.VersionMismatch, result.State);
        Assert.Equal("v2", result.DetectedVersion);
        Assert.Equal(0, handler.CallCount);
    }
}
