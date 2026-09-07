using System.Net;
using WhisparrSync.Adapters;
using WhisparrSync.Client;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.Adapters;

/// <summary>
/// The per-scene movie resolve: what the by-id read answers, what the index rule then accepts, and what a
/// failed read must not be reported as. The resolve asks Whisparr for rows on ITS predicate (the row's own
/// foreignId) and keeps only what the whole-set index would have keyed, so the two cases that matter are a
/// row the filter returns but the index rejects, and a row both accept.
/// </summary>
[Trait("Tier", "L0")]
public sealed class V3SceneLookupTests
{
    private const string BaseUrl = "http://localhost:6969";
    private const string ApiKey = "test-api-key";
    private const string SceneStashId = "3f2a1b4c-5d6e-4f70-8a9b-0c1d2e3f4a5b";

    private static V3Adapter V3(FakeHttpMessageHandler handler)
        => new(new WhisparrClient(new HttpClient(handler)), TimeSpan.Zero);

    private static Func<HttpResponseMessage> Ok(string body)
        => FakeHttpMessageHandler.Respond(HttpStatusCode.OK, "application/json", body);

    // A movie-typed row whose foreignId equals the asked id — the shape Whisparr's filter answers but the
    // index never keys, because a movie-typed foreignId is a tmdbId rather than a StashDB UUID.
    private static string ForeignOnlyMovieRow(string foreignId)
        => $$"""
        [{"id": 41, "title": "A Movie", "stashId": "a-different-stash-id", "foreignId": "{{foreignId}}",
          "itemType": "movie", "monitored": true, "hasFile": false}]
        """;

    private static string SceneRow(int id, string foreignId)
        => $$"""
        [{"id": {{id}}, "title": "A Scene", "stashId": null, "foreignId": "{{foreignId}}",
          "itemType": "scene", "monitored": true, "hasFile": false}]
        """;

    [Fact]
    public async Task Resolves_a_matching_row_by_its_identity()
    {
        var handler = FakeHttpMessageHandler.Sequence(Ok(SceneRow(77, SceneStashId)));

        var result = await V3(handler).FindSceneMovieAsync(BaseUrl, ApiKey, [SceneStashId], default);

        Assert.True(result.IsOk);
        Assert.Equal(77, result.Value!.Id);
        Assert.Single(handler.Requests);
        Assert.Contains($"stashId={SceneStashId}", handler.Requests[0].Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rejects_a_row_the_whole_set_index_would_not_have_keyed()
    {
        var handler = FakeHttpMessageHandler.Sequence(Ok(ForeignOnlyMovieRow(SceneStashId)));

        var result = await V3(handler).FindSceneMovieAsync(BaseUrl, ApiKey, [SceneStashId], default);

        Assert.True(result.IsOk);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task Asks_each_id_in_order_and_stops_at_the_first_hit()
    {
        var handler = FakeHttpMessageHandler.Sequence(Ok("[]"), Ok(SceneRow(88, "second-id")));

        var result = await V3(handler).FindSceneMovieAsync(BaseUrl, ApiKey, ["first-id", "second-id", "third-id"], default);

        Assert.Equal(88, result.Value!.Id);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("stashId=first-id", handler.Requests[0].Url, StringComparison.Ordinal);
        Assert.Contains("stashId=second-id", handler.Requests[1].Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_scene_with_no_usable_id_resolves_to_no_movie_without_asking()
    {
        var handler = FakeHttpMessageHandler.Sequence(Ok(SceneRow(99, SceneStashId)));

        var result = await V3(handler).FindSceneMovieAsync(BaseUrl, ApiKey, ["", "   "], default);

        Assert.True(result.IsOk);
        Assert.Null(result.Value);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task A_failed_read_propagates_rather_than_reading_as_not_added()
    {
        var handler = FakeHttpMessageHandler.Sequence(
            FakeHttpMessageHandler.Respond(HttpStatusCode.Unauthorized, "application/json", "{}"));

        var result = await V3(handler).FindSceneMovieAsync(BaseUrl, ApiKey, [SceneStashId], default);

        Assert.False(result.IsOk);
        Assert.Equal(WhisparrResultState.BadKey, result.State);
    }

    [Fact]
    public async Task Never_reads_the_whole_movie_set()
    {
        var handler = FakeHttpMessageHandler.Sequence(Ok("[]"));

        await V3(handler).FindSceneMovieAsync(BaseUrl, ApiKey, [SceneStashId], default);

        var breakdown = WhisparrRequestCounter.Classify(handler);
        Assert.Equal(0, breakdown.WholeSetMovieReads);
        Assert.Equal(1, breakdown.NarrowMovieReads);
    }
}
