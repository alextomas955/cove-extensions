using WhisparrSync.Client;
using WhisparrSync.Library;

namespace WhisparrSync.SceneStatus;

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
    public static IReadOnlyDictionary<string, T> BuildMovieIndex<T>(IReadOnlyList<T> movies)
        where T : class, IWhisparrMovieFacts
    {
        var index = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        foreach (var movie in movies)
        {
            IndexMovie(index, movie);
        }

        return index;
    }

    /// <summary>
    /// <see cref="BuildMovieIndex{T}"/> for a full row specifically.
    /// </summary>
    /// <remarks>
    /// An empty collection expression has no element for generic inference to read, so the generic form cannot
    /// be called with one. This overload is what lets every caller holding full rows — including the ones whose
    /// argument is <c>[]</c> on a failed upstream read — keep passing a literal.
    /// </remarks>
    public static IReadOnlyDictionary<string, WhisparrMovie> BuildMovieIndex(IReadOnlyList<WhisparrMovie> movies)
        => BuildMovieIndex<WhisparrMovie>(movies);

    /// <summary>
    /// Adds one row to <paramref name="index"/> under every key <see cref="BuildMovieIndex{T}"/> would key it
    /// by; first row wins on a duplicate key, as in a whole-set build.
    /// </summary>
    /// <remarks>
    /// The seam a caller folding rows one at a time indexes through, so a streamed read and a materialized one
    /// cannot come to key the same row differently.
    /// </remarks>
    public static void IndexMovie<T>(Dictionary<string, T> index, T movie)
        where T : class, IWhisparrMovieFacts
    {
        foreach (var key in IndexKeys(movie))
        {
            index.TryAdd(key, movie);
        }
    }

    /// <summary>
    /// Whether <paramref name="movie"/> is a movie for a scene carrying <paramref name="stashIds"/> — the
    /// keying rule of <see cref="BuildMovieIndex"/> applied to one row instead of a set.
    /// </summary>
    /// <remarks>
    /// A per-scene read asks Whisparr to select rows on ITS predicate, which is not this one. Passing each
    /// answered row through this rule is what keeps such a resolve a subset of the whole-set resolve: a row
    /// the index would not have keyed under any of these ids can never come back as a match.
    /// </remarks>
    public static bool IsMovieFor<T>(T movie, IReadOnlyList<string> stashIds)
        where T : class, IWhisparrMovieFacts
    {
        foreach (var key in IndexKeys(movie))
        {
            foreach (var id in stashIds)
            {
                if (!string.IsNullOrEmpty(id) && string.Equals(id, key, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    // The one copy of the keying rule: the whole-set index and the single-row predicate both read it, so a
    // status pill and the action taken on it cannot come to disagree about which movie a scene is.
    private static IEnumerable<string> IndexKeys<T>(T movie)
        where T : class, IWhisparrMovieFacts
    {
        if (!string.IsNullOrEmpty(movie.StashId))
        {
            yield return movie.StashId;
        }

        if (string.Equals(movie.ItemType, "scene", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrEmpty(movie.ForeignId))
        {
            yield return movie.ForeignId;
        }
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
    public static SceneWhisparrState Classify<T>(
        IReadOnlyList<string> stashIds,
        IReadOnlyDictionary<string, T> movieIndex,
        IReadOnlySet<string> excludedSet)
        where T : class, IWhisparrMovieFacts
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
    public static SceneCardStatus CardStatus<T>(
        IReadOnlyList<string> stashIds,
        IReadOnlyDictionary<string, T> movieIndex,
        IReadOnlySet<string> excludedSet)
        where T : class, IWhisparrMovieFacts
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
    public static SceneStatusCounts SummaryCounts<T>(
        IEnumerable<CoveVideo> videos,
        IReadOnlyDictionary<string, T> movieIndex,
        IReadOnlySet<string> excludedSet)
        where T : class, IWhisparrMovieFacts
    {
        var counts = new SummaryAccumulator();
        foreach (var video in videos)
        {
            counts.Add(video, movieIndex, excludedSet);
        }

        return counts.ToCounts();
    }

    /// <summary>
    /// Folds a STREAM of Cove videos into the same by-state counts <see cref="SummaryCounts"/> produces.
    /// </summary>
    /// <remarks>
    /// What the library-wide summary reads through, since the result is six integers at any library size.
    /// Classification is per-scene and order-independent, so a streamed fold answers identically to a full pass.
    /// </remarks>
    public static async Task<SceneStatusCounts> SummaryCountsAsync<T>(
        IAsyncEnumerable<CoveVideo> videos,
        IReadOnlyDictionary<string, T> movieIndex,
        IReadOnlySet<string> excludedSet,
        CancellationToken ct = default)
        where T : class, IWhisparrMovieFacts
    {
        var counts = new SummaryAccumulator();
        await foreach (var video in videos.WithCancellation(ct))
        {
            counts.Add(video, movieIndex, excludedSet);
        }

        return counts.ToCounts();
    }

    // Shared by both folds: two copies would let the toolbar count a library-wide read differently from a
    // fixture-sized one.
    private struct SummaryAccumulator
    {
        private int _monitored;
        private int _unmonitored;
        private int _notAdded;
        private int _excluded;
        private int _inLibrary;
        private int _total;

        public void Add<T>(
            CoveVideo video,
            IReadOnlyDictionary<string, T> movieIndex,
            IReadOnlySet<string> excludedSet)
            where T : class, IWhisparrMovieFacts
        {
            _total++;
            switch (Classify(video.StashIds, movieIndex, excludedSet))
            {
                case SceneWhisparrState.Monitored:
                    _monitored++;
                    break;
                case SceneWhisparrState.Unmonitored:
                    _unmonitored++;
                    break;
                case SceneWhisparrState.Excluded:
                    _excluded++;
                    break;
                default:
                    _notAdded++;
                    break;
            }

            // Reuse the same matched-movie lookup (already O(scene-ids)) for the secondary file signal — no new pass.
            if (FindMovie(video.StashIds, movieIndex)?.HasFile == true)
            {
                _inLibrary++;
            }
        }

        public SceneStatusCounts ToCounts()
            => new(_monitored, _unmonitored, _notAdded, _excluded, _inLibrary, _total);
    }

    /// <summary>
    /// The first of <paramref name="stashIds"/> that indexes a movie in <paramref name="movieIndex"/> (id order
    /// preserved), or null when none match.
    /// </summary>
    /// <remarks>
    /// The one id-resolution rule the status projection and the per-scene action routes both read. Two copies
    /// would let a scene's status pill and the action taken on it disagree about which movie the scene is.
    /// </remarks>
    public static T? FindMovie<T>(
        IReadOnlyList<string> stashIds,
        IReadOnlyDictionary<string, T> movieIndex)
        where T : class, IWhisparrMovieFacts
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
