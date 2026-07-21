namespace WhisparrSync.Push;

/// <summary>
/// The outcome of a per-scene add or monitor toggle: the resulting Whisparr
/// <see cref="MovieId"/>, whether this call performed the create (<see cref="Added"/>), and the movie's
/// <see cref="Monitored"/> state afterward. A repeat add / re-toggle of an already-present scene reports
/// <see cref="Added"/> <c>false</c> (a create's 409/exists is success, never a duplicate), so the
/// caller can distinguish a first add from an idempotent no-op. <see cref="MovieId"/> is <c>0</c> only for the
/// absent + unmonitor no-op (there is no movie to act on). <see cref="Path"/> carries the created/existing
/// movie's on-disk directory so the add-then-flip monitor toggle can echo it back on the <c>PUT /movie/{id}</c>
/// (Whisparr Eros rejects a flip body with no <c>path</c>); null when unknown/irrelevant.
/// </summary>
internal sealed record SceneActionResult(int MovieId, bool Added, bool Monitored, string? Path = null);

/// <summary>
/// The outcome of a bulk operation over movie ids — a search-all here, and the add-all-missing
/// the service layers on top: <see cref="Total"/> items acted on, <see cref="Succeeded"/> that
/// completed, and <see cref="Failed"/> that did not. A bulk over an empty input is <see cref="Empty"/>
/// (all-zero) and issues no wire call. <see cref="Message"/> carries an optional user-facing note (e.g. the
/// owned-import flat-layout fall-back reason) surfaced to the caller/UI; null when there is nothing to report.
/// </summary>
internal sealed record BulkActionResult(int Total, int Succeeded, int Failed, string? Message = null)
{
    /// <summary>The all-zero result for a bulk over an empty input (no wire call issued).</summary>
    internal static BulkActionResult Empty { get; } = new(0, 0, 0);
}

/// <summary>
/// One scene in a batch add/exclude fan-out: the StashDB <see cref="StashId"/> plus the display
/// <see cref="Title"/>/<see cref="Year"/> Whisparr needs for the add/exclusion body. The endpoint layer
/// resolves the selected Cove ids into these before calling the batch helpers — the service never
/// touches Cove ids directly.
/// </summary>
internal sealed record SceneRef(string StashId, string? Title = null, int? Year = null);
