using System.Text.Json.Serialization;
using Cove.Extensions.Shared;

namespace Renamer.Planner;

/// <summary>
/// The media-file kinds this extension can renamer. Drives entity-type-aware token degradation in
/// the <c>MetadataProjector</c>: only the media tokens a kind actually carries are projected.
/// Gallery is not yet renamed but is listed for completeness.
/// </summary>
[JsonConverter(typeof(CamelCaseStringEnumConverter))]
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

/// <summary>The entity tables a stored rule can name.</summary>
public enum RenamerEntityKind
{
    Tag,
    Performer,
}

/// <summary>The outcome of resolving stored rule names against one entity table.</summary>
/// <param name="TableHasRows">Whether the table holds any row at all, independent of the names asked for.</param>
/// <param name="Matches">Every <c>(id, name)</c> row whose name matched one of the requested names, case-insensitively.</param>
public readonly record struct NameResolution(
    bool TableHasRows,
    IReadOnlyList<(int Id, string Name)> Matches);

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
/// record (name plus the id/favorite/gender used for ordering); tags carry the id/name pairs the tag
/// rules key on. Both are resolved from Cove's JOIN collections at the port boundary rather than here.
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
/// <param name="TagRefs">
/// The item's tags as <c>(int Id, string Name)</c> pairs. The <c>Id</c> is the rule key - tag routing,
/// tag exclusion and the tag whitelist/blacklist all match on it, so a renamed tag keeps its rules -
/// while the <c>Name</c> drives the <c>$tags</c> display token and the user-visible route reason. They
/// travel as pairs rather than as parallel id and name lists precisely because routing takes the FIRST
/// tag in this order whose id has a rule: two lists that drift by one element would silently route to
/// another tag's destination.
/// </param>
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
    IReadOnlyList<(int Id, string Name)> TagRefs,
    IReadOnlyList<RenamerFile> Files,
    int? StudioId = null,
    IReadOnlyList<(int Id, string Name)>? ParentStudios = null,
    string? Director = null)
{
    /// <summary>The tag NAMES the <c>$tags</c> token renders, in <see cref="TagRefs"/> order.</summary>
    /// <remarks>
    /// Derived rather than accepted beside the ids, because the two drifting apart is silent in both
    /// directions: ids without matching names render the token empty, and names without ids match no
    /// rule at all. Neither state is constructible.
    /// <para>
    /// Recomputed per read rather than cached, because a cached list is copied verbatim by a
    /// <c>with</c> expression and would go stale exactly where a caller replaces the pairs. One
    /// projection over an item's own tags is bounded work; nothing here scales with the library.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> Tags => [.. TagRefs.Select(t => t.Name)];
}

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
    /// The absolute library paths Cove is configured to scan, in configuration order.
    /// </summary>
    /// <remarks>
    /// The anchor a rename cannot move, and the reason this member exists. A destination's folder
    /// template resolves against a library path; anchoring it on the file's own parent folder, which
    /// is the previous run's output, re-appends the rendered folder every run, so the item descends
    /// one directory per pass until the path length refuses it.
    /// <para>
    /// A property rather than a query: this is host configuration held in memory. Empty means the host
    /// declares no library path at all, and a file under none of the declared paths is planned as
    /// <see cref="RenamerStatus.SkipUnanchored"/> rather than moved relative to itself.
    /// </para>
    /// </remarks>
    IReadOnlyList<string> LibraryRoots { get; }

    /// <summary>
    /// Resolves stored rule names to the stable ids they name, reading only
    /// <paramref name="names"/> rather than the whole table.
    /// </summary>
    /// <remarks>
    /// Contract a caller cannot read off the signature. Matching is case-insensitive, so a name
    /// matching several entities that differ only by case returns ALL of them and the caller decides
    /// which one the rule collapses onto. A name with no match is simply absent from
    /// <see cref="NameResolution.Matches"/>.
    /// <para>
    /// <see cref="NameResolution.TableHasRows"/> travels WITH the matches because the two are only
    /// meaningful together: no matches over a populated table means those entities are genuinely gone,
    /// while no matches over an empty table means the library is not readable yet. Returning the
    /// matches alone would let a caller convert during the second state and discard every rule the user
    /// wrote.
    /// </para>
    /// </remarks>
    Task<NameResolution> ResolveNamesAsync(
        RenamerEntityKind kind, IReadOnlyList<string> names, CancellationToken ct = default);

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
    /// Returns the next page of <paramref name="kind"/>'s entity ids after
    /// <paramref name="afterEntityId"/>, at most <paramref name="take"/> long.
    /// </summary>
    /// <remarks>
    /// Contract a caller cannot read off the signature: the result is STRICTLY ASCENDING and holds
    /// only ids greater than <paramref name="afterEntityId"/>, so the last id of a page is the cursor
    /// for the next one. That total order is the whole reason this member exists — a cursor over a
    /// provider-ordered result would silently skip and repeat entities across pages as rows are
    /// inserted and deleted. A page shorter than <paramref name="take"/> means the kind is exhausted;
    /// a non-positive <paramref name="take"/> and a non-renamable kind both return empty rather than
    /// throwing, mirroring <see cref="LoadAllEntityIdsAsync"/>.
    /// </remarks>
    Task<IReadOnlyList<int>> LoadEntityIdPageAsync(
        RenamerFileKind kind, int afterEntityId, int take, CancellationToken ct = default);

    /// <summary>
    /// The batch counterpart to <see cref="LoadEntityAsync"/>: loads many entities of one
    /// <paramref name="kind"/> across a few chunked <c>WHERE Id IN (...)</c> queries — the SAME Include
    /// graph, mapped through the SAME per-entity mapper — so each returned DTO is byte-identical to the
    /// single-load path. Lets a whole-library scan collapse N per-entity round-trips into ~N/chunk.
    /// </summary>
    /// <remarks>
    /// Contract a caller cannot infer from the signature: the result holds one entry per id that
    /// EXISTS — a missing id is omitted, never a null slot and never a throw. Ordering within the
    /// result is NOT guaranteed by the DB, so an order-sensitive caller (the scan) re-orders by its
    /// own id list. Gallery (non-renamable) and an empty <paramref name="ids"/> return an empty list.
    /// </remarks>
    Task<IReadOnlyList<RenamerEntity>> LoadEntitiesAsync(RenamerFileKind kind, IReadOnlyList<int> ids, CancellationToken ct = default);

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

    /// <summary>
    /// The mutating write-seam: applies each mutation (Basename / ParentFolderId / caption filenames)
    /// and persists them in one save, returning each saved file's recomputed <c>Path</c>.
    /// </summary>
    /// <remarks>
    /// Contract the executor's rollback spine depends on: an implementation throws on a save failure
    /// (e.g. a unique-index violation) rather than swallowing it, so the caller's catch can roll the
    /// on-disk move back.
    /// <para>
    /// The signature takes a SET, but both callers — the executor's per-item save and the undo
    /// replayer's — pass exactly one mutation, and the shipped implementation is written for that: it
    /// issues one tracked query per mutation, and another per mutation whose folder changed, with no
    /// dedup. It is therefore single-mutation in practice, and a caller that ever passes a real batch
    /// should chunk it the way <c>LoadEntitiesAsync</c> already does (one <c>WHERE Id IN (…)</c> per
    /// chunk) rather than inherit the per-row shape.
    /// </para>
    /// <para>
    /// An implementation may return FEWER rows than it was handed. The shipped one throws instead, but
    /// that is its own behavior and not a guarantee stated here, so a caller reading a specific file's
    /// result must handle its absence rather than index into the list.
    /// </para>
    /// </remarks>
    Task<IReadOnlyList<SavedFile>> ApplyAndSaveAsync(IReadOnlyList<RenamerFileMutation> mutations, CancellationToken ct = default);
}

