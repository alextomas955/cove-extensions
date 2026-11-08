using Renamer.Planner;

namespace Renamer.Tests.TestSupport;

// In-memory IRenamerDataPort, so the planning, collision, gating and suffix logic can be driven
// with no disk and no database.
public sealed class FakeRenamerDataPort : IRenamerDataPort
{
    private readonly Dictionary<(RenamerFileKind kind, int id), RenamerEntity> _entities = [];

    private readonly HashSet<(int folderId, string basename, int fileId)> _occupied = [];

    private readonly Dictionary<string, int> _folderIds = new(StringComparer.Ordinal);
    private int _nextFolderId = 1000;

    private readonly Dictionary<RenamerFileKind, List<int>> _allIds = [];

    public void SeedEntity(RenamerEntity entity) => _entities[(entity.Kind, entity.EntityId)] = entity;

    public void SeedOccupied(int folderId, string basename, int fileId) => _occupied.Add((folderId, basename, fileId));

    // Pre-registers a folder path that GetOrCreateFolderIdAsync would otherwise mint an id for.
    public void SeedFolder(string path, int id) => _folderIds[path] = id;

    public void SeedAllIds(RenamerFileKind kind, params int[] ids) => _allIds[kind] = [.. ids];

    // Every seeded id of kind, ascending. Test support, not a port member: the production interface
    // offers no whole-kind read, so a reference sequence for a paging-equivalence comparison has to
    // come from the fake's own state.
    public IReadOnlyList<int> SeededIds(RenamerFileKind kind) =>
        _allIds.TryGetValue(kind, out var seeded) ? [.. seeded.OrderBy(id => id)] : [];

    // Forward-slash source paths the test declares absent on disk; everything else reports present.
    public HashSet<string> MissingSources { get; } = new(StringComparer.Ordinal);

    public void SeedMissingSource(string fullPath) => MissingSources.Add(fullPath);

    public int LoadEntityCallCount { get; private set; }

    // The library paths the fake declares; empty by default, so a test opts in to an anchor.
    public List<string> LibraryPathList { get; } = [];

    public void SeedLibraryPaths(params string[] paths) => LibraryPathList.AddRange(paths);

    public IReadOnlyList<string> LibraryRoots => LibraryPathList;

    public Task<NameResolution> ResolveNamesAsync(
        RenamerEntityKind kind, IReadOnlyList<string> names, CancellationToken ct = default)
        => Task.FromResult(new NameResolution(false, []));

    public Task<RenamerEntity?> LoadEntityAsync(RenamerFileKind kind, int entityId, CancellationToken ct = default)
    {
        LoadEntityCallCount++;
        return Task.FromResult(_entities.TryGetValue((kind, entityId), out var e) ? e : null);
    }

    public Task<IReadOnlyList<RenamerEntity>> LoadEntitiesAsync(RenamerFileKind kind, IReadOnlyList<int> ids, CancellationToken ct = default)
    {
        var found = ids
            .Where(id => _entities.ContainsKey((kind, id)))
            .Select(id => _entities[(kind, id)])
            .ToList();
        return Task.FromResult<IReadOnlyList<RenamerEntity>>(found);
    }

    public List<(RenamerFileKind Kind, int After, int Take)> IdPageRequests { get; } = [];

    public Task<IReadOnlyList<int>> LoadEntityIdPageAsync(
        RenamerFileKind kind, int afterEntityId, int take, CancellationToken ct = default)
    {
        IdPageRequests.Add((kind, afterEntityId, take));

        if (take <= 0 || !_allIds.TryGetValue(kind, out var ids))
        {
            return Task.FromResult<IReadOnlyList<int>>([]);
        }

        return Task.FromResult<IReadOnlyList<int>>(
            [.. ids.Where(id => id > afterEntityId).OrderBy(id => id).Take(take)]);
    }

    public Task<int> CountEntitiesAsync(RenamerFileKind kind, CancellationToken ct = default)
        => Task.FromResult(_allIds.TryGetValue(kind, out var ids) ? ids.Count : 0);

    // Source paths the test declares as named by more than one file row.
    public Dictionary<string, int> SourcePathClaims { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Task<IReadOnlyDictionary<string, int>> CountSourcePathClaimsAsync(
        IReadOnlyList<string> sourcePaths, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyDictionary<string, int>>(
            sourcePaths
                .Where(SourcePathClaims.ContainsKey)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToDictionary(p => p, p => SourcePathClaims[p], StringComparer.OrdinalIgnoreCase));

    public Task<bool> CollisionExistsAsync(int folderId, string basename, int selfFileId, CancellationToken ct = default)
    {
        var taken = _occupied.Any(o => o.folderId == folderId && o.basename == basename && o.fileId != selfFileId);
        return Task.FromResult(taken);
    }

    // Records every GetOrCreateFolderIdAsync call's path, in order - a created folder is a
    // mutation, so a preview-purity test asserts this stays empty.
    public List<string> CreatedFolderPaths { get; } = [];

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

    public List<RenamerFileMutation> ApplyAndSaveCalls { get; } = [];

    public Task<string> ApplyAndSaveAsync(RenamerFileMutation mutation, CancellationToken ct = default)
    {
        ApplyAndSaveCalls.Add(mutation);
        return Task.FromResult(mutation.NewBasename);
    }
}
