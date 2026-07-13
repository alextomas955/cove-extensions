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
    /// MATCH-01 identity chain. Read-only (<c>AsNoTracking</c>); never mutates.
    /// </summary>
    Task<IReadOnlyList<CoveVideo>> LoadAllVideosAsync(CancellationToken ct = default);
}

/// <summary>
/// A Cove video in the reconciliation boundary's own vocabulary — enough to run the identity chain
/// against the Whisparr movie set (MATCH-01) without ever exposing a Cove.Core entity.
/// </summary>
/// <param name="CoveId">The Cove <c>Video.Id</c>.</param>
/// <param name="Title">Entity title (fuzzy leg); null/empty degrades.</param>
/// <param name="Date">Entity date (fuzzy leg year); null degrades.</param>
/// <param name="StashIds">
/// The video's StashDB ids — its <c>RemoteIds</c> filtered to the configured StashDB endpoint. This is
/// the PRIMARY match key (Cove StashDB UUID ↔ Whisparr scene stashId). Empty when the video carries no
/// id for that endpoint.
/// </param>
/// <param name="FilePaths">Each <c>VideoFile.Path</c> (denormalized forward-slash) — the path leg.</param>
/// <param name="Fingerprints">
/// The files' content fingerprints (oshash/md5/phash). Carried for completeness / Cove-internal use only:
/// Whisparr exposes no comparable file hash, so this leg has NO cross-system counterpart and never gates
/// a match (Pitfall 3).
/// </param>
internal sealed record CoveVideo(
    int CoveId,
    string? Title,
    DateOnly? Date,
    IReadOnlyList<string> StashIds,
    IReadOnlyList<string> FilePaths,
    IReadOnlyList<CoveFingerprint> Fingerprints);

/// <summary>One content fingerprint of a Cove file: <see cref="Type"/> is "oshash"/"md5"/"phash".</summary>
internal sealed record CoveFingerprint(string Type, string Value);
