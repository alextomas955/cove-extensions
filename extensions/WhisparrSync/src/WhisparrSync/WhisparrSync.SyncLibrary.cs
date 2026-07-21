using Cove.Core.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WhisparrSync.Adapters;
using WhisparrSync.Client;
using WhisparrSync.Library;
using WhisparrSync.Monitor;
using WhisparrSync.Options;
using static Cove.Extensions.Shared.MinimalApiPermissions;

namespace WhisparrSync;

/// <summary>
/// The bulk "Sync my library to Whisparr" surface: the configure-gated preview that counts, before the user
/// commits, how many studios/performers (and v3 scenes) will register vs be skipped for carrying no
/// connected-version id. The Wave-2 job reuses the same whole-library enumeration seam.
/// </summary>
public sealed partial class WhisparrSync
{
    /// <summary>
    /// Counts, per bucket, the entities that carry a connected-version id (WILL sync) vs none (skipped). The
    /// connected version's id list is chosen by <see cref="WhisparrOptions.IdentityEndpoint"/> — StashDB on v3,
    /// ThePornDB on v2 — mirroring the reflect-owned/monitor resolution rule. Performer + scene buckets are gated
    /// on the adapter's capabilities (a v2 connection has no performer entity and no per-scene add), so they
    /// report all-zero on v2; studios count on both versions. Empty inputs yield all-zero counts, never a throw.
    /// </summary>
    internal static SyncPreviewResponse SyncPreviewCore(
        IReadOnlyList<CoveEntityRef> studioRefs,
        IReadOnlyList<CoveEntityRef> performerRefs,
        IReadOnlyList<CoveVideo> videos,
        WhisparrOptions options,
        IWhisparrAdapter adapterCaps)
    {
        var useTpdb = string.Equals(options.SelectedVersion, "v2", StringComparison.OrdinalIgnoreCase);

        var studios = CountByConnectedId(
            studioRefs.Select(r => useTpdb ? r.TpdbIds : r.StashIds), adapterCaps.SupportsOwnedImport);
        var performers = CountByConnectedId(
            performerRefs.Select(r => useTpdb ? r.TpdbIds : r.StashIds),
            adapterCaps.SupportsEntityMonitor(EntityKind.Performer));
        var scenes = CountByConnectedId(
            videos.Select(v => useTpdb ? v.TpdbIds : v.StashIds), adapterCaps.SupportsSceneAdd);

        return new SyncPreviewResponse(studios, performers, scenes);
    }

    // A bucket the connected version cannot register is all-zero (not "skipped") — its entities are not a concept
    // on that version. When supported, an entity is sync-able iff it carries a non-empty id on the connected
    // version's endpoint; the rest are skipped-for-no-id.
    private static SyncPreviewCount CountByConnectedId(
        IEnumerable<IReadOnlyList<string>> connectedIdsPerEntity, bool supported)
    {
        if (!supported)
        {
            return new SyncPreviewCount(0, 0);
        }

        int withId = 0, skipped = 0;
        foreach (var ids in connectedIdsPerEntity)
        {
            if (ids.Any(id => !string.IsNullOrEmpty(id)))
            {
                withId++;
            }
            else
            {
                skipped++;
            }
        }

        return new SyncPreviewCount(withId, skipped);
    }

    /// <summary>
    /// The <c>/sync-preview</c> handler: configure-gated + stored-creds-only (no body carries a url/key). Reads
    /// the whole Cove library under the System principal — CoveContext's per-principal authz filters would
    /// undercount it otherwise — and returns the by-state counts. The response carries counts only (no scene id,
    /// path, key, or Whisparr URL). A version this build cannot manage is a clean 400.
    /// </summary>
    internal async Task<IResult> SyncPreviewAsync(
        WhisparrClient client, ICurrentPrincipalAccessor principal, CancellationToken ct)
    {
        if (Forbidden(principal, Permissions.ExtensionsConfigure) is { } denied)
        {
            return denied;
        }

        var (options, _, _) = await ResolveCredsAsync(new TestConnectionRequest(null, null), ct);
        if (AdapterSelector.SelectForVersion(options.SelectedVersion, client) is not { } adapter)
        {
            return Results.Json(new { code = "VERSION_UNSUPPORTED" }, statusCode: 400);
        }

        var (studioRefs, performerRefs, videos) = await LoadWholeLibraryAsSystemAsync(options, ct);
        var counts = SyncPreviewCore(studioRefs, performerRefs, videos, options, adapter);
        return Results.Json(counts, MonitorResponseJsonOptions);
    }

    // Enumerates the whole library (studio + performer refs, all videos) in a fresh scope under the System
    // principal, restoring the prior principal in finally — the same trusted-read span IngestCoordinator uses.
    // CoveContext applies per-principal authz query filters that undercount a library-wide read under the request
    // principal; the System principal bypasses them. Degrades to empty when no host DB scope is available.
    private async Task<(IReadOnlyList<CoveEntityRef> Studios, IReadOnlyList<CoveEntityRef> Performers, IReadOnlyList<CoveVideo> Videos)>
        LoadWholeLibraryAsSystemAsync(WhisparrOptions options, CancellationToken ct)
    {
        if (_scopeFactory is null)
        {
            return ([], [], []);
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        if (scope.ServiceProvider.GetService<DbContext>() is not { } db)
        {
            return ([], [], []);
        }

        var principals = scope.ServiceProvider.GetService<ICurrentPrincipalAccessor>();
        var previousPrincipal = principals?.Current;
        principals?.Set(CovePrincipal.System());
        try
        {
            var library = new CoveLibraryPort(db, options.StashDbEndpoint, options.TpdbEndpoint);
            var studios = await library.LoadAllEntityRefsAsync(EntityKind.Studio, ct);
            var performers = await library.LoadAllEntityRefsAsync(EntityKind.Performer, ct);
            var videos = await library.LoadAllVideosAsync(ct);
            return (studios, performers, videos);
        }
        finally
        {
            principals?.Set(previousPrincipal);
        }
    }
}
