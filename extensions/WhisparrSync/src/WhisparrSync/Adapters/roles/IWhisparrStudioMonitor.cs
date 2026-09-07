using WhisparrSync.Client;
using WhisparrSync.Contracts;
using WhisparrSync.Monitor;

namespace WhisparrSync.Adapters;

/// <summary>
/// Monitor a Cove studio — implemented by BOTH versions, because a Cove studio maps to a v3 studio AND a v2
/// SITE. The monitor role is the ONE entity sub-split (studio here, performer on
/// <see cref="IWhisparrPerformerMonitor"/>): v2 registers this role but not the performer one, so a v2 performer
/// monitor structurally has no method to call.
/// </summary>
internal interface IWhisparrStudioMonitor
{
    /// <summary>
    /// Sets a studio's monitor state via add-then-flip: if absent it is first created with
    /// <c>monitored:false</c> (carrying the origin <paramref name="tagIds"/> + the <paramref name="rootFolderPath"/>
    /// / <paramref name="qualityProfileId"/>), then a separate PUT sets <paramref name="monitored"/>. A create that
    /// returns 409/exists is success (re-read, never a duplicate); turning monitor OFF only PUTs
    /// <c>monitored:false</c>. NEVER triggers a search on add. <paramref name="scope"/> selects how far monitoring
    /// cascades (<see cref="MonitorScope.NewReleases"/> vs <see cref="MonitorScope.AllScenes"/>); it is ignored
    /// when turning monitor OFF.
    /// </summary>
    Task<WhisparrResult<EntityMonitorResult>> SetStudioMonitorAsync(
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
    /// Registers a studio's PRESENCE with monitoring OFF and grabbing disarmed: created <c>monitored:false</c>, no
    /// search, no monitor cascade, no <c>/command</c>, origin-tagged via <paramref name="tagIds"/>. Distinct from
    /// <see cref="SetStudioMonitorAsync"/> (which only ever creates an entity while turning monitoring ON, so a
    /// monitor-OFF caller has no other add path). Idempotent: an entity already present, and a create that returns
    /// 409/exists, both resolve to <see cref="WhisparrResultState.Ok"/>. On v3 presence is registered by the
    /// per-scene add, so this is a defensive Ok no-op there.
    /// </summary>
    Task<WhisparrResult<EntityMonitorResult>> RegisterStudioAsync(
        string baseUrl,
        string apiKey,
        string stashId,
        string rootFolderPath,
        int qualityProfileId,
        IReadOnlyList<int> tagIds,
        CancellationToken ct);

    /// <summary>
    /// Projects the quiet-status for a studio: whether it is added + currently monitored, plus the
    /// "grabbed of total" counts — no StashDB call. An absent studio returns added:false / 0-of-0.
    /// </summary>
    Task<WhisparrResult<EntityStatus>> GetStudioStatusAsync(string baseUrl, string apiKey, string stashId, CancellationToken ct);

    /// <summary>
    /// Lists the Whisparr movie ids attributed to a studio — the search-all input, computed ONLY from the
    /// already-fetched Whisparr movie set (the SAME attribution predicate the status uses), never a StashDB call.
    /// When <paramref name="monitoredOnly"/> is <c>true</c> the result is filtered to monitored movies. An absent
    /// studio returns an empty array.
    /// </summary>
    Task<WhisparrResult<int[]>> ListStudioAttributedIdsAsync(
        string baseUrl, string apiKey, string stashId, bool monitoredOnly, CancellationToken ct);
}
