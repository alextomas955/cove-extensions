using Cove.Core.Interfaces;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WhisparrSync.Adapters;
using WhisparrSync.Client;
using WhisparrSync.Contracts;
using WhisparrSync.Library;
using WhisparrSync.Monitor;
using WhisparrSync.Options;
using WhisparrSync.Push;
using static Cove.Extensions.Shared.RunAsSystem;
using static WhisparrSync.Contracts.WireSerializers;

namespace WhisparrSync;

/// <summary>
/// The bulk "Sync my library to Whisparr" slice: the configure-gated preview that counts, before the user
/// commits, how many studios/performers (and v3 scenes) will register vs be skipped for carrying no
/// connected-version id, plus the exclusive background job that performs the fan-out.
/// </summary>
public sealed partial class WhisparrSync
{
    // The bulk "Sync my library to Whisparr" preview (bodiless GET) + action (enqueues one exclusive job).
    private const string SyncPreviewRoute = RouteBase + "/sync-preview";
    private const string SyncLibraryRoute = RouteBase + "/sync-library";

    // One exclusive job per library-wide sync — the Job Drawer carries its progress + summary.
    private const string SyncLibraryJobType = "whisparr-sync-library";

    /// <summary>
    /// Registers the library-sync slice's routes: the bodiless preview GET and the action POST (which enqueues
    /// one exclusive job). Both configure-gated + stored-creds-only.
    /// </summary>
    private void MapSyncEndpoints(IEndpointRouteBuilder endpoints)
    {
        // The bulk "Sync my library to Whisparr" preview: a bodiless GET (stored creds only) returning the
        // sync-able-vs-skipped counts.
        endpoints.MapGet(SyncPreviewRoute,
            (WhisparrClient client, CancellationToken ct)
                => SyncPreviewAsync(client, ct)).ConfigureGated();

        // The bulk "Sync my library to Whisparr" action: POSTs {AlsoMonitor, Scope} and enqueues one exclusive
        // background job (stored creds only). Returns { jobId, description } — never a synchronous fan-out.
        endpoints.MapPost(SyncLibraryRoute,
            (SyncLibraryRequest req, WhisparrClient client, CancellationToken ct)
                => SyncLibraryAsync(req, client, ct)).ConfigureGated();
    }

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
        => SyncPreviewCore(
            studioRefs,
            performerRefs,
            SceneCountByConnectedId(videos, options, adapterCaps),
            options,
            adapterCaps);

    // The scene bucket taken as an already-folded count, so the whole-library preview can stream its scenes while
    // the entity buckets stay list-shaped (they are bounded by the studio/performer counts, not the file count).
    internal static SyncPreviewResponse SyncPreviewCore(
        IReadOnlyList<CoveEntityRef> studioRefs,
        IReadOnlyList<CoveEntityRef> performerRefs,
        SyncPreviewCount scenes,
        WhisparrOptions options,
        IWhisparrAdapter adapterCaps)
    {
        var useTpdb = string.Equals(options.SelectedVersion, "v2", StringComparison.OrdinalIgnoreCase);

        var studios = CountByConnectedId(
            studioRefs.Select(r => useTpdb ? r.TpdbIds : r.StashIds), adapterCaps is IWhisparrOwnedImport);
        var performers = CountByConnectedId(
            performerRefs.Select(r => useTpdb ? r.TpdbIds : r.StashIds),
            adapterCaps is IWhisparrPerformerMonitor);

        return new SyncPreviewResponse(studios, performers, scenes);
    }

    private static SyncPreviewCount SceneCountByConnectedId(
        IEnumerable<CoveVideo> videos, WhisparrOptions options, IWhisparrAdapter adapterCaps)
    {
        var useTpdb = string.Equals(options.SelectedVersion, "v2", StringComparison.OrdinalIgnoreCase);
        return CountByConnectedId(
            videos.Select(v => useTpdb ? v.TpdbIds : v.StashIds), adapterCaps is IWhisparrScenePush);
    }

