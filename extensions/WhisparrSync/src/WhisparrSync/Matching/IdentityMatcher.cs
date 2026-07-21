using WhisparrSync.Client;
using WhisparrSync.Library;

namespace WhisparrSync.Matching;

/// <summary>Where a Whisparr movie landed in the reconciliation once reconciliation classified it.</summary>
internal enum MatchOutcome
{
    Matched,
    NeedsReview,
    Unmatched,
}

/// <summary>
/// One Whisparr movie's match result: the Cove video it resolved to (or none), which remote id matched, the
/// bucket it landed in, and whether the link may auto-apply. An id shared by two Cove videos is the only
/// case that lands in <see cref="MatchOutcome.NeedsReview"/> with <see cref="AutoApplies"/> false.
/// </summary>
/// <param name="Movie">The Whisparr movie being classified.</param>
/// <param name="MatchedVideo">The Cove video it links to, or null when unmatched.</param>
/// <param name="Leg">Which remote id produced the link, or null when unmatched.</param>
/// <param name="Outcome">Which reconciliation bucket the movie landed in.</param>
/// <param name="AutoApplies">True for an unambiguous id match; false when the same id is shared by more than one Cove video.</param>
internal sealed record MatchResult(
    WhisparrMovie Movie,
    CoveVideo? MatchedVideo,
    MatchedBy? Leg,
    MatchOutcome Outcome,
    bool AutoApplies);

/// <summary>
/// Matches a Whisparr movie to a Cove video by the one remote id both systems already key on — the
/// StashDB UUID for a v3 scene, the ThePornDB id for a v2 scene. Cove owns content identification via its
/// own Identify pipeline; Whisparr only tracks acquisition, so this correlates by id rather than
/// re-deriving identity from a path or title guess. An id shared by two Cove videos is still the safe
/// needs-review default, never a silent arbitrary pick.
/// </summary>
internal static class IdentityMatcher
{
    /// <summary>
    /// Classifies every Whisparr movie against the Cove library via its remote id.
    /// </summary>
    public static IReadOnlyList<MatchResult> Match(
        IReadOnlyList<CoveVideo> coveVideos,
        IReadOnlyList<WhisparrMovie> whisparrMovies)
    {
        var results = new List<MatchResult>(whisparrMovies.Count);
        foreach (var movie in whisparrMovies)
        {
            // Remote id (exact): a v3 movie by StashDB UUID, a v2 episode by ThePornDB id. A single
            // unambiguous hit auto-applies; but if TWO Cove videos carry the same id (Cove does not
            // enforce cross-video id uniqueness), the match is ambiguous — degrade to needs-review so a
            // human disambiguates rather than silently auto-matching an arbitrary first candidate.
            var idMatches = coveVideos.Where(c => StashMatches(c, movie) || TpdbMatches(c, movie)).ToList();
            if (idMatches.Count is 1 or > 1)
            {
                // A v2 scene only ever hits via the TPDB leg (its StashId is null and ItemType is "v2scene"),
                // so the itemType is what distinguishes which key produced the link for the label.
                var leg = string.Equals(movie.ItemType, "v2scene", StringComparison.OrdinalIgnoreCase)
                    ? MatchedBy.Tpdb
                    : MatchedBy.StashId;
                var outcome = idMatches.Count == 1 ? MatchOutcome.Matched : MatchOutcome.NeedsReview;
                results.Add(new MatchResult(movie, idMatches[0], leg, outcome, AutoApplies: idMatches.Count == 1));
                continue;
            }

            results.Add(new MatchResult(movie, MatchedVideo: null, Leg: null, MatchOutcome.Unmatched, AutoApplies: false));
        }

        return results;
    }

    /// <summary>
    /// The StashDB leg: a Whisparr scene's key is its <c>stashId</c> when present, else its
    /// <c>foreignId</c> but ONLY where <c>itemType == "scene"</c> — a movie-typed <c>foreignId</c> is a
    /// tmdbId and must never be compared to a Cove StashDB UUID. Compared case-insensitively.
    /// </summary>
    private static bool StashMatches(CoveVideo cove, WhisparrMovie movie)
    {
        var whisparrStash = !string.IsNullOrEmpty(movie.StashId)
            ? movie.StashId
            : (string.Equals(movie.ItemType, "scene", StringComparison.OrdinalIgnoreCase) ? movie.ForeignId : null);

        return !string.IsNullOrEmpty(whisparrStash)
            && cove.StashIds.Any(s => string.Equals(s, whisparrStash, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The ThePornDB leg: a v2 episode's key is its <c>foreignId</c> (the TPDB id in the tvdbId slot), matched
    /// against the video's TPDB ids. Guarded to <c>itemType == "v2scene"</c> so a v3 movie's tmdbId/StashDB
    /// foreignId is never compared to a TPDB id. Compared case-insensitively.
    /// </summary>
    private static bool TpdbMatches(CoveVideo cove, WhisparrMovie movie)
    {
        return string.Equals(movie.ItemType, "v2scene", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrEmpty(movie.ForeignId)
            && cove.TpdbIds.Any(t => string.Equals(t, movie.ForeignId, StringComparison.OrdinalIgnoreCase));
    }
}
