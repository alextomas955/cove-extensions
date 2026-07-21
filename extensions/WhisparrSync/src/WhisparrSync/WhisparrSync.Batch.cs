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
using WhisparrSync.Scene;
using static Cove.Extensions.Shared.MinimalApiPermissions;

namespace WhisparrSync;

/// <summary>
/// The bulk-action machinery for the videos-list and studios/performers-list selections: the two
/// endpoint handlers, their Job-backed runners, and the pure per-op planning/dispatch helpers.
/// </summary>
public sealed partial class WhisparrSync
{
    /// <summary>
    /// Runs one batch op (<c>add</c>/<c>search</c>/<c>searchUpgrades</c>/<c>exclude</c>/<c>unExclude</c>)
    /// over a selection of Cove video ids from the videos-list bulk action. Configure-gated +
    /// stored-creds-only (the body carries no url/key) + v3-only (v2 → 400). The id list is capped BEFORE
    /// any per-item work (fan-out containment); an unknown op is a clean 400. Each id is resolved to a
    /// scene SERVER-SIDE inside a fresh DB scope, and an id with no StashDB identity (or, for a search
    /// op, not yet an added Whisparr movie) is counted as skipped with no outbound call. Only
    /// <c>search</c>/<c>searchUpgrades</c> may grab; <c>add</c>/<c>exclude</c>/<c>unExclude</c> never search
    /// (loop-safety is LOCKED). Returns the aggregate <see cref="VideosBatchResult"/> (never the key).
    /// </summary>
    internal async Task<IResult> VideosBatchAsync(
        VideosBatchRequest req, WhisparrClient client, ICurrentPrincipalAccessor principal, CancellationToken ct)
    {
        if (Forbidden(principal, Permissions.ExtensionsConfigure) is { } denied)
        {
            return denied;
        }

        if (!TryParseBatchOp(req.Op, out var op))
        {
            return Results.Json(new { code = "UNKNOWN_OP" }, statusCode: 400);
        }

        // Cap the selection BEFORE any per-id DB read or outbound call. A null array is an empty batch.
        var coveIds = req.CoveIds ?? [];
        if (coveIds.Length > MaxEntityIdsPerRequest)
        {
            return Results.Json(new { code = "TOO_MANY_IDS", max = MaxEntityIdsPerRequest }, statusCode: 400);
        }

        var (options, baseUrl, apiKey) = await ResolveCredsAsync(new TestConnectionRequest(null, null), ct);
        if (AdapterSelector.SelectForVersion(options.SelectedVersion, client) is not V3Adapter adapter)
        {
            return Results.Json(new { code = "VERSION_UNSUPPORTED" }, statusCode: 400);
        }

        var description = DescribeVideosBatch(op, coveIds.Length);

        // No host job service (a unit-test host): run inline with the request-scoped adapter and return directly.
        if (_jobs is null)
        {
            return await WithScopedLibraryAsync(options.StashDbEndpoint, options.TpdbEndpoint, async library =>
            {
                var result = await VideosBatchCoreAsync(op, coveIds, client, adapter, library, options, baseUrl, apiKey, ct);
                if (result.IsOk)
                {
                    var value = result.Value!;
                    LogVideosBatch(value.Op, value.Total, value.Succeeded, value.Skipped, value.Failed);
                }

                return ToMonitorResult(result);
            });
        }

        // The per-scene fan-out outlives this request, so the job re-opens its OWN scope + client and reports
        // per-scene progress into the Job Drawer via RunBatchAsync.
        var jobId = _jobs.Enqueue(
            VideosBatchJobType, description,
            (progress, jobCt) => RunVideosBatchJobAsync(op, coveIds, options, baseUrl, apiKey, progress, jobCt),
            exclusive: false);
        return Results.Json(new { jobId, description }, MonitorResponseJsonOptions);
    }

