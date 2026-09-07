using WhisparrSync.Contracts;
using WhisparrSync.Library;

namespace WhisparrSync.Tests.TestSupport;

/// <summary>
/// In-memory <see cref="ICoveLibraryPort"/> for DB-free unit tests of the downstream matcher /
/// reconciliation logic: the seam faked so that logic is testable without a live CoveContext.
/// Returns caller-seeded <see cref="CoveVideo"/> DTOs from <see cref="StreamAllVideosAsync"/>. It references
/// only the WhisparrSync-owned DTO — no Cove.Core type — so it compiles on every test path. No disk, no DB.
/// </summary>
internal sealed class FakeCoveLibraryPort : ICoveLibraryPort
{
    private readonly List<CoveVideo> _videos = new();
    private readonly Dictionary<(EntityKind Kind, int Id), List<CoveVideo>> _entityVideos = new();
    private readonly Dictionary<(EntityKind Kind, int Id), CoveEntityIdentity> _entityIdentities = new();

    /// <summary>Number of <see cref="LoadVideoByIdAsync"/> calls — lets a test prove the scene-detail read hits the seam once.</summary>
    public int LoadVideoByIdCallCount { get; private set; }

    /// <summary>Number of <see cref="LoadVideosForEntityAsync"/> calls — lets a test prove the bulk diff reads the entity's scenes once.</summary>
    public int LoadVideosForEntityCallCount { get; private set; }

    /// <summary>Seeds the videos <see cref="StreamAllVideosAsync"/> returns (additive across calls).</summary>
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

    /// <summary>Number of <see cref="StreamAllVideosAsync"/> enumerations.</summary>
    public int StreamAllVideosCallCount { get; private set; }

    public async IAsyncEnumerable<CoveVideo> StreamAllVideosAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        StreamAllVideosCallCount++;
        foreach (var video in _videos.OrderBy(v => v.CoveId))
        {
            ct.ThrowIfCancellationRequested();
            yield return video;
        }

        await Task.CompletedTask;
    }

    /// <summary>Number of <see cref="CountVideosAsync"/> calls — lets a test prove the count is read once per run.</summary>
    public int CountVideosCallCount { get; private set; }

    public Task<int> CountVideosAsync(CancellationToken ct = default)
    {
        CountVideosCallCount++;
        return Task.FromResult(_videos.Count);
    }

    /// <summary>Number of <see cref="StreamVideoIdsAsync"/> enumerations.</summary>
    public int StreamVideoIdsCallCount { get; private set; }

    public async IAsyncEnumerable<int> StreamVideoIdsAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        StreamVideoIdsCallCount++;
        foreach (var video in _videos.OrderBy(v => v.CoveId))
        {
            ct.ThrowIfCancellationRequested();
            yield return video.CoveId;
        }

        await Task.CompletedTask;
    }

    /// <summary>The (after, upTo) bounds of every <see cref="StreamVideosInRangeAsync"/> enumeration, in order.</summary>
    public List<(int AfterCoveId, int UpToCoveId)> RangeReads { get; } = [];

    public async IAsyncEnumerable<CoveVideo> StreamVideosInRangeAsync(
        int afterCoveId, int upToCoveId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        RangeReads.Add((afterCoveId, upToCoveId));

        // The same half-open comparison the SQL predicate makes, so a fake-backed test and a database-backed one
        // disagree only where the production query is wrong.
        foreach (var video in _videos.Where(v => v.CoveId > afterCoveId && v.CoveId <= upToCoveId).OrderBy(v => v.CoveId))
        {
            ct.ThrowIfCancellationRequested();
            yield return video;
        }

        await Task.CompletedTask;
    }

    public async IAsyncEnumerable<string> StreamFilePathsAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var path in _videos.SelectMany(v => v.FilePaths))
        {
            ct.ThrowIfCancellationRequested();
            yield return path;
        }

        await Task.CompletedTask;
    }

    public Task<IReadOnlySet<string>> LoadOwnedRemoteIdsAsync(
        DiscoveryIdFamily family, IReadOnlyCollection<string> candidateIds, CancellationToken ct = default)
    {
        var owned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var video in _videos)
        {
            foreach (var id in family == DiscoveryIdFamily.Tpdb ? video.TpdbIds : video.StashIds)
            {
                if (candidateIds.Contains(id, StringComparer.OrdinalIgnoreCase))
                {
                    owned.Add(id);
                }
            }
        }

        return Task.FromResult<IReadOnlySet<string>>(owned);
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

    private readonly Dictionary<int, List<CoveEntityIdentity>> _studioChildIdentities = new();

    /// <summary>Seeds the child sub-studio identities <see cref="LoadStudioChildIdentitiesAsync"/> returns for one parent studio (additive).</summary>
    public void SeedStudioChildIdentities(int coveEntityId, params CoveEntityIdentity[] children)
    {
        if (!_studioChildIdentities.TryGetValue(coveEntityId, out var list))
        {
            list = new List<CoveEntityIdentity>();
            _studioChildIdentities[coveEntityId] = list;
        }

        list.AddRange(children);
    }

    public Task<IReadOnlyList<CoveEntityIdentity>> LoadStudioChildIdentitiesAsync(
        int coveEntityId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<CoveEntityIdentity>>(
            _studioChildIdentities.TryGetValue(coveEntityId, out var seeded) ? [.. seeded] : []);

    public Task<IReadOnlyList<CoveEntityIdentity>> LoadAllEntityIdentitiesAsync(
        EntityKind kind, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<CoveEntityIdentity>>(
            [.. _entityIdentities.Where(kv => kv.Key.Kind == kind).Select(kv => kv.Value)]);

    private readonly Dictionary<EntityKind, List<CoveEntityRef>> _entityRefs = new();

    public Task<IReadOnlyList<CoveEntityRef>> LoadAllEntityRefsAsync(
        EntityKind kind, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<CoveEntityRef>>(
            _entityRefs.TryGetValue(kind, out var seeded) ? [.. seeded] : []);

    private readonly List<CovePerformerImage> _performerImages = new();

    /// <summary>Seeds the image-bearing performers <see cref="LoadPerformerImagesAsync"/> returns (additive).</summary>
    public void SeedPerformerImages(params CovePerformerImage[] performers) => _performerImages.AddRange(performers);

    public Task<IReadOnlyList<CovePerformerImage>> LoadPerformerImagesAsync(
        IReadOnlyCollection<string> stashIds, IReadOnlyCollection<string> names, CancellationToken ct = default)
    {
        var wantedNames = names.Select(n => n.Trim().ToLowerInvariant()).ToHashSet();
        return Task.FromResult<IReadOnlyList<CovePerformerImage>>(
            [.. _performerImages.Where(p =>
                p.StashIds.Any(id => stashIds.Contains(id))
                || wantedNames.Contains(p.Name.Trim().ToLowerInvariant()))]);
    }
}
