using WhisparrSync.Client;
using WhisparrSync.Library;

namespace WhisparrSync.Scene;

/// <summary>
/// The pure, I/O-free derivation of a scene's Whisparr status. It takes ONLY pre-fetched
/// collections — the Whisparr movie index, the exclusion set, and a scene's own StashDB ids — and computes
/// the 4-state model + panel detail + summary counts. It holds NO client, DB, or store handle, so a StashDB
/// call is not merely avoided but STRUCTURALLY impossible: the derivation reuses the already-
/// built reconciliation data and never reaches back out. Mirrors <c>IdentityMatcher</c>'s StashDB keying (a
/// movie's <c>stashId</c>, or its <c>foreignId</c> only for a <c>scene</c>-typed row) so preview and status
/// agree on identity, adding no new key.
/// </summary>
internal static class SceneStatusProjector
{
    /// <summary>
    /// Indexes the Whisparr movie set by every StashDB-comparable id a scene could match: a movie's
    /// <see cref="WhisparrMovie.StashId"/> when present, and additionally its
    /// <see cref="WhisparrMovie.ForeignId"/> ONLY when <see cref="WhisparrMovie.ItemType"/> is <c>"scene"</c>
    /// (the field-polymorphism rule documented on <see cref="WhisparrMovie"/> — a movie-typed foreignId is a
    /// tmdbId, never a StashDB UUID). Case-insensitive; first row wins on a duplicate key.
    /// </summary>
    public static IReadOnlyDictionary<string, WhisparrMovie> BuildMovieIndex(IReadOnlyList<WhisparrMovie> movies)
    {
        var index = new Dictionary<string, WhisparrMovie>(StringComparer.OrdinalIgnoreCase);
        foreach (var movie in movies)
        {
            if (!string.IsNullOrEmpty(movie.StashId))
            {
                index.TryAdd(movie.StashId, movie);
            }

            if (string.Equals(movie.ItemType, "scene", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(movie.ForeignId))
            {
                index.TryAdd(movie.ForeignId, movie);
            }
        }

        return index;
    }

    /// <summary>
    /// Builds the exclusion lookup: the non-empty <see cref="WhisparrExclusion.ForeignId"/> values (the
    /// scene StashDB ids Whisparr excludes), case-insensitive.
    /// </summary>
    public static IReadOnlySet<string> BuildExcludedSet(IReadOnlyList<WhisparrExclusion> exclusions)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var exclusion in exclusions)
        {
            if (!string.IsNullOrEmpty(exclusion.ForeignId))
            {
                set.Add(exclusion.ForeignId);
            }
        }

