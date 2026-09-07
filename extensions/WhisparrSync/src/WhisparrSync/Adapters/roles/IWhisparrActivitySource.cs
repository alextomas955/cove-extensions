using WhisparrSync.Client;

namespace WhisparrSync.Adapters;

/// <summary>
/// The read-only activity surface both Whisparr generations honor: the live download queue (and, added
/// alongside it, the live wanted set). Both endpoints are served on the same <c>/api/v3</c> path by v3 (Eros)
/// and v2 (Sonarr-shaped), so this rides the <see cref="IWhisparrAdapter"/> aggregate rather than a v3-only
/// role — a caller gets queue + wanted uniformly on either version, and the projection layer normalizes the
/// per-version row shape. Every method is a pure read: it issues only a GET, never an add/monitor/search/grab.
/// </summary>
internal interface IWhisparrActivitySource
{
    /// <summary>Reads one page of the live Whisparr download queue — the same paged shape on v3 and v2.</summary>
    Task<WhisparrResult<WhisparrQueuePage>> ListQueueAsync(
        string baseUrl, string apiKey, int page, int pageSize, CancellationToken ct);

    /// <summary>
    /// Reads one page of the live wanted set (monitored scenes without a file), derived LIVE from Whisparr's own
    /// <c>wanted/missing</c> — never a persisted local list, so an imported scene (now with a file) simply drops
    /// out of the next read. The same paged shape on v3 and v2 (v2 rows bind into the movie shape).
    /// </summary>
    Task<WhisparrResult<WhisparrMoviePage>> ListWantedAsync(
        string baseUrl, string apiKey, int page, int pageSize, CancellationToken ct);
}
