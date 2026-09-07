using WhisparrSync.Contracts;

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
    /// Streams every Cove video projected into a <see cref="CoveVideo"/>, in ascending <c>Video.Id</c> order.
    /// Read-only (<c>AsNoTracking</c>); never mutates.
    /// </summary>
    /// <remarks>
    /// The seam every library-wide aggregate reads through, and the only one that answers the whole library at
    /// all. Paged by keyset rather than one long-lived reader, because a reader held open across the fold would
    /// pin <c>CoveContext</c> — which is not thread-safe — for the whole pass.
    /// There is no snapshot: a video inserted mid-stream above the cursor is included, one below it is not.
    /// </remarks>
    IAsyncEnumerable<CoveVideo> StreamAllVideosAsync(CancellationToken ct = default);

    /// <summary>Counts the Cove videos. Read-only (<c>AsNoTracking</c>); never mutates.</summary>
    /// <remarks>
    /// Answers how many scenes a library-wide pass will visit without holding any of them, so the pass can be
    /// divided into a fixed number of ranges before it starts.
    /// </remarks>
    Task<int> CountVideosAsync(CancellationToken ct = default);

    /// <summary>
    /// Streams every Cove video's id ALONE, in ascending <c>Video.Id</c> order. Read-only
    /// (<c>AsNoTracking</c>); never mutates.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="StreamAllVideosAsync"/> because finding range boundaries needs nothing but the
    /// id, so projecting the column skips the remote-id and fingerprint Include graph entirely. Keyset-paged for
    /// the same reason that stream is: an offset re-walks the rows it skips. A caller retains only the ids it
    /// cuts on, so the pass is O(library) in time with an output bounded by the number of ranges.
    /// </remarks>
    IAsyncEnumerable<int> StreamVideoIdsAsync(CancellationToken ct = default);

    /// <summary>
    /// Streams the Cove videos whose id falls in one range, projected identically to
    /// <see cref="StreamAllVideosAsync"/>. Read-only (<c>AsNoTracking</c>); never mutates.
    /// </summary>
    /// <remarks>
    /// The range is EXCLUSIVE of <paramref name="afterCoveId"/> and INCLUSIVE of <paramref name="upToCoveId"/>,
    /// so consecutive ranges share a boundary id and every id belongs to exactly one of them — no gap at a
    /// boundary and no scene visited twice. A range whose upper bound is <see cref="int.MaxValue"/> also picks
    /// up rows inserted after the boundaries were chosen. Same Include chain and same mapping as the
    /// whole-library stream, so a scene projects identically whichever way it is read.
    /// </remarks>
    IAsyncEnumerable<CoveVideo> StreamVideosInRangeAsync(
        int afterCoveId, int upToCoveId, CancellationToken ct = default);

    /// <summary>
    /// Streams every Cove file path (denormalized forward-slash), projecting the path column ALONE. Read-only
    /// (<c>AsNoTracking</c>); never mutates.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="StreamAllVideosAsync"/> because the folder derivation needs nothing but the path,
    /// so projecting the column skips the remote-id and fingerprint Include graph entirely.
    /// </remarks>
    IAsyncEnumerable<string> StreamFilePathsAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns the subset of <paramref name="candidateIds"/> that at least one Cove video carries as a remote id
    /// on <paramref name="family"/>'s endpoint. Read-only (<c>AsNoTracking</c>); never mutates.
    /// </summary>
    /// <remarks>
    /// The bounded answer to "does Cove own any of these?", for a diff that only ever asks about the ids on the
    /// catalogue page in front of the reader. Reading the whole owned library to answer it would scale with the
    /// library while the question scales with the page. Ids are matched case-insensitively, as every remote-id
    /// comparison here is.
    /// </remarks>
    Task<IReadOnlySet<string>> LoadOwnedRemoteIdsAsync(
        DiscoveryIdFamily family, IReadOnlyCollection<string> candidateIds, CancellationToken ct = default);

    /// <summary>
    /// Loads a single Cove video by its <c>Video.Id</c>, mapped identically to
    /// <see cref="StreamAllVideosAsync"/> — the scene-detail read seam. Returns null when the id is
    /// absent. Read-only (<c>AsNoTracking</c>); never mutates.
    /// </summary>
    Task<CoveVideo?> LoadVideoByIdAsync(int coveId, CancellationToken ct = default);

    /// <summary>
    /// Loads the Cove videos for a set of ids in ONE query — the per-card status-badge read seam, so a visible
    /// grid page classifies with a single DB read + a single Whisparr fetch rather than one call per card. Ids
    /// with no video are simply absent from the result; each present one is mapped identically to
    /// <see cref="StreamAllVideosAsync"/>. Read-only (<c>AsNoTracking</c>); never mutates.
    /// </summary>
    Task<IReadOnlyList<CoveVideo>> LoadVideosByIdsAsync(IReadOnlyList<int> coveIds, CancellationToken ct = default);

    /// <summary>
    /// Loads the Cove videos attributed to one entity — a studio (by <c>Video.StudioId</c>) or a performer
    /// (by a <c>VideoPerformers</c> membership) — each projected identically to <see cref="StreamAllVideosAsync"/>
    /// (same StashDB-endpoint filter). This is the "add all missing" diff source: the entity's OWN
    /// Cove scenes are diffed locally against the fetched Whisparr movie set, so the missing set is computed
    /// WITHOUT any StashDB call. <paramref name="coveEntityId"/> is the Cove <c>Studio.Id</c> / <c>Performer.Id</c>.
    /// Read-only (<c>AsNoTracking</c>); never mutates.
    /// </summary>
    /// <remarks>
    /// A <see cref="EntityKind.Tag"/> read returns the WHOLE owned library (not the tag's scenes): a
    /// tag-catalogue scene Cove owns under ANY tag must be subtracted from the missing diff, so the owned set is
    /// deliberately not tag-scoped — which makes it the WHOLE LIBRARY. This seam therefore REFUSES
    /// <see cref="EntityKind.Tag"/> rather than serving it: use
    /// <see cref="LoadOwnedRemoteIdsAsync"/> over one catalogue page's candidate ids, the bounded form of the
    /// same subtraction that the discovery path already takes.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="kind"/> is <see cref="EntityKind.Tag"/>.
    /// </exception>
    Task<IReadOnlyList<CoveVideo>> LoadVideosForEntityAsync(
        EntityKind kind, int coveEntityId, CancellationToken ct = default);

    /// <summary>
    /// Loads one studio's / performer's / tag's OWN metadata-server ids (not its scenes') by Cove id — the
    /// batch-monitor + tag-discovery identity source. A monitor/search over a multi-selection has only the Cove
    /// entity ids (the host bulk-action payload carries no remote ids), so the server resolves each entity's
    /// StashDB/TPDB id here, exactly as the per-entity <c>/monitor</c> handler resolves the id the slot forwards.
    /// A tag resolves its own id from <c>Tag.RemoteIds</c> the same way, on either generation — the StashDB id
    /// on the newer one, the ThePornDB id on the older. Read-only
    /// (<c>AsNoTracking</c>); returns null when no entity has <paramref name="coveEntityId"/>.
    /// </summary>
    Task<CoveEntityIdentity?> LoadEntityIdentityAsync(
        EntityKind kind, int coveEntityId, CancellationToken ct = default);

    /// <summary>
    /// Loads a studio's child sub-studios' OWN metadata-server ids by the parent Cove id — one
    /// <see cref="CoveEntityIdentity"/> per child. Empty when the studio has no children (the byte-unchanged
    /// single-studio case) or the id is absent. Read-only (<c>AsNoTracking</c>); never mutates.
    /// </summary>
    /// <remarks>
    /// A studio hierarchy read spans the whole library, so CoveContext's per-principal authz query filters
    /// undercount it under a non-owner principal; the call site runs it under <c>CovePrincipal.System()</c>. The
    /// resolved child ids join the parent's own id into one <c>studios INCLUDES [ids]</c> catalogue read, so the
    /// parent's missing list unions the network in a single query rather than one query per child.
    /// </remarks>
    Task<IReadOnlyList<CoveEntityIdentity>> LoadStudioChildIdentitiesAsync(
        int coveEntityId, CancellationToken ct = default);

    /// <summary>
    /// Loads every Cove studio's / performer's OWN metadata-server ids in ONE query — the library-wide
    /// monitored-summary source for the studios/performers toolbar row. Each entity yields one
    /// <see cref="CoveEntityIdentity"/> (its StashDB/TPDB ids), including entities with none (empty ids) so the
    /// caller's total reflects the whole library, not just the mappable ones. Read-only (<c>AsNoTracking</c>).
    /// </summary>
    Task<IReadOnlyList<CoveEntityIdentity>> LoadAllEntityIdentitiesAsync(
        EntityKind kind, CancellationToken ct = default);

    /// <summary>
    /// Loads every Cove studio's / performer's Cove id alongside its OWN metadata-server ids in ONE query — the
    /// whole-library enumeration for the bulk "Sync my library to Whisparr" surface. Each entity yields one
    /// <see cref="CoveEntityRef"/> (its Cove PK + its StashDB/TPDB ids); an entity with no remote id yields empty
    /// id lists and is never omitted. Read-only (<c>AsNoTracking</c>); never mutates.
    /// </summary>
    /// <remarks>
    /// CoveContext applies per-principal authz query filters that undercount a library-wide read under a
    /// non-request principal; the call site sets <c>CovePrincipal.System()</c> to bypass them.
    /// </remarks>
    Task<IReadOnlyList<CoveEntityRef>> LoadAllEntityRefsAsync(
        EntityKind kind, CancellationToken ct = default);

    /// <summary>
    /// Loads Cove's own image-bearing performers matching any of <paramref name="stashIds"/> (StashDB ids) or
    /// <paramref name="names"/> (case-insensitive). Returns only performers that carry an image, so an absent
    /// match is the glyph-fallback signal. Read-only (<c>AsNoTracking</c>); never mutates.
    /// </summary>
    /// <remarks>
    /// A library-wide performer read is undercounted under a non-owner principal by CoveContext's per-principal
    /// authz query filters, so the call site runs it under <c>CovePrincipal.System()</c>.
    /// </remarks>
    Task<IReadOnlyList<CovePerformerImage>> LoadPerformerImagesAsync(
        IReadOnlyCollection<string> stashIds, IReadOnlyCollection<string> names, CancellationToken ct = default);
}

