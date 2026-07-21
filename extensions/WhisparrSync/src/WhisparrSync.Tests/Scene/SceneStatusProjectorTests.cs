using WhisparrSync.Client;
using WhisparrSync.Library;
using WhisparrSync.Scene;

namespace WhisparrSync.Tests.Scene;

/// <summary>
/// The pure scene-status classification contract. Every test drives the projector with plain in-memory
/// collections (a movie array, an exclusion array, a scene's ids) — the projector takes no client/DB/store
/// handle, so "makes no StashDB call" is STRUCTURAL here, not mocked. Proves exclusion-first precedence, the
/// NotAdded graceful fallback (no id / no match), the monitored-primary split (a downloaded-and-monitored
/// movie stays Monitored, never a separate file state), scene-row foreignId indexing, the card-status +
/// panel-detail projections, and the summary partition with its secondary InLibrary count.
/// </summary>
public sealed class SceneStatusProjectorTests
{
    private static WhisparrMovie Movie(
        int id, string? stashId, bool monitored, bool hasFile,
        string? itemType = "movie", string? foreignId = null,
        string? qualityName = null, bool? cutoffNotMet = null)
        => new(
            Id: id,
            Title: $"Movie {id}",
            Year: 2021,
            StashId: stashId,
            ForeignId: foreignId,
            ItemType: itemType,
            Monitored: monitored,
            HasFile: hasFile,
            MovieFile: hasFile
                ? new WhisparrMovieFile(id, $"/media/{id}.mkv", qualityName is null ? null : new WhisparrFileQuality(new WhisparrQualityName(qualityName)))
                : null,
            QualityCutoffNotMet: cutoffNotMet);

    private static CoveVideo Video(int id, params string[] stashIds)
        => new(id, $"Video {id}", new DateOnly(2021, 1, 1), stashIds, [], [], []);

    // --- Exclusion-first precedence ---

    [Fact]
    public void Excluded_wins_over_a_matching_movie_with_a_file()
    {
        var index = SceneStatusProjector.BuildMovieIndex([Movie(1, "uuid-a", monitored: true, hasFile: true)]);
        var excluded = SceneStatusProjector.BuildExcludedSet([new WhisparrExclusion(1, "uuid-a", "X", 2021)]);

        var state = SceneStatusProjector.Classify(["uuid-a"], index, excluded);

        Assert.Equal(SceneWhisparrState.Excluded, state);
    }

    [Fact]
    public void Excluded_matches_case_insensitively_across_any_of_the_scenes_ids()
    {
        var index = SceneStatusProjector.BuildMovieIndex([]);
        var excluded = SceneStatusProjector.BuildExcludedSet([new WhisparrExclusion(1, "UUID-A", null, null)]);

        var state = SceneStatusProjector.Classify(["other", "uuid-a"], index, excluded);

        Assert.Equal(SceneWhisparrState.Excluded, state);
    }

    // --- NotAdded graceful fallback ---

    [Fact]
    public void No_stash_id_resolves_NotAdded_without_throwing()
    {
        var index = SceneStatusProjector.BuildMovieIndex([Movie(1, "uuid-a", monitored: true, hasFile: false)]);
        var excluded = SceneStatusProjector.BuildExcludedSet([]);

        Assert.Equal(SceneWhisparrState.NotAdded, SceneStatusProjector.Classify([], index, excluded));
        Assert.Equal(SceneWhisparrState.NotAdded, SceneStatusProjector.Classify(["unknown-id"], index, excluded));
    }

    // --- Monitored / Unmonitored split (monitored-primary; hasFile never overrides the state) ---

    [Fact]
    public void Monitored_movie_with_a_file_is_Monitored_not_a_file_state()
    {
        var index = SceneStatusProjector.BuildMovieIndex([Movie(1, "uuid-a", monitored: true, hasFile: true)]);
        Assert.Equal(SceneWhisparrState.Monitored, SceneStatusProjector.Classify(["uuid-a"], index, EmptyExcluded()));
    }

    [Fact]
    public void Monitored_movie_without_file_is_Monitored()
    {
        var index = SceneStatusProjector.BuildMovieIndex([Movie(1, "uuid-a", monitored: true, hasFile: false)]);
        Assert.Equal(SceneWhisparrState.Monitored, SceneStatusProjector.Classify(["uuid-a"], index, EmptyExcluded()));
    }

    [Fact]
    public void Unmonitored_movie_without_file_is_Unmonitored_not_NotAdded()
    {
        var index = SceneStatusProjector.BuildMovieIndex([Movie(1, "uuid-a", monitored: false, hasFile: false)]);
        Assert.Equal(SceneWhisparrState.Unmonitored, SceneStatusProjector.Classify(["uuid-a"], index, EmptyExcluded()));
    }

    [Fact]
    public void Unmonitored_movie_with_a_file_is_still_Unmonitored()
    {
        var index = SceneStatusProjector.BuildMovieIndex([Movie(1, "uuid-a", monitored: false, hasFile: true)]);
        Assert.Equal(SceneWhisparrState.Unmonitored, SceneStatusProjector.Classify(["uuid-a"], index, EmptyExcluded()));
    }

    // --- Card status (primary state + secondary hasFile) ---

