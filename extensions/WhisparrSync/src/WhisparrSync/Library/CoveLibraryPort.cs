using Cove.Core.Entities;
using Microsoft.EntityFrameworkCore;
using WhisparrSync.Contracts;

namespace WhisparrSync.Library;

/// <summary>
/// The ONE class that speaks to a live Cove database. It implements <see cref="ICoveLibraryPort"/> as a
/// pure read seam: an <c>AsNoTracking</c> load of the video graph mapped into WhisparrSync-owned DTOs at
/// the boundary. It NEVER calls <see cref="DbContext.SaveChangesAsync(CancellationToken)"/> and holds no
/// mutation primitives — reconciliation is zero-mutation.
///
/// WHY <see cref="DbContext"/> and not the concrete <c>CoveContext</c>: the production
/// <c>WhisparrSync.csproj</c> references <c>Cove.Sdk</c> (compile-time, runtime-excluded) which
/// transitively exposes <c>Cove.Core</c> entities + EF Core — but NOT <c>Cove.Data</c> where
/// <c>CoveContext</c> lives. The host registers its <c>CoveContext</c> in DI resolvable as the base
/// <see cref="DbContext"/>, so this class works against the base type + <c>db.Set&lt;Video&gt;()</c>.
/// Tests inject a SQLite-in-memory <c>CoveContext</c> (which IS-A <see cref="DbContext"/>) directly.
///
/// One instance wraps one context (a scope's context). In production the reconciliation opens a scope per
/// run via <c>IServiceScopeFactory.CreateAsyncScope()</c> and constructs this over the scoped
/// <see cref="DbContext"/>.
/// </summary>
internal sealed class CoveLibraryPort(DbContext db, string stashEndpoint, string tpdbEndpoint) : ICoveLibraryPort
{
    // The keyset page size. Large enough that a whole-library fold costs few round trips, small enough that the
    // page plus its Include graph stays a bounded allocation at any library size. Internal because the sync's
    // per-page progress throttle has to report at the same grain the stream pages at, and two independently
    // written constants would drift.
    internal const int StreamPageSize = 500;

    public async IAsyncEnumerable<CoveVideo> StreamAllVideosAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var cursor = 0;
        while (true)
        {
            // Keyset rather than Skip/Take: an offset re-walks the rows it skips, which is quadratic over a
            // library of millions.
            var page = await db.Set<Video>()
                .AsNoTracking()
                .Include(v => v.RemoteIds)
                .Include(v => v.Files).ThenInclude(f => f.Fingerprints)
                .Where(v => v.Id > cursor)
                .OrderBy(v => v.Id)
                .Take(StreamPageSize)
                .ToListAsync(ct);

            if (page.Count == 0)
            {
                yield break;
            }

            cursor = page[^1].Id;
            foreach (var video in page)
            {
                yield return Map(video);
            }
        }
    }

    public Task<int> CountVideosAsync(CancellationToken ct = default)
        => db.Set<Video>().AsNoTracking().CountAsync(ct);

    public async IAsyncEnumerable<int> StreamVideoIdsAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var cursor = 0;
        while (true)
        {
            var page = await db.Set<Video>()
                .AsNoTracking()
                .Where(v => v.Id > cursor)
                .OrderBy(v => v.Id)
                .Take(StreamPageSize)
                .Select(v => v.Id)
                .ToListAsync(ct);

            if (page.Count == 0)
            {
                yield break;
            }

            cursor = page[^1];
            foreach (var id in page)
            {
                yield return id;
            }
        }
    }

    public async IAsyncEnumerable<CoveVideo> StreamVideosInRangeAsync(
        int afterCoveId, int upToCoveId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var cursor = afterCoveId;
        while (true)
        {
            // The two comparisons ARE the half-open range contract: strictly greater than the lower bound, at
            // most the upper one. Loosening either end makes adjacent ranges overlap or leave a hole, and a
            // whole-library pass would then visit a scene twice or not at all with nothing to show for it.
            var page = await db.Set<Video>()
                .AsNoTracking()
                .Include(v => v.RemoteIds)
                .Include(v => v.Files).ThenInclude(f => f.Fingerprints)
                .Where(v => v.Id > cursor && v.Id <= upToCoveId)
                .OrderBy(v => v.Id)
                .Take(StreamPageSize)
                .ToListAsync(ct);

            if (page.Count == 0)
            {
                yield break;
            }

            cursor = page[^1].Id;
            foreach (var video in page)
            {
                yield return Map(video);
            }
        }
    }

    public async IAsyncEnumerable<string> StreamFilePathsAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var cursor = 0;
        while (true)
        {
            // The Id rides along only as the keyset cursor; the caller wants the path alone.
            var page = await db.Set<VideoFile>()
                .AsNoTracking()
                .Where(f => f.Id > cursor)
                .OrderBy(f => f.Id)
                .Take(StreamPageSize)
                .Select(f => new { f.Id, f.Path })
                .ToListAsync(ct);

            if (page.Count == 0)
            {
                yield break;
            }

            cursor = page[^1].Id;
            foreach (var row in page)
            {
                yield return row.Path;
            }
        }
    }

    public async Task<IReadOnlySet<string>> LoadOwnedRemoteIdsAsync(
        DiscoveryIdFamily family, IReadOnlyCollection<string> candidateIds, CancellationToken ct = default)
    {
        var owned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (candidateIds.Count == 0)
        {
            return owned;
        }

        var endpoint = (family == DiscoveryIdFamily.Tpdb ? tpdbEndpoint : stashEndpoint).ToUpperInvariant();
        var distinct = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in candidateIds)
        {
            if (!string.IsNullOrEmpty(id))
            {
                distinct.Add(id.ToUpperInvariant());
            }
        }

        // Upper-cased on both sides so the match holds under any column collation — the case-insensitive fold every
        // other remote-id comparison here applies. ToUpper() inside the predicate is translated to SQL upper() and
        // never runs in .NET, so the culture analyzers are false positives and their overloads are not translatable.
        // Chunked because the candidate set is a whole catalogue page, which can exceed a provider's parameter ceiling.
