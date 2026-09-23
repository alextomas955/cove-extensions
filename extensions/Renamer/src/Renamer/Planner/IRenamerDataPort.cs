using Renamer.Engine;


namespace Renamer.Planner;

/// <summary>One physical file row in the renamer's own vocabulary.</summary>
/// <remarks>
/// Mapped at the port boundary so the planner and engine take no dependency on Cove.Core. A null
/// media-metadata field means the kind does not carry that token and the projector omits it.
/// <c>ParentFolderPath</c> is denormalized forward-slash form; <c>Format</c> is
/// the token source for <c>$ext</c> and may be empty; <c>Captions</c> is empty for non-video kinds.
/// <c>SizeBytes</c> feeds the per-volume free-space sum, where <c>0</c> never pushes a volume over
/// its headroom. <c>BitRate</c> is bits/sec, <c>null</c> where none is stored, and renders as kbps.
/// </remarks>
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

/// <summary>A sidecar caption row; <see cref="Filename"/> is a basename only.</summary>
public sealed record RenamerCaption(int CaptionId, string Filename);

/// <summary>The entity tables a stored rule can name.</summary>
public enum RenamerEntityKind
{
    Tag,
    Performer,
}

/// <summary>The outcome of resolving stored rule names against one entity table.</summary>
/// <param name="TableHasRows">Whether the table holds any row at all, independent of the names asked for.</param>
/// <param name="Matches">Every <c>(id, name)</c> row whose name matched a requested name, case-insensitively.</param>
public readonly record struct NameResolution(
    bool TableHasRows,
    IReadOnlyList<(int Id, string Name)> Matches);

/// <summary>A loaded library item in the renamer's own vocabulary.</summary>
/// <remarks>
/// Holds the entity-level metadata the projector turns into scalar tokens plus every one of the
/// item's files, each rendered independently. Performers and tags are resolved from Cove's join
/// collections at the port boundary. <c>Title</c>, <c>Code</c>, <c>StudioName</c> and <c>Date</c>
/// degrade when null or empty, and <c>Organized</c> is Cove's curation flag driving the
/// only-organized gate.
/// <para>
/// Routing keys on stable ids, never names. In <c>TagRefs</c> the id is the rule key for tag
/// routing, exclusion and the whitelist, so a renamed tag keeps its rules; the name drives the
/// <c>$tags</c> token and the route reason. Routing takes the first tag in this order whose id has a
/// rule, so the pairs must stay aligned. <c>StudioId</c> is <c>null</c> when the item has no studio.
/// <c>ParentStudios</c> is the ancestor chain nearest-first, which is the order the "first ancestor
/// with a rule wins" walk takes. <c>Director</c> is video-only.
/// </para>
/// </remarks>
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
    /// <summary>The tag names the <c>$tags</c> token renders, in <see cref="TagRefs"/> order.</summary>
    /// <remarks>
    /// Derived, so ids and names cannot drift apart. Recomputed per read: a cached list is copied
    /// verbatim by a <c>with</c> expression and would go stale where a caller replaces the pairs.
    /// </remarks>
    public IReadOnlyList<string> Tags => [.. TagRefs.Select(t => t.Name)];
}

/// <summary>The database seam between the planner and executor and a live <c>CoveContext</c>.</summary>
/// <remarks>
/// This interface speaks only in the Renamer-owned records above, never in Cove.Core entity types,
/// so the planner and engine depend on nothing of Cove's. The Cove-backed implementation maps live
/// entity graphs into these records at the boundary.
/// </remarks>
public interface IRenamerDataPort
{
    // EF Core translates an in list to one bound parameter per id, so an unchunked load would exceed
    // Postgres's parameter cap and generate pathological SQL. Chunking keeps the parameter count bounded
    // at one round-trip per chunk. The in list is provider-agnostic: the provider is host-supplied,
    // Postgres in production and SQLite in tests, and a raw Npgsql array parameter would not translate
    // on SQLite.
    const int LoadChunkSize = 200;

