namespace WhisparrSync.Matching;

/// <summary>
/// One persisted Cove↔Whisparr link in the match map (MATCH-04). Stored as one row of a single JSON
/// array blob over <c>IExtensionStore</c> (see <c>MatchStateStore</c>), keyed on the StashDB UUID.
/// </summary>
/// <param name="CoveId">The Cove <c>Video.Id</c> the Whisparr movie resolved to.</param>
/// <param name="WhisparrMovieId">The Whisparr movie's own id — the durable handle for this row across re-runs (a fuzzy suggestion has no StashDB UUID, so this, not <see cref="StashId"/>, is what re-reconcile keys the merge on).</param>
/// <param name="StashId">The StashDB UUID the link was made on, or empty for a non-StashDB (path/fuzzy) leg.</param>
/// <param name="MatchedBy">Which fallback-chain leg produced the link.</param>
/// <param name="MatchedAtUtcTicks">Server-written <c>DateTime.UtcNow.Ticks</c> — NEVER a browser value (the map is read back later/elsewhere).</param>
/// <param name="Status">The user-decision lifecycle: a confirmed link is reused on re-run, a rejected one is suppressed so it does not re-surface.</param>
internal readonly record struct MatchState(
    int CoveId,
    int WhisparrMovieId,
    string StashId,
    MatchedBy MatchedBy,
    long MatchedAtUtcTicks,
    MatchStatus Status);

/// <summary>The fallback-chain leg that produced a link (content-hash is a documented no-op cross-system, so it is not a leg here).</summary>
internal enum MatchedBy
{
    StashId,
    Path,
    Fuzzy,
}

/// <summary>The user-decision lifecycle of a match-map entry.</summary>
internal enum MatchStatus
{
    Confirmed,
    NeedsReview,
    Rejected,
}
