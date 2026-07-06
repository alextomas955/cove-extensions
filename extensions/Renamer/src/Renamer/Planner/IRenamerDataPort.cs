namespace Renamer.Planner;

/// <summary>
/// The media-file kinds this extension can renamer. Drives entity-type-aware token degradation in
/// the <c>MetadataProjector</c>: only the media tokens a kind actually carries are projected.
/// Gallery is not yet renamed but is listed for completeness.
/// </summary>
public enum RenamerFileKind
{
    Video,
    Image,
    Audio,
    Gallery,
}

/// <summary>
/// A single physical file row in the renamer boundary's own vocabulary. This is a
/// <em>Renamer-owned</em> projection of a Cove <c>BaseFileEntity</c> + its media-typed subclass —
/// the production <c>Renamer.csproj</c> does NOT take a runtime dependency on Cove.Core entities,
/// so the Cove-backed <c>IRenamerDataPort</c> implementation maps live entities into this DTO at
/// the port boundary. It carries enough to (a) project tokens, (b) compute the OLD path
/// (<see cref="Basename"/> + <see cref="ParentFolderPath"/>), and (c) move sidecar captions.
///
/// Media-metadata fields are nullable: <c>null</c> means "this file kind does not carry this
/// token", which the projector OMITS from its dict so the engine's <c>{}</c> groups degrade
/// cleanly. (e.g. an audio file has null <see cref="Width"/>/<see cref="VideoCodec"/>.)
/// </summary>
/// <param name="FileId">The Cove <c>BaseFileEntity.Id</c>.</param>
/// <param name="Kind">Discriminates which media tokens are valid for this file.</param>
/// <param name="Basename">Current on-disk basename (e.g. <c>"clip.mkv"</c>).</param>
/// <param name="ParentFolderId">FK to the file's current parent <c>Folder</c>.</param>
/// <param name="ParentFolderPath">The parent folder's denormalized path (forward-slash form).</param>
/// <param name="Format">The file's container/format token source for <c>$ext</c> (may be empty).</param>
/// <param name="Width">Pixel width (Video/Image only); null otherwise.</param>
/// <param name="Height">Pixel height (Video/Image only); null otherwise.</param>
/// <param name="Duration">Seconds (Video/Audio only); null otherwise.</param>
/// <param name="VideoCodec">Video codec (Video only); null otherwise.</param>
/// <param name="AudioCodec">Audio codec (Video/Audio only); null otherwise.</param>
/// <param name="FrameRate">Frames/sec (Video only); null otherwise.</param>
/// <param name="Captions">Sidecar caption basenames + their ids (Video only; empty otherwise).</param>
/// <param name="SizeBytes">
/// The file's size in bytes (from Cove's <c>BaseFileEntity.Size</c>). Used ONLY for the cross-drive
/// free-space sum: the batch boundary sums each routed file's projected bytes per destination volume
/// and refuses rather than fill a disk. <c>0</c> is a benign default for unsized or test rows (a
/// 0-byte projection never pushes a volume over its headroom).
/// </param>
/// <param name="BitRate">
/// The file's stored overall bitrate in bits/sec (from Cove's <c>VideoFile.BitRate</c>); <c>null</c>
/// for kinds without a stored bitrate (e.g. images, or test rows). Projected as <c>$bitrate</c> in
/// kbps (bits/sec ÷ 1000), omitted when 0/absent.
/// </param>
public sealed record RenamerFile(
    int FileId,
    RenamerFileKind Kind,
    string Basename,
    int ParentFolderId,
    string ParentFolderPath,
    string Format = "",
    int? Width = null,
    int? Height = null,
    double? Duration = null,
    string? VideoCodec = null,
    string? AudioCodec = null,
    double? FrameRate = null,
    IReadOnlyList<RenamerCaption>? Captions = null,
    long SizeBytes = 0,
    long? BitRate = null);

/// <summary>A sidecar caption row (FK <c>FileId</c>); <see cref="Filename"/> is a basename only.</summary>
public sealed record RenamerCaption(int CaptionId, string Filename);