#pragma warning disable CA1304, CA1311, CA1862
        foreach (var chunk in distinct.Chunk(StreamPageSize))
        {
            var matched = await db.Set<VideoRemoteId>()
                .AsNoTracking()
                .Where(r => r.Endpoint.ToUpper() == endpoint && chunk.Contains(r.RemoteId.ToUpper()))
                .Select(r => r.RemoteId)
                .Distinct()
                .ToListAsync(ct);

            foreach (var id in matched)
            {
                owned.Add(id);
            }
        }
#pragma warning restore CA1304, CA1311, CA1862

        return owned;
    }

    public async Task<CoveVideo?> LoadVideoByIdAsync(int coveId, CancellationToken ct = default)
    {
        var video = await db.Set<Video>()
            .AsNoTracking()
            .Include(v => v.RemoteIds)
            .Include(v => v.Files).ThenInclude(f => f.Fingerprints)
            .FirstOrDefaultAsync(v => v.Id == coveId, ct);

        return video is null ? null : Map(video);
    }

    public async Task<IReadOnlyList<CoveVideo>> LoadVideosByIdsAsync(
        IReadOnlyList<int> coveIds, CancellationToken ct = default)
    {
        if (coveIds.Count == 0)
        {
            return [];
        }

        var videos = await db.Set<Video>()
            .AsNoTracking()
            .Include(v => v.RemoteIds)
            .Include(v => v.Files).ThenInclude(f => f.Fingerprints)
            .Where(v => coveIds.Contains(v.Id))
            .ToListAsync(ct);

        return [.. videos.Select(Map)];
    }

    public async Task<IReadOnlyList<CoveVideo>> LoadVideosForEntityAsync(
        EntityKind kind, int coveEntityId, CancellationToken ct = default)
    {
        // A tag's owned subtraction is deliberately NOT tag-scoped — a scene Cove owns under ANY tag is never
        // "missing" just because Cove filed it under a different one — so answering a tag HERE would mean
        // materializing the whole library, with two Include chains, onto the managed heap. Libraries here reach
        // millions of files, so that is refused rather than served: the bounded way to ask the same question is
        // LoadOwnedRemoteIdsAsync over the ids one catalogue page could be subtracted by, which is what the
        // discovery path already does. Refusing is what keeps a caller from reintroducing the unbounded read —
        // the request body carries the kind, so this is reachable without any UI offering it.
        if (kind == EntityKind.Tag)
        {
            throw new ArgumentOutOfRangeException(
                nameof(kind), kind,
                "A tag's owned set is the whole library; use LoadOwnedRemoteIdsAsync for the bounded subtraction.");
        }

        // Same AsNoTracking + Include chain as the other reads, so an entity's scenes project identically to
        // StreamAllVideosAsync. Filter: studio -> the video's StudioId; performer -> a VideoPerformers membership.
        var query = db.Set<Video>()
            .AsNoTracking()
            .Include(v => v.RemoteIds)
            .Include(v => v.Files).ThenInclude(f => f.Fingerprints);

        var filtered = kind == EntityKind.Studio
            ? query.Where(v => v.StudioId == coveEntityId)
            : query.Where(v => v.VideoPerformers.Any(vp => vp.PerformerId == coveEntityId));

        var rows = await filtered.ToListAsync(ct);
        return [.. rows.Select(Map)];
    }

    public async Task<CoveEntityIdentity?> LoadEntityIdentityAsync(
        EntityKind kind, int coveEntityId, CancellationToken ct = default)
    {
        if (kind == EntityKind.Studio)
        {
            var studio = await db.Set<Studio>()
                .AsNoTracking()
                .Include(s => s.RemoteIds)
                .FirstOrDefaultAsync(s => s.Id == coveEntityId, ct);
            return studio is null
                ? null
                : MapEntity(studio.RemoteIds.Select(r => (r.Endpoint, r.RemoteId)), studio.Name);
        }

        if (kind == EntityKind.Tag)
        {
            var tag = await db.Set<Tag>()
                .AsNoTracking()
                .Include(t => t.RemoteIds)
                .FirstOrDefaultAsync(t => t.Id == coveEntityId, ct);
            return tag is null
                ? null
                : MapEntity(tag.RemoteIds.Select(r => (r.Endpoint, r.RemoteId)), tag.Name);
        }

        var performer = await db.Set<Performer>()
            .AsNoTracking()
            .Include(p => p.RemoteIds)
            .FirstOrDefaultAsync(p => p.Id == coveEntityId, ct);
        return performer is null
            ? null
            : MapEntity(performer.RemoteIds.Select(r => (r.Endpoint, r.RemoteId)), performer.Name);
    }

    public async Task<IReadOnlyList<CoveEntityIdentity>> LoadStudioChildIdentitiesAsync(
        int coveEntityId, CancellationToken ct = default)
    {
        var studio = await db.Set<Studio>()
            .AsNoTracking()
            .Include(s => s.Children).ThenInclude(c => c.RemoteIds)
            .FirstOrDefaultAsync(s => s.Id == coveEntityId, ct);
        if (studio is null)
        {
            return [];
        }

        // Each child's RemoteIds map through the same endpoint split as the parent's own id, so a child's
        // StashDB id lands in the same family the union query and the owned subtraction key on. The child's NAME
        // rides along: it is what labels the sub-studio a reader picks, and Cove's own hierarchy is the only
        // place that name exists — no provider aggregate offers it.
        return
        [
            .. studio.Children.Select(c => MapEntity(c.RemoteIds.Select(r => (r.Endpoint, r.RemoteId)), c.Name)),
        ];
    }

    public async Task<IReadOnlyList<CoveEntityIdentity>> LoadAllEntityIdentitiesAsync(
        EntityKind kind, CancellationToken ct = default)
    {
        // One AsNoTracking pass over the whole kind (id + RemoteIds only) — the row's monitored count is a
        // library-wide fold, so it enumerates every entity once and matches against the cached Whisparr list.
        if (kind == EntityKind.Studio)
        {
            var studios = await db.Set<Studio>()
                .AsNoTracking()
                .Include(s => s.RemoteIds)
                .ToListAsync(ct);
            return [.. studios.Select(s => MapEntity(s.RemoteIds.Select(r => (r.Endpoint, r.RemoteId))))];
        }

        var performers = await db.Set<Performer>()
            .AsNoTracking()
            .Include(p => p.RemoteIds)
            .ToListAsync(ct);
        return [.. performers.Select(p => MapEntity(p.RemoteIds.Select(r => (r.Endpoint, r.RemoteId))))];
    }

    public async Task<IReadOnlyList<CoveEntityRef>> LoadAllEntityRefsAsync(
        EntityKind kind, CancellationToken ct = default)
    {
        // One AsNoTracking pass over the whole kind, projecting the Cove PK + endpoint-split ids.
        if (kind == EntityKind.Studio)
        {
            var studios = await db.Set<Studio>()
                .AsNoTracking()
                .Include(s => s.RemoteIds)
                .ToListAsync(ct);
            return [.. studios.Select(s => MapEntityRef(s.Id, s.RemoteIds.Select(r => (r.Endpoint, r.RemoteId))))];
        }

        var performers = await db.Set<Performer>()
            .AsNoTracking()
            .Include(p => p.RemoteIds)
            .ToListAsync(ct);
        return [.. performers.Select(p => MapEntityRef(p.Id, p.RemoteIds.Select(r => (r.Endpoint, r.RemoteId))))];
    }

    public async Task<IReadOnlyList<CovePerformerImage>> LoadPerformerImagesAsync(
        IReadOnlyCollection<string> stashIds, IReadOnlyCollection<string> names, CancellationToken ct = default)
    {
        if (stashIds.Count == 0 && names.Count == 0)
        {
            return [];
        }

        // A url for an image-less performer would 404, so filter to those that carry an image blob.
        var withImage = db.Set<Performer>()
            .AsNoTracking()
            .Include(p => p.RemoteIds)
            .Where(p => p.ImageBlobId != null || p.ImageOverrideBlobId != null);

        // Two passes (id, then name) merged by Cove id: a single OR spanning a navigation `Any` and a scalar `IN`
        // does not reliably translate to SQL. Name matching is case-insensitive — Whisparr's casing need not match Cove's.
        var matched = new Dictionary<int, Performer>();

        if (stashIds.Count > 0)
        {
            var idList = stashIds.ToList();
            var byId = await withImage
                .Where(p => p.RemoteIds.Any(r => idList.Contains(r.RemoteId)))
                .ToListAsync(ct);
            foreach (var performer in byId)
            {
                matched[performer.Id] = performer;
            }
        }

        if (names.Count > 0)
        {
            var loweredNames = names
                .Select(n => n.Trim().ToLowerInvariant())
                .Where(n => n.Length > 0)
                .Distinct()
                .ToList();
            if (loweredNames.Count > 0)
            {
                // ToLower() here is translated to SQL lower() by EF and never runs in .NET, so the
                // culture-ToLower analyzers are false positives; their culture overloads are not translatable.
#pragma warning disable CA1304, CA1311
                var byName = await withImage
                    .Where(p => loweredNames.Contains(p.Name.ToLower()))
                    .ToListAsync(ct);
#pragma warning restore CA1304, CA1311
                foreach (var performer in byName)
                {
                    matched[performer.Id] = performer;
                }
            }
        }

        return
        [
            .. matched.Values.Select(p => new CovePerformerImage(
                p.Id,
                p.Name,
                [.. p.RemoteIds
                    .Where(r => string.Equals(r.Endpoint, stashEndpoint, StringComparison.OrdinalIgnoreCase))
                    .Select(r => r.RemoteId)])),
        ];
    }

    private CoveEntityIdentity MapEntity(
        IEnumerable<(string Endpoint, string RemoteId)> remoteIds, string? name = null)
    {
        var (stashIds, tpdbIds) = SplitByEndpoint(remoteIds);
        return new CoveEntityIdentity(stashIds, tpdbIds, name);
    }

    private CoveEntityRef MapEntityRef(int coveId, IEnumerable<(string Endpoint, string RemoteId)> remoteIds)
    {
        var (stashIds, tpdbIds) = SplitByEndpoint(remoteIds);
        return new CoveEntityRef(coveId, stashIds, tpdbIds);
    }

    // Compared case-insensitively: Cove dedups endpoints with ToUpperInvariant, and the one RemoteIds list stores
    // both providers' ids — the endpoint match is what keeps StashDB ids out of the TPDB list and vice versa.
    private (IReadOnlyList<string> StashIds, IReadOnlyList<string> TpdbIds) SplitByEndpoint(
        IEnumerable<(string Endpoint, string RemoteId)> remoteIds)
    {
        var list = remoteIds.ToList();
        return (
            [.. list
                .Where(r => string.Equals(r.Endpoint, stashEndpoint, StringComparison.OrdinalIgnoreCase))
                .Select(r => r.RemoteId)],
            [.. list
                .Where(r => string.Equals(r.Endpoint, tpdbEndpoint, StringComparison.OrdinalIgnoreCase))
                .Select(r => r.RemoteId)]);
    }

    // The single boundary mapper every read shares, so LoadVideoByIdAsync projects a scene EXACTLY as
    // StreamAllVideosAsync does (same StashDB-endpoint filter, same path/fingerprint shape).
    private CoveVideo Map(Video v) => new(
        CoveId: v.Id,
        Title: v.Title,
        Date: v.Date,
        // StashDB leg: RemoteId where Endpoint == the configured StashDB endpoint. Compared
        // case-insensitively because Cove itself dedups endpoints with ToUpperInvariant, and the
        // same field also stores ThePornDB's endpoint — the filter is what keeps those ids out.
        StashIds: [.. v.RemoteIds
            .Where(r => string.Equals(r.Endpoint, stashEndpoint, StringComparison.OrdinalIgnoreCase))
            .Select(r => r.RemoteId)],
        // ThePornDB leg (v2 identity): the same RemoteIds list also stores TPDB ids; the endpoint filter is
        // what keeps them separate from the StashDB ids above. Compared case-insensitively for the same reason.
        TpdbIds: [.. v.RemoteIds
            .Where(r => string.Equals(r.Endpoint, tpdbEndpoint, StringComparison.OrdinalIgnoreCase))
            .Select(r => r.RemoteId)],
        FilePaths: [.. v.Files.Select(f => f.Path)],
        Fingerprints: [.. v.Files.SelectMany(f => f.Fingerprints)
            .Select(fp => new CoveFingerprint(fp.Type, fp.Value))]);
}
