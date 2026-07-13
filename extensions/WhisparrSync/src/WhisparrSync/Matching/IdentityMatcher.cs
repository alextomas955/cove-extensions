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
    {
        var results = new List<MatchResult>(whisparrMovies.Count);
        foreach (var movie in whisparrMovies)
        {
            // Leg 1 — StashDB UUID (exact, HIGH, auto).
            var byStash = coveVideos.FirstOrDefault(c => StashMatches(c, movie));
            if (byStash is not null)
            {
                results.Add(new MatchResult(movie, byStash, MatchedBy.StashId, MatchOutcome.Matched, AutoApplies: true));
                continue;
            }

            // Leg 2 — content hash. Documented no-op: Whisparr's movie/movieFile resource exposes no
            // comparable file hash, so there is nothing to compare Cove's fingerprints against. Falls
            // through by design; it is NOT an omission.

            // Leg 3 — normalized path with translation (MEDIUM, auto only on translated equality).
            var byPath = coveVideos.FirstOrDefault(c => PathMatches(c, movie, pathTranslations));
            if (byPath is not null)
            {
                results.Add(new MatchResult(movie, byPath, MatchedBy.Path, MatchOutcome.Matched, AutoApplies: true));
                continue;
            }

            // Leg 4 — fuzzy title+year (LOW). A suggestion ONLY: needs-review, never auto-applied (MATCH-02).
            var byFuzzy = coveVideos.FirstOrDefault(c => FuzzyMatches(c, movie));
            if (byFuzzy is not null)
            {
                results.Add(new MatchResult(movie, byFuzzy, MatchedBy.Fuzzy, MatchOutcome.NeedsReview, AutoApplies: false));
                continue;
            }

            results.Add(new MatchResult(movie, MatchedVideo: null, Leg: null, MatchOutcome.Unmatched, AutoApplies: false));
        }

        return results;
    }

    /// <summary>
    /// The StashDB leg: a Whisparr scene's key is its <c>stashId</c> when present, else its
    /// <c>foreignId</c> but ONLY where <c>itemType == "scene"</c> — a movie-typed <c>foreignId</c> is a
    /// tmdbId and must never be compared to a Cove StashDB UUID (Pitfall 4). Compared case-insensitively.
    /// </summary>
    private static bool StashMatches(CoveVideo cove, WhisparrMovie movie)
    {
        var whisparrStash = !string.IsNullOrEmpty(movie.StashId)
            ? movie.StashId
            : (string.Equals(movie.ItemType, "scene", StringComparison.OrdinalIgnoreCase) ? movie.ForeignId : null);

        return !string.IsNullOrEmpty(whisparrStash)
            && cove.StashIds.Any(s => string.Equals(s, whisparrStash, StringComparison.OrdinalIgnoreCase));
    }

    private static bool PathMatches(CoveVideo cove, WhisparrMovie movie, IReadOnlyDictionary<string, string>? translations)
    {
        var whisparrPath = movie.MovieFile?.Path;
        if (string.IsNullOrEmpty(whisparrPath))
        {
            return false;
        }

        // Translate the Whisparr path into Cove's mount namespace, then require exact equality — a raw
        // string-unequal path with no matching translation deliberately does not match (Pitfall 2).
        var translated = Translate(whisparrPath, translations);
        return cove.FilePaths.Any(p => string.Equals(Normalize(p), translated, StringComparison.Ordinal));
    }

    /// <summary>Fuzzy title+year overlap (a tiny hand-rolled Jaccard, no NuGet). Requires an equal year so this is title+year, never title-only.</summary>
    private static bool FuzzyMatches(CoveVideo cove, WhisparrMovie movie)
    {
        if (string.IsNullOrWhiteSpace(cove.Title) || string.IsNullOrWhiteSpace(movie.Title))
        {
            return false;
        }

        if (cove.Date?.Year is not { } coveYear || movie.Year is not { } movieYear || coveYear != movieYear)
        {
            return false;
        }

        var coveTokens = Tokenize(cove.Title);
        var movieTokens = Tokenize(movie.Title);
        if (coveTokens.Count == 0 || movieTokens.Count == 0)
        {
            return false;
        }

        var union = new HashSet<string>(coveTokens);
        union.UnionWith(movieTokens);
        var overlap = coveTokens.Count(movieTokens.Contains);
        return (double)overlap / union.Count >= 0.6;
    }

    private static string Translate(string path, IReadOnlyDictionary<string, string>? translations)
    {
        var normalized = Normalize(path);
        if (translations is null)
        {
            return normalized;
        }

        // Longest matching root prefix wins so a more specific mapping is not shadowed by a shorter one.
        foreach (var (from, to) in translations.OrderByDescending(kv => kv.Key.Length))
        {
            var normalizedFrom = Normalize(from);
            if (normalized.StartsWith(normalizedFrom, StringComparison.Ordinal))
            {
                return Normalize(to) + normalized[normalizedFrom.Length..];
            }
        }

        return normalized;
    }

    // Cove stores forward-slash paths; fold any backslash so a Windows-origin Whisparr path compares cleanly.
    private static string Normalize(string path) => path.Replace('\\', '/');

    private static HashSet<string> Tokenize(string title)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in title.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var token = new string([.. raw.Where(char.IsLetterOrDigit)]);
            if (token.Length > 0)
            {
                tokens.Add(token);
            }
        }

        return tokens;
    }
}