/// <summary>
/// A single performer of a media item in the renamer boundary's own vocabulary. Carries the fields
/// needed to order and filter the performer list before it is joined into <c>$performers</c>:
/// the stable <see cref="Id"/> (ascending-id ordering), the <see cref="Favorite"/> flag
/// (favorites-first ordering), and <see cref="Gender"/> (gender ordering / gender filtering).
/// The <c>$performers</c> token itself still renders the <see cref="Name"/> — the extra fields
/// only influence which performers survive a max-count limit and in what order.
/// <para>
/// <see cref="Gender"/> is a plain string (e.g. <c>"Female"</c>/<c>"Male"</c>) or <c>null</c> when
/// unset. The Cove gender enum is converted to its string name at the data-port boundary so this
/// record never depends on the Cove entity types.
/// </para>
/// </summary>
public sealed record RenamerPerformer(int Id, string Name, bool Favorite, string? Gender);

/// <summary>
/// A loaded media item (Video/Image/Audio) in the renamer boundary's own vocabulary — the
/// entity-level metadata the projector turns into scalar tokens + the per-file rows it renders
/// independently (every file is processed, not just the first). Performers carry a per-performer
/// record (name plus the id/favorite/gender used for ordering); tags are a pre-flattened name
/// list. Both are resolved from Cove's JOIN collections at the port boundary rather than here.
/// </summary>
/// <param name="EntityId">The Cove entity id (Video/Image/Audio).</param>
/// <param name="Kind">The media kind (used as the per-file <see cref="RenamerFile.Kind"/> too).</param>
/// <param name="Title">Entity title (<c>$title</c>); null/empty degrades.</param>
/// <param name="Code">Entity code (<c>$studioCode</c>); null/empty degrades.</param>
/// <param name="StudioName">Resolved <c>Studio?.Name</c> (<c>$studio</c>); null/empty degrades.</param>
/// <param name="Date">Entity date (<c>$date</c>/<c>$year</c>); null degrades.</param>
/// <param name="Organized">Cove's curation flag — drives the only-organized gate.</param>
/// <param name="Performers">
/// The item's performers as per-performer records (<c>$performers</c> multi-value side-input). The
/// token renders the names; the id/favorite/gender fields drive the optional performer ordering and
/// gender filtering applied before the max-count limit.
/// </param>
/// <param name="Tags">Tag names (<c>$tags</c> multi-value side-input).</param>
/// <param name="Files">Every physical file of the item (all files, not just the first).</param>
/// <param name="StudioId">
/// The entity's STABLE studio id (Cove's <c>Video/Image/Audio.StudioId</c>; <c>null</c> when the item
/// has no studio). The studio routing rule keys on THIS id — never on <see cref="StudioName"/> — so a
/// name typo or sanitization variant can never split one studio across two destination trees: route on
/// the stable id, then render the destination folder from the rewritten name.
/// </param>
/// <param name="ParentStudios">
/// The parent-studio ancestor chain, stored NEAREST-FIRST: index 0 is the direct studio's immediate
/// parent, walking toward the root. Lets an ancestor-studio rule match — the resolver's "first
/// ancestor with a rule wins" walk takes index 0 first. Each entry is a Renamer-owned
/// <c>(int Id, string Name)</c> tuple: the <c>Id</c> is the rule key; the <c>Name</c> drives the
/// <c>$parent_studio</c> display token. <c>null</c>/empty means no parent chain (no studio, or a
/// top-level studio).
/// </param>
/// <param name="Director">
/// The video's director (Cove's <c>Video.Director</c>); <c>null</c> for non-video kinds — Director is a
/// Video-only column, like <see cref="RenamerFile.VideoCodec"/> — so the <c>$director</c> token omits
/// naturally for image/audio. Projected as <c>$director</c>, omitted when null/empty.
/// </param>
public sealed record RenamerEntity(
    int EntityId,
    RenamerFileKind Kind,
    string? Title,
    string? Code,
    string? StudioName,
    DateOnly? Date,
    bool Organized,
    IReadOnlyList<RenamerPerformer> Performers,
    IReadOnlyList<string> Tags,
    IReadOnlyList<RenamerFile> Files,
    int? StudioId = null,
    IReadOnlyList<(int Id, string Name)>? ParentStudios = null,
    string? Director = null);