    // Runs the videos batch as a background job: a fresh scope + client (this outlives the request). Resolves each
    // Cove id to a scene (skipping ids with no StashDB identity), fetches the Whisparr movie index once for the
    // search ops, then one Job-Drawer unit per RESOLVED scene via RunBatchAsync — each unit runs the SAME
    // per-scene op (RunVideoOpAsync) the aggregator does. Ids with no identity are reported as skipped units too,
    // so the drawer's total matches the selection.
    private async Task RunVideosBatchJobAsync(
        BatchOp op, IReadOnlyList<int> coveIds, WhisparrOptions options, string baseUrl, string apiKey,
        Cove.Core.Interfaces.IJobProgress progress, CancellationToken ct)
    {
        await using var dbScope = ScopeFactory.CreateAsyncScope();
        var client = dbScope.ServiceProvider.GetRequiredService<WhisparrClient>();
        if (AdapterSelector.SelectForVersion(options.SelectedVersion, client) is not V3Adapter adapter)
        {
            progress.Report(1d, "Whisparr v3 is required for this action.");
            return;
        }

        var library = dbScope.ServiceProvider.GetService<DbContext>() is { } db
            ? new CoveLibraryPort(db, options.StashDbEndpoint, options.TpdbEndpoint)
            : (ICoveLibraryPort)EmptyCoveLibraryPort.Instance;

        var plan = await PlanVideosBatchAsync(op, coveIds, adapter, library, baseUrl, apiKey, ct);
        if (!plan.IsOk)
        {
            // A movie-index fetch failure fails the whole run — the drawer shows the job as failed.
            throw new InvalidOperationException($"Whisparr movie list unavailable ({plan.State}).");
        }

        // Make ONE unit per SELECTED id (not just the resolved scenes) so the drawer's total matches the
        // selection and unresolvable ids (no StashDB identity) show as Skipped — consistent with the entities job.
        var (scenes, _, movieIndex) = plan.Value!;
        var byId = scenes.ToDictionary(scene => scene.CoveId);
        var actions = new SceneActions(client, options, library);
        var batch = await _jobs!.RunBatchAsync(
            coveIds,
            maxInFlight: 1, // sequential mutations — one origin-tag get-or-create, gentle on Whisparr
            async (id, unit, unitCt) =>
                unit.Complete(byId.TryGetValue(id, out var video)
                    ? ToJobUnitOutcome(await RunVideoOpAsync(op, video, movieIndex, actions, unitCt))
                    : JobUnitOutcome.Skipped),
            progress,
            unitIdFactory: (id, _) => $"video:{id}",
            labelFactory: id => byId.TryGetValue(id, out var video) ? (video.Title ?? $"Scene #{id}") : $"Scene #{id}",
            ct: ct);

        var wireOp = WireOp(op);
        LogVideosBatch(wireOp, batch.TotalUnits, batch.SucceededUnits, batch.SkippedUnits, batch.FailedUnits);
    }

    // The Job-Drawer description for a videos batch, e.g. "Whisparr: add 4 scenes".
    private static string DescribeVideosBatch(BatchOp op, int count)
    {
        var verb = op switch
        {
            BatchOp.Add => "add",
            BatchOp.Exclude => "exclude",
            BatchOp.SearchUpgrades => "search upgrades for",
            _ => "search",
        };
        return $"Whisparr: {verb} {count} {(count == 1 ? "scene" : "scenes")}";
    }

