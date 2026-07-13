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
        => throw new NotImplementedException();
}