    private static async Task<SyncPreviewCount> SceneCountByConnectedIdAsync(
        IAsyncEnumerable<CoveVideo> videos, WhisparrOptions options, IWhisparrAdapter adapterCaps, CancellationToken ct)
    {
        if (adapterCaps is not IWhisparrScenePush)
        {
            return new SyncPreviewCount(0, 0);
        }

        var useTpdb = string.Equals(options.SelectedVersion, "v2", StringComparison.OrdinalIgnoreCase);
        int withId = 0, skipped = 0;
        await foreach (var video in videos.WithCancellation(ct))
        {
            if ((useTpdb ? video.TpdbIds : video.StashIds).Any(id => !string.IsNullOrEmpty(id)))
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
        WhisparrClient client, CancellationToken ct)
    {
        var (options, _, _) = await StoredCredsAsync(ct);
        if (AdapterSelector.SelectForVersion(options.SelectedVersion, client) is not { } adapter)
        {
            return VersionUnsupported();
        }

        return Results.Json(await SyncPreviewCountsAsSystemAsync(options, adapter, ct), EnumStringResponseJsonOptions);
    }

    // Folds the whole-library preview in a fresh scope under the System principal via the RunAsSystem seam — the
    // same trusted-read span IngestCoordinator uses. The scene bucket streams; only the three counts cross back
    // out. Degrades to all-zero when no host DB scope is available.
    private async Task<SyncPreviewResponse> SyncPreviewCountsAsSystemAsync(
        WhisparrOptions options, IWhisparrAdapter adapter, CancellationToken ct)
    {
        if (_scopeFactory is null)
        {
            return SyncPreviewCore([], [], new SyncPreviewCount(0, 0), options, adapter);
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        if (scope.ServiceProvider.GetService<DbContext>() is not { } db)
        {
            return SyncPreviewCore([], [], new SyncPreviewCount(0, 0), options, adapter);
        }

        return await RunAsSystemAsync(scope.ServiceProvider, async () =>
        {
            var library = new CoveLibraryPort(db, options.StashDbEndpoint, options.TpdbEndpoint);
            var studios = await library.LoadAllEntityRefsAsync(EntityKind.Studio, ct);
            var performers = await library.LoadAllEntityRefsAsync(EntityKind.Performer, ct);
            var scenes = await SceneCountByConnectedIdAsync(library.StreamAllVideosAsync(ct), options, adapter, ct);
            return SyncPreviewCore(studios, performers, scenes, options, adapter);
        });
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
        SyncLibraryRequest req, WhisparrClient client, CancellationToken ct)
    {
        var (options, _, _) = await StoredCredsAsync(ct);
        if (AdapterSelector.SelectForVersion(options.SelectedVersion, client) is null)
        {
            return VersionUnsupported();
        }

        // The extension's highest-blast-radius route: one incomplete configuration fans across a whole-library
        // run, so it is refused here rather than inside the job. Deliberately above the no-job-service check —
        // when both hold, the configuration is the one the user can act on.
        if (RefuseIncompleteConfig(options) is { } refusal)
        {
            return refusal;
        }

        var scope = ParseMonitorScope(req.Scope, options.DefaultMonitorScope);
        if (_jobs is null)
        {
            return Results.Json(new ErrorResponse("JOB_SERVICE_UNAVAILABLE"), statusCode: 400);
        }

        var description = DescribeSyncLibrary(req.AlsoMonitor);
        var jobId = _jobs.Enqueue(
            SyncLibraryJobType, description,
            (progress, jobCt) => RunSyncLibraryJobAsync(req.AlsoMonitor, scope, options, progress, jobCt),
            exclusive: true); // one library-wide sync at a time — no concurrent full-library fan-out
        return Results.Json(new JobAcceptedResponse(jobId, description), EnumStringResponseJsonOptions);
    }

    // Runs the library sync as a background job: a fresh scope + client (this outlives the request). The whole
    // enumerate+fan-out runs under CovePrincipal.System() via the RunAsSystem seam — a background scope carries
    // no request principal, and CoveContext's per-principal authz filters would undercount a library-wide read
    // otherwise. Builds the unified SyncUnit list and drives each unit through the EXISTING reflect-owned/add/
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

        await RunAsSystemAsync(dbScope.ServiceProvider, async () =>
        {
            var studioRefs = await library.LoadAllEntityRefsAsync(EntityKind.Studio, ct);
            var performerRefs = await library.LoadAllEntityRefsAsync(EntityKind.Performer, ct);

            // A connection with no per-scene add plans no scene bucket, so it must not pay for one either: the
            // count and the boundary pass are skipped entirely rather than computed and discarded.
            var sceneCount = 0;
            IReadOnlyList<SyncSlice> slices = [];
            if (adapter is IWhisparrScenePush)
            {
                sceneCount = await library.CountVideosAsync(ct);
                slices = PlanSceneSlices(sceneCount, await ReadSceneCutIdsAsync(library, sceneCount, ct));
            }

            var units = BuildSyncUnits(studioRefs, performerRefs, slices, alsoMonitor, scope, options, adapter);

            // StartUnit is idempotent per id — an id already present reuses its state and skips the recount — so
            // registering every planned id here makes the drawer's denominator the run's own total from the first
            // report, instead of the units started so far. The batch's own StartUnit then finds each one. This is
            // affordable only because the scene bucket is a bounded number of slices. IJobProgress.StartUnit also
            // has a default implementation returning a no-op unit, so a host that does not override it degrades to
            // a denominator that grows during the run rather than failing.
            foreach (var unit in units)
            {
                progress.StartUnit(SyncUnitId(unit), SyncUnitLabel(unit)).Dispose();
            }

            var isV2 = string.Equals(options.SelectedVersion, "v2", StringComparison.OrdinalIgnoreCase);
            var monitor = new EntityMonitor(client, options);
            var actions = new SceneActions(client, options, library, CapabilityPort(client));
            var tally = new SyncTally();

            await _jobs!.RunBatchAsync(
                units,
                // maxInFlight MUST stay 1: a reflect-owned/monitor unit can originate a targeted metadata
                // refresh, so a parallel whole-library fan-out would burst faster than Whisparr's command queue
                // drains — a refresh storm. Coarser units change the GRAIN of a unit, never this.
                maxInFlight: 1,
                async (unit, jobUnit, unitCt) =>
                {
                    var outcome = await DispatchSyncUnitAsync(
                        unit, isV2, sceneCount, monitor, actions, library, jobUnit, tally, unitCt);
                    if (unit.Op != SyncOp.AddSlice)
                    {
                        tally.AddUnit(outcome);
                    }

                    jobUnit.Complete(ToJobUnitOutcome(outcome));
                },
                progress,
                unitIdFactory: (unit, _) => SyncUnitId(unit),
                labelFactory: SyncUnitLabel,
                ct: ct);

            // Fed the accumulated per-SCENE tally, not the batch's unit counts: a unit is now a slice of many
            // scenes, so the batch counts would turn this line into a slice count.
            LogSyncLibrary(tally.Total, tally.Succeeded, tally.Failed, tally.Skipped);
        });
    }

    // Reads the ids the slice boundaries fall on in ONE ascending pass over the id column, retaining at most
    // MaxSceneSlices - 1 of them. Rejected alternative: one Skip(k*size).Take(1) per boundary — SQL OFFSET
    // re-walks the rows it skips, so the boundaries would cost many times the single pass. The pass stops as
    // soon as the last boundary is seen.
    private static async Task<IReadOnlyList<int>> ReadSceneCutIdsAsync(
        ICoveLibraryPort library, int videoCount, CancellationToken ct)
    {
        var wanted = SceneCutOrdinals(videoCount);
        if (wanted.Count == 0)
        {
            return [];
        }

        var cutIds = new List<int>(wanted.Count);
        var ordinal = 0;
        await foreach (var id in library.StreamVideoIdsAsync(ct))
        {
            ordinal++;
            if (ordinal == wanted[cutIds.Count])
            {
                cutIds.Add(id);
                if (cutIds.Count == wanted.Count)
                {
                    break;
                }
            }
        }

        return cutIds;
    }

    // The smallest slice worth planning: one keyset page of the range read, so even the smallest slice is a
    // single round trip rather than a query per handful of scenes.
    private const int MinSliceScenes = Library.CoveLibraryPort.StreamPageSize;

    // The ceiling on the scene fan-out. Sixty-four steps move the bar in increments finer than it can be read,
    // pre-registering that many ids costs nothing, and past it the per-studio and per-performer units dominate
    // the unit count anyway. The COUNT is clamped rather than the slice SIZE fixed: a fixed size would leave the
    // unit count growing with the library, divided by a constant but still growing.
    private const int MaxSceneSlices = 64;

    /// <summary>
    /// How many scene slices a library of <paramref name="videoCount"/> videos is divided into: one per
    /// <see cref="MinSliceScenes"/> scenes, capped at <see cref="MaxSceneSlices"/>, and none at all for an empty
    /// library.
    /// </summary>
    /// <remarks>
    /// Below <see cref="MaxSceneSlices"/> × <see cref="MinSliceScenes"/> scenes the count still varies with
    /// library size, which is intended — the property that matters is that it stops varying in the unbounded
    /// direction.
    /// </remarks>
    internal static int SceneSliceCount(int videoCount)
        => videoCount <= 0
            ? 0
            : Math.Clamp((videoCount + MinSliceScenes - 1) / MinSliceScenes, 1, MaxSceneSlices);

    /// <summary>
    /// The 1-based scene positions the slice boundaries fall on — the last ordinal of every slice but the final
    /// one, ascending. Empty when the library plans fewer than two slices.
    /// </summary>
    internal static IReadOnlyList<int> SceneCutOrdinals(int videoCount)
    {
        var slices = SceneSliceCount(videoCount);
        if (slices < 2)
        {
            return [];
        }

        // Sizes differ by at most one, so no slice is empty and the ordinals strictly increase.
        var baseSize = videoCount / slices;
        var remainder = videoCount % slices;
        var ordinals = new List<int>(slices - 1);
        var last = 0;
        for (var i = 0; i < slices - 1; i++)
        {
            last += baseSize + (i < remainder ? 1 : 0);
            ordinals.Add(last);
        }

        return ordinals;
    }

    /// <summary>
    /// Divides the library into a bounded number of half-open Cove-video-id ranges, given the total count and
    /// the ids sitting on the boundaries <see cref="SceneCutOrdinals"/> asked for. Pure.
    /// </summary>
    /// <remarks>
    /// Consecutive slices SHARE their boundary id: slice i's upper bound is slice i+1's lower bound. The first
    /// slice's lower bound is 0 (Cove ids are positive) and the last slice's upper bound is
    /// <see cref="int.MaxValue"/>, so every id satisfies exactly one slice's <c>After &lt; id &lt;= UpTo</c> —
    /// total coverage by construction rather than by luck, and a scene inserted after the boundaries were chosen
    /// still falls in the final slice. Fewer boundary ids than expected (the library shrank between the count and
    /// the boundary pass) simply plans fewer slices; every scene is still covered.
    /// </remarks>
    internal static IReadOnlyList<SyncSlice> PlanSceneSlices(int videoCount, IReadOnlyList<int> cutIds)
    {
        if (videoCount <= 0)
        {
            return [];
        }

        var ordinals = SceneCutOrdinals(videoCount);
        var boundaries = Math.Min(ordinals.Count, cutIds.Count);
        var slices = new List<SyncSlice>(boundaries + 1);
        var after = 0;
        var firstOrdinal = 1;
        for (var i = 0; i < boundaries; i++)
        {
            slices.Add(new SyncSlice(after, cutIds[i], firstOrdinal, ordinals[i]));
            after = cutIds[i];
            firstOrdinal = ordinals[i] + 1;
        }

        slices.Add(new SyncSlice(after, int.MaxValue, firstOrdinal, Math.Max(firstOrdinal, videoCount)));
        return slices;
    }

    /// <summary>
    /// The pure fan-out planner: turns the enumerated entities and the planned scene slices into the ordered
    /// list of units the job runs. Extracted so the fan-out shape is unit-testable host-free (the batch
    /// aggregators' posture).
    /// </summary>
    /// <remarks>
    /// Gating is loop-safety, not cosmetics: an unsupported (kind, op, version) combo or an id-less entity is
    /// never planned, so it can make no outbound call. The list carries NO search op — the whole pass
    /// registers/monitors without grabbing (the LOCKED boundary). Monitor units are planned ONLY when
    /// <paramref name="alsoMonitor"/> is set (add and monitor are decoupled). A performer entity and per-scene
    /// add exist on v3 only; a studio reflects + monitors on both versions. A scene's own eligibility is NOT
    /// decided here — a slice is a range, and each scene in it is tested when the range is read.
    /// A <see cref="SyncOp.RegisterEntity"/> studio unit is emitted (v2/site only, gated
    /// <c>adapter is not IWhisparrScenePush and IWhisparrOwnedImport</c>) BEFORE that studio's reflect-owned unit and
    /// INDEPENDENT of <paramref name="alsoMonitor"/>: v2 reflect-owned can only attach files to an ALREADY-present
    /// site, and a clean v2 with monitor OFF creates none, so the register verb makes the site present first. v3
    /// registers presence via its per-scene add, so RegisterEntity is never planned there.
    /// </remarks>
    internal static IReadOnlyList<SyncUnit> BuildSyncUnits(
        IReadOnlyList<CoveEntityRef> studioRefs,
        IReadOnlyList<CoveEntityRef> performerRefs,
        IReadOnlyList<SyncSlice> slices,
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
        if (adapterCaps is not IWhisparrScenePush and IWhisparrOwnedImport)
        {
            foreach (var studio in studioRefs.Where(r => HasConnectedId(r.StashIds, r.TpdbIds, useTpdb)))
            {
                units.Add(SyncUnit.EntityOp(SyncOp.RegisterEntity, EntityKind.Studio, studio.CoveId, scope));
            }
        }

        if (adapterCaps is IWhisparrOwnedImport)
        {
            foreach (var studio in studioRefs.Where(r => HasConnectedId(r.StashIds, r.TpdbIds, useTpdb)))
            {
                units.Add(SyncUnit.EntityOp(SyncOp.ReflectOwned, EntityKind.Studio, studio.CoveId, scope));
            }

            // Performer reflect-owned only where the version HAS a performer entity (v3); v2 has none.
            if (adapterCaps is IWhisparrPerformerMonitor)
            {
                foreach (var performer in performerRefs.Where(r => HasConnectedId(r.StashIds, r.TpdbIds, useTpdb)))
                {
                    units.Add(SyncUnit.EntityOp(SyncOp.ReflectOwned, EntityKind.Performer, performer.CoveId, scope));
                }
            }
        }

        // One unit per SLICE, not per scene. Eligibility is no longer decided here: a slice is a range of ids,
        // and each scene in it is tested for a connected id when the slice is read at dispatch time.
        if (adapterCaps is IWhisparrScenePush)
        {
            foreach (var slice in slices)
            {
                units.Add(SyncUnit.AddSlice(slice));
            }
        }

        if (alsoMonitor && adapterCaps is IWhisparrStudioMonitor)
        {
            foreach (var studio in studioRefs.Where(r => HasConnectedId(r.StashIds, r.TpdbIds, useTpdb)))
            {
                units.Add(SyncUnit.EntityOp(SyncOp.Monitor, EntityKind.Studio, studio.CoveId, scope));
            }
        }

        if (alsoMonitor && adapterCaps is IWhisparrPerformerMonitor)
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
        SyncUnit unit, bool isV2, int sceneCount, EntityMonitor monitor, SceneActions actions,
        ICoveLibraryPort library, IJobUnit jobUnit, SyncTally tally, CancellationToken ct)
        => unit.Op switch
        {
            SyncOp.AddSlice => await DispatchSceneSliceAsync(
                unit.Slice!.Value, isV2, sceneCount, actions, library, jobUnit, tally, ct),
            SyncOp.Monitor => await RunEntityOpAsync(
                unit.Kind, EntityBatchOp.Monitor, unit.Scope, unit.CoveId, isV2, monitor, actions, library, ct),
            SyncOp.RegisterEntity => await RunEntityOpAsync(
                unit.Kind, EntityBatchOp.RegisterEntity, unit.Scope, unit.CoveId, isV2, monitor, actions, library, ct),
            _ => await RunEntityOpAsync(
                unit.Kind, EntityBatchOp.ReflectOwned, unit.Scope, unit.CoveId, isV2, monitor, actions, library, ct),
        };

    // Runs one slice: reads its id range at dispatch time and puts every eligible scene through the SAME add
    // runner a single-scene action uses, so no second mutation spine exists. Reading the range now is also what
    // makes a scene deleted between planning and this slice's turn simply absent rather than a reported fault.
    // The slice NEVER stops on a failure — an early exit would silently leave part of the range unvisited, which
    // is the truncated answer this design exists to avoid.
    internal static async Task<BatchUnitOutcome> DispatchSceneSliceAsync(
        SyncSlice slice, bool useTpdb, int sceneCount, SceneActions actions, ICoveLibraryPort library,
        IJobUnit jobUnit, SyncTally tally, CancellationToken ct)
    {
        int succeeded = 0, failed = 0, skipped = 0;
        var ordinal = slice.FirstOrdinal - 1;
        var sincePage = 0;

        await foreach (var video in library.StreamVideosInRangeAsync(slice.AfterCoveId, slice.UpToCoveId, ct))
        {
            ordinal++;
            if (!HasConnectedId(video.StashIds, video.TpdbIds, useTpdb))
            {
                skipped++;
            }
            else
            {
                switch (await RunVideoOpAsync(BatchOp.Add, video, movieIndex: null, actions, ct))
                {
                    case BatchUnitOutcome.Succeeded:
                        succeeded++;
                        break;
                    case BatchUnitOutcome.Skipped:
                        skipped++;
                        break;
                    default:
                        failed++;
                        break;
                }
            }

            // Throttled to the grain the range read pages at, because IJobUnit.Report re-counts the job's whole
            // unit dictionary under a host-wide lock: reporting once per scene would reintroduce exactly the cost
            // slicing removes, and would stall every other job on the instance while it did.
            if (++sincePage >= Library.CoveLibraryPort.StreamPageSize)
            {
                sincePage = 0;
                ReportSliceProgress(jobUnit, slice, ordinal, sceneCount);
            }
        }

        ReportSliceProgress(jobUnit, slice, ordinal, sceneCount);
        tally.AddScenes(succeeded, failed, skipped);

        // Failed if any scene failed; Succeeded if any scene registered; otherwise Skipped — which covers both a
        // range whose scenes all carry no connected id and a range that holds none at all.
        return failed > 0
            ? BatchUnitOutcome.Failed
            : succeeded > 0 ? BatchUnitOutcome.Succeeded : BatchUnitOutcome.Skipped;
    }

    private static void ReportSliceProgress(IJobUnit jobUnit, SyncSlice slice, int ordinal, int sceneCount)
    {
        var span = Math.Max(1, slice.LastOrdinal - slice.FirstOrdinal + 1);
        var done = Math.Clamp(ordinal - slice.FirstOrdinal + 1, 0, span);
        jobUnit.Report((double)done / span, $"Scene {ordinal} of {sceneCount}");
    }

    private static string SyncUnitId(SyncUnit unit) => unit.Op switch
    {
        SyncOp.AddSlice => $"scenes:{unit.Slice!.Value.FirstOrdinal}-{unit.Slice.Value.LastOrdinal}",
        SyncOp.Monitor => $"monitor:{unit.Kind}:{unit.CoveId}",
        SyncOp.RegisterEntity => $"register:{unit.Kind}:{unit.CoveId}",
        _ => $"reflect:{unit.Kind}:{unit.CoveId}",
    };

    private static string SyncUnitLabel(SyncUnit unit) => unit.Op switch
    {
        SyncOp.AddSlice => $"Scenes {unit.Slice!.Value.FirstOrdinal}-{unit.Slice.Value.LastOrdinal}",
        SyncOp.Monitor => $"Monitor {unit.Kind} #{unit.CoveId}",
        SyncOp.RegisterEntity => $"Register {unit.Kind} #{unit.CoveId}",
        _ => $"Reflect owned {unit.Kind} #{unit.CoveId}",
    };

    // The run's real per-SCENE outcome counts, accumulated across slices and entity units. The batch's own
    // counts are per UNIT, and a unit is now a slice, so they cannot answer how many scenes the run touched.
    internal sealed class SyncTally
    {
        private int _succeeded;
        private int _failed;
        private int _skipped;

        public int Succeeded => Volatile.Read(ref _succeeded);

        public int Failed => Volatile.Read(ref _failed);

        public int Skipped => Volatile.Read(ref _skipped);

        public int Total => Succeeded + Failed + Skipped;

        public void AddScenes(int succeeded, int failed, int skipped)
        {
            Interlocked.Add(ref _succeeded, succeeded);
            Interlocked.Add(ref _failed, failed);
            Interlocked.Add(ref _skipped, skipped);
        }

        public void AddUnit(BatchUnitOutcome outcome)
        {
            switch (outcome)
            {
                case BatchUnitOutcome.Succeeded:
                    Interlocked.Increment(ref _succeeded);
                    break;
                case BatchUnitOutcome.Skipped:
                    Interlocked.Increment(ref _skipped);
                    break;
                default:
                    Interlocked.Increment(ref _failed);
                    break;
            }
        }
    }

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
        AddSlice,
        RegisterEntity,
    }