    [Fact]
    public void CardStatus_carries_the_monitored_state_and_the_secondary_hasFile()
    {
        var index = SceneStatusProjector.BuildMovieIndex([Movie(1, "uuid-a", monitored: true, hasFile: true)]);

        var card = SceneStatusProjector.CardStatus(["uuid-a"], index, EmptyExcluded());

        Assert.Equal(SceneWhisparrState.Monitored, card.State);
        Assert.True(card.HasFile);
    }

    [Fact]
    public void CardStatus_for_an_absent_scene_is_NotAdded_without_a_file()
    {
        var card = SceneStatusProjector.CardStatus(["unknown"], SceneStatusProjector.BuildMovieIndex([]), EmptyExcluded());

        Assert.Equal(SceneWhisparrState.NotAdded, card.State);
        Assert.False(card.HasFile);
    }

    // --- Scene-row foreignId indexing (field polymorphism) ---

    [Fact]
    public void Scene_row_is_indexed_by_foreign_id_but_movie_row_is_not()
    {
        var index = SceneStatusProjector.BuildMovieIndex(
        [
            Movie(1, stashId: null, monitored: true, hasFile: true, itemType: "scene", foreignId: "scene-uuid"),
            Movie(2, stashId: null, monitored: true, hasFile: true, itemType: "movie", foreignId: "tmdb-123"),
        ]);

        // The scene-typed foreignId is a StashDB id -> indexed and matched (monitored -> Monitored).
        Assert.Equal(SceneWhisparrState.Monitored, SceneStatusProjector.Classify(["scene-uuid"], index, EmptyExcluded()));
        // The movie-typed foreignId is a tmdbId -> NOT indexed, never matched as a StashDB id.
        Assert.Equal(SceneWhisparrState.NotAdded, SceneStatusProjector.Classify(["tmdb-123"], index, EmptyExcluded()));
    }

    // --- Detail projection ---

    [Fact]
    public void Detail_carries_quality_and_cutoff_from_the_matched_movie()
    {
        var index = SceneStatusProjector.BuildMovieIndex(
            [Movie(1, "uuid-a", monitored: true, hasFile: true, qualityName: "WEB-DL 1080p", cutoffNotMet: false)]);

        var detail = SceneStatusProjector.Detail(["uuid-a"], index, EmptyExcluded());

        Assert.Equal(SceneWhisparrState.Monitored, detail.State);
        Assert.True(detail.Added);
        Assert.True(detail.Monitored);
        Assert.True(detail.HasFile);
        Assert.Equal("WEB-DL 1080p", detail.Quality);
        Assert.True(detail.CutoffMet); // qualityCutoffNotMet:false -> cutoff IS met
    }

    [Fact]
    public void Detail_for_an_absent_scene_is_NotAdded_with_unknown_cutoff()
    {
        var detail = SceneStatusProjector.Detail(["unknown"], SceneStatusProjector.BuildMovieIndex([]), EmptyExcluded());

        Assert.Equal(SceneWhisparrState.NotAdded, detail.State);
        Assert.False(detail.Added);
        Assert.Null(detail.Quality);
        Assert.Null(detail.CutoffMet);
    }

    [Fact]
    public void Detail_of_an_excluded_scene_still_reports_its_movie_facts()
    {
        var index = SceneStatusProjector.BuildMovieIndex([Movie(1, "uuid-a", monitored: true, hasFile: true)]);
        var excluded = SceneStatusProjector.BuildExcludedSet([new WhisparrExclusion(1, "uuid-a", null, null)]);

        var detail = SceneStatusProjector.Detail(["uuid-a"], index, excluded);

        Assert.Equal(SceneWhisparrState.Excluded, detail.State);
        Assert.True(detail.Added);
        Assert.True(detail.HasFile);
    }

    // --- Summary partition ---

    [Fact]
    public void SummaryCounts_partitions_by_primary_state_and_counts_InLibrary_separately()
    {
        var index = SceneStatusProjector.BuildMovieIndex(
        [
            Movie(1, "monfile", monitored: true, hasFile: true),
            Movie(2, "mon", monitored: true, hasFile: false),
            Movie(3, "unmon", monitored: false, hasFile: false),
        ]);
        var excluded = SceneStatusProjector.BuildExcludedSet([new WhisparrExclusion(1, "excl", null, null)]);

        var counts = SceneStatusProjector.SummaryCounts(
            [Video(1, "monfile"), Video(2, "mon"), Video(3, "unmon"), Video(4, "excl"), Video(5, "none"), Video(6)],
            index, excluded);

        Assert.Equal(2, counts.Monitored); // "monfile" (downloaded+monitored) + "mon" both stay Monitored
        Assert.Equal(1, counts.Unmonitored);
        Assert.Equal(1, counts.Excluded);
        Assert.Equal(2, counts.NotAdded); // "none" (no matching movie) + the id-less video
        Assert.Equal(6, counts.Total);
        // The four primary buckets partition Total; InLibrary is a secondary, non-partitioning count.
        Assert.Equal(counts.Total, counts.Monitored + counts.Unmonitored + counts.NotAdded + counts.Excluded);
        Assert.Equal(1, counts.InLibrary); // only "monfile" has a file
    }

    private static IReadOnlySet<string> EmptyExcluded() => SceneStatusProjector.BuildExcludedSet([]);
}
