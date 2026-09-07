using WhisparrSync.Client;
using WhisparrSync.Library;
using WhisparrSync.SceneStatus;

namespace WhisparrSync.Tests.SceneStatus;

/// <summary>
/// The one keying rule, read through both shapes it now serves: the full <see cref="WhisparrMovie"/> row and
/// the narrow <see cref="WhisparrMovieFacts"/> projection a whole-library read binds. Every assertion here is
/// about the two agreeing — a second copy of the rule would let a scene's status pill and the action taken on
/// it disagree about which movie the scene is, and it would agree on every uniform fixture while doing so.
/// </summary>
[Trait("Tier", "L0")]
public sealed class MovieFactsIndexTests
{
    private static WhisparrMovie Wide(
        int id, string? stashId, string? foreignId, string? itemType, bool monitored = false, bool hasFile = false)
        => new(
            Id: id,
            Title: $"Movie {id}",
            Year: 2026,
            StashId: stashId,
            ForeignId: foreignId,
            ItemType: itemType,
            Monitored: monitored,
            HasFile: hasFile,
            MovieFile: null);

    [Fact]
    public void The_narrow_projection_carries_every_member_the_full_row_does()
    {
        var wide = Wide(1, "stash-a", "foreign-a", "scene", monitored: true, hasFile: true);

        var facts = wide.Facts;

        Assert.Equal(wide.StashId, facts.StashId);
        Assert.Equal(wide.ForeignId, facts.ForeignId);
        Assert.Equal(wide.ItemType, facts.ItemType);
        Assert.Equal(wide.Monitored, facts.Monitored);
        Assert.Equal(wide.HasFile, facts.HasFile);
    }

    [Theory]
    // A scene-typed row keys on BOTH ids; every other item type keys on the stash id alone, because a
    // movie-typed foreignId is a tmdbId rather than a StashDB UUID.
    [InlineData("stash-a", "foreign-a", "scene")]
    [InlineData("stash-a", "foreign-a", "movie")]
    [InlineData("stash-a", "foreign-a", "v2scene")]
    [InlineData("stash-a", "foreign-a", null)]
    [InlineData(null, "foreign-a", "scene")]
    [InlineData("stash-a", null, "scene")]
    [InlineData("stash-a", "", "scene")]
    [InlineData("", "foreign-a", "scene")]
    public void A_wide_row_and_a_narrow_row_carrying_the_same_members_key_identically(
        string? stashId, string? foreignId, string? itemType)
    {
        var wide = Wide(1, stashId, foreignId, itemType);

        var fromWide = SceneStatusProjector.BuildMovieIndex([wide]);
        var fromNarrow = SceneStatusProjector.BuildMovieIndex([wide.Facts]);

        Assert.Equal(fromWide.Keys.Order(StringComparer.Ordinal), fromNarrow.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_scene_typed_row_keys_on_both_its_ids_through_the_narrow_shape()
    {
        var index = SceneStatusProjector.BuildMovieIndex(
            [new WhisparrMovieFacts("stash-a", "foreign-a", "scene", Monitored: true, HasFile: false)]);

        Assert.Equal(["foreign-a", "stash-a"], index.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(SceneWhisparrState.Monitored, SceneStatusProjector.Classify(["foreign-a"], index, EmptyExclusions));
        Assert.Equal(SceneWhisparrState.Monitored, SceneStatusProjector.Classify(["stash-a"], index, EmptyExclusions));
    }

    [Fact]
    public void A_non_scene_typed_row_keys_on_its_stash_id_only_through_the_narrow_shape()
    {
        var index = SceneStatusProjector.BuildMovieIndex(
            [new WhisparrMovieFacts("stash-a", "999001", "movie", Monitored: true, HasFile: false)]);

        Assert.Equal(["stash-a"], index.Keys);
        Assert.Equal(SceneWhisparrState.NotAdded, SceneStatusProjector.Classify(["999001"], index, EmptyExclusions));
    }

    [Fact]
    public void Folding_rows_one_at_a_time_builds_the_index_a_whole_set_build_builds()
    {
        WhisparrMovieFacts[] rows =
        [
            new("stash-a", "foreign-a", "scene", Monitored: true, HasFile: false),
            new("stash-b", "999001", "movie", Monitored: false, HasFile: true),
            // A duplicate key, so "first row wins" is exercised rather than assumed to be unreachable.
            new("stash-a", null, "scene", Monitored: false, HasFile: false),
        ];

        var wholeSet = SceneStatusProjector.BuildMovieIndex(rows);
        var folded = new Dictionary<string, WhisparrMovieFacts>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            SceneStatusProjector.IndexMovie(folded, row);
        }

        Assert.Equal(wholeSet.Keys.Order(StringComparer.Ordinal), folded.Keys.Order(StringComparer.Ordinal));
        foreach (var (key, value) in wholeSet)
        {
            Assert.Same(value, folded[key]);
        }
    }

    [Fact]
    public void The_summary_counts_are_the_same_over_full_rows_and_over_their_narrow_projections()
    {
        WhisparrMovie[] rows =
        [
            Wide(1, "stash-a", "foreign-a", "scene", monitored: true, hasFile: true),
            Wide(2, "stash-b", "999001", "movie", monitored: false, hasFile: false),
            Wide(3, null, "foreign-c", "scene", monitored: true, hasFile: false),
        ];
        CoveVideo[] videos =
        [
            new(1, "One", null, ["foreign-a"], [], [], []),
            new(2, "Two", null, ["stash-b"], [], [], []),
            new(3, "Three", null, ["foreign-c"], [], [], []),
            new(4, "Four", null, ["nothing-matches"], [], [], []),
        ];

        var overWideRows = SceneStatusProjector.SummaryCounts(
            videos, SceneStatusProjector.BuildMovieIndex(rows), EmptyExclusions);
        var overNarrowRows = SceneStatusProjector.SummaryCounts(
            videos, SceneStatusProjector.BuildMovieIndex(Array.ConvertAll(rows, row => row.Facts)), EmptyExclusions);

        Assert.Equal(overWideRows, overNarrowRows);
        Assert.Equal(new SceneStatusCounts(2, 1, 1, 0, 1, 4), overWideRows);
    }

    private static readonly IReadOnlySet<string> EmptyExclusions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}
