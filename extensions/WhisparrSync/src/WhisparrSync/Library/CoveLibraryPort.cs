using Cove.Core.Entities;
using Microsoft.EntityFrameworkCore;
using WhisparrSync.Monitor;

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
    public async Task<IReadOnlyList<CoveVideo>> LoadAllVideosAsync(CancellationToken ct = default)
    {
        var rows = await db.Set<Video>()
            .AsNoTracking()
            .Include(v => v.RemoteIds)
            .Include(v => v.Files).ThenInclude(f => f.Fingerprints)
            .ToListAsync(ct);

        return [.. rows.Select(Map)];
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
        // Same AsNoTracking + Include chain as the other reads, so an entity's scenes project identically to
        // LoadAllVideosAsync. Filter: studio -> the video's StudioId; performer -> a VideoPerformers membership.
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
            return studio is null ? null : MapEntity(studio.RemoteIds.Select(r => (r.Endpoint, r.RemoteId)));
        }

        var performer = await db.Set<Performer>()
            .AsNoTracking()
            .Include(p => p.RemoteIds)
            .FirstOrDefaultAsync(p => p.Id == coveEntityId, ct);
        return performer is null ? null : MapEntity(performer.RemoteIds.Select(r => (r.Endpoint, r.RemoteId)));
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

    // The entity-identity mapper: the SAME endpoint-filter split as Map (StashDB vs TPDB), so a studio's/
    // performer's ids resolve by the connected version exactly as a video's do.
    private CoveEntityIdentity MapEntity(IEnumerable<(string Endpoint, string RemoteId)> remoteIds)
    {
        var list = remoteIds.ToList();
        return new CoveEntityIdentity(
            StashIds: [.. list
                .Where(r => string.Equals(r.Endpoint, stashEndpoint, StringComparison.OrdinalIgnoreCase))
                .Select(r => r.RemoteId)],
            TpdbIds: [.. list
                .Where(r => string.Equals(r.Endpoint, tpdbEndpoint, StringComparison.OrdinalIgnoreCase))
                .Select(r => r.RemoteId)]);
    }

    // The single boundary mapper both reads share, so LoadVideoByIdAsync projects a scene EXACTLY as
    // LoadAllVideosAsync does (same StashDB-endpoint filter, same path/fingerprint shape).
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
