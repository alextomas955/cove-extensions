using WhisparrSync.Client;
using WhisparrSync.Library;
using WhisparrSync.Matching;

namespace WhisparrSync.Tests.Matching;

/// <summary>
/// The single id-only match leg and its ambiguity-safe needs-review behavior, proven against
/// fabricated DTOs (pure — no DB, no HTTP, no store): StashDB-UUID exact (case-insensitive; scene
/// foreignId fallback but never a movie-typed tmdbId) and ThePornDB id exact for v2 rows; two Cove
/// videos sharing the same id degrade to needs-review, never an arbitrary auto-match.
/// </summary>
public sealed class IdentityMatcherTests
{
    private static CoveVideo Cove(
        int id,
        string? title = null,
        DateOnly? date = null,
        string[]? stashIds = null,
        string[]? tpdbIds = null,
        string[]? paths = null,
        CoveFingerprint[]? fingerprints = null)
        => new(id, title, date, stashIds ?? [], tpdbIds ?? [], paths ?? [], fingerprints ?? []);

    private static WhisparrMovie Movie(
        int id,
        string? title = null,
        int? year = null,
        string? stashId = null,
        string? foreignId = null,
        string? itemType = null,
        string? path = null)
        => new(id, title, year, stashId, foreignId, itemType, Monitored: false, HasFile: path is not null,
            path is null ? null : new WhisparrMovieFile(1, path));

    private static MatchResult Only(IReadOnlyList<MatchResult> results) => Assert.Single(results);

    [Fact]
    public void StashId_ExactMatch_IsAutoHigh()
    {
        var cove = Cove(10, stashIds: ["UUID-A"]);
        var movie = Movie(1, stashId: "uuid-a", itemType: "scene");

        var result = Only(IdentityMatcher.Match([cove], [movie]));

        Assert.Equal(MatchOutcome.Matched, result.Outcome);
        Assert.Equal(MatchedBy.StashId, result.Leg);
        Assert.True(result.AutoApplies);
        Assert.Equal(10, result.MatchedVideo!.CoveId);
    }

    [Fact]
    public void StashId_SceneForeignIdFallback_Matches()
    {
        var cove = Cove(10, stashIds: ["uuid-a"]);
        var movie = Movie(1, stashId: null, foreignId: "uuid-a", itemType: "scene");

        var result = Only(IdentityMatcher.Match([cove], [movie]));

        Assert.Equal(MatchOutcome.Matched, result.Outcome);
        Assert.Equal(MatchedBy.StashId, result.Leg);
    }

    [Fact]
    public void StashId_MovieForeignIdIsNeverComparedToACoveUuid()
    {
        // itemType == "movie" → foreignId is a tmdbId. Even when it string-equals a Cove
        // "StashId" value, it must NOT match (a movie-typed foreignId is never a StashDB UUID).
        var cove = Cove(10, stashIds: ["681682"]);
        var movie = Movie(1, stashId: null, foreignId: "681682", itemType: "movie");

        var result = Only(IdentityMatcher.Match([cove], [movie]));

        Assert.Equal(MatchOutcome.Unmatched, result.Outcome);
        Assert.Null(result.Leg);
    }

    [Fact]
    public void Unmatched_WhenNoLegCorresponds()
    {
        var cove = Cove(10, title: "Alpha", date: new DateOnly(2019, 1, 1), stashIds: ["uuid-a"]);
        var movie = Movie(1, title: "Zeta", year: 2024, stashId: "uuid-z", itemType: "scene");

        var result = Only(IdentityMatcher.Match([cove], [movie]));

        Assert.Equal(MatchOutcome.Unmatched, result.Outcome);
        Assert.Null(result.MatchedVideo);
        Assert.Null(result.Leg);
        Assert.False(result.AutoApplies);
    }

    [Fact]
    public void StashId_AmbiguousMultipleCandidates_IsNeedsReviewNotAuto()
    {
        // Two Cove videos share the same StashDB id (Cove does not enforce cross-video uniqueness).
        // The exact leg must NOT silently auto-match an arbitrary first candidate — it degrades to
        // needs-review so a human disambiguates (WR-01).
        var coveA = Cove(10, stashIds: ["uuid-a"]);
        var coveB = Cove(11, stashIds: ["uuid-a"]);
        var movie = Movie(1, stashId: "uuid-a", itemType: "scene");

        var result = Only(IdentityMatcher.Match([coveA, coveB], [movie]));

        Assert.Equal(MatchOutcome.NeedsReview, result.Outcome);
        Assert.Equal(MatchedBy.StashId, result.Leg);
        Assert.False(result.AutoApplies);
    }

