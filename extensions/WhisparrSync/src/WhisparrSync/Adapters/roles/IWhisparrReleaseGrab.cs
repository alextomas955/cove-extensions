using WhisparrSync.Client;
using WhisparrSync.Push;

namespace WhisparrSync.Adapters;

/// <summary>
/// Interactive + upgrade release grab — v3-ONLY. v2 (Sonarr) has no per-movie release endpoint and no
/// cutoff-upgrade-only command, so a v2 adapter never implements this role.
/// </summary>
internal interface IWhisparrReleaseGrab
{
    /// <summary>
    /// Lists the on-demand indexer releases for one movie (count + the interactive picker's enriched rows).
    /// </summary>
    Task<WhisparrResult<WhisparrRelease[]>> GetReleasesAsync(string baseUrl, string apiKey, int movieId, CancellationToken ct);

    /// <summary>
    /// Grabs one specific indexer release (interactive grab): POSTs the <paramref name="guid"/> +
    /// <paramref name="indexerId"/> pair plus the <paramref name="movieId"/> Whisparr needs to address the release.
    /// A distinct single-shot grab verb — never fused into an add/exclusion path.
    /// </summary>
    Task<WhisparrResult<bool>> GrabReleaseAsync(
        string baseUrl, string apiKey, string guid, int indexerId, int movieId, CancellationToken ct);

    /// <summary>
    /// Searches the given movies for a quality upgrade by posting one <c>MoviesSearch</c> command — Whisparr grabs
    /// an upgrade ONLY when the movie is monitored and its cutoff is unmet (enforced server-side). An empty
    /// <paramref name="movieIds"/> is an <see cref="WhisparrResultState.Ok"/> no-op issuing NO command.
    /// </summary>
    Task<WhisparrResult<BulkActionResult>> SearchForUpgradesAsync(
        string baseUrl, string apiKey, IReadOnlyList<int> movieIds, CancellationToken ct);
}