/// <summary>The recomputed identity of a saved file row, read back after a save for the Path assertion + event.</summary>
/// <param name="FileId">The file row id.</param>
/// <param name="RecomputedPath">The <c>BaseFileEntity.Path</c> Cove recomputed on save (forward-slash).</param>
public readonly record struct SavedFile(int FileId, string RecomputedPath);

/// <summary>
/// One file's intended DB mutation, produced by the executor and handed to
/// <see cref="IRenamerDataPort.SaveAsync"/>. Caption renames travel with their file.
/// </summary>
/// <param name="FileId">The file row to mutate.</param>
/// <param name="NewBasename">The new basename to set.</param>
/// <param name="NewParentFolderId">The new parent folder id, or null for an in-place renamer.</param>
/// <param name="CaptionRenames">(captionId, newFilename) pairs for moved sidecars.</param>
/// <param name="EntityTitle">
/// The one-time title write that rides with this file's save, or <c>null</c> when the item already
/// carries a title or the filename-as-title fallback is off. It names the OWNING entity rather than the
/// file because a title belongs to the item; it travels on the file mutation so it lands in the same
/// <c>SaveChangesAsync</c> as the rename that derived it, which is what makes "renamed but title not
/// recorded" unreachable rather than merely unlikely.
/// </param>
public sealed record RenamerFileMutation(
    int FileId,
    string NewBasename,
    int? NewParentFolderId,
    IReadOnlyList<(int CaptionId, string NewFilename)>? CaptionRenames = null,
    RenamerEntityTitleWrite? EntityTitle = null);

/// <summary>A filename-derived title to record on a media entity that has none.</summary>
/// <remarks>
/// Recording the derivation once is what turns the filename-as-title fallback into a first-run-only
/// path; left un-recorded it re-reads its own output every pass. See
/// <c>MetadataProjector.DerivedTitle</c> for the whole statement.
/// </remarks>
/// <param name="Kind">Which media table holds the entity.</param>
/// <param name="EntityId">The entity row to write.</param>
/// <param name="Title">The derived title; never empty, since an empty derivation travels as no write at all.</param>
public readonly record struct RenamerEntityTitleWrite(RenamerFileKind Kind, int EntityId, string Title);