    /// <summary>The absolute library paths Cove is configured to scan, in configuration order.</summary>
    /// <remarks>
    /// A destination's folder template resolves against a library path, never against the file's own
    /// parent folder, which is the previous run's output and would make the item descend one
    /// directory per pass until the path length refuses it. Host configuration held in memory, so
    /// this is a property. Empty means the host declares no library path, and a file under none of
    /// them is planned as <see cref="RenamerStatus.SkipUnanchored"/>.
    /// </remarks>
    IReadOnlyList<string> LibraryRoots { get; }

    /// <summary>Resolves stored rule names to the stable ids they name, reading only <paramref name="names"/>.</summary>
    /// <remarks>
    /// Matching is case-insensitive, so a name matching several entities that differ only by case
    /// returns all of them and the caller decides which one the rule collapses onto. A name with no
    /// match is absent from <see cref="NameResolution.Matches"/>.
    /// <see cref="NameResolution.TableHasRows"/> travels with the matches because the two are only
    /// meaningful together: no matches over a populated table means those entities are gone, while no
    /// matches over an empty table means the library is not readable yet.
    /// </remarks>
    Task<NameResolution> ResolveNamesAsync(
        RenamerEntityKind kind, IReadOnlyList<string> names, CancellationToken ct = default);

    /// <summary>
    /// Loads a media item's full file graph: entity metadata, every file, parent folder paths and
    /// captions. Returns <c>null</c> when the item does not exist.
    /// </summary>
    Task<RenamerEntity?> LoadEntityAsync(RenamerFileKind kind, int entityId, CancellationToken ct = default);

    /// <summary>The number of entities of <paramref name="kind"/> currently in the library.</summary>
    /// <remarks>
    /// The progress denominator a paged walk needs before it starts. A non-renamable kind counts 0
    /// and does not throw. The count is a snapshot, so rows inserted or deleted while the walk runs
    /// make it drift from what the pages yield.
    /// </remarks>
    Task<int> CountEntitiesAsync(RenamerFileKind kind, CancellationToken ct = default);

    /// <summary>
    /// Of <paramref name="sourcePaths"/>, the ones more than one file row names, each with how many
    /// rows name it.
    /// </summary>
    /// <remarks>
    /// Two file rows naming one path is state a rename cannot arbitrate, and the twin can sit in
    /// another page of the walk or under another media kind. A path named by one row or by none is
    /// absent from the result. Case sensitivity is the database collation's, which is not always the
    /// volume's: on a case-sensitive collation over a case-insensitive volume, two rows differing
    /// only in case read as two paths here.
    /// </remarks>
    Task<IReadOnlyDictionary<string, int>> CountSourcePathClaimsAsync(
        IReadOnlyList<string> sourcePaths, CancellationToken ct = default);

    /// <summary>
    /// The next page of <paramref name="kind"/>'s entity ids after <paramref name="afterEntityId"/>,
    /// at most <paramref name="take"/> long.
    /// </summary>
    /// <remarks>
    /// The result is strictly ascending and holds only ids greater than
    /// <paramref name="afterEntityId"/>, so the last id of a page is the cursor for the next one and
    /// rows inserted or deleted mid-walk neither skip nor repeat a page. A page shorter than
    /// <paramref name="take"/> means the kind is exhausted; a non-positive <paramref name="take"/> or
    /// a non-renamable kind returns empty without throwing. This is the only way to enumerate a kind,
    /// because a library reaches millions of entities.
    /// </remarks>
    Task<IReadOnlyList<int>> LoadEntityIdPageAsync(
        RenamerFileKind kind, int afterEntityId, int take, CancellationToken ct = default);