        return set;
    }

    /// <summary>
    /// Classifies a scene into the four-state management axis with EXCLUSION-FIRST precedence: if any of
    /// the scene's StashDB ids is excluded → <see cref="SceneWhisparrState.Excluded"/> (even if a movie row also
    /// matches). Else the first id that indexes a movie is keyed on the movie's <c>monitored</c> flag alone →
    /// <see cref="SceneWhisparrState.Monitored"/> when monitored / <see cref="SceneWhisparrState.Unmonitored"/>
    /// when not. Else (no id, or no matching movie/exclusion) → <see cref="SceneWhisparrState.NotAdded"/>.
    /// Never throws on empty/null ids.
    /// </summary>
    /// <remarks>
    /// The state is the actionable MANAGEMENT axis — Whisparr's <c>monitored</c> flag — not whether Whisparr has
    /// a file: a downloaded-and-monitored scene stays <see cref="SceneWhisparrState.Monitored"/> so Cove's
    /// Monitored count equals Whisparr's. <c>hasFile</c> is reported separately (see
    /// <see cref="CardStatus"/> / <see cref="Detail"/>), never as a state here.
    /// </remarks>
    public static SceneWhisparrState Classify(
        IReadOnlyList<string> stashIds,
        IReadOnlyDictionary<string, WhisparrMovie> movieIndex,
        IReadOnlySet<string> excludedSet)
    {
        if (stashIds is null || stashIds.Count == 0)
        {
            return SceneWhisparrState.NotAdded;
        }

        // Exclusion-first: an excluded id wins over a matching movie row.
        foreach (var id in stashIds)
        {
            if (!string.IsNullOrEmpty(id) && excludedSet.Contains(id))
            {
                return SceneWhisparrState.Excluded;
            }
        }

        var movie = FindMovie(stashIds, movieIndex);
        if (movie is null)
        {
            return SceneWhisparrState.NotAdded;
        }

        return movie.Monitored ? SceneWhisparrState.Monitored : SceneWhisparrState.Unmonitored;
    }

    /// <summary>
    /// Projects a scene's card status — the primary <see cref="Classify"/> state plus the secondary
    /// <c>hasFile</c> fact from the matched movie (false when no movie matches). The single home the card badge +
    /// file dot read from, so the two facts stay consistent with the toolbar count and scene panel.
    /// </summary>
    public static SceneCardStatus CardStatus(
        IReadOnlyList<string> stashIds,
        IReadOnlyDictionary<string, WhisparrMovie> movieIndex,
        IReadOnlySet<string> excludedSet)
    {
        var state = Classify(stashIds, movieIndex, excludedSet);
        var movie = FindMovie(stashIds ?? [], movieIndex);
        return new SceneCardStatus(state, movie?.HasFile ?? false);
    }

    /// <summary>
    /// Projects the scene-panel detail — the exclusion-first <see cref="Classify"/> state plus the
    /// matched movie's Whisparr-owned facts (added/monitored/hasFile/quality/cutoff). The facts come from the
    /// matched movie regardless of exclusion, so an excluded scene that also has a row still reports its
    /// honest quality/monitored state alongside the <see cref="SceneWhisparrState.Excluded"/> badge.
    /// </summary>
    public static SceneDetail Detail(
        IReadOnlyList<string> stashIds,
        IReadOnlyDictionary<string, WhisparrMovie> movieIndex,
        IReadOnlySet<string> excludedSet)
    {
        var state = Classify(stashIds, movieIndex, excludedSet);
        var movie = FindMovie(stashIds, movieIndex);

        var quality = movie?.MovieFile?.Quality?.Quality?.Name;
        bool? cutoffMet = movie?.QualityCutoffNotMet is bool notMet ? !notMet : null;

        return new SceneDetail(
            State: state,
            Added: movie is not null,
            Monitored: movie?.Monitored ?? false,
            HasFile: movie?.HasFile ?? false,
            Quality: quality,
            CutoffMet: cutoffMet);
    }

    /// <summary>
    /// Partitions a set of Cove videos into by-state counts (toolbar summary), classifying each by
    /// its StashDB ids against the same movie index + exclusion set. Every video lands in exactly one PRIMARY
    /// state, so those four buckets sum to <see cref="SceneStatusCounts.Total"/>;
    /// <see cref="SceneStatusCounts.InLibrary"/> is the secondary file count (matched movie has a file), which
    /// cross-cuts the buckets and does not partition them.
    /// </summary>
    public static SceneStatusCounts SummaryCounts(
        IEnumerable<CoveVideo> videos,
        IReadOnlyDictionary<string, WhisparrMovie> movieIndex,
        IReadOnlySet<string> excludedSet)
    {
        int monitored = 0, unmonitored = 0, notAdded = 0, excluded = 0, inLibrary = 0, total = 0;
        foreach (var video in videos)
        {
            total++;
            switch (Classify(video.StashIds, movieIndex, excludedSet))
            {
                case SceneWhisparrState.Monitored:
                    monitored++;
                    break;
                case SceneWhisparrState.Unmonitored:
                    unmonitored++;
                    break;
                case SceneWhisparrState.Excluded:
                    excluded++;
                    break;
                default:
                    notAdded++;
                    break;
            }

            // Reuse the same matched-movie lookup (already O(scene-ids)) for the secondary file signal — no new pass.
            if (FindMovie(video.StashIds, movieIndex)?.HasFile == true)
            {
                inLibrary++;
            }
        }

        return new SceneStatusCounts(monitored, unmonitored, notAdded, excluded, inLibrary, total);
    }

    // The first StashDB id that indexes a Whisparr movie (id order preserved), or null when none match.
    private static WhisparrMovie? FindMovie(
        IReadOnlyList<string> stashIds,
        IReadOnlyDictionary<string, WhisparrMovie> movieIndex)
    {
        foreach (var id in stashIds)
        {
            if (!string.IsNullOrEmpty(id) && movieIndex.TryGetValue(id, out var movie))
            {
                return movie;
            }
        }

        return null;
    }
}
