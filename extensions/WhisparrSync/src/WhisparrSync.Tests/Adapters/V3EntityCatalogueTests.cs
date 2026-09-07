using System.Net;
using System.Text.Json;
using WhisparrSync.Adapters;
using WhisparrSync.Client;
using WhisparrSync.Contracts;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.Adapters;

/// <summary>
/// The v3 per-entity catalogue read: what it costs, and what it answers when the row set is empty.
/// </summary>
/// <remarks>
/// The costs are asserted as EXACT request counts against the <c>1 + ceil(k/chunk)</c> formula rather than as
/// "more than one", because the failure this guards against is a loop that stops at a chunk count instead of at
/// the end of the id list — which is a row cap wearing a different costume and would still pass a
/// greater-than assertion.
/// </remarks>
[Trait("Tier", "L0")]
public sealed class V3EntityCatalogueTests
{
    private const string BaseUrl = "http://localhost:6969";
    private const string ApiKey = "test-api-key";
    private const string StudioId = "a0000000-0000-4000-8000-000000000001";
    private const string PerformerId = "c0000000-0000-4000-8000-000000000001";

    // The grain the adapter chunks its hydration at, restated here so the boundary cases below read as
    // boundaries. A mismatch with the shipped constant shows up as a failing count, not as a silent pass.
    private const int Chunk = 1000;

    private static V3EntityCatalogueAdapter Adapter(FakeHttpMessageHandler handler)
        => new(new WhisparrClient(new HttpClient(handler)), TimeSpan.Zero);

    private static HttpResponseMessage Json(string body)
        => FakeHttpMessageHandler.Respond(HttpStatusCode.OK, "application/json", body)();

    private static object Row(int id) => new
    {
        id,
        foreignId = $"row-{id}",
        stashId = $"row-{id}",
        studioForeignId = StudioId,
        performerForeignIds = new[] { PerformerId },
        hasFile = false,
        monitored = true,
    };

    /// <summary>
    /// A v3 instance answering the two catalogue routes, the entity existence route, and the by-id hydration
    /// POST. Anything else falls through to a whole-set movie body, so a path that reached for the whole set
    /// is caught by the counter rather than by a missing response.
    /// </summary>
    private static FakeHttpMessageHandler Instance(
        int[] siblingIds, HttpStatusCode existence = HttpStatusCode.OK)
        => FakeHttpMessageHandler.Json("[]").Also(request =>
        {
            if (request.Url.Contains("/movie/listby", StringComparison.OrdinalIgnoreCase))
            {
                return Json(JsonSerializer.Serialize(siblingIds));
            }

            if (request.Url.Contains("/api/v3/studio/", StringComparison.Ordinal)
                || request.Url.Contains("/api/v3/performer/", StringComparison.Ordinal))
            {
                return existence == HttpStatusCode.OK
                    ? Json(JsonSerializer.Serialize(new { id = 7, foreignId = StudioId, title = "Studio Aurora" }))
                    : FakeHttpMessageHandler.Respond(existence, "application/json", "{\"message\":\"NotFound\"}")();
            }

            if (request.Url.EndsWith("/api/v3/movie/bulk", StringComparison.Ordinal))
            {
                var asked = JsonSerializer.Deserialize<int[]>(request.Body!)!;
                return Json(JsonSerializer.Serialize(asked.Select(Row)));
            }

            return null;
        });

    private static async Task<(WhisparrResult<EntityCatalogue> Result, WhisparrRequestBreakdown Breakdown, FakeHttpMessageHandler Handler)>
        Read(int[] siblingIds, EntityKind kind = EntityKind.Studio, HttpStatusCode existence = HttpStatusCode.OK)
    {
        var handler = Instance(siblingIds, existence);
        var result = await Adapter(handler).ListEntityMoviesAsync(
            BaseUrl, ApiKey, kind, kind == EntityKind.Performer ? PerformerId : StudioId, CancellationToken.None);
        return (result, WhisparrRequestCounter.Classify(handler), handler);
    }

