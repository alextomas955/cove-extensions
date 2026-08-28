using Renamer.Planner;

namespace Renamer.Tests.TestSupport;

/// <summary>
/// In-memory <see cref="IRenamerDataPort"/> for DB-free unit tests of the planner / collision /
/// gating / suffix logic: this is the seam faked so the pure planning logic is testable without a
/// live CoveContext. No disk, no DB.
/// </summary>
public sealed class FakeRenamerDataPort : IRenamerDataPort
{
    private readonly Dictionary<(RenamerFileKind kind, int id), RenamerEntity> _entities = new();

    /// <summary>Pre-seeded (folderId, basename) pairs treated as occupied (paired with the file id that holds them).</summary>
    private readonly HashSet<(int folderId, string basename, int fileId)> _occupied = new();

    private readonly Dictionary<string, int> _folderIds = new(StringComparer.Ordinal);
    private int _nextFolderId = 1000;

    private readonly Dictionary<RenamerFileKind, List<int>> _allIds = new();

    /// <summary>Every <see cref="SaveAsync"/> call's mutations, in order, for assertions.</summary>
    public List<IReadOnlyList<RenamerFileMutation>> SaveCalls { get; } = new();

    /// <summary>Seeds a loadable entity (returned by <see cref="LoadEntityAsync"/>).</summary>
    public void SeedEntity(RenamerEntity entity) => _entities[(entity.Kind, entity.EntityId)] = entity;

    /// <summary>Marks (folderId, basename) as occupied by <paramref name="fileId"/> for collision tests.</summary>
    public void SeedOccupied(int folderId, string basename, int fileId) => _occupied.Add((folderId, basename, fileId));

    /// <summary>Pre-registers a folder path → id mapping (otherwise <see cref="GetOrCreateFolderIdAsync"/> mints one).</summary>
    public void SeedFolder(string path, int id) => _folderIds[path] = id;

    /// <summary>Seeds the id set <see cref="LoadAllEntityIdsAsync"/> returns for <paramref name="kind"/>.</summary>
    public void SeedAllIds(RenamerFileKind kind, params int[] ids) => _allIds[kind] = [.. ids];

    /// <summary>Forward-slash source paths the test declares absent on disk; everything else reports present.</summary>
    public HashSet<string> MissingSources { get; } = new(StringComparer.Ordinal);

    /// <summary>Declares <paramref name="fullPath"/> absent on disk for <see cref="SourceExistsAsync"/>.</summary>
    public void SeedMissingSource(string fullPath) => MissingSources.Add(fullPath);

    /// <summary>Number of <see cref="LoadEntityAsync"/> calls — lets a test prove PHASE A loads each id once, not twice.</summary>
    public int LoadEntityCallCount { get; private set; }

    /// <summary>Number of <see cref="LoadEntitiesAsync"/> calls — one per CALL (not per id), so a scan test can prove batching issues far fewer than N loads.</summary>
    public int LoadEntitiesCallCount { get; private set; }

    /// <summary>The library paths the fake declares; empty by default, so a test opts in to an anchor.</summary>
    public List<string> LibraryPathList { get; } = [];

    /// <summary>Declares <paramref name="paths"/> as Cove's configured library paths.</summary>
    public void SeedLibraryPaths(params string[] paths) => LibraryPathList.AddRange(paths);

    public IReadOnlyList<string> LibraryRoots => LibraryPathList;

    /// <summary>Rows <see cref="ResolveNamesAsync"/> resolves against, per entity table.</summary>
    private readonly Dictionary<RenamerEntityKind, List<(int Id, string Name)>> _namedEntities = new();

    /// <summary>Seeds resolvable <c>(id, name)</c> rows for <paramref name="kind"/>.</summary>
    public void SeedNamedEntities(RenamerEntityKind kind, params (int Id, string Name)[] rows)
        => _namedEntities[kind] = [.. rows];

    public Task<NameResolution> ResolveNamesAsync(
        RenamerEntityKind kind, IReadOnlyList<string> names, CancellationToken ct = default)
    {
        var rows = _namedEntities.TryGetValue(kind, out var seeded) ? seeded : [];
        IReadOnlyList<(int Id, string Name)> matches =
            [.. rows.Where(r => names.Contains(r.Name, StringComparer.OrdinalIgnoreCase))];
        return Task.FromResult(new NameResolution(rows.Count > 0, matches));
    }

    public Task<RenamerEntity?> LoadEntityAsync(RenamerFileKind kind, int entityId, CancellationToken ct = default)
    {
        LoadEntityCallCount++;
        return Task.FromResult(_entities.TryGetValue((kind, entityId), out var e) ? e : null);
    }

