using WhisparrSync.Client;
using WhisparrSync.Library;
using WhisparrSync.Matching;

namespace WhisparrSync.Tests.Matching;

/// <summary>
/// The two id-only match legs, proven against fabricated DTOs (pure — no DB, no HTTP, no store): StashDB-UUID
/// exact (case-insensitive; scene <c>foreignId</c> fallback but never a movie-typed tmdbId) and ThePornDB id
/// exact for v2 rows. Each leg is keyed strictly on its own id family, which is what stops a TPDB id being read
/// as a StashDB UUID or the reverse.
/// </summary>
[Trait("Tier", "L0")]
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

    [Fact]
    public void StashId_ExactMatch_IsCaseInsensitive()
    {
        Assert.True(IdentityMatcher.StashMatches(
            Cove(10, stashIds: ["UUID-A"]), Movie(1, stashId: "uuid-a", itemType: "scene")));
    }

    [Fact]
    public void StashId_SceneForeignIdFallback_Matches()
    {
        Assert.True(IdentityMatcher.StashMatches(
            Cove(10, stashIds: ["uuid-a"]), Movie(1, stashId: null, foreignId: "uuid-a", itemType: "scene")));
    }

    [Fact]
    public void StashId_MovieForeignIdIsNeverComparedToACoveUuid()
    {
        // itemType == "movie" → foreignId is a tmdbId. Even when it string-equals a Cove "StashId" value, it must
        // NOT match: a movie-typed foreignId is never a StashDB UUID.
        Assert.False(IdentityMatcher.StashMatches(
            Cove(10, stashIds: ["681682"]), Movie(1, stashId: null, foreignId: "681682", itemType: "movie")));
    }

    [Fact]
    public void NeitherLegCorresponds_WhenNoIdIsShared()
    {
        var cove = Cove(10, title: "Alpha", date: new DateOnly(2019, 1, 1), stashIds: ["uuid-a"]);
        var movie = Movie(1, title: "Zeta", year: 2024, stashId: "uuid-z", itemType: "scene");

        Assert.False(IdentityMatcher.StashMatches(cove, movie));
        Assert.False(IdentityMatcher.TpdbMatches(cove, movie));
    }

    [Fact]
    public void StashId_MatchesEveryVideoCarryingTheId_NotOnlyOne()
    {
        // Cove does not enforce cross-video id uniqueness. The predicate answers per video, so a caller resolving
        // one arbitrary candidate is the caller's bug — the acquisition worklist confirms every candidate.
        var movie = Movie(1, stashId: "uuid-a", itemType: "scene");

        Assert.True(IdentityMatcher.StashMatches(Cove(10, stashIds: ["uuid-a"]), movie));
        Assert.True(IdentityMatcher.StashMatches(Cove(11, stashIds: ["uuid-a"]), movie));
    }

    // --- v2-shaped rows: StashId=null + ItemType="v2scene" => the StashDB leg MUST no-op ---

    [Fact]
    public void V2Row_StashDbLegNoOps_EvenWhenForeignIdEqualsACoveStashId()
    {
        // A v2 synthesized row carries the TPDB scene id in ForeignId with ItemType="v2scene" (never "scene").
        // Even when a Cove video's StashId string-equals that TPDB id, the StashDB leg must NOT fire.
        Assert.False(IdentityMatcher.StashMatches(
            Cove(10, stashIds: ["1010276"]), Movie(1, stashId: null, foreignId: "1010276", itemType: "v2scene")));
    }

    // --- v2 ThePornDB id leg: a v2scene resolves by TPDB id, keyed strictly on TpdbIds ---

    [Fact]
    public void V2Row_TpdbIdExactMatch_Matches()
    {
        Assert.True(IdentityMatcher.TpdbMatches(
            Cove(10, tpdbIds: ["1010705"]), Movie(1, stashId: null, foreignId: "1010705", itemType: "v2scene")));
    }

    [Fact]
    public void V2Row_TpdbLegIsKeyedOnTpdbIds_NotStashIds()
    {
        // A video carrying only the same value as a StashId (no TpdbIds) must NOT satisfy the TPDB leg — the leg
        // is keyed on the TPDB endpoint.
        Assert.False(IdentityMatcher.TpdbMatches(
            Cove(10, stashIds: ["1010705"]), Movie(1, stashId: null, foreignId: "1010705", itemType: "v2scene")));
    }

    [Fact]
    public void V3Row_MatchesByStashId_AndNeverByTheTpdbLeg()
    {
        var cove = Cove(10, stashIds: ["3f9c1e2a-0000-4a1b-9c3d-abcdef012345"]);
        var movie = Movie(1, stashId: "3f9c1e2a-0000-4a1b-9c3d-abcdef012345", itemType: "scene");

        Assert.True(IdentityMatcher.StashMatches(cove, movie));
        Assert.False(IdentityMatcher.TpdbMatches(cove, movie));
    }

    [Fact]
    public void V2Row_TpdbIdMatchesNoVideo_FallsThrough()
    {
        Assert.False(IdentityMatcher.TpdbMatches(
            Cove(10, tpdbIds: ["9999999"]), Movie(1, stashId: null, foreignId: "1010705", itemType: "v2scene")));
    }
}