    /// <summary>
    /// The scene-resolution + per-op dispatch half of <see cref="VideosBatchAsync"/>, extracted so it is
    /// unit-testable host-free with a seeded <see cref="ICoveLibraryPort"/> + a fake-HTTP <see cref="V3Adapter"/>
    /// (no DB scope, no live Whisparr). Resolves each Cove id to a scene via the injected
    /// <paramref name="library"/> (skipping ids with no StashDB identity), then dispatches the batch to the
    /// <see cref="SceneActions"/> helpers — add/exclude/un-exclude by <see cref="SceneRef"/>/stashId,
    /// and search/search-upgrades by the movie ids resolved from the fetched movie set (a not-added scene is
    /// skipped). Aggregates total/succeeded/skipped/failed; a transport failure on the movie read or a batch leg
    /// propagates verbatim (mapped to a 400/502 by <see cref="ToMonitorResult{T}"/>). Only search/search-upgrades
    /// issue a command — the grab boundary is LOCKED here as in the service.
    /// </summary>
    internal static async Task<WhisparrResult<VideosBatchResult>> VideosBatchCoreAsync(
        BatchOp op, IReadOnlyList<int> coveIds, WhisparrClient client, V3Adapter adapter, ICoveLibraryPort library,
        WhisparrOptions options, string baseUrl, string apiKey, CancellationToken ct)
    {
        var total = coveIds.Count;
        var plan = await PlanVideosBatchAsync(op, coveIds, adapter, library, baseUrl, apiKey, ct);
        if (!plan.IsOk)
        {
            return PropagateBatch(plan);
        }

        var (scenes, unresolved, movieIndex) = plan.Value!;
        if (scenes.Count == 0)
        {
            return WhisparrResult<VideosBatchResult>.Ok(new VideosBatchResult(WireOp(op), total, 0, unresolved, 0));
        }

        var actions = new SceneActions(client, options, library);
        int succeeded = 0, skipped = unresolved, failed = 0;
        foreach (var video in scenes)
        {
            switch (await RunVideoOpAsync(op, video, movieIndex, actions, ct))
            {
                case BatchUnitOutcome.Succeeded: succeeded++; break;
                case BatchUnitOutcome.Skipped: skipped++; break;
                default: failed++; break;
            }
        }

        return WhisparrResult<VideosBatchResult>.Ok(new VideosBatchResult(WireOp(op), total, succeeded, skipped, failed));
    }

    // The resolved input to a videos batch: the scenes that carry a StashDB identity, the count of selected ids
    // that did NOT (skipped, never a call), and — for the search ops — the Whisparr movie index fetched once.
    private readonly record struct VideosBatchPlan(
        IReadOnlyList<CoveVideo> Scenes, int Unresolved, IReadOnlyDictionary<string, WhisparrMovie>? MovieIndex);

    // Resolves the selection once for both the aggregator (VideosBatchCoreAsync) and the job. An id with no
    // StashDB identity is Unresolved (skipped, no call); the search ops additionally fetch the movie index once
    // (a transport failure on that read propagates so the inline path 502s / the job fails).
    private static async Task<WhisparrResult<VideosBatchPlan>> PlanVideosBatchAsync(
        BatchOp op, IReadOnlyList<int> coveIds, V3Adapter adapter, ICoveLibraryPort library,
        string baseUrl, string apiKey, CancellationToken ct)
    {
        var unresolved = 0;
        var scenes = new List<CoveVideo>();
        foreach (var id in coveIds)
        {
            var video = await library.LoadVideoByIdAsync(id, ct);
            if (video is null || video.StashIds.Count == 0 || string.IsNullOrEmpty(video.StashIds[0]))
            {
                unresolved++; // no resolvable StashDB identity — skipped, no outbound call
                continue;
            }

            scenes.Add(video);
        }

        IReadOnlyDictionary<string, WhisparrMovie>? movieIndex = null;
        if (op is BatchOp.Search or BatchOp.SearchUpgrades && scenes.Count > 0)
        {
            var movies = await adapter.ListMoviesAsync(baseUrl, apiKey, ct);
            if (!movies.IsOk)
            {
                return PropagateError<VideosBatchPlan, WhisparrMovie[]>(movies);
            }

            movieIndex = SceneStatusProjector.BuildMovieIndex(movies.Value!);
        }

        return WhisparrResult<VideosBatchPlan>.Ok(new VideosBatchPlan(scenes, unresolved, movieIndex));
    }