    [Fact]
    public void StashId_SingleCandidate_StillAutoApplies()
    {
        // Regression guard for WR-01: the ambiguity check must not break the unambiguous single-hit auto path.
        var cove = Cove(10, stashIds: ["uuid-a"]);
        var movie = Movie(1, stashId: "uuid-a", itemType: "scene");

        var result = Only(IdentityMatcher.Match([cove], [movie]));

        Assert.Equal(MatchOutcome.Matched, result.Outcome);
        Assert.True(result.AutoApplies);
    }

    // --- v2-shaped rows: StashId=null + ItemType="v2scene" => the StashDB leg MUST no-op ---

    [Fact]
    public void V2Row_StashDbLegNoOps_EvenWhenForeignIdEqualsACoveStashId()
    {
        // A v2 synthesized row carries the TPDB scene id in ForeignId with ItemType="v2scene" (never
        // "scene"). Even when a Cove video's StashId string-equals that TPDB id, the StashDB leg must NOT
        // fire — a TPDB id is never a StashDB UUID. No other leg corresponds => Unmatched.
        var cove = Cove(10, stashIds: ["1010276"]);
        var movie = Movie(1, stashId: null, foreignId: "1010276", itemType: "v2scene");

        var result = Only(IdentityMatcher.Match([cove], [movie]));

        Assert.Equal(MatchOutcome.Unmatched, result.Outcome);
        Assert.Null(result.Leg);
    }

    // --- v2 ThePornDB id leg: a v2scene resolves HIGH by TPDB id, keyed strictly on TpdbIds ---

    [Fact]
    public void V2Row_TpdbIdExactMatch_IsAutoHigh()
    {
        var cove = Cove(10, tpdbIds: ["1010705"]);
        var movie = Movie(1, stashId: null, foreignId: "1010705", itemType: "v2scene");

        var result = Only(IdentityMatcher.Match([cove], [movie]));

        Assert.Equal(MatchOutcome.Matched, result.Outcome);
        Assert.Equal(MatchedBy.Tpdb, result.Leg);
        Assert.True(result.AutoApplies);
        Assert.Equal(10, result.MatchedVideo!.CoveId);
    }

    [Fact]
    public void V2Row_TpdbLegIsKeyedOnTpdbIds_NotStashIds()
    {
        // A video carrying only the same value as a StashId (no TpdbIds) must NOT satisfy the TPDB leg —
        // the id leg is keyed on the TPDB endpoint, so this falls through to unmatched.
        var cove = Cove(10, stashIds: ["1010705"]);
        var movie = Movie(1, stashId: null, foreignId: "1010705", itemType: "v2scene");

        var result = Only(IdentityMatcher.Match([cove], [movie]));

        Assert.Equal(MatchOutcome.Unmatched, result.Outcome);
        Assert.Null(result.Leg);
    }

    [Fact]
    public void V3Row_StillMatchesByStashId_NotTpdb()
    {
        // Regression guard: a v3 scene with a UUID identity keeps its StashId label even though the id leg
        // now also covers TPDB.
        var cove = Cove(10, stashIds: ["3f9c1e2a-0000-4a1b-9c3d-abcdef012345"]);
        var movie = Movie(1, stashId: "3f9c1e2a-0000-4a1b-9c3d-abcdef012345", itemType: "scene");

        var result = Only(IdentityMatcher.Match([cove], [movie]));

        Assert.Equal(MatchOutcome.Matched, result.Outcome);
        Assert.Equal(MatchedBy.StashId, result.Leg);
        Assert.True(result.AutoApplies);
    }

    [Fact]
    public void V2Row_TpdbIdMatchesNoVideo_FallsThrough()
    {
        // No video's TpdbIds carries the episode's foreignId → the id leg no-ops, so the movie is unmatched.
        var cove = Cove(10, tpdbIds: ["9999999"]);
        var movie = Movie(1, stashId: null, foreignId: "1010705", itemType: "v2scene");

        var result = Only(IdentityMatcher.Match([cove], [movie]));

        Assert.Equal(MatchOutcome.Unmatched, result.Outcome);
        Assert.Null(result.Leg);
    }
}
