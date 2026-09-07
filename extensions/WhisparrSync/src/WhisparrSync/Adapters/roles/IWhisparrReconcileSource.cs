using WhisparrSync.Client;

namespace WhisparrSync.Adapters;

/// <summary>
/// The reconciliation read surface both versions honor: the full movie set and one page of history. v2
/// synthesizes the movie set from series → episode; v3 reads <c>/movie</c> directly.
/// </summary>
internal interface IWhisparrReconcileSource
{
    /// <summary>Lists the full Whisparr movie set (unpaged), materialized.</summary>
    /// <remarks>
    /// <para>
    /// Three callers reach this member, and each for a reason that names what a narrower read cannot give them:
    /// the owned-reflection path's branch for the entity kinds the older generation cannot enumerate, where
    /// there is no per-entity catalogue to ask for; the memoised list read, which serves the per-card batch
    /// classify and the discovery status index and needs the full row's quality and cover members; and the
    /// videos batch planner's search and search-upgrades ops, which resolve a Whisparr movie id per selected
    /// scene.
    /// </para>
    /// <para>
    /// A fourth whole-set movie read exists and does not come through here: the attributed-id collector behind
    /// the grab-capable search surfaces reads <see cref="WhisparrClient.ListMoviesAsync"/> — the transport —
    /// directly, so repointing it is a change to those surfaces' loop-safety rather than to this interface.
    /// </para>
    /// <para>
    /// The videos toolbar summary is NOT among them on either generation: it only ever folds, so it reads
    /// through <see cref="IWhisparrStatusIndexSource"/> and holds no row on either side. What survives here
    /// is the set of callers that need a whole row per scene.
    /// </para>
    /// </remarks>
    Task<WhisparrResult<WhisparrMovie[]>> ListMoviesAsync(string baseUrl, string apiKey, CancellationToken ct);

    /// <summary>Reads one newest-first page of Whisparr history — the polling-reconcile data source.</summary>
    Task<WhisparrResult<WhisparrHistoryPage>> ListHistoryAsync(
        string baseUrl, string apiKey, int page, int pageSize, CancellationToken ct);
}