    // One scene's op — the single source both the aggregator (VideosBatchCoreAsync, unit-tested) and the job run.
    // Add/exclude reuse the batched helpers with a ONE-scene list so the payload (title + year) is byte-identical
    // to the multi-scene path; search resolves the scene's Whisparr movie from the pre-fetched index (a not-added
    // scene is Skipped, never a false success). Only search/search-upgrades issue a grab — the loop-safety
    // boundary is LOCKED here as in the service.
    private static async Task<BatchUnitOutcome> RunVideoOpAsync(
        BatchOp op, CoveVideo video, IReadOnlyDictionary<string, WhisparrMovie>? movieIndex,
        SceneActions actions, CancellationToken ct)
    {
        switch (op)
        {
            case BatchOp.Add:
                return SucceededIfOne(await actions.AddScenesAsync([ToSceneRef(video)], ct));
            case BatchOp.Exclude:
                return SucceededIfOne(await actions.ExcludeScenesAsync([ToSceneRef(video)], ct));
            default:
                if (ResolveMovieForScene(video.StashIds, movieIndex!) is not { } movie)
                {
                    return BatchUnitOutcome.Skipped; // has a StashDB id but no Whisparr movie (not added) — nothing to search
                }

                return SucceededIfOne(op == BatchOp.SearchUpgrades
                    ? await actions.SearchForUpgradesAsync(movie.Id, ct)
                    : await actions.SearchSceneAsync(movie.Id, ct));
        }
    }

    // A one-scene batch/search leg succeeded iff its single item succeeded; a transport failure (or a 0-succeeded
    // result) is a Failed unit, never a false success.
    private static BatchUnitOutcome SucceededIfOne(WhisparrResult<BulkActionResult> result)
        => result.IsOk && result.Value!.Succeeded >= 1 ? BatchUnitOutcome.Succeeded : BatchUnitOutcome.Failed;

    // Maps a resolved batch scene to the add/exclude SceneRef: its first StashDB id + a Cove-derived non-empty
    // title (Whisparr Eros rejects an empty title) + the scene year from the Cove date (fuzzy match aid).
    private static SceneRef ToSceneRef(CoveVideo video)
        => new(video.StashIds[0], SceneActions.ResolveTitle(video.Title, video.FilePaths, video.StashIds[0]), video.Date?.Year);

    // Re-shapes a non-Ok result of any payload type into the same state for a different return payload type, so
    // a transport/version failure propagates through ToMonitorResult to a 400 (version) / 502 (transport).
    private static WhisparrResult<TTo> PropagateError<TTo, TFrom>(WhisparrResult<TFrom> source)
        => WhisparrResult<TTo>.PropagateFrom(source);

    private static WhisparrResult<VideosBatchResult> PropagateBatch<TFrom>(WhisparrResult<TFrom> source)
        => WhisparrResult<VideosBatchResult>.PropagateFrom(source);

    /// <summary>
    /// Parses the videos-batch <c>Op</c> (case-insensitive) into a <see cref="BatchOp"/>. Returns <c>false</c>
    /// for anything else so the caller rejects an unknown op with a 400 rather than guessing.
    /// </summary>
    private static bool TryParseBatchOp(string? op, out BatchOp batchOp)
    {
        switch (op?.Trim().ToLowerInvariant())
        {
            case "add": batchOp = BatchOp.Add; return true;
            case "search": batchOp = BatchOp.Search; return true;
            case "searchupgrades": batchOp = BatchOp.SearchUpgrades; return true;
            case "exclude": batchOp = BatchOp.Exclude; return true;
            default: batchOp = default; return false;
        }
    }

    // The camelCase wire spelling echoed back in the VideosBatchResult (matches the JS bundle's BatchOp union).
    private static string WireOp(BatchOp op) => op switch
    {
        BatchOp.Add => "add",
        BatchOp.Search => "search",
        BatchOp.SearchUpgrades => "searchUpgrades",
        BatchOp.Exclude => "exclude",
        _ => "unknown",
    };