    /// <summary>
    /// One contiguous run of library scenes, addressed as the half-open Cove-video-id range
    /// <c>(AfterCoveId, UpToCoveId]</c>. The ordinals are the 1-based scene positions the boundaries produced and
    /// exist only to label the Job-Drawer row.
    /// </summary>
    internal readonly record struct SyncSlice(int AfterCoveId, int UpToCoveId, int FirstOrdinal, int LastOrdinal);

    /// <summary>
    /// One planned fan-out unit: an entity op (reflect-owned / monitor / register over a studio or performer
    /// addressed by its <see cref="Kind"/> + <see cref="CoveId"/>) or a <see cref="SyncOp.AddSlice"/> over the
    /// <see cref="Slice"/> range. <see cref="Op"/> is the discriminant the dispatcher and the loop-safety guard
    /// read; <see cref="Scope"/> is honored only by a monitor unit, and <see cref="Slice"/> is set only for a
    /// slice unit.
    /// </summary>
    /// <remarks>
    /// A slice unit carries a range rather than a scene, so the scene fan-out no longer grows with the library.
    /// The entity units still do not: each studio and performer is a distinct outbound operation with its own
    /// outcome, so that term stays one unit per entity.
    /// </remarks>
    internal readonly record struct SyncUnit(
        SyncOp Op, EntityKind Kind, int CoveId, MonitorScope Scope, SyncSlice? Slice)
    {
        public static SyncUnit EntityOp(SyncOp op, EntityKind kind, int coveId, MonitorScope scope)
            => new(op, kind, coveId, scope, null);

        // Kind and CoveId are unused for a slice (a range of scenes is not a studio/performer and has no single
        // Cove id); they take neutral values to satisfy the struct, and the dispatcher keys on Op.
        public static SyncUnit AddSlice(SyncSlice slice)
            => new(SyncOp.AddSlice, EntityKind.Studio, 0, MonitorScope.NewReleases, slice);
    }
}
