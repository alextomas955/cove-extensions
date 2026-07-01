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

    public Task<RenamerEntity?> LoadEntityAsync(RenamerFileKind kind, int entityId, CancellationToken ct = default)
        => Task.FromResult(_entities.TryGetValue((kind, entityId), out var e) ? e : null);

    public Task<IReadOnlyList<int>> LoadAllEntityIdsAsync(RenamerFileKind kind, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<int>>(_allIds.TryGetValue(kind, out var ids) ? ids : []);

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

    public Task<int> SaveAsync(IReadOnlyList<RenamerFileMutation> mutations, CancellationToken ct = default)
    {
        SaveCalls.Add(mutations);
        return Task.FromResult(mutations.Count);
    }
}
