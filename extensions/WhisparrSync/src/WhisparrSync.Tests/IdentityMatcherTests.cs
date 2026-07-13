using WhisparrSync.Client;
using WhisparrSync.Library;
using WhisparrSync.Matching;

namespace WhisparrSync.Tests;

/// <summary>
/// The MATCH-01 confidence chain and the MATCH-02 never-auto-apply safety valve, proven against
/// fabricated DTOs (pure — no DB, no HTTP, no store): StashDB-UUID exact (case-insensitive; scene
/// foreignId fallback but never a movie-typed tmdbId), translated-path MEDIUM, content-hash no-op,
/// and fuzzy strictly as a needs-review suggestion.
/// </summary>
public sealed class IdentityMatcherTests
{
    private static CoveVideo Cove(
        int id,
        string? title = null,
        DateOnly? date = null,
        string[]? stashIds = null,
        string[]? paths = null,
        CoveFingerprint[]? fingerprints = null)
        => new(id, title, date, stashIds ?? [], paths ?? [], fingerprints ?? []);

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
        // Pitfall 4: itemType == "movie" → foreignId is a tmdbId. Even when it string-equals a Cove
        // "StashId" value, it must NOT match (a movie-typed foreignId is never a StashDB UUID).
        var cove = Cove(10, stashIds: ["681682"]);
        var movie = Movie(1, stashId: null, foreignId: "681682", itemType: "movie");

        var result = Only(IdentityMatcher.Match([cove], [movie]));

        Assert.Equal(MatchOutcome.Unmatched, result.Outcome);
        Assert.Null(result.Leg);
    }

    [Fact]
    public void Path_TranslatedEquality_IsAutoMedium()
    {
        var cove = Cove(10, paths: ["/mnt/tank/media/x.mkv"]);
        var movie = Movie(1, path: "/data/media/x.mkv");
        var translations = new Dictionary<string, string> { ["/data/media"] = "/mnt/tank/media" };

        var result = Only(IdentityMatcher.Match([cove], [movie], translations));

        Assert.Equal(MatchOutcome.Matched, result.Outcome);
        Assert.Equal(MatchedBy.Path, result.Leg);
        Assert.True(result.AutoApplies);
        Assert.Equal(10, result.MatchedVideo!.CoveId);
    }

    [Fact]
    public void Path_WithoutTranslation_DoesNotMatchDifferingRoots()
    {
        var cove = Cove(10, paths: ["/mnt/tank/media/x.mkv"]);
        var movie = Movie(1, path: "/data/media/x.mkv");

        var result = Only(IdentityMatcher.Match([cove], [movie]));

        Assert.Equal(MatchOutcome.Unmatched, result.Outcome);
    }

    [Fact]
    public void ContentHash_IsANoOp_NeverMatchesOnFingerprints()
    {
        // Whisparr exposes no comparable hash, so fingerprints must never gate a cross-system match.
        var cove = Cove(10, title: "Totally Different", stashIds: [], paths: ["/mnt/tank/a.mkv"],
            fingerprints: [new CoveFingerprint("oshash", "deadbeef")]);
        var movie = Movie(1, title: "Something Else Entirely", path: "/data/other/b.mkv");

        var result = Only(IdentityMatcher.Match([cove], [movie]));

        Assert.Equal(MatchOutcome.Unmatched, result.Outcome);
    }

    [Fact]
    public void FuzzyNeverAutoApplies()
    {
        // Title near-match + same year, no StashDB id, no path match → needs-review, never auto.
        var cove = Cove(10, title: "The Great Scene", date: new DateOnly(2021, 5, 1));
        var movie = Movie(1, title: "Great Scene", year: 2021);

        var result = Only(IdentityMatcher.Match([cove], [movie]));

        Assert.Equal(MatchOutcome.NeedsReview, result.Outcome);
        Assert.Equal(MatchedBy.Fuzzy, result.Leg);
        Assert.False(result.AutoApplies);
    }

    [Fact]
    public void Unmatched_WhenNoLegCorresponds()
    {
        var cove = Cove(10, title: "Alpha", date: new DateOnly(2019, 1, 1), stashIds: ["uuid-a"],
            paths: ["/mnt/tank/alpha.mkv"]);
        var movie = Movie(1, title: "Zeta", year: 2024, stashId: "uuid-z", itemType: "scene",
            path: "/data/zeta.mkv");

        var result = Only(IdentityMatcher.Match([cove], [movie]));

        Assert.Equal(MatchOutcome.Unmatched, result.Outcome);
        Assert.Null(result.MatchedVideo);
        Assert.Null(result.Leg);
        Assert.False(result.AutoApplies);
    }

    [Fact]
    public void Fuzzy_DifferentYear_DoesNotMatch()
    {
        // Fuzzy is title+year, not title-only: a same-title different-year pair is unmatched, never a suggestion.
        var cove = Cove(10, title: "The Great Scene", date: new DateOnly(2010, 1, 1));
        var movie = Movie(1, title: "The Great Scene", year: 2021);

        var result = Only(IdentityMatcher.Match([cove], [movie]));

        Assert.Equal(MatchOutcome.Unmatched, result.Outcome);
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

    [Fact]
    public void Path_AmbiguousMultipleCandidates_IsNeedsReviewNotAuto()
    {
        // Two Cove files normalize to the identical path → ambiguous, needs-review, never an arbitrary pick (WR-01).
        var coveA = Cove(10, paths: ["/mnt/tank/media/x.mkv"]);
        var coveB = Cove(11, paths: ["/mnt/tank/media/x.mkv"]);
        var movie = Movie(1, path: "/data/media/x.mkv");
        var translations = new Dictionary<string, string> { ["/data/media"] = "/mnt/tank/media" };

        var result = Only(IdentityMatcher.Match([coveA, coveB], [movie], translations));

        Assert.Equal(MatchOutcome.NeedsReview, result.Outcome);
        Assert.Equal(MatchedBy.Path, result.Leg);
        Assert.False(result.AutoApplies);
    }
}
