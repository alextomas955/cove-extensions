using WhisparrSync.Client;
using WhisparrSync.Push;

namespace WhisparrSync.Adapters;

/// <summary>
/// The one grab-capable verb both versions have: search the monitored scenes now (v3 <c>MoviesSearch</c>, v2
/// <c>EpisodeSearch</c>). Every add/monitor path stays search-free; this is the deliberate exception.
/// </summary>
internal interface IWhisparrSceneSearch
{
    /// <summary>
    /// Posts a single search command over <paramref name="movieIds"/> (search-now / search-all). This is the ONLY
    /// shared verb that can cause a grab — every add/monitor path is search-free. An empty
    /// <paramref name="movieIds"/> is an <see cref="WhisparrResultState.Ok"/> no-op that issues NO command.
    /// </summary>
    Task<WhisparrResult<BulkActionResult>> SearchScenesAsync(
        string baseUrl, string apiKey, IReadOnlyList<int> movieIds, CancellationToken ct);
}