    private static int[] Ids(int count) => [.. Enumerable.Range(1, count)];

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(999)]
    public async Task A_studio_holding_rows_costs_one_sibling_read_plus_one_hydration_and_no_existence_read(int k)
    {
        var (result, breakdown, handler) = await Read(Ids(k));

        Assert.Equal(EntityCatalogueState.Known, result.Value!.State);
        Assert.Equal(k, result.Value.Movies.Length);
        Assert.Equal(0, breakdown.WholeSetMovieReads);
        Assert.Equal(2, breakdown.Total);
        Assert.DoesNotContain(handler.Requests, r => r.Url.Contains("/api/v3/studio/", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(Chunk, 1)]
    [InlineData(Chunk + 1, 2)]
    [InlineData((2 * Chunk) + 1, 3)]
    public async Task The_hydration_loop_is_driven_by_the_end_of_the_id_list_not_by_a_chunk_count(int k, int expectedChunks)
    {
        var (result, breakdown, handler) = await Read(Ids(k));

        var hydrations = handler.Requests.Count(r => r.Url.EndsWith("/api/v3/movie/bulk", StringComparison.Ordinal));
        Assert.Equal(expectedChunks, hydrations);
        Assert.Equal(k, result.Value!.Movies.Length);
        Assert.Equal(0, breakdown.WholeSetMovieReads);
        Assert.Equal(1 + expectedChunks, breakdown.Total);
    }

    [Fact]
    public async Task A_duplicated_id_in_the_siblings_answer_is_hydrated_once()
    {
        var (result, _, handler) = await Read([4, 4, 9, 4]);

        var asked = JsonSerializer.Deserialize<int[]>(
            handler.Requests.Single(r => r.Url.EndsWith("/api/v3/movie/bulk", StringComparison.Ordinal)).Body!)!;
        Assert.Equal([4, 9], asked);
        Assert.Equal(2, result.Value!.Movies.Length);
    }

    [Fact]
    public async Task An_empty_sibling_answer_with_a_404_existence_read_is_the_entity_unknown_state()
    {
        var (result, breakdown, handler) = await Read([], existence: HttpStatusCode.NotFound);

        Assert.Equal(EntityCatalogueState.EntityUnknown, result.Value!.State);
        Assert.Empty(result.Value.Movies);
        Assert.Equal(0, breakdown.WholeSetMovieReads);
        Assert.Equal(2, breakdown.Total);
        Assert.Contains(handler.Requests, r => r.Url.Contains("/api/v3/studio/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_empty_sibling_answer_with_a_200_existence_read_is_known_and_holding_nothing()
    {
        var (result, breakdown, _) = await Read([]);

        Assert.Equal(EntityCatalogueState.Known, result.Value!.State);
        Assert.Empty(result.Value.Movies);
        Assert.Equal(0, breakdown.WholeSetMovieReads);
        Assert.Equal(2, breakdown.Total);
    }

    [Fact]
    public async Task An_empty_performer_catalogue_discriminates_through_the_performer_route()
    {
        var (unknown, _, _) = await Read([], EntityKind.Performer, HttpStatusCode.NotFound);
        var (known, _, handler) = await Read([], EntityKind.Performer);

        Assert.Equal(EntityCatalogueState.EntityUnknown, unknown.Value!.State);
        Assert.Equal(EntityCatalogueState.Known, known.Value!.State);
        Assert.Contains(handler.Requests, r => r.Url.Contains("/api/v3/performer/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_non_ok_sibling_read_propagates_and_never_falls_back_to_the_whole_set()
    {
        var handler = FakeHttpMessageHandler.Html(HttpStatusCode.BadGateway);

        var result = await Adapter(handler).ListEntityMoviesAsync(
            BaseUrl, ApiKey, EntityKind.Studio, StudioId, CancellationToken.None);

        Assert.False(result.IsOk);
        Assert.Equal(WhisparrResultState.NotWhisparr, result.State);
        Assert.Equal(0, WhisparrRequestCounter.Classify(handler).WholeSetMovieReads);
    }

    [Fact]
    public async Task A_non_ok_hydration_read_propagates_and_never_falls_back_to_the_whole_set()
    {
        var handler = FakeHttpMessageHandler.Json("[]").Also(request =>
            request.Url.Contains("/movie/listby", StringComparison.OrdinalIgnoreCase)
                ? Json("[1,2,3]")
                : request.Url.EndsWith("/api/v3/movie/bulk", StringComparison.Ordinal)
                    ? FakeHttpMessageHandler.Respond(HttpStatusCode.BadGateway, "text/html", "<html></html>")()
                    : null);

        var result = await Adapter(handler).ListEntityMoviesAsync(
            BaseUrl, ApiKey, EntityKind.Studio, StudioId, CancellationToken.None);

        Assert.False(result.IsOk);
        Assert.Equal(0, WhisparrRequestCounter.Classify(handler).WholeSetMovieReads);
    }

    [Fact]
    public async Task A_tag_enumerates_nothing_on_v3_with_no_wire_call()
    {
        var handler = FakeHttpMessageHandler.Json("[]");

        var result = await Adapter(handler).ListEntityMoviesAsync(
            BaseUrl, ApiKey, EntityKind.Tag, StudioId, CancellationToken.None);

        Assert.Equal(EntityCatalogueState.NotEnumerableOnThisVersion, result.Value!.State);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task The_narrow_rows_are_a_subset_of_what_the_whole_set_predicate_would_have_selected()
    {
        // The sibling names three rows; the whole-set attribution predicate over the same instance selects the
        // rows whose studioForeignId is the studio's. Every narrow row must satisfy it — a narrow read that
        // returned a row the predicate rejects would be selecting on something else.
        var (result, _, _) = await Read([1, 2, 3]);

        Assert.All(
            result.Value!.Movies,
            movie => Assert.Equal(StudioId, movie.StudioForeignId));
    }
}