    /// <summary>
    /// The studios/performers bulk action over a capped selection of Cove ids. Configure-gated, stored creds only.
    /// An op the connected version+kind can't do is refused up front with <c>VERSION_UNSUPPORTED</c> (the same
    /// capability split the per-entity menu enforces). Only the explicit "search" op grabs (loop-safety LOCKED).
    /// </summary>
    internal async Task<IResult> EntitiesBatchAsync(
        EntitiesBatchRequest req, WhisparrClient client, ICurrentPrincipalAccessor principal, CancellationToken ct)
    {
        if (Forbidden(principal, Permissions.ExtensionsConfigure) is { } denied)
        {
            return denied;
        }

        if (!TryParseEntityKind(req.Kind, out var kind))
        {
            return Results.Json(new { code = "UNKNOWN_KIND" }, statusCode: 400);
        }

        if (!TryParseEntityBatchOp(req.Op, out var op))
        {
            return Results.Json(new { code = "UNKNOWN_OP" }, statusCode: 400);
        }

        var coveIds = req.CoveEntityIds ?? [];
        if (coveIds.Length > MaxEntityIdsPerRequest)
        {
            return Results.Json(new { code = "TOO_MANY_IDS", max = MaxEntityIdsPerRequest }, statusCode: 400);
        }

        var (options, _, _) = await ResolveCredsAsync(new TestConnectionRequest(null, null), ct);
        if (AdapterSelector.SelectForVersion(options.SelectedVersion, client) is not { } adapter)
        {
            return Results.Json(new { code = "VERSION_UNSUPPORTED" }, statusCode: 400);
        }

        // Capability gate BEFORE any per-id read/call: the same split the per-entity menu shows, so an op the
        // connected version+kind can't do is a clean 400, never a per-entity fan-out that all fails.
        if (!EntityBatchOpSupported(op, kind, adapter))
        {
            return Results.Json(new { code = "VERSION_UNSUPPORTED", detected = options.SelectedVersion }, statusCode: 400);
        }

        var scope = ParseMonitorScope(req.Scope, options.DefaultMonitorScope);
        var description = DescribeEntitiesBatch(op, kind, coveIds.Length);

        // No host job service (a unit-test host): run inline and return the aggregate directly.
        if (_jobs is null)
        {
            return await WithScopedLibraryAsync(options.StashDbEndpoint, options.TpdbEndpoint, async library =>
            {
                var result = await EntitiesBatchCoreAsync(kind, op, scope, coveIds, client, library, options, ct);
                LogBulkAction(kind, result.Total, result.Succeeded, result.Failed);
                return Results.Json(result, MonitorResponseJsonOptions);
            });
        }

        // The per-entity fan-out outlives this request, so the job re-opens its OWN scope + client (never the
        // request-scoped ones) and reports per-entity progress into the Job Drawer via RunBatchAsync.
        var jobId = _jobs.Enqueue(
            EntitiesBatchJobType, description,
            (progress, jobCt) => RunEntitiesBatchJobAsync(kind, op, scope, coveIds, options, progress, jobCt),
            exclusive: false);
        return Results.Json(new { jobId, description }, MonitorResponseJsonOptions);
    }

    // The three-state outcome of one entity/video's op — mapped to a JobUnitOutcome in the job, and aggregated
    // into the batch counts in the *CoreAsync aggregators the unit tests drive.
    internal enum BatchUnitOutcome { Succeeded, Failed, Skipped }

    private static JobUnitOutcome ToJobUnitOutcome(BatchUnitOutcome outcome) => outcome switch
    {
        BatchUnitOutcome.Succeeded => JobUnitOutcome.Succeeded,
        BatchUnitOutcome.Skipped => JobUnitOutcome.Skipped,
        _ => JobUnitOutcome.Failed,
    };

