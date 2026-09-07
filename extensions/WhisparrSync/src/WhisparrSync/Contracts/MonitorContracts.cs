namespace WhisparrSync.Contracts;

/// <summary>
/// The <c>/monitor</c> request body: the entity <see cref="Kind"/> (<c>"studio"</c> /
/// <c>"performer"</c>), the entity's own Cove <see cref="RemoteIds"/> forwarded straight from the slot
/// context, and the target <see cref="Monitored"/> state. It carries NO base URL or API key — the handler
/// uses the stored creds only, and resolves the Whisparr lookup id server-side from
/// <see cref="RemoteIds"/> (the pair whose endpoint matches the stored StashDbEndpoint). Fields are
/// nullable so a malformed body is rejected cleanly rather than throwing on bind.
/// </summary>
internal sealed record MonitorRequest(string? Kind, RemoteIdInput[]? RemoteIds, bool Monitored, string? Scope = null);

/// <summary>
/// The <c>/monitor-status</c> request body: the entity <see cref="Kind"/> + its Cove
/// <see cref="RemoteIds"/>. Like <see cref="MonitorRequest"/> it carries no url/key.
/// </summary>
internal sealed record MonitorStatusRequest(string? Kind, RemoteIdInput[]? RemoteIds);

/// <summary>
/// The <c>/bulk-add-missing</c> request body: the entity <see cref="Kind"/> (<c>"studio"</c> /
/// <c>"performer"</c>) + the Cove <see cref="CoveEntityId"/> (<c>Studio.Id</c> / <c>Performer.Id</c>) whose
/// OWN scenes are enumerated for the local diff. It carries NO url/key and — deliberately — NO
/// <c>remoteIds</c>: the missing-set diff is keyed by the Cove entity id, never by a forwarded
/// stashId, so a <c>remoteIds</c> field would be a dead input. <see cref="Kind"/> is nullable so a malformed
/// body is rejected cleanly (400) rather than throwing on bind.
/// </summary>
internal sealed record BulkAddMissingRequest(string? Kind, int CoveEntityId);

/// <summary>
/// The <c>/bulk-search-monitored</c> request body: the entity <see cref="Kind"/> + its Cove
/// <see cref="RemoteIds"/>. The entity's StashDB id is resolved server-side from <see cref="RemoteIds"/> (the
/// pair whose endpoint matches the stored StashDbEndpoint), exactly like <see cref="MonitorRequest"/>; the
/// body carries no url/key. Fields are nullable so a malformed body is rejected cleanly.
/// </summary>
internal sealed record BulkSearchMonitoredRequest(string? Kind, RemoteIdInput[]? RemoteIds);

/// <summary>
/// The <c>/reflect-owned</c> request body: the entity <see cref="Kind"/> (<c>"studio"</c> /
/// <c>"performer"</c>) + the Cove <see cref="CoveEntityId"/> (<c>Studio.Id</c> / <c>Performer.Id</c>) whose
/// OWN scenes are enumerated for the owned-scene import. Like <see cref="BulkAddMissingRequest"/> it carries
/// NO url/key and NO <c>remoteIds</c>: the match is keyed by the Cove entity id + each video's own TPDB id,
/// never a forwarded stashId. <see cref="Kind"/> is nullable so a malformed body is rejected cleanly (400).
/// </summary>
internal sealed record ReflectOwnedRequest(string? Kind, int CoveEntityId);

/// <summary>The studio/performer card-badge batch request: the entity kind + the visible page's Cove ids.</summary>
internal sealed record EntityStatusBatchRequest(string? Kind, int[]? CoveEntityIds);

/// <summary>The studios/performers toolbar row's library-wide count: total Cove entities of the kind and how
/// many are monitored in Whisparr.</summary>
internal sealed record EntityLibrarySummary(int Total, int Monitored);

/// <summary>
/// One Cove remote-id pair as forwarded from the entity's slot context: the metadata-server
/// <see cref="Endpoint"/> (e.g. <c>https://stashdb.org/graphql</c>) and the entity's <see cref="RemoteId"/>
/// on it. The handler selects the pair whose endpoint matches the stored StashDbEndpoint to obtain the
/// Whisparr lookup id — so the StashDB-endpoint match stays a single server-side source of truth.
/// </summary>
internal sealed record RemoteIdInput(string? Endpoint, string? RemoteId);

/// <summary>
/// The <c>/monitor-status</c> answer: the entity's quiet status plus which bulk verbs the connected generation
/// can honor for it.
/// </summary>
/// <remarks>
/// The two capability flags exist so the bulk menu does not offer an action the connected version answers with
/// a 400 — they are read from role-interface presence, not from a version comparison.
/// </remarks>
internal sealed record MonitorStatusResponse(
    bool Added, bool Monitored, int ScenesPresent, int ScenesTotal, bool HasCounts,
    bool AddSupported, bool OwnedImportSupported);

/// <summary>
/// The <c>/entity-status-batch</c> answer, keyed by Cove entity id.
/// </summary>
/// <remarks>An entity with no identity on the connected version, or none in Whisparr, is ABSENT from the map —
/// a failed upstream read degrades to no badges rather than to a thrown page.</remarks>
internal sealed record EntityStatusBatchResponse(
    IReadOnlyDictionary<int, Monitor.EntityStatus> States);

/// <summary>
/// The <c>/entity-library-summary</c> answer: the toolbar count over the connected instance's own entity list.
/// </summary>
/// <remarks><see cref="Available"/> is false when that list could not be read, which is what separates a real
/// zero from an unanswered one.</remarks>
internal sealed record EntityLibrarySummaryResponse(bool Available, int Total, int Monitored);
