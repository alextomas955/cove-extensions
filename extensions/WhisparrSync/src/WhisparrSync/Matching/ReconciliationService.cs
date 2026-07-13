using WhisparrSync.Client;
using WhisparrSync.Library;

namespace WhisparrSync.Matching;

/// <summary>The reconciliation bucket counts (MATCH-03); <see cref="Total"/> is the number of Whisparr movies classified.</summary>
internal sealed record ReconciliationCounts(int Matched, int Unmatched, int NeedsReview, int Total);

/// <summary>
/// The zero-mutation reconciliation diff: every Whisparr movie sorted into matched / unmatched /
/// needs-review, with counts. Fuzzy suggestions live ONLY in <see cref="NeedsReview"/> — they are never
/// promoted here (MATCH-02).
/// </summary>
internal sealed record ReconciliationDiff(
    IReadOnlyList<MatchResult> Matched,
    IReadOnlyList<MatchResult> Unmatched,
    IReadOnlyList<MatchResult> NeedsReview,
    ReconciliationCounts Counts);

/// <summary>
/// Composes the already-loaded Cove videos, the fetched Whisparr movies, and the persisted match map into
/// a reconciliation diff (MATCH-03). PURE and I/O-free: it opens no scope, calls no port, touches no store
/// — the 02-03 endpoint supplies the loaded lists and owns any confirm/reject write, so a plain reconcile
/// is provably zero-mutation. Confirmed links are kept as-is across re-runs and rejected suggestions are
/// suppressed, so reconciliation is incremental and stable (MATCH-04).
/// </summary>
internal static class ReconciliationService
{
    public static ReconciliationDiff Reconcile(
        IReadOnlyList<CoveVideo> coveVideos,
        IReadOnlyList<WhisparrMovie> whisparrMovies,
        IReadOnlyList<MatchState> persisted,
        IReadOnlyDictionary<string, string>? pathTranslations = null)
    {
        var derived = IdentityMatcher.Match(coveVideos, whisparrMovies, pathTranslations);
        var persistedByMovie = persisted
            .GroupBy(p => p.WhisparrMovieId)
            .ToDictionary(g => g.Key, g => g.ToList());
        var coveById = coveVideos.ToDictionary(c => c.CoveId);

        var matched = new List<MatchResult>();
        var unmatched = new List<MatchResult>();
        var needsReview = new List<MatchResult>();

        foreach (var result in derived)
        {
            var entries = persistedByMovie.GetValueOrDefault(result.Movie.Id);

            // A confirmed decision wins over whatever the chain would re-derive — the link is stable
            // across re-runs (MATCH-04). Cast to nullable so an absent entry is null, not a zero-valued
            // default struct (MatchStatus.Confirmed is 0).
            var confirmed = entries?
                .Where(e => e.Status == MatchStatus.Confirmed)
                .Select(e => (MatchState?)e)
                .FirstOrDefault();
            if (confirmed is { } pinned)
            {
                var pinnedVideo = coveById.GetValueOrDefault(pinned.CoveId);
                if (pinnedVideo is null)
                {
                    // The confirmed Cove video was deleted since the link was made. Never emit a "matched"
                    // row with a null MatchedVideo — that paints a green pill next to an empty Cove cell and
                    // inflates the matched count. Degrade to unmatched: the Whisparr side is present, the
                    // Cove side is gone (WR-02).
                    unmatched.Add(result with { MatchedVideo = null, Leg = null, Outcome = MatchOutcome.Unmatched, AutoApplies = false });
                    continue;
                }

                matched.Add(new MatchResult(
                    result.Movie,
                    pinnedVideo,
                    pinned.MatchedBy,
                    MatchOutcome.Matched,
                    AutoApplies: true));
                continue;
            }

            // A rejected decision for this movie↔cove pair suppresses the (low-confidence) suggestion so
            // it does not re-surface in needs-review (MATCH-02/04): the movie falls to unmatched.
            var suppressed = result.MatchedVideo is not null
                && (entries?.Any(e => e.Status == MatchStatus.Rejected && e.CoveId == result.MatchedVideo.CoveId) ?? false);
            if (suppressed)
            {
                unmatched.Add(result with { MatchedVideo = null, Leg = null, Outcome = MatchOutcome.Unmatched, AutoApplies = false });
                continue;
            }

            switch (result.Outcome)
            {
                case MatchOutcome.Matched:
                    matched.Add(result);
                    break;
                case MatchOutcome.NeedsReview:
                    needsReview.Add(result);
                    break;
                default:
                    unmatched.Add(result);
                    break;
            }
        }

        var counts = new ReconciliationCounts(matched.Count, unmatched.Count, needsReview.Count, whisparrMovies.Count);
        return new ReconciliationDiff(matched, unmatched, needsReview, counts);
    }
}