    // Runs the entities batch as a background job: a fresh scope + client (this outlives the request), then one
    // Job-Drawer unit per entity via RunBatchAsync — each unit dispatches the SAME per-entity op the aggregator
    // does (RunEntityOpAsync), so the drawer's succeeded/failed/skipped summary is the shipped per-entity result.
    private async Task RunEntitiesBatchJobAsync(
        EntityKind kind, EntityBatchOp op, MonitorScope scope, IReadOnlyList<int> coveEntityIds,
        WhisparrOptions options, Cove.Core.Interfaces.IJobProgress progress, CancellationToken ct)
    {
        await using var dbScope = ScopeFactory.CreateAsyncScope();
        var client = dbScope.ServiceProvider.GetRequiredService<WhisparrClient>();
        var library = dbScope.ServiceProvider.GetService<DbContext>() is { } db
            ? new CoveLibraryPort(db, options.StashDbEndpoint, options.TpdbEndpoint)
            : (ICoveLibraryPort)EmptyCoveLibraryPort.Instance;

        var isV2 = string.Equals(options.SelectedVersion, "v2", StringComparison.OrdinalIgnoreCase);
        var monitor = new EntityMonitor(client, options);
        var actions = new SceneActions(client, options, library);

        var batch = await _jobs!.RunBatchAsync(
            coveEntityIds,
            // maxInFlight MUST stay 1 (loop-safety / scale). Each monitor op now issues a TARGETED metadata
            // refresh + a bounded command-completion wait + a bulk editor toggle, so a parallel fan-out over
            // many studios would originate an N-entity refresh burst faster than Whisparr's command queue
            // drains — the StashDB-hammering refresh storm this phase forbids. Sequential pacing keeps it to
            // one entity's refresh (a single-id array, never global) settling before the next entity's flip.
            maxInFlight: 1,
            async (id, unit, unitCt) =>
                unit.Complete(ToJobUnitOutcome(await RunEntityOpAsync(kind, op, scope, id, isV2, monitor, actions, library, unitCt))),
            progress,
            unitIdFactory: (id, _) => $"{kind}:{id}",
            labelFactory: id => $"{kind} #{id}",
            ct: ct);

        LogBulkAction(kind, batch.TotalUnits, batch.SucceededUnits, batch.FailedUnits);
    }

    // The Job-Drawer description: an imperative summary of what the batch does, e.g. "Whisparr: monitor 3 studios".
    private static string DescribeEntitiesBatch(EntityBatchOp op, EntityKind kind, int count)
    {
        var verb = op switch
        {
            EntityBatchOp.Monitor => "monitor",
            EntityBatchOp.Unmonitor => "unmonitor",
            EntityBatchOp.AddMissing => "add missing scenes for",
            EntityBatchOp.Search => "search monitored scenes for",
            EntityBatchOp.ReflectOwned => "reflect owned scenes for",
            _ => "process",
        };
        var noun = kind == EntityKind.Studio
            ? (count == 1 ? "studio" : "studios")
            : (count == 1 ? "performer" : "performers");
        return $"Whisparr: {verb} {count} {noun}";
    }

    /// <summary>
    /// The per-entity dispatch half of <see cref="EntitiesBatchAsync"/>, extracted so it is unit-testable
    /// host-free with a seeded <see cref="ICoveLibraryPort"/> + a fake-HTTP client (no DB scope, no live
    /// Whisparr). Loops the selected entity ids and runs the chosen op via the SAME per-entity helpers the
    /// single-entity menu uses (<see cref="EntityMonitor"/> / <see cref="SceneActions"/>); an entity is
    /// <c>Succeeded</c> when its op returns Ok, else <c>Failed</c>. Monitor/unmonitor/search resolve the entity's
    /// own identity id from its Cove id (a selection carries no remote ids); an entity with no id for the
    /// connected version is <c>Skipped</c> (no outbound call), never a false success.
    /// </summary>
    internal static async Task<EntitiesBatchResult> EntitiesBatchCoreAsync(
        EntityKind kind, EntityBatchOp op, MonitorScope scope, IReadOnlyList<int> coveEntityIds,
        WhisparrClient client, ICoveLibraryPort library, WhisparrOptions options, CancellationToken ct)
    {
        var isV2 = string.Equals(options.SelectedVersion, "v2", StringComparison.OrdinalIgnoreCase);
        var monitor = new EntityMonitor(client, options);
        var actions = new SceneActions(client, options, library);

        int total = coveEntityIds.Count, succeeded = 0, failed = 0, skipped = 0;
        foreach (var id in coveEntityIds)
        {
            switch (await RunEntityOpAsync(kind, op, scope, id, isV2, monitor, actions, library, ct))
            {
                case BatchUnitOutcome.Succeeded: succeeded++; break;
                case BatchUnitOutcome.Skipped: skipped++; break;
                default: failed++; break;
            }
        }

        return new EntitiesBatchResult(WireEntityOp(op), total, succeeded, failed, skipped);
    }