/// <summary>
/// One Cove performer with an image: its Cove id (the <c>/api/performers/{id}/image</c> handle),
/// <see cref="Name"/> (the name match key), and endpoint-filtered StashDB ids (the id match key).
/// </summary>
internal sealed record CovePerformerImage(int CoveId, string Name, IReadOnlyList<string> StashIds);

/// <summary>
/// One studio's / performer's metadata-server ids in the reconciliation boundary's own vocabulary — the
/// per-version identity keys for a batch monitor/search, never a Cove.Core entity.
/// </summary>
/// <param name="StashIds">The entity's StashDB ids (its <c>RemoteIds</c> filtered to the configured StashDB endpoint) — the v3 key.</param>
/// <param name="TpdbIds">The entity's ThePornDB ids (filtered to the configured TPDB endpoint) — the v2 key.</param>
/// <param name="Name">
/// The entity's display name, when the read supplied one. A provider that can look an entity up by name (TPDB
/// resolves a tag name to the numeric id its scene filter requires) uses this to recover an id the entity does
/// not store; null simply means no name-based recovery is possible.
/// </param>
internal sealed record CoveEntityIdentity(
    IReadOnlyList<string> StashIds,
    IReadOnlyList<string> TpdbIds,
    string? Name = null);

/// <summary>
/// One studio's / performer's Cove id + its metadata-server ids. It carries the Cove PK that
/// <see cref="CoveEntityIdentity"/> omits — the id a bulk action addresses the entity by.
/// </summary>
/// <param name="CoveId">The Cove <c>Studio.Id</c> / <c>Performer.Id</c>.</param>
/// <param name="StashIds">The entity's StashDB ids (its <c>RemoteIds</c> filtered to the configured StashDB endpoint) — the v3 key.</param>
/// <param name="TpdbIds">The entity's ThePornDB ids (filtered to the configured TPDB endpoint) — the v2 key.</param>
internal sealed record CoveEntityRef(
    int CoveId,
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