    /// <summary>
    /// The batch counterpart to <see cref="LoadEntityAsync"/>, loading many entities of one kind over
    /// chunked <c>WHERE Id IN (...)</c> queries.
    /// </summary>
    /// <remarks>
    /// The same include graph and mapper as the single load, so a returned DTO is identical either
    /// way. A missing id is omitted, never a null slot and never a throw. Order is not guaranteed, so
    /// an order-sensitive caller re-orders by its own id list. A non-renamable kind and an empty
    /// <paramref name="ids"/> return an empty list.
    /// </remarks>
    Task<IReadOnlyList<RenamerEntity>> LoadEntitiesAsync(RenamerFileKind kind, IReadOnlyList<int> ids, CancellationToken ct = default);

    /// <summary>
    /// True when a file row other than <paramref name="selfFileId"/> already occupies
    /// (<paramref name="folderId"/>, <paramref name="basename"/>).
    /// </summary>
    /// <remarks>The pre-check for Cove's <c>(ParentFolderId, Basename)</c> unique index.</remarks>
    Task<bool> CollisionExistsAsync(int folderId, string basename, int selfFileId, CancellationToken ct = default);

    /// <summary>Resolves the destination folder by its path, creating it when absent, and returns its id.</summary>
    Task<int> GetOrCreateFolderIdAsync(string folderPath, CancellationToken ct = default);

    /// <summary>
    /// Looks up an existing destination folder id by path; <c>null</c> when no folder row exists for
    /// that path.
    /// </summary>
    /// <remarks>
    /// Never creates and never saves, so a dry-run preview persists nothing. The planner treats a
    /// null result as collision-free, since an absent folder holds no file rows.
    /// </remarks>
    Task<int?> TryGetFolderIdAsync(string folderPath, CancellationToken ct = default);

    /// <summary>True when the source file currently exists on disk.</summary>
    /// <remarks>
    /// Takes the forward-slash full path the planner already computes and normalizes it to a native
    /// path internally. Never creates and never saves, so preview purity holds; preview purity
    /// forbids database mutation, not disk reads.
    /// </remarks>
    Task<bool> SourceExistsAsync(string fullPath, CancellationToken ct = default);

    /// <summary>
    /// Applies the mutation to the file's basename, parent folder and caption filenames, saves it, and
    /// returns the path Cove recomputed for the file, in forward-slash form.
    /// </summary>
    /// <remarks>
    /// Throws on a save failure, such as a unique-index violation, so the caller's catch can roll the
    /// on-disk move back, and when the file row no longer exists.
    /// </remarks>
    Task<string> ApplyAndSaveAsync(RenamerFileMutation mutation, CancellationToken ct = default);
}

/// <summary>One file's intended database mutation, handed to <see cref="IRenamerDataPort.ApplyAndSaveAsync"/>.</summary>
/// <remarks>
/// <c>NewParentFolderId</c> is null for an in-place rename, and <c>CaptionRenames</c> carries caption
/// id and new filename pairs for sidecars that moved. <c>EntityTitle</c> is the one-time title write
/// riding with this file's save, <c>null</c> when the item already carries a title or the
/// filename-as-title fallback is off. It names the owning entity because a title belongs to the
/// item, and it travels on the file mutation so it lands in the same <c>SaveChangesAsync</c> as the
/// rename that derived it.
/// </remarks>
public sealed record RenamerFileMutation(
    int FileId,
    string NewBasename,
    int? NewParentFolderId,
    IReadOnlyList<(int CaptionId, string NewFilename)>? CaptionRenames = null,
    RenamerEntityTitleWrite? EntityTitle = null);

/// <summary>A filename-derived title to record on a media entity that has none.</summary>
/// <remarks>
/// Recording the derivation once keeps the filename-as-title fallback to the first run; left
/// unrecorded it re-reads its own output every pass. The title is never empty, since an empty
/// derivation travels as no write at all.
/// </remarks>
public readonly record struct RenamerEntityTitleWrite(RenamerFileKind Kind, int EntityId, string Title);
