using WhisparrSync.Client;
using WhisparrSync.Library;

namespace WhisparrSync.Matching;

/// <summary>Where a Whisparr movie landed in the reconciliation once the chain ran.</summary>
internal enum MatchOutcome
{
    Matched,
    NeedsReview,
    Unmatched,
}

/// <summary>
/// One Whisparr movie's chain result: the Cove video it resolved to (or none), which leg matched, the
/// bucket it landed in, and whether the link may auto-apply. Fuzzy is the only leg that ever lands in
/// <see cref="MatchOutcome.NeedsReview"/> with <see cref="AutoApplies"/> false (MATCH-02).
/// </summary>
/// <param name="Movie">The Whisparr movie being classified.</param>
/// <param name="MatchedVideo">The Cove video it links to, or null when unmatched.</param>
/// <param name="Leg">The chain leg that produced the link, or null when unmatched.</param>
/// <param name="Outcome">Which reconciliation bucket the movie landed in.</param>
/// <param name="AutoApplies">True only for the high/medium-confidence auto legs (StashId, Path); a fuzzy suggestion is never auto-applied.</param>
internal sealed record MatchResult(
    WhisparrMovie Movie,
    CoveVideo? MatchedVideo,
    MatchedBy? Leg,
    MatchOutcome Outcome,
    bool AutoApplies);

/// <summary>
/// The confidence-ordered identity chain (MATCH-01), pure and I/O-free so it is unit-testable against
/// fabricated DTOs. Legs run in descending confidence and the FIRST that fires wins; an unknown/ambiguous
/// case is the safe default (needs-review or unmatched), never a silent auto-match.
/// </summary>
internal static class IdentityMatcher
{
    /// <summary>
    /// Classifies every Whisparr movie against the Cove library via the ordered chain.
    /// </summary>
    /// <remarks>
    /// <c>pathTranslations</c> maps Whisparr-root → Cove-root prefixes for the path leg (Whisparr and Cove
    /// usually mount the same bytes under different roots); null/empty means no translation, so only an
    /// already-equal path can match.
    /// </remarks>
    public static IReadOnlyList<MatchResult> Match(
        IReadOnlyList<CoveVideo> coveVideos,
        IReadOnlyList<WhisparrMovie> whisparrMovies,
        IReadOnlyDictionary<string, string>? pathTranslations = null)
        => throw new NotImplementedException();
}
