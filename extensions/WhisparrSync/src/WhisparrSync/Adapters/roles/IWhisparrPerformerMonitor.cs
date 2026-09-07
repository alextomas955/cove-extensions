using WhisparrSync.Client;
using WhisparrSync.Contracts;
using WhisparrSync.Monitor;

namespace WhisparrSync.Adapters;

/// <summary>
/// Monitor a performer — v3-ONLY. This is the one entity-shaped gap: v2 has no performer resource (performers
/// are embedded <c>episode.actors</c> metadata), so a v2 adapter never implements this role and a performer
/// monitor structurally defers on v2 (no method to call, no stray <c>cove-sync</c> tag).
/// </summary>
internal interface IWhisparrPerformerMonitor
{
    /// <summary>
    /// Sets a performer's monitor state via add-then-flip — the performer mirror of
    /// <see cref="IWhisparrStudioMonitor.SetStudioMonitorAsync"/>. NEVER triggers a search on add;
    /// <paramref name="scope"/> selects the cascade and is ignored when turning monitor OFF.
    /// </summary>
    Task<WhisparrResult<EntityMonitorResult>> SetPerformerMonitorAsync(
        string baseUrl,
        string apiKey,
        string stashId,
        bool monitored,
        MonitorScope scope,
        string rootFolderPath,
        int qualityProfileId,
        IReadOnlyList<int> tagIds,
        CancellationToken ct);

    /// <summary>
    /// Projects the quiet-status for a performer: added + monitored + "grabbed of total", off Whisparr's own
    /// counts — no StashDB call. An absent performer returns added:false / 0-of-0.
    /// </summary>
    Task<WhisparrResult<EntityStatus>> GetPerformerStatusAsync(string baseUrl, string apiKey, string stashId, CancellationToken ct);

    /// <summary>
    /// Lists the Whisparr movie ids attributed to a performer (by <c>performerForeignIds</c>) — the search-all
    /// input, computed ONLY from the already-fetched Whisparr movie set. When <paramref name="monitoredOnly"/> is
    /// <c>true</c> the result is filtered to monitored movies. An absent performer returns an empty array.
    /// </summary>
    Task<WhisparrResult<int[]>> ListPerformerAttributedIdsAsync(
        string baseUrl, string apiKey, string stashId, bool monitoredOnly, CancellationToken ct);
}