/// <summary>
/// The DB seam: the ONLY surface between the planner/executor and a live <c>CoveContext</c>.
/// Faking it (<c>FakeRenamerDataPort</c>) lets the planner / collision / gating / suffix logic be
/// unit-tested with zero DB; the Cove-backed implementation (<c>CoveRenamerDataPort</c>) does the
/// entity-graph load + mapping.
///
/// TYPE BOUNDARY: this interface speaks ONLY in the Renamer-owned DTOs above — never in Cove.Core
/// entity types — because the production <c>Renamer.csproj</c> does not take a runtime dependency
/// on Cove.Core. The Cove-backed implementation maps live <c>Video</c>/<c>VideoFile</c>/… graphs
/// into these records at the boundary (via the EF Include chain on the Cove side).
/// </summary>
public interface IRenamerDataPort
{
    /// <summary>
    /// Loads a media item's full file graph (entity metadata + every file + parent folder paths
    /// + captions) for the given kind + id, mapped into a <see cref="RenamerEntity"/>. Returns
    /// <c>null</c> if the item does not exist.
    /// </summary>
    Task<RenamerEntity?> LoadEntityAsync(RenamerFileKind kind, int entityId, CancellationToken ct = default);

    /// <summary>
    /// Returns every entity id of <paramref name="kind"/> currently in the library — an
    /// <c>AsNoTracking</c> id-only bulk query, NOT full <see cref="RenamerEntity"/> graphs. The
    /// per-id planner already does that full load when it actually plans each item, so a
    /// whole-library scan calls this first to enumerate candidates, then <see cref="LoadEntityAsync"/>
    /// per id exactly as it already does today.
    /// </summary>
    Task<IReadOnlyList<int>> LoadAllEntityIdsAsync(RenamerFileKind kind, CancellationToken ct = default);

    /// <summary>
    /// True iff some OTHER file row (id != <paramref name="selfFileId"/>) already occupies
    /// (<paramref name="folderId"/>, <paramref name="basename"/>) — the
    /// <c>(ParentFolderId, Basename)</c> unique-index pre-check.
    /// </summary>
    Task<bool> CollisionExistsAsync(int folderId, string basename, int selfFileId, CancellationToken ct = default);

    /// <summary>
    /// Resolves the destination <c>Folder</c> by its path, creating it if absent, and returns its
    /// id (mirrors Cove's own <c>FileOpsController.MoveFiles</c>). Used by the folder-move path.
    /// </summary>
    Task<int> GetOrCreateFolderIdAsync(string folderPath, CancellationToken ct = default);

    /// <summary>
    /// Read-only lookup of an existing destination <c>Folder</c> id by path; returns <c>null</c> when
    /// no folder row exists for that path. Unlike <see cref="GetOrCreateFolderIdAsync"/> this NEVER
    /// creates or saves, so the planner can resolve a target folder during a dry-run preview without
    /// persisting anything. A null result means the destination folder does not exist yet — which the
    /// planner treats as collision-free, since an absent folder holds no file rows.
    /// </summary>
    Task<int?> TryGetFolderIdAsync(string folderPath, CancellationToken ct = default);

    /// <summary>
    /// A READ-ONLY on-disk existence probe of a source file's current full path. Takes the
    /// forward-slash full path the planner already computes (<c>ParentFolderPath/Basename</c>); the
    /// port normalizes it to a native path internally. Used by the preview to warn a dry-run that a
    /// DB-listed source is gone. NEVER creates or saves, so it does not break preview purity (which
    /// forbids DB mutation, not disk reads). Returns true iff the file currently exists on disk.
    /// </summary>
    Task<bool> SourceExistsAsync(string fullPath, CancellationToken ct = default);

    /// <summary>
    /// Persists a planned set of file mutations (new basename / parent folder / caption renames)
    /// to the DB. The executor sets <c>Basename</c>/<c>ParentFolderId</c> only — never <c>.Path</c>,
    /// which Cove recomputes on save. Returns the number of file rows changed.
    /// </summary>
    Task<int> SaveAsync(IReadOnlyList<RenamerFileMutation> mutations, CancellationToken ct = default);
}

/// <summary>
/// One file's intended DB mutation, produced by the executor and handed to
/// <see cref="IRenamerDataPort.SaveAsync"/>. Caption renames travel with their file.
/// </summary>
/// <param name="FileId">The file row to mutate.</param>
/// <param name="NewBasename">The new basename to set.</param>
/// <param name="NewParentFolderId">The new parent folder id, or null for an in-place renamer.</param>
/// <param name="CaptionRenames">(captionId, newFilename) pairs for moved sidecars.</param>
public sealed record RenamerFileMutation(
    int FileId,
    string NewBasename,
    int? NewParentFolderId,
    IReadOnlyList<(int CaptionId, string NewFilename)>? CaptionRenames = null);
