using System.Net;
using System.Text.Json;
using WhisparrSync.Adapters;
using WhisparrSync.Client;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.Adapters;

/// <summary>
/// Which movie rows belong to a studio. The rule is the studio's own StashDB id, matching the performer
/// predicate beside it, so a studio whose title in Whisparr differs from Cove's still attributes its scenes
/// and a studio with no title at all is still attributable.
/// </summary>
/// <remarks>
/// The read shape is asserted here alongside the rule: this predicate reads the whole movie set, and a
/// request count that moved would mean a performance change had ridden in on a semantics change.
/// </remarks>
[Trait("Tier", "L0")]
public sealed class V3AttributionTests
{
    private const string BaseUrl = "http://localhost:6969";
    private const string ApiKey = "test-api-key";
    private const string StudioStashId = "157c9e0d-5f8e-446a-b1c5-dddf3cb5b2d1";

    // The catalogue-carrying v3 adapter, so both consumers of the attribution predicate are reachable from one
    // helper. It IS a V3Adapter, so the attributed-id assertions below are unaffected by the choice.
    private static V3EntityCatalogueAdapter V3(FakeHttpMessageHandler handler)
        => new(new WhisparrClient(new HttpClient(handler)), TimeSpan.Zero);

    private static Func<HttpResponseMessage> Ok(string body)
        => FakeHttpMessageHandler.Respond(HttpStatusCode.OK, "application/json", body);

    private static string StudioRow(string? foreignId, string? title) => JsonSerializer.Serialize(new
    {
        id = 7,
        foreignId,
        title,
        monitored = true,
    });

    private static string Movies(params object[] rows) => JsonSerializer.Serialize(rows);

    private static object Movie(int id, string? studioForeignId, string? studioTitle, bool monitored = true)
        => new { id, studioForeignId, studioTitle, monitored, hasFile = false };

    private static FakeHttpMessageHandler Instance(string studio, string movies)
        => FakeHttpMessageHandler.Sequence(Ok($"[{studio}]"), Ok(movies));

    [Fact]
    public async Task A_row_whose_studio_identity_matches_is_attributed_even_when_the_titles_differ()
    {
        var handler = Instance(
            StudioRow(StudioStashId, "Studio Aurora"),
            Movies(
                Movie(1, StudioStashId, "Aurora Productions"),
                Movie(2, "another-studio-id", "Studio Aurora")));

        var ids = await V3(handler).ListStudioAttributedIdsAsync(
            BaseUrl, ApiKey, StudioStashId, monitoredOnly: false, CancellationToken.None);

        // Row 1 is the gain the switch exists for; row 2 is the mis-attribution the title comparison allowed.
        Assert.Equal([1], ids.Value!);
    }

    // The ruling this encodes: the delta measurement seeded a row carrying a studio title with no studio
    // identity and the instance declined to emit that shape — it attaches no studio title without an identity.
    // So a row with no identity is attributed to no studio, and no union with the title comparison exists.
    [Fact]
    public async Task A_row_carrying_no_studio_identity_is_attributed_to_no_studio()
    {
        var handler = Instance(
            StudioRow(StudioStashId, "Studio Aurora"),
            Movies(
                Movie(1, studioForeignId: null, studioTitle: "Studio Aurora"),
                Movie(2, StudioStashId, "Studio Aurora")));

        var ids = await V3(handler).ListStudioAttributedIdsAsync(
            BaseUrl, ApiKey, StudioStashId, monitoredOnly: false, CancellationToken.None);

        Assert.Equal([2], ids.Value!);
    }

    [Fact]
    public async Task A_studio_with_no_title_still_attributes_its_rows()
    {
        var handler = Instance(
            StudioRow(StudioStashId, title: ""),
            Movies(Movie(1, StudioStashId, studioTitle: "")));

        var ids = await V3(handler).ListStudioAttributedIdsAsync(
            BaseUrl, ApiKey, StudioStashId, monitoredOnly: false, CancellationToken.None);

        Assert.Equal([1], ids.Value!);
    }

    [Fact]
    public async Task The_identity_comparison_is_case_insensitive_like_the_performer_predicate()
    {
        var handler = Instance(
            StudioRow(StudioStashId.ToUpperInvariant(), "Studio Aurora"),
            Movies(Movie(1, StudioStashId.ToLowerInvariant(), "Studio Aurora")));

        var ids = await V3(handler).ListStudioAttributedIdsAsync(
            BaseUrl, ApiKey, StudioStashId, monitoredOnly: false, CancellationToken.None);

        Assert.Equal([1], ids.Value!);
    }

    // By identity, not by count: a search acting on a different 2 rows than a surface counted would satisfy a
    // count assertion and still be the divergence this pins. The per-entity catalogue read no longer computes
    // its set from the whole movie set — Whisparr selects it — so the two are no longer comparable here; that
    // the narrow rows still satisfy this predicate is asserted in V3EntityCatalogueTests.
    [Fact]
    public async Task The_attributed_ids_are_exactly_the_rows_carrying_the_studio_identity()
    {
        var studio = StudioRow(StudioStashId, "Studio Aurora");
        var movies = Movies(
            Movie(1, StudioStashId, "Aurora Productions"),
            Movie(2, StudioStashId, "Studio Aurora"),
            Movie(3, "another-studio-id", "Studio Aurora"),
            Movie(4, studioForeignId: null, studioTitle: "Studio Aurora"));

        var ids = await V3(Instance(studio, movies)).ListStudioAttributedIdsAsync(
            BaseUrl, ApiKey, StudioStashId, monitoredOnly: false, CancellationToken.None);

        Assert.Equal([1, 2], ids.Value!);
    }

    [Fact]
    public async Task The_attribution_read_is_still_the_whole_movie_set()
    {
        var handler = Instance(
            StudioRow(StudioStashId, "Studio Aurora"),
            Movies(Movie(1, StudioStashId, "Studio Aurora")));

        await V3(handler).ListStudioAttributedIdsAsync(
            BaseUrl, ApiKey, StudioStashId, monitoredOnly: false, CancellationToken.None);

        var breakdown = WhisparrRequestCounter.Classify(handler);
        Assert.Equal(1, breakdown.WholeSetMovieReads);
        Assert.Equal(0, breakdown.NarrowMovieReads);
    }

    [Fact]
    public async Task The_movie_row_deserializes_its_studio_identity_off_the_wire()
    {
        var handler = FakeHttpMessageHandler.Json(Movies(Movie(1, StudioStashId, "Studio Aurora")));

        var movies = await new WhisparrClient(new HttpClient(handler))
            .ListMoviesAsync(BaseUrl, ApiKey, CancellationToken.None);

        // The member rides the source-generated serializer with no mapping code, so the value is asserted
        // rather than the naming policy assumed to have done the right thing.
        Assert.Equal(StudioStashId, Assert.Single(movies.Value!).StudioForeignId);
    }
}