    public Task<IReadOnlyList<RenamerEntity>> LoadEntitiesAsync(RenamerFileKind kind, IReadOnlyList<int> ids, CancellationToken ct = default)
    {
        LoadEntitiesCallCount++;
        var found = ids
            .Where(id => _entities.ContainsKey((kind, id)))
            .Select(id => _entities[(kind, id)])
            .ToList();
        return Task.FromResult<IReadOnlyList<RenamerEntity>>(found);
    }

    public Task<IReadOnlyList<int>> LoadAllEntityIdsAsync(RenamerFileKind kind, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<int>>(
            _allIds.TryGetValue(kind, out var ids) ? [.. ids.OrderBy(id => id)] : []);

    public Task<IReadOnlyList<int>> LoadEntityIdPageAsync(
        RenamerFileKind kind, int afterEntityId, int take, CancellationToken ct = default)
    {
        if (take <= 0 || !_allIds.TryGetValue(kind, out var ids))
        {
            return Task.FromResult<IReadOnlyList<int>>([]);
        }

        return Task.FromResult<IReadOnlyList<int>>(
            [.. ids.Where(id => id > afterEntityId).OrderBy(id => id).Take(take)]);
    }

    public Task<bool> CollisionExistsAsync(int folderId, string basename, int selfFileId, CancellationToken ct = default)
    {
        // Occupied by some OTHER file row (excluding self) → collision.
        var taken = _occupied.Any(o => o.folderId == folderId && o.basename == basename && o.fileId != selfFileId);
        return Task.FromResult(taken);
    }

    /// <summary>Records every <see cref="GetOrCreateFolderIdAsync"/> call's path, in order — a created folder is a mutation, so a preview-purity test asserts this stays empty.</summary>
    public List<string> CreatedFolderPaths { get; } = new();

    public Task<int> GetOrCreateFolderIdAsync(string folderPath, CancellationToken ct = default)
    {
        if (!_folderIds.TryGetValue(folderPath, out var id))
        {
            id = _nextFolderId++;
            _folderIds[folderPath] = id;
            CreatedFolderPaths.Add(folderPath);
        }
        return Task.FromResult(id);
    }

    public Task<int?> TryGetFolderIdAsync(string folderPath, CancellationToken ct = default)
        => Task.FromResult(_folderIds.TryGetValue(folderPath, out var id) ? id : (int?)null);

    public Task<bool> SourceExistsAsync(string fullPath, CancellationToken ct = default)
        => Task.FromResult(!MissingSources.Contains(fullPath));

    public Task<int> SaveAsync(IReadOnlyList<RenamerFileMutation> mutations, CancellationToken ct = default)
    {
        SaveCalls.Add(mutations);
        return Task.FromResult(mutations.Count);
    }

    /// <summary>When set, <see cref="ApplyAndSaveAsync"/> throws this — the seam that drives the executor's rollback-on-save-failure path with no live DB.</summary>
    public Exception? ApplyAndSaveThrow { get; set; }

    /// <summary>Per-file <c>RecomputedPath</c> a successful <see cref="ApplyAndSaveAsync"/> returns; a file id absent here echoes its new basename.</summary>
    public Dictionary<int, string> RecomputedPaths { get; } = new();

    /// <summary>Every <see cref="ApplyAndSaveAsync"/> call's mutations, in order, for assertions.</summary>
    public List<IReadOnlyList<RenamerFileMutation>> ApplyAndSaveCalls { get; } = new();

    /// <summary>
    /// File ids a successful <see cref="ApplyAndSaveAsync"/> reports NO row for, returning a shorter list
    /// than it was handed.
    /// </summary>
    /// <remarks>
    /// The production port throws on a missing row, so this state is unreachable through it — but the
    /// method is <c>virtual</c> and the interface states no such guarantee, which is what makes the
    /// callers' handling of it worth pinning.
    /// </remarks>
    public HashSet<int> OmitSavedRowsFor { get; } = new();

    public Task<IReadOnlyList<SavedFile>> ApplyAndSaveAsync(IReadOnlyList<RenamerFileMutation> mutations, CancellationToken ct = default)
    {
        ApplyAndSaveCalls.Add(mutations);
        if (ApplyAndSaveThrow is not null)
        {
            throw ApplyAndSaveThrow;
        }

        return Task.FromResult<IReadOnlyList<SavedFile>>(
            [.. mutations
                .Where(m => !OmitSavedRowsFor.Contains(m.FileId))
                .Select(m => new SavedFile(m.FileId, RecomputedPaths.GetValueOrDefault(m.FileId, m.NewBasename)))]);
    }
}
