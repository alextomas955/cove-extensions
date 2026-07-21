namespace WhisparrSync.Matching;

/// <summary>
/// One persisted Cove↔Whisparr link in the match map. Stored as one row of a single JSON
/// array blob over <c>IExtensionStore</c> (see <c>MatchStateStore</c>), keyed on the Whisparr movie id.
/// </summary>
/// <param name="CoveId">The Cove <c>Video.Id</c> the Whisparr movie resolved to.</param>
/// <param name="WhisparrMovieId">The Whisparr movie's own id — the durable handle for this row across re-runs (a ThePornDB-matched row also has no StashDB UUID, so this, not <see cref="StashId"/>, is what re-reconcile keys the merge on).</param>
/// <param name="StashId">The StashDB UUID the link was made on, or empty for a non-StashDB (ThePornDB) link.</param>
/// <param name="MatchedBy">Which remote id (StashDB or ThePornDB) produced the link.</param>
/// <param name="MatchedAtUtcTicks">Server-written <c>DateTime.UtcNow.Ticks</c> — NEVER a browser value (the map is read back later/elsewhere).</param>
/// <param name="Status">The user-decision lifecycle: a confirmed link is reused on re-run, a rejected one is suppressed so it does not re-surface.</param>
internal readonly record struct MatchState(
    int CoveId,
    int WhisparrMovieId,
    string StashId,
    MatchedBy MatchedBy,
    long MatchedAtUtcTicks,
    MatchStatus Status);

/// <summary>Which remote id — the StashDB UUID (v3) or the ThePornDB id (v2) — produced a link.</summary>
internal enum MatchedBy
{
    StashId,
    Tpdb,
}

/// <summary>The user-decision lifecycle of a match-map entry.</summary>
internal enum MatchStatus
{
    Confirmed,
    NeedsReview,
    Rejected,
}
