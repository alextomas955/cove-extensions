using WhisparrSync.Library;
using WhisparrSync.Monitor;

namespace WhisparrSync.Tests.TestSupport;

/// <summary>
/// In-memory <see cref="ICoveLibraryPort"/> for DB-free unit tests of the downstream matcher /
/// reconciliation logic: the seam faked so that logic is testable without a live CoveContext.
/// Returns caller-seeded <see cref="CoveVideo"/> DTOs from <see cref="LoadAllVideosAsync"/>. It references
/// only the WhisparrSync-owned DTO — no Cove.Core type — so it compiles on every test path. No disk, no DB.
/// </summary>
internal sealed class FakeCoveLibraryPort : ICoveLibraryPort
{
    private readonly List<CoveVideo> _videos = new();
    private readonly Dictionary<(EntityKind Kind, int Id), List<CoveVideo>> _entityVideos = new();
    private readonly Dictionary<(EntityKind Kind, int Id), CoveEntityIdentity> _entityIdentities = new();

    /// <summary>Number of <see cref="LoadAllVideosAsync"/> calls — lets a test prove the reconciliation reads the library once.</summary>
    public int LoadAllVideosCallCount { get; private set; }

    /// <summary>Number of <see cref="LoadVideoByIdAsync"/> calls — lets a test prove the scene-detail read hits the seam once.</summary>
    public int LoadVideoByIdCallCount { get; private set; }

    /// <summary>Number of <see cref="LoadVideosForEntityAsync"/> calls — lets a test prove the bulk diff reads the entity's scenes once.</summary>
    public int LoadVideosForEntityCallCount { get; private set; }

    /// <summary>Seeds the videos <see cref="LoadAllVideosAsync"/> returns (additive across calls).</summary>
    public void Seed(params CoveVideo[] videos) => _videos.AddRange(videos);

    /// <summary>
    /// Seeds the videos <see cref="LoadVideosForEntityAsync"/> returns for one (<paramref name="kind"/>,
    /// <paramref name="coveEntityId"/>) pair (additive across calls) — the bulk-diff source.
    /// </summary>
    public void SeedForEntity(EntityKind kind, int coveEntityId, params CoveVideo[] videos)
    {
        if (!_entityVideos.TryGetValue((kind, coveEntityId), out var list))
        {
            list = new List<CoveVideo>();
            _entityVideos[(kind, coveEntityId)] = list;
        }

        list.AddRange(videos);
    }

    public Task<IReadOnlyList<CoveVideo>> LoadAllVideosAsync(CancellationToken ct = default)
    {
        LoadAllVideosCallCount++;
        return Task.FromResult<IReadOnlyList<CoveVideo>>([.. _videos]);
    }

    public Task<CoveVideo?> LoadVideoByIdAsync(int coveId, CancellationToken ct = default)
    {
        LoadVideoByIdCallCount++;
        return Task.FromResult(_videos.FirstOrDefault(v => v.CoveId == coveId));
    }

    public Task<IReadOnlyList<CoveVideo>> LoadVideosByIdsAsync(
        IReadOnlyList<int> coveIds, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<CoveVideo>>([.. _videos.Where(v => coveIds.Contains(v.CoveId))]);

    public Task<IReadOnlyList<CoveVideo>> LoadVideosForEntityAsync(
        EntityKind kind, int coveEntityId, CancellationToken ct = default)
    {
        LoadVideosForEntityCallCount++;
        var list = _entityVideos.TryGetValue((kind, coveEntityId), out var seeded) ? seeded : [];
        return Task.FromResult<IReadOnlyList<CoveVideo>>([.. list]);
    }

    /// <summary>Seeds the identity <see cref="LoadEntityIdentityAsync"/> returns for one entity (its own StashDB/TPDB ids).</summary>
    public void SeedEntityIdentity(EntityKind kind, int coveEntityId, CoveEntityIdentity identity)
        => _entityIdentities[(kind, coveEntityId)] = identity;

    /// <summary>Number of <see cref="LoadEntityIdentityAsync"/> calls — lets a test prove the batch resolves each entity's id once.</summary>
    public int LoadEntityIdentityCallCount { get; private set; }

    public Task<CoveEntityIdentity?> LoadEntityIdentityAsync(
        EntityKind kind, int coveEntityId, CancellationToken ct = default)
    {
        LoadEntityIdentityCallCount++;
        return Task.FromResult(_entityIdentities.TryGetValue((kind, coveEntityId), out var id) ? id : null);
    }

    public Task<IReadOnlyList<CoveEntityIdentity>> LoadAllEntityIdentitiesAsync(
        EntityKind kind, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<CoveEntityIdentity>>(
            [.. _entityIdentities.Where(kv => kv.Key.Kind == kind).Select(kv => kv.Value)]);
}
