namespace WhisparrSync.Contracts;

/// <summary>
/// The <c>/videos-batch</c> request body: the <see cref="Op"/>
/// (<c>add</c>/<c>search</c>/<c>searchUpgrades</c>/<c>exclude</c>/<c>unExclude</c>, case-insensitive) and the
/// selected Cove video ids. Every id is resolved to a scene SERVER-SIDE (never a caller-supplied StashDB id),
/// the list is capped before any per-item work (<c>MaxEntityIdsPerRequest</c>), and the body
/// carries no url/key — the handler uses the stored creds only. Per-op grab posture: only
/// <c>search</c>/<c>searchUpgrades</c> may grab; <c>add</c>/<c>exclude</c>/<c>unExclude</c> never search
/// (loop-safety is LOCKED). Fields are nullable so a malformed body is rejected cleanly.
/// </summary>
internal sealed record VideosBatchRequest(string? Op, int[]? CoveIds);

/// <summary>
/// The <c>/videos-batch</c> response: the resolved <see cref="Op"/> plus the aggregate counts —
/// <see cref="Total"/> selected, <see cref="Succeeded"/>, <see cref="Skipped"/> (no StashDB identity; for a
/// search op not yet an added Whisparr movie; for unmonitor a not-added scene — nothing to unmonitor, never
/// an add), and <see cref="Failed"/>. Carries no scene id/key.
/// </summary>
internal sealed record VideosBatchResult(string Op, int Total, int Succeeded, int Skipped, int Failed);

/// <summary>The videos-batch operations, parsed from the wire <c>Op</c> string by <c>TryParseBatchOp</c>.</summary>
internal enum BatchOp
{
    Add,
    Monitor,
    Unmonitor,
    Search,
    SearchUpgrades,
    Exclude,
}

/// <summary>
/// The <c>/entities-batch</c> request body: the entity <see cref="Kind"/> (<c>"studio"</c>/<c>"performer"</c>),
/// the selected Cove <see cref="CoveEntityIds"/> (capped before any per-item work), the
/// <see cref="Op"/> (<c>monitor</c>/<c>unmonitor</c>/<c>addMissing</c>/<c>search</c>/<c>reflectOwned</c>,
/// case-insensitive), and the monitor <see cref="Scope"/> (<c>NewReleases</c>/<c>AllScenes</c>, used only by
/// monitor). No url/key and no remote ids — each entity's identity is resolved SERVER-SIDE from its Cove id.
/// Fields are nullable so a malformed body is rejected cleanly (400).
/// </summary>
internal sealed record EntitiesBatchRequest(string? Kind, int[]? CoveEntityIds, string? Op, string? Scope);

/// <summary>
/// The <c>/entities-batch</c> response: the resolved <see cref="Op"/> plus aggregate counts —
/// <see cref="Total"/> selected entities, <see cref="Succeeded"/> (op returned Ok), <see cref="Failed"/>, and
/// <see cref="Skipped"/> (no identity for the connected version — no outbound call). Carries no id/key.
/// </summary>
internal sealed record EntitiesBatchResult(string Op, int Total, int Succeeded, int Failed, int Skipped);

/// <summary>
/// The answer every enqueueing route shares: the job the Cove Job Drawer will track, and the description it
/// renders. The work outlives the request, so this is an acceptance — never a result.
/// </summary>
internal sealed record JobAcceptedResponse(string JobId, string Description);

/// <summary>The entities-batch operations, parsed from the wire <c>Op</c> string by <c>TryParseEntityBatchOp</c>.</summary>
internal enum EntityBatchOp
{
    Monitor,
    Unmonitor,
    AddMissing,
    Search,
    ReflectOwned,
    RegisterEntity,
}
