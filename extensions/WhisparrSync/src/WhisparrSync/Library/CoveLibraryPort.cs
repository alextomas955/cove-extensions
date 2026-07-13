using Cove.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace WhisparrSync.Library;

/// <summary>
/// The ONE class that speaks to a live Cove database. It implements <see cref="ICoveLibraryPort"/> as a
/// pure read seam: an <c>AsNoTracking</c> load of the video graph mapped into WhisparrSync-owned DTOs at
/// the boundary. It NEVER calls <see cref="DbContext.SaveChangesAsync(CancellationToken)"/> and holds no
/// mutation primitives — this phase is zero-mutation (MATCH-03).
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
internal sealed class CoveLibraryPort(DbContext db, string stashEndpoint) : ICoveLibraryPort
{
    public async Task<IReadOnlyList<CoveVideo>> LoadAllVideosAsync(CancellationToken ct = default)
    {
        var rows = await db.Set<Video>()
            .AsNoTracking()
            .Include(v => v.RemoteIds)
            .Include(v => v.Files).ThenInclude(f => f.Fingerprints)
            .ToListAsync(ct);

        return [.. rows.Select(v => new CoveVideo(
            CoveId: v.Id,
            Title: v.Title,
            Date: v.Date,
            // StashDB leg: RemoteId where Endpoint == the configured StashDB endpoint. Compared
            // case-insensitively because Cove itself dedups endpoints with ToUpperInvariant, and the
            // same field also stores ThePornDB's endpoint — the filter is what keeps those ids out.
            StashIds: [.. v.RemoteIds
                .Where(r => string.Equals(r.Endpoint, stashEndpoint, StringComparison.OrdinalIgnoreCase))
                .Select(r => r.RemoteId)],
            FilePaths: [.. v.Files.Select(f => f.Path)],
            Fingerprints: [.. v.Files.SelectMany(f => f.Fingerprints)
                .Select(fp => new CoveFingerprint(fp.Type, fp.Value))]))];
    }
}
