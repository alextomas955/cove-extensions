using WhisparrSync.Monitor;

namespace WhisparrSync.Library;

/// <summary>
/// The Cove-library read seam: the ONLY surface between reconciliation and a live Cove database. Faking
/// it (<c>FakeCoveLibraryPort</c>) lets the downstream matcher / reconciliation logic be unit-tested with
/// zero DB; the Cove-backed implementation (<c>CoveLibraryPort</c>) does the entity-graph load + mapping.
///
/// TYPE BOUNDARY: this interface speaks ONLY in the WhisparrSync-owned DTOs below — never in Cove.Core
/// entity types — because the production <c>WhisparrSync.csproj</c> takes no runtime dependency on
/// Cove.Core (the entity types are compile-time only, host-provided at runtime). The Cove-backed
/// implementation maps live <c>Video</c>/<c>VideoFile</c>/<c>VideoRemoteId</c> graphs into these records
/// at the boundary via the EF Include chain.
/// </summary>
internal interface ICoveLibraryPort
{
    /// <summary>
    /// Loads every Cove video projected into a <see cref="CoveVideo"/> — the raw material for the
    /// identity chain. Read-only (<c>AsNoTracking</c>); never mutates.
    /// </summary>
    Task<IReadOnlyList<CoveVideo>> LoadAllVideosAsync(CancellationToken ct = default);

    /// <summary>
    /// Loads a single Cove video by its <c>Video.Id</c>, mapped identically to
    /// <see cref="LoadAllVideosAsync"/> — the scene-detail read seam. Returns null when the id is
    /// absent. Read-only (<c>AsNoTracking</c>); never mutates.
    /// </summary>
    Task<CoveVideo?> LoadVideoByIdAsync(int coveId, CancellationToken ct = default);

    /// <summary>
    /// Loads the Cove videos for a set of ids in ONE query — the per-card status-badge read seam, so a visible
    /// grid page classifies with a single DB read + a single Whisparr fetch rather than one call per card. Ids
    /// with no video are simply absent from the result; each present one is mapped identically to
    /// <see cref="LoadAllVideosAsync"/>. Read-only (<c>AsNoTracking</c>); never mutates.
    /// </summary>
    Task<IReadOnlyList<CoveVideo>> LoadVideosByIdsAsync(IReadOnlyList<int> coveIds, CancellationToken ct = default);

    /// <summary>
    /// Loads the Cove videos attributed to one entity — a studio (by <c>Video.StudioId</c>) or a performer
    /// (by a <c>VideoPerformers</c> membership) — each projected identically to <see cref="LoadAllVideosAsync"/>
    /// (same StashDB-endpoint filter). This is the "add all missing" diff source: the entity's OWN
    /// Cove scenes are diffed locally against the fetched Whisparr movie set, so the missing set is computed
    /// WITHOUT any StashDB call. <paramref name="coveEntityId"/> is the Cove <c>Studio.Id</c> / <c>Performer.Id</c>.
    /// Read-only (<c>AsNoTracking</c>); never mutates.
    /// </summary>
    Task<IReadOnlyList<CoveVideo>> LoadVideosForEntityAsync(
        EntityKind kind, int coveEntityId, CancellationToken ct = default);

    /// <summary>
    /// Loads one studio's / performer's OWN metadata-server ids (not its scenes') by Cove id — the
    /// batch-monitor identity source. A monitor/search over a multi-selection has only the Cove entity ids (the
    /// host bulk-action payload carries no remote ids), so the server resolves each entity's StashDB/TPDB id
    /// here, exactly as the per-entity <c>/monitor</c> handler resolves the id the slot forwards. Read-only
    /// (<c>AsNoTracking</c>); returns null when no entity has <paramref name="coveEntityId"/>.
    /// </summary>
    Task<CoveEntityIdentity?> LoadEntityIdentityAsync(
        EntityKind kind, int coveEntityId, CancellationToken ct = default);

    /// <summary>
    /// Loads every Cove studio's / performer's OWN metadata-server ids in ONE query — the library-wide
    /// monitored-summary source for the studios/performers toolbar row. Each entity yields one
    /// <see cref="CoveEntityIdentity"/> (its StashDB/TPDB ids), including entities with none (empty ids) so the
    /// caller's total reflects the whole library, not just the mappable ones. Read-only (<c>AsNoTracking</c>).
    /// </summary>
    Task<IReadOnlyList<CoveEntityIdentity>> LoadAllEntityIdentitiesAsync(
        EntityKind kind, CancellationToken ct = default);
}

/// <summary>
/// One studio's / performer's metadata-server ids in the reconciliation boundary's own vocabulary — the
/// per-version identity keys for a batch monitor/search, never a Cove.Core entity.
/// </summary>
/// <param name="StashIds">The entity's StashDB ids (its <c>RemoteIds</c> filtered to the configured StashDB endpoint) — the v3 key.</param>
/// <param name="TpdbIds">The entity's ThePornDB ids (filtered to the configured TPDB endpoint) — the v2 key.</param>
internal sealed record CoveEntityIdentity(
    IReadOnlyList<string> StashIds,
    IReadOnlyList<string> TpdbIds);

/// <summary>
/// A Cove video in the reconciliation boundary's own vocabulary — enough to run the identity chain
/// against the Whisparr movie set without ever exposing a Cove.Core entity.
/// </summary>
/// <param name="CoveId">The Cove <c>Video.Id</c>.</param>
/// <param name="Title">Entity title (fuzzy leg); null/empty degrades.</param>
/// <param name="Date">Entity date (fuzzy leg year); null degrades.</param>
/// <param name="StashIds">
/// The video's StashDB ids — its <c>RemoteIds</c> filtered to the configured StashDB endpoint. This is
/// the PRIMARY match key (Cove StashDB UUID ↔ Whisparr scene stashId). Empty when the video carries no
/// id for that endpoint.
/// </param>
/// <param name="TpdbIds">
/// The video's ThePornDB ids — its <c>RemoteIds</c> filtered to the configured ThePornDB endpoint. This is
/// the v2 match key (Cove TPDB id ↔ Whisparr v2 episode foreignId). Empty when the video carries no id for
/// that endpoint.
/// </param>
/// <param name="FilePaths">Each <c>VideoFile.Path</c> (denormalized forward-slash) — the path leg.</param>
/// <param name="Fingerprints">
/// The files' content fingerprints (oshash/md5/phash). Carried for completeness / Cove-internal use only:
/// Whisparr exposes no comparable file hash, so this leg has NO cross-system counterpart and never gates
/// a match.
/// </param>
internal sealed record CoveVideo(
    int CoveId,
    string? Title,
    DateOnly? Date,
    IReadOnlyList<string> StashIds,
    IReadOnlyList<string> TpdbIds,
    IReadOnlyList<string> FilePaths,
    IReadOnlyList<CoveFingerprint> Fingerprints);

/// <summary>One content fingerprint of a Cove file: <see cref="Type"/> is "oshash"/"md5"/"phash".</summary>
internal sealed record CoveFingerprint(string Type, string Value);
