namespace WhisparrSync.Contracts;

/// <summary>
/// The <c>/scene-detail</c> request body: ONLY the scene's Cove entity id. The scene's StashDB
/// identity is resolved SERVER-SIDE from this id (never a caller-supplied remote id), and the body carries
/// no url/key — the handler uses the stored creds only.
/// </summary>
internal sealed record SceneDetailRequest(int CoveId);

/// <summary>
/// The <c>/scene-releases-list</c> request body (on-expand): ONLY the scene's Cove entity id. Same
/// server-side identity resolution + stored-creds-only posture as <see cref="SceneDetailRequest"/>.
/// </summary>
internal sealed record SceneReleasesRequest(int CoveId);

/// <summary>
/// The <c>/scene-add</c> request body: ONLY the scene's Cove entity id. The scene's
/// StashDB identity + title are resolved SERVER-SIDE from this id (never caller-supplied), and the body
/// carries no url/key — the handler uses the stored creds only. Mirrors <see cref="SceneDetailRequest"/>.
/// </summary>
internal sealed record SceneAddRequest(int CoveId);

/// <summary>
/// The <c>/scene-search</c> request body: ONLY the scene's Cove entity id. Same server-side
/// identity resolution + stored-creds-only posture as <see cref="SceneAddRequest"/>.
/// </summary>
internal sealed record SceneSearchRequest(int CoveId);

/// <summary>
/// The <c>/scene-monitor</c> request body: the scene's Cove entity id + the target
/// <see cref="Monitored"/> state. The scene's StashDB identity is resolved server-side from
/// <see cref="CoveId"/>; the body carries no url/key.
/// </summary>
internal sealed record SceneMonitorRequest(int CoveId, bool Monitored);

/// <summary>
/// The <c>/scene-exclusion</c> request body: the scene's Cove id + the target
/// <see cref="Exclude"/> state (true adds the exclusion, false removes it). The scene's StashDB identity is
/// resolved SERVER-SIDE from <see cref="CoveId"/> (never a caller-supplied StashDB/exclusion id — the
/// un-exclude id is matched by the adapter via foreignId), and the body carries no url/key — the handler
/// uses the stored creds only.
/// </summary>
internal sealed record SceneExclusionRequest(int CoveId, bool Exclude);

/// <summary>
/// The <c>/scene-grab-release</c> request body: the scene's Cove id + the picked release's
/// <see cref="Guid"/> and <see cref="IndexerId"/>. The scene identity is resolved server-side from
/// <see cref="CoveId"/>; the guid/indexerId are release handles the picker obtained from this extension's
/// own <c>/scene-releases-list</c> read. The body carries no url/key; the guid is never echoed to a
/// log. Fields are nullable so a malformed body is rejected cleanly.
/// </summary>
internal sealed record SceneGrabReleaseRequest(int CoveId, string? Guid, int IndexerId);

/// <summary>The per-card status batch request: the visible grid page's Cove video ids. Nullable for a clean 400.</summary>
internal sealed record SceneStatusBatchRequest(int[]? CoveIds);

/// <summary>The <c>/scene-status-summary</c> answer: the library-wide by-state partition the toolbar paints.</summary>
internal sealed record SceneStatusSummaryResponse(SceneStatus.SceneStatusCounts Counts);

/// <summary>
/// The <c>/scene-status-batch</c> answer, keyed by Cove video id.
/// </summary>
/// <remarks>An id the server could not resolve is ABSENT from the map rather than present as a null state, so
/// a caller distinguishes "not classifiable" from "not added".</remarks>
internal sealed record SceneStatusBatchResponse(
    IReadOnlyDictionary<int, SceneStatus.SceneCardStatus> States);

/// <summary>
/// The answer to a search verb. <see cref="Searched"/> is false for a scene Whisparr does not hold as a movie
/// — nothing to search, which is a handled outcome rather than a failure.
/// </summary>
internal sealed record SceneSearchResponse(bool Searched);

/// <summary>The <c>/scene-exclusion</c> answer, echoing the exclusion state now in force.</summary>
internal sealed record SceneExclusionResponse(bool Excluded);

/// <summary>The <c>/scene-grab-release</c> answer for the one interactive grab.</summary>
internal sealed record SceneGrabResponse(bool Grabbed);

/// <summary>
/// The <c>/scene-releases-list</c> answer.
/// </summary>
/// <remarks>An empty list covers three cases the picker renders identically — the movie set could not be read,
/// the scene is not an added movie, or the indexers returned nothing. None is an error: this read grabs nothing.</remarks>
internal sealed record SceneReleasesResponse(IReadOnlyList<Client.WhisparrRelease> Releases);
