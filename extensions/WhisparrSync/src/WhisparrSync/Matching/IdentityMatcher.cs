using WhisparrSync.Client;
using WhisparrSync.Library;

namespace WhisparrSync.Matching;

/// <summary>
/// The one rule for deciding that a Whisparr movie and a Cove video are the same scene: the remote id both
/// systems already key on — the StashDB UUID for a v3 scene, the ThePornDB id for a v2 scene. Cove owns content
/// identification via its own Identify pipeline; Whisparr only tracks acquisition, so this correlates by id
/// rather than re-deriving identity from a path or title guess.
/// </summary>
internal static class IdentityMatcher
{
    /// <summary>
    /// The StashDB leg: a Whisparr scene's key is its <c>stashId</c> when present, else its
    /// <c>foreignId</c> but ONLY where <c>itemType == "scene"</c> — a movie-typed <c>foreignId</c> is a
    /// tmdbId and must never be compared to a Cove StashDB UUID. Compared case-insensitively.
    /// </summary>
    /// <remarks>
    /// The acquisition worklist's id leg confirms every dictionary candidate through this predicate. A second copy
    /// of the keying rule would let the worklist and the scene badge disagree about which movie a scene is — the
    /// hazard <c>SceneStatusProjector.FindMovie</c> records.
    /// </remarks>
    internal static bool StashMatches(CoveVideo cove, WhisparrMovie movie)
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
    /// <remarks>Visible to the acquisition worklist's id leg for the same reason as <see cref="StashMatches"/>.</remarks>
    internal static bool TpdbMatches(CoveVideo cove, WhisparrMovie movie)
    {
        return string.Equals(movie.ItemType, "v2scene", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrEmpty(movie.ForeignId)
            && cove.TpdbIds.Any(t => string.Equals(t, movie.ForeignId, StringComparison.OrdinalIgnoreCase));
    }
}
