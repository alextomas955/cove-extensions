using Cove.Core.Auth;
using Cove.Core.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WhisparrSync.Adapters;
using WhisparrSync.Client;
using WhisparrSync.Library;
using WhisparrSync.Monitor;
using WhisparrSync.Options;
using WhisparrSync.Push;
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

    /// <summary>
    /// The <c>/sync-library</c> handler: configure-gated + stored-creds-only. Enqueues ONE exclusive
    /// <c>whisparr-sync-library</c> job and returns <c>{ jobId, description }</c>.
    /// </summary>
    /// <remarks>
    /// Job-only by design: a whole-library fan-out is too long to run inline on the request thread, so it never
    /// falls back to an inline run — a host with no job service is a handled 400, not an inline sync.
    /// </remarks>
    internal async Task<IResult> SyncLibraryAsync(
        SyncLibraryRequest req, WhisparrClient client, ICurrentPrincipalAccessor principal, CancellationToken ct)
    {
        if (Forbidden(principal, Permissions.ExtensionsConfigure) is { } denied)
        {
            return denied;
        }

        var (options, _, _) = await ResolveCredsAsync(new TestConnectionRequest(null, null), ct);
        if (AdapterSelector.SelectForVersion(options.SelectedVersion, client) is null)
        {
            return Results.Json(new { code = "VERSION_UNSUPPORTED" }, statusCode: 400);
        }

        var scope = ParseMonitorScope(req.Scope, options.DefaultMonitorScope);
        if (_jobs is null)
        {
            return Results.Json(new { code = "JOB_SERVICE_UNAVAILABLE" }, statusCode: 400);
        }

        var description = DescribeSyncLibrary(req.AlsoMonitor);
        var jobId = _jobs.Enqueue(
            SyncLibraryJobType, description,
            (progress, jobCt) => RunSyncLibraryJobAsync(req.AlsoMonitor, scope, options, progress, jobCt),
            exclusive: true); // one library-wide sync at a time — no concurrent full-library fan-out
        return Results.Json(new { jobId, description }, MonitorResponseJsonOptions);
    }

    // Runs the library sync as a background job: a fresh scope + client (this outlives the request). The whole
    // enumerate+fan-out runs under CovePrincipal.System() — CoveContext's per-principal authz filters would
    // undercount a library-wide read otherwise (IngestCoordinator pattern) — restoring the prior principal in
    // finally. Builds the unified SyncUnit list and drives each unit through the EXISTING reflect-owned/add/
    // monitor runners via ONE maxInFlight:1 RunBatchAsync — no new mutation spine, no grab path.
    private async Task RunSyncLibraryJobAsync(
        bool alsoMonitor, MonitorScope scope, WhisparrOptions options, IJobProgress progress, CancellationToken ct)
    {
        await using var dbScope = ScopeFactory.CreateAsyncScope();
        var client = dbScope.ServiceProvider.GetRequiredService<WhisparrClient>();
        if (AdapterSelector.SelectForVersion(options.SelectedVersion, client) is not { } adapter)
        {
            progress.Report(1d, "This Whisparr version is not supported.");
            return;
        }

        var library = dbScope.ServiceProvider.GetService<DbContext>() is { } db
            ? new CoveLibraryPort(db, options.StashDbEndpoint, options.TpdbEndpoint)
            : (ICoveLibraryPort)EmptyCoveLibraryPort.Instance;

        var principals = dbScope.ServiceProvider.GetService<ICurrentPrincipalAccessor>();
        var previousPrincipal = principals?.Current;
        principals?.Set(CovePrincipal.System());
        try
        {
            var studioRefs = await library.LoadAllEntityRefsAsync(EntityKind.Studio, ct);
            var performerRefs = await library.LoadAllEntityRefsAsync(EntityKind.Performer, ct);
            var videos = await library.LoadAllVideosAsync(ct);
            var units = BuildSyncUnits(studioRefs, performerRefs, videos, alsoMonitor, scope, options, adapter);

            var isV2 = string.Equals(options.SelectedVersion, "v2", StringComparison.OrdinalIgnoreCase);
            var monitor = new EntityMonitor(client, options);
            var actions = new SceneActions(client, options, library);

            var batch = await _jobs!.RunBatchAsync(
                units,
                // maxInFlight MUST stay 1: a reflect-owned/monitor unit can originate a targeted metadata
                // refresh, so a parallel whole-library fan-out would burst faster than Whisparr's command queue
                // drains — the refresh storm this phase forbids.
                maxInFlight: 1,
                async (unit, jobUnit, unitCt) =>
                    jobUnit.Complete(ToJobUnitOutcome(await DispatchSyncUnitAsync(unit, isV2, monitor, actions, library, unitCt))),
                progress,
                unitIdFactory: (unit, _) => SyncUnitId(unit),
                labelFactory: SyncUnitLabel,
                ct: ct);

            LogSyncLibrary(batch.TotalUnits, batch.SucceededUnits, batch.FailedUnits, batch.SkippedUnits);
        }
        finally
        {
            principals?.Set(previousPrincipal);
        }
    }

    /// <summary>
    /// The pure fan-out planner: turns the enumerated library into the ordered list of units the job runs.
    /// Extracted so the fan-out shape is unit-testable host-free (the batch aggregators' posture).
    /// </summary>
    /// <remarks>
    /// Gating is loop-safety, not cosmetics: an unsupported (kind, op, version) combo or an id-less entity is
    /// never planned, so it can make no outbound call. The list carries NO search op — the whole pass
    /// registers/monitors without grabbing (the LOCKED boundary). Monitor units are planned ONLY when
    /// <paramref name="alsoMonitor"/> is set (add and monitor are decoupled). A performer entity and per-scene
    /// add exist on v3 only; a studio reflects + monitors on both versions.
    /// A <see cref="SyncOp.RegisterEntity"/> studio unit is emitted (v2/site only, gated
    /// <c>!SupportsSceneAdd &amp;&amp; SupportsOwnedImport</c>) BEFORE that studio's reflect-owned unit and
    /// INDEPENDENT of <paramref name="alsoMonitor"/>: v2 reflect-owned can only attach files to an ALREADY-present
    /// site, and a clean v2 with monitor OFF creates none, so the register verb makes the site present first. v3
    /// registers presence via its per-scene add, so RegisterEntity is never planned there.
    /// </remarks>
    internal static IReadOnlyList<SyncUnit> BuildSyncUnits(
        IReadOnlyList<CoveEntityRef> studioRefs,
        IReadOnlyList<CoveEntityRef> performerRefs,
        IReadOnlyList<CoveVideo> videos,
        bool alsoMonitor,
        MonitorScope scope,
        WhisparrOptions options,
        IWhisparrAdapter adapterCaps)
    {
        var useTpdb = string.Equals(options.SelectedVersion, "v2", StringComparison.OrdinalIgnoreCase);
        var units = new List<SyncUnit>();

        // v2/site register-then-reflect: reflect-owned attaches owned files only to a site that ALREADY exists,
        // but a clean v2 (no per-scene add) registers no site — so plan a non-grabbing RegisterEntity per owned
        // studio FIRST. Emitting it before the reflect-owned loop guarantees register precedes reflect for the
        // same studio in list order (the job runs units in order at maxInFlight:1). Independent of alsoMonitor:
        // registering presence is decoupled from monitoring. v3 registers presence via its per-scene add.
        if (!adapterCaps.SupportsSceneAdd && adapterCaps.SupportsOwnedImport)
        {
            foreach (var studio in studioRefs.Where(r => HasConnectedId(r.StashIds, r.TpdbIds, useTpdb)))
            {
                units.Add(SyncUnit.EntityOp(SyncOp.RegisterEntity, EntityKind.Studio, studio.CoveId, scope));
            }
        }

        if (adapterCaps.SupportsOwnedImport)
        {
            foreach (var studio in studioRefs.Where(r => HasConnectedId(r.StashIds, r.TpdbIds, useTpdb)))
            {
                units.Add(SyncUnit.EntityOp(SyncOp.ReflectOwned, EntityKind.Studio, studio.CoveId, scope));
            }

            // Performer reflect-owned only where the version HAS a performer entity (v3); v2 has none.
            if (adapterCaps.SupportsEntityMonitor(EntityKind.Performer))
            {
                foreach (var performer in performerRefs.Where(r => HasConnectedId(r.StashIds, r.TpdbIds, useTpdb)))
                {
                    units.Add(SyncUnit.EntityOp(SyncOp.ReflectOwned, EntityKind.Performer, performer.CoveId, scope));
                }
            }
        }

        if (adapterCaps.SupportsSceneAdd)
        {
            foreach (var video in videos.Where(v => HasConnectedId(v.StashIds, v.TpdbIds, useTpdb)))
            {
                units.Add(SyncUnit.SceneAdd(video));
            }
        }

        if (alsoMonitor && adapterCaps.SupportsEntityMonitor(EntityKind.Studio))
        {
            foreach (var studio in studioRefs.Where(r => HasConnectedId(r.StashIds, r.TpdbIds, useTpdb)))
            {
                units.Add(SyncUnit.EntityOp(SyncOp.Monitor, EntityKind.Studio, studio.CoveId, scope));
            }
        }

        if (alsoMonitor && adapterCaps.SupportsEntityMonitor(EntityKind.Performer))
        {
            foreach (var performer in performerRefs.Where(r => HasConnectedId(r.StashIds, r.TpdbIds, useTpdb)))
            {
                units.Add(SyncUnit.EntityOp(SyncOp.Monitor, EntityKind.Performer, performer.CoveId, scope));
            }
        }

        return units;
    }

    // An entity/scene is sync-able only when it carries a non-empty id on the CONNECTED version's endpoint
    // (StashDB on v3, ThePornDB on v2) — the same identity rule the runners resolve by; the rest are skipped
    // with no outbound call.
    private static bool HasConnectedId(IReadOnlyList<string> stashIds, IReadOnlyList<string> tpdbIds, bool useTpdb)
        => (useTpdb ? tpdbIds : stashIds).Any(id => !string.IsNullOrEmpty(id));

    // Dispatches ONE planned unit through the EXISTING per-entity / per-scene runners (RunEntityOpAsync /
    // RunVideoOpAsync). The op is one of the three loop-safe verbs — reflect-owned / monitor / non-grabbing add —
    // never a search, so this fan-out can never start a grab.
    private static async Task<BatchUnitOutcome> DispatchSyncUnitAsync(
        SyncUnit unit, bool isV2, EntityMonitor monitor, SceneActions actions, ICoveLibraryPort library, CancellationToken ct)
        => unit.Op switch
        {
            SyncOp.Add => await RunVideoOpAsync(BatchOp.Add, unit.Video!, movieIndex: null, actions, ct),
            SyncOp.Monitor => await RunEntityOpAsync(
                unit.Kind, EntityBatchOp.Monitor, unit.Scope, unit.CoveId, isV2, monitor, actions, library, ct),
            SyncOp.RegisterEntity => await RunEntityOpAsync(
                unit.Kind, EntityBatchOp.RegisterEntity, unit.Scope, unit.CoveId, isV2, monitor, actions, library, ct),
            _ => await RunEntityOpAsync(
                unit.Kind, EntityBatchOp.ReflectOwned, unit.Scope, unit.CoveId, isV2, monitor, actions, library, ct),
        };

    private static string SyncUnitId(SyncUnit unit) => unit.Op switch
    {
        SyncOp.Add => $"scene:{unit.CoveId}",
        SyncOp.Monitor => $"monitor:{unit.Kind}:{unit.CoveId}",
        SyncOp.RegisterEntity => $"register:{unit.Kind}:{unit.CoveId}",
        _ => $"reflect:{unit.Kind}:{unit.CoveId}",
    };

    private static string SyncUnitLabel(SyncUnit unit) => unit.Op switch
    {
        SyncOp.Add => unit.Video?.Title ?? $"Scene #{unit.CoveId}",
        SyncOp.Monitor => $"Monitor {unit.Kind} #{unit.CoveId}",
        SyncOp.RegisterEntity => $"Register {unit.Kind} #{unit.CoveId}",
        _ => $"Reflect owned {unit.Kind} #{unit.CoveId}",
    };

    // The Job-Drawer description, e.g. "Whisparr: sync my library" (+ " and monitor" when the user opted in).
    private static string DescribeSyncLibrary(bool alsoMonitor)
        => alsoMonitor ? "Whisparr: sync my library and monitor" : "Whisparr: sync my library";

    /// <summary>
    /// The loop-safe fan-out verbs a library sync plans. There is deliberately no search/grab member, so the
    /// whole pass cannot start a download; a grab verb here would break the LOCKED loop-safety contract.
    /// <see cref="RegisterEntity"/> is a non-grabbing v2/site registration verb (add-a-site-present with monitor
    /// and grabbing disarmed), not a search.
    /// </summary>
    internal enum SyncOp
    {
        ReflectOwned,
        Monitor,
        Add,
        RegisterEntity,
    }

    /// <summary>
    /// One planned fan-out unit: an entity op (reflect-owned / monitor over a studio or performer addressed by
    /// its <see cref="Kind"/> + <see cref="CoveId"/>) or a scene <see cref="SyncOp.Add"/> carrying its
    /// <see cref="Video"/>. <see cref="Op"/> is the discriminant the dispatcher and the loop-safety guard read;
    /// <see cref="Scope"/> is honored only by a monitor unit.
    /// </summary>
    internal readonly record struct SyncUnit(SyncOp Op, EntityKind Kind, int CoveId, CoveVideo? Video, MonitorScope Scope)
    {
        public static SyncUnit EntityOp(SyncOp op, EntityKind kind, int coveId, MonitorScope scope)
            => new(op, kind, coveId, null, scope);

        // Kind is unused for a scene add (a scene is not a studio/performer); it defaults to Studio to satisfy
        // the struct, and the dispatcher keys on Op, never Kind, for an add.
        public static SyncUnit SceneAdd(CoveVideo video)
            => new(SyncOp.Add, EntityKind.Studio, video.CoveId, video, MonitorScope.NewReleases);
    }
}