    // One entity's op — the single source both the aggregator (EntitiesBatchCoreAsync, unit-tested) and the
    // background job (RunEntitiesBatchJobAsync) run. Monitor/unmonitor/search resolve the entity's OWN identity id
    // from its Cove id (a selection carries no remote ids); an entity with no id for the connected version is
    // Skipped with no outbound call, never a false success. Add-missing / reflect-owned dispatch by Cove id.
    private static async Task<BatchUnitOutcome> RunEntityOpAsync(
        EntityKind kind, EntityBatchOp op, MonitorScope scope, int id, bool isV2,
        EntityMonitor monitor, SceneActions actions, ICoveLibraryPort library, CancellationToken ct)
    {
        switch (op)
        {
            case EntityBatchOp.AddMissing:
                // The stashId param is reserved-for-parity and unused by the local diff (keyed by Cove id).
                return (await actions.AddAllMissingAsync(kind, id, ct)).IsOk
                    ? BatchUnitOutcome.Succeeded : BatchUnitOutcome.Failed;
            case EntityBatchOp.ReflectOwned:
                return (await actions.ReflectOwnedAsync(kind, id, ct)).IsOk
                    ? BatchUnitOutcome.Succeeded : BatchUnitOutcome.Failed;
            default:
                var identity = await library.LoadEntityIdentityAsync(kind, id, ct);
                var idKey = (isV2 ? identity?.TpdbIds : identity?.StashIds)?
                    .FirstOrDefault(x => !string.IsNullOrEmpty(x));
                if (idKey is null)
                {
                    return BatchUnitOutcome.Skipped;
                }

                var ok = op switch
                {
                    EntityBatchOp.Monitor => (await monitor.SetMonitorAsync(kind, idKey, true, scope, ct)).IsOk,
                    EntityBatchOp.Unmonitor => (await monitor.SetMonitorAsync(kind, idKey, false, scope, ct)).IsOk,
                    _ => (await actions.SearchAllMonitoredAsync(kind, idKey, ct)).IsOk,
                };
                return ok ? BatchUnitOutcome.Succeeded : BatchUnitOutcome.Failed;
        }
    }

    // The per-version+kind capability split, matching the per-entity menu: monitor/unmonitor/search need a
    // monitorable entity (studio on both; performer v3-only); add-all-missing needs the per-scene add (v3-only);
    // reflect-owned needs owned-import (both). The batch endpoint gates on this so an unsupported op is a clean 400.
    private static bool EntityBatchOpSupported(EntityBatchOp op, EntityKind kind, IWhisparrAdapter adapter) => op switch
    {
        EntityBatchOp.Monitor or EntityBatchOp.Unmonitor or EntityBatchOp.Search => adapter.SupportsEntityMonitor(kind),
        EntityBatchOp.AddMissing => adapter.SupportsSceneAdd,
        EntityBatchOp.ReflectOwned => adapter.SupportsOwnedImport,
        _ => false,
    };

    /// <summary>Parses the entities-batch <c>Op</c> (case-insensitive) into an <see cref="EntityBatchOp"/>; <c>false</c> for anything else (caller 400s).</summary>
    private static bool TryParseEntityBatchOp(string? op, out EntityBatchOp entityOp)
    {
        switch (op?.Trim().ToLowerInvariant())
        {
            case "monitor": entityOp = EntityBatchOp.Monitor; return true;
            case "unmonitor": entityOp = EntityBatchOp.Unmonitor; return true;
            case "addmissing": entityOp = EntityBatchOp.AddMissing; return true;
            case "search": entityOp = EntityBatchOp.Search; return true;
            case "reflectowned": entityOp = EntityBatchOp.ReflectOwned; return true;
            default: entityOp = default; return false;
        }
    }

    // The camelCase wire spelling echoed back in the EntitiesBatchResult (matches the JS bundle's union).
    private static string WireEntityOp(EntityBatchOp op) => op switch
    {
        EntityBatchOp.Monitor => "monitor",
        EntityBatchOp.Unmonitor => "unmonitor",
        EntityBatchOp.AddMissing => "addMissing",
        EntityBatchOp.Search => "search",
        EntityBatchOp.ReflectOwned => "reflectOwned",
        _ => "unknown",
    };
}
