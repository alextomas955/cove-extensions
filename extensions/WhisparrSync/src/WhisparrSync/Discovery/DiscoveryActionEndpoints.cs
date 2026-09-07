using System.Globalization;
using Cove.Core.Interfaces;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using WhisparrSync.Adapters;
using WhisparrSync.Client;
using WhisparrSync.Contracts;
using WhisparrSync.Discovery;
using WhisparrSync.Options;
using WhisparrSync.Push;
using static Cove.Extensions.Shared.RunAsSystem;
using static WhisparrSync.Contracts.WireSerializers;

namespace WhisparrSync;

/// <summary>
/// The per-entity discovery slice's MUTATION surface: one op-parameterized <c>/discovery/action</c> endpoint over
/// a non-owned catalogue scene. It funnels the shipped single push add leg — no second mutation spine — so the
/// loop-safety contract (origin-tagged, <c>searchForMovie:false</c> = no immediate grab, 409-idempotent) is held
/// once. The client supplies the stable source id it received from this extension's own <c>/discovery/entity</c>
/// projection; the server re-derives the entity's missing set and rejects a source id not in it, so an action can
/// only ever target a scene Cove does NOT own (the catalogue-minus-owned diff is the loop-safety boundary).
/// </summary>
public sealed partial class WhisparrSync
{
    // The per-entity discovery MUTATION route: POSTs {CoveEntityId, Kind, SourceId, Op}. Configure-gated +
    // stored-creds-only (the body carries no url/key). Op is monitor / unmonitor / search — only search issues an
    // immediate grab; monitor + unmonitor are PUT flips that never search.
    private const string DiscoveryActionRoute = RouteBase + "/discovery/action";

    // The BULK discovery MUTATION route: POSTs {CoveEntityId, Kind, Op, SourceIds?}. Configure-gated +
    // stored-creds-only. A supplied SourceIds is a validated selection subset; omitting it marks the whole
    // re-derived missing set. Enqueues a background IJobService job so the Job Drawer (not a window.alert)
    // carries the progress + summary.
    private const string DiscoveryActionAllRoute = RouteBase + "/discovery/action-all";
    private const string DiscoveryActionAllJobType = "whisparr-discovery-action-all";

    /// <summary>
    /// Registers the discovery action routes. Both reach the stored creds and make an outbound Whisparr call, so
    /// they sit in the same configure-gated tier as the discovery reads. The remote id is never client-supplied —
    /// the client passes only the stable source ids from this extension's own projection, validated server-side.
    /// </summary>
    private void MapDiscoveryActionEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(DiscoveryActionRoute,
            (DiscoveryActionRequest req, WhisparrClient client, StashDbGraphQlClient stashDbClient,
                TpdbClient tpdbClient, CancellationToken ct)
                => DiscoveryActionAsync(req, client, stashDbClient, tpdbClient, ct)).ConfigureGated();

        endpoints.MapPost(DiscoveryActionAllRoute,
            (DiscoveryActionAllRequest req, WhisparrClient client, StashDbGraphQlClient stashDbClient,
                TpdbClient tpdbClient, CancellationToken ct)
                => DiscoveryActionAllAsync(req, client, stashDbClient, tpdbClient, ct)).ConfigureGated();
    }

    /// <summary>
    /// Dispatches a discovery action by its <see cref="DiscoveryActionRequest.Op"/>. Declares the configure
    /// tier (it reaches the stored creds). Handles <c>monitor</c>
    /// (arm acquisition), <c>unmonitor</c> (un-mark a wanted scene), and <c>search</c> (the sole immediate grab);
    /// an unknown op is a clean <c>400 UNKNOWN_OP</c>. Only <c>search</c> ever reaches a grab command — monitor and
    /// unmonitor are PUT flips that issue none (the loop-safety boundary is crossed only by an explicit search).
    /// </summary>
    internal async Task<IResult> DiscoveryActionAsync(
        DiscoveryActionRequest req, WhisparrClient client, StashDbGraphQlClient stashDbClient,
        TpdbClient tpdbClient, CancellationToken ct)
    {
        return req.Op?.Trim().ToLowerInvariant() switch
        {
            "monitor" => await DiscoveryMonitorAsync(req, client, stashDbClient, tpdbClient, ct),
            "unmonitor" => await DiscoveryUnmonitorAsync(req, client, stashDbClient, tpdbClient, ct),
            "search" => await DiscoverySearchAsync(req, client, stashDbClient, tpdbClient, ct),
            _ => Results.Json(new ErrorResponse("UNKNOWN_OP"), statusCode: 400),
        };
    }

    // The client-supplied catalogue page, clamped to a valid 1-based index (an over-range or negative value can
    // never loop or error — the derive returns an empty page); null is the whole-catalogue re-derive. The
    // coordinate bounds the derive without being trusted: a page that does not contain the acted-on source id
    // falls through to the membership gate's NOT_IN_MISSING_SET refusal with no mutation, which is what admits a
    // page coordinate on a generation whose provider ordering carries no guarantee. Same expression as the
    // discovery READ path's clamp (Discovery/DiscoveryEndpoints.cs).
    private static int? ClampedPage(int? requested) => requested is { } page ? Math.Max(1, page) : null;

    /// <summary>
    /// Marks one non-owned catalogue scene WANTED: adds it <c>monitored:true</c> + <c>searchForMovie:false</c>
    /// (no immediate grab), origin-tagged, 409-idempotent — through the shipped <see cref="SceneActions"/> add
    /// leg. Order matters for loop-safety: require a well-formed body, then defer v2 BEFORE any wire call (there
    /// is no per-scene add on v2), then re-derive the entity's missing set and confirm the source id is a member
    /// (else <c>NOT_IN_MISSING_SET</c> / no add — the source id can only ever name a scene Cove does not own).
    /// </summary>
    private async Task<IResult> DiscoveryMonitorAsync(
        DiscoveryActionRequest req, WhisparrClient client, StashDbGraphQlClient stashDbClient,
        TpdbClient tpdbClient, CancellationToken ct)
    {
        var (terminal, scene, actions) = await ResolveValidatedSceneAsync(
            req, client, stashDbClient, tpdbClient, ct);
        if (terminal is not null)
        {
            return terminal;
        }

        // The single push add leg (monitored:true parameter): no owned-scene enumeration is needed here (the
        // source id is already validated), so the no-op library port satisfies SceneActions.
        var sceneRef = new SceneRef(
            req.SourceId!, SceneActions.ResolveTitle(scene!.Title, null, req.SourceId!), YearOf(scene.ReleaseDate));
        var result = await actions!.MarkScenesWantedAsync([sceneRef], ct);

        LogDiscoveryAction(result.IsOk ? result.Value!.Succeeded : 0);
        return ToMonitorResult(result);
    }

    /// <summary>
    /// The shared monitor/unmonitor preamble: require a well-formed body, resolve the stored creds, defer v2 BEFORE
    /// any wire call (per-scene push is the v3-only <see cref="IWhisparrScenePush"/> capability), re-derive the
    /// entity's missing set, and confirm the client-held source id is a member. Returns a terminal
    /// <see cref="IResult"/> — a 400 (<c>INVALID_REQUEST</c> / <c>VERSION_UNSUPPORTED</c> / <c>NOT_IN_MISSING_SET</c>)
    /// or a through-Whisparr outage — to short-circuit on, OR the validated non-owned <see cref="MissingScene"/>
    /// plus a <see cref="SceneActions"/> for the caller's PUT-flip tail. The membership gate is the loop-safety
    /// boundary: a monitor/unmonitor can only ever target a scene Cove does NOT own.
    /// </summary>
    private async Task<(IResult? Terminal, MissingScene? Scene, SceneActions? Actions)> ResolveValidatedSceneAsync(
        DiscoveryActionRequest req, WhisparrClient client, StashDbGraphQlClient stashDbClient,
        TpdbClient tpdbClient, CancellationToken ct)
    {
        if (req.CoveEntityId is not { } coveEntityId
            || !TryParseEntityKind(req.Kind, out var kind)
            || string.IsNullOrWhiteSpace(req.SourceId))
        {
            return (Results.Json(new ErrorResponse("INVALID_REQUEST"), statusCode: 400), null, null);
        }

        var (options, _, _) = await StoredCredsAsync(ct);

        // v2 has no per-scene add (no POST /episode), so a per-scene monitor/unmonitor defers HERE — before the
        // missing-set re-derive and any wire call — exactly as the shipped SceneActions add leg does.
        if (AdapterSelector.SelectForVersion(options.SelectedVersion, client) is not IWhisparrScenePush)
        {
            return (Results.Json(new VersionUnsupportedResponse("VERSION_UNSUPPORTED", options.SelectedVersion), statusCode: 400), null, null);
        }

        // Above the re-derive below, which is itself an outbound read, so the guard cannot sit in the two callers
        // after this returns. It makes no wire call at all, which is the property this placement protects.
        if (RefuseIncompleteConfig(options) is { } refusal)
        {
            return (refusal, null, null);
        }

        // Re-derive the entity's missing set (the SAME catalogue-minus-owned diff the reads render) and confirm
        // the client-held source id is a member — the loop-safety gate: an action can only ever target a scene
        // Cove does NOT own. A through-Whisparr outage surfaces verbatim; any non-served state has an empty set,
        // and an out-of-set id is rejected with no mutation. A supplied page narrows the derive to the one
        // rendered page; the gate's shape over whatever set comes back is identical either way.
        var page = ClampedPage(req.Page);
        var query = req.Query;
        var computation = await ComputeDiscoveryAsync(kind, coveEntityId, client, stashDbClient, tpdbClient, ct, page, query);
        if (computation.Terminal is { } terminal)
        {
            return (terminal, null, null);
        }

        var scene = computation.Missing.FirstOrDefault(
            m => string.Equals(m.SourceId, req.SourceId, StringComparison.OrdinalIgnoreCase));
        if (scene is null)
        {
            return (Results.Json(new ErrorResponse("NOT_IN_MISSING_SET"), statusCode: 400), null, null);
        }

        // No owned-scene enumeration is needed (the source id is already validated), so the no-op library port
        // satisfies SceneActions for the PUT-flip tail.
        return (null, scene, new SceneActions(client, options, EmptyCoveLibraryPort.Instance, CapabilityPort(client)));
    }

    /// <summary>
    /// Un-marks one wanted catalogue scene: flips its Whisparr movie <c>monitored:false</c> through the shipped
    /// <see cref="SceneActions.SetSceneMonitorAsync"/> un-path (read-or-find the movie then PUT — no add, no
    /// command). Order mirrors <see cref="DiscoveryMonitorAsync"/>: well-formed body, defer v2 BEFORE any wire
    /// call (per-scene monitor is the v3-only <see cref="IWhisparrScenePush"/> capability), then re-derive the
    /// missing set and confirm membership (a wanted-but-fileless scene is still non-owned, so it stays in
    /// catalogue−owned). A scene not present in Whisparr resolves to <c>MovieId 0</c> — a handled safe no-op
    /// (never an add), reported <c>unmonitored:false</c>. Loop-safe: this never grabs.
    /// </summary>
    private async Task<IResult> DiscoveryUnmonitorAsync(
        DiscoveryActionRequest req, WhisparrClient client, StashDbGraphQlClient stashDbClient,
        TpdbClient tpdbClient, CancellationToken ct)
    {
        var (terminal, scene, actions) = await ResolveValidatedSceneAsync(
            req, client, stashDbClient, tpdbClient, ct);
        if (terminal is not null)
        {
            return terminal;
        }

        var result = await actions!.SetSceneMonitorAsync(
            req.SourceId!, SceneActions.ResolveTitle(scene!.Title, null, req.SourceId!), monitored: false, ct);
        if (!result.IsOk)
        {
            LogDiscoveryUnmonitorAction(false);
            return ToMonitorResult(result);
        }

        // MovieId 0 is the absent-scene no-op (nothing to unmonitor, never an add); any real movie was flipped.
        var unmonitored = result.Value!.MovieId != 0;
        LogDiscoveryUnmonitorAction(unmonitored);
        return Results.Json(new SceneUnmonitorResponse(unmonitored), EnumStringResponseJsonOptions);
    }

    /// <summary>
    /// The sole immediate grab in the discovery slice: searches now for one missing scene. Order: well-formed
    /// body, re-derive via <see cref="ComputeDiscoveryAsync"/> (which gates the version — both v3 and v2 offer
    /// discovery — and 502s a through-Whisparr outage), then run the extracted <see cref="DiscoverySearchCoreAsync"/>
    /// — it confirms the source id is in the re-derived missing set (loop-safety: only a non-owned catalogue scene
    /// can be targeted), resolves the scene's ADDED Whisparr movie id SERVER-SIDE from the raw movie index (v3 the
    /// movie id, v2 the episode id), and issues the one grab (<c>MoviesSearch</c> on v3, <c>EpisodeSearch</c> on
    /// v2). A scene that resolves to no added movie (a not-added / synthesized row) is a handled
    /// <c>searched:false</c> with NO command. This is the ONLY discovery op that can reach a grab.
    /// </summary>
    private async Task<IResult> DiscoverySearchAsync(
        DiscoveryActionRequest req, WhisparrClient client, StashDbGraphQlClient stashDbClient,
        TpdbClient tpdbClient, CancellationToken ct)
    {
        if (req.CoveEntityId is not { } coveEntityId
            || !TryParseEntityKind(req.Kind, out var kind)
            || string.IsNullOrWhiteSpace(req.SourceId))
        {
            return Results.Json(new ErrorResponse("INVALID_REQUEST"), statusCode: 400);
        }

        var (options, _, _) = await StoredCredsAsync(ct);

        // A supplied page narrows the derive to the one rendered page; the in-set check inside the search unit
        // treats whatever set comes back the same way, and an id absent from it issues no command. The page is
        // half the coordinate: page three of one ordering and page three of another are disjoint sets, which is
        // why the query travels beside it at every derive site.
        var page = ClampedPage(req.Page);
        var query = req.Query;
        var computation = await ComputeDiscoveryAsync(kind, coveEntityId, client, stashDbClient, tpdbClient, ct, page, query);
        if (computation.Terminal is { } terminal)
        {
            return terminal;
        }

        var actions = new SceneActions(client, options, EmptyCoveLibraryPort.Instance, CapabilityPort(client));
        var result = await DiscoverySearchCoreAsync(
            computation.Missing, computation.MovieIndex, req.SourceId!, actions, ct);
        LogDiscoverySearchAction(result.IsOk && result.Value);
        return result.IsOk
            ? Results.Json(new SceneSearchResponse(result.Value), EnumStringResponseJsonOptions)
            : ToMonitorResult(result);
    }

    /// <summary>
    /// The single-scene discovery search unit, extracted host-free so a hand-built missing set + a fake-HTTP
    /// <see cref="SceneActions"/> drive it with no host. Only a scene in the re-derived <paramref name="missing"/>
    /// set whose <paramref name="sourceId"/> resolves to an ADDED Whisparr movie (<c>Id != 0</c>) in the raw
    /// <paramref name="movieIndex"/> issues a grab via the shipped <see cref="SceneActions.SearchSceneAsync"/>
    /// (<c>MoviesSearch</c> v3 / <c>EpisodeSearch</c> v2); an out-of-set id or a not-added / synthesized row
    /// (<c>Id 0</c> or absent) is a handled <c>Ok(false)</c> with NO command. <c>Ok(true)</c> means a grab was
    /// issued; a transport failure propagates verbatim (mapped to a 502 by <see cref="ToMonitorResult{T}"/>).
    /// </summary>
    internal static async Task<WhisparrResult<bool>> DiscoverySearchCoreAsync(
        IReadOnlyList<MissingScene> missing, IReadOnlyDictionary<string, WhisparrMovie> movieIndex,
        string sourceId, SceneActions actions, CancellationToken ct)
    {
        var inSet = missing.Any(m => string.Equals(m.SourceId, sourceId, StringComparison.OrdinalIgnoreCase));
        if (!inSet || ResolveAddedMovieId(movieIndex, sourceId) is not { } movieId)
        {
            // Out-of-set, not added, or a synthesized direct row: nothing to grab, so no command is issued.
            return WhisparrResult<bool>.Ok(false);
        }

        var result = await actions.SearchSceneAsync(movieId, ct);
        return result.IsOk
            ? WhisparrResult<bool>.Ok(result.Value!.Succeeded >= 1)
            : WhisparrResult<bool>.PropagateFrom(result);
    }

    // A missing scene's ADDED Whisparr movie id from the raw catalogue index (v3 the movie id, v2 the episode
    // id), or null when the source id resolves to no added movie — a not-added scene (no index entry) or a
    // synthesized direct-catalogue row (Id 0). A search over a null resolution issues no command (loop-safe: an
    // unresolvable id never grabs).
    private static int? ResolveAddedMovieId(IReadOnlyDictionary<string, WhisparrMovie> movieIndex, string sourceId)
        => movieIndex.TryGetValue(sourceId, out var movie) && movie.Id != 0 ? movie.Id : null;

    /// <summary>
    /// Marks a BULK set of non-owned catalogue scenes WANTED — a client-held <see cref="DiscoveryActionAllRequest.SourceIds"/>
    /// selection, or the whole entity's re-derived missing set when omitted (mark-all). Order matters for
    /// loop-safety: parse the op, require a well-formed body, cap a supplied selection BEFORE any
    /// per-item work, then defer v2 BEFORE enqueue (no per-scene add on v2). Runs as a background
    /// <see cref="IJobService"/> job so progress + the summary ride the Job Drawer (never a native alert); with no
    /// host job service (a unit-test host) it re-derives + runs the bulk core inline and returns the aggregate.
    /// Every add ends <c>monitored:true</c> + <c>searchForMovie:false</c> (no immediate grab), origin-tagged,
    /// 409-idempotent, through the shipped <see cref="SceneActions.MarkScenesWantedAsync"/> spine — no second
    /// mutation path.
    /// </summary>
    internal async Task<IResult> DiscoveryActionAllAsync(
        DiscoveryActionAllRequest req, WhisparrClient client, StashDbGraphQlClient stashDbClient,
        TpdbClient tpdbClient, CancellationToken ct)
    {
        if (!TryParseDiscoverySceneOp(req.Op, out var op))
        {
            return Results.Json(new ErrorResponse("UNKNOWN_OP"), statusCode: 400);
        }

        if (req.CoveEntityId is not { } coveEntityId || !TryParseEntityKind(req.Kind, out var kind))
        {
            return Results.Json(new ErrorResponse("INVALID_REQUEST"), statusCode: 400);
        }

        // A whole-entity mutation (null SourceIds) is refused for a tag. A tag's catalogue spans the entire library,
        // not one studio's or performer's output: tens of thousands of scenes, neither intendable nor undoable in
        // one click. The UI omits the control; this is the guarantee behind it, because a hidden button is not an
        // authorization check. A BOUNDED selection stays allowed on every kind.
        if (kind == EntityKind.Tag && req.SourceIds is null)
        {
            return Results.Json(new ErrorResponse("WHOLE_TAG_NOT_ALLOWED"), statusCode: 400);
        }

        // Cap a supplied selection BEFORE any missing-set re-derive or per-item work (fan-out containment). A null
        // SourceIds is not a selection at all — it means "the whole re-derived missing set", which is bounded by
        // the entity's own catalogue, so it is not capped here.
        var sourceIds = req.SourceIds;
        if (sourceIds is { Length: > MaxEntityIdsPerRequest })
        {
            return Results.Json(new TooManyIdsResponse("TOO_MANY_IDS", MaxEntityIdsPerRequest), statusCode: 400);
        }

        var (options, _, _) = await StoredCredsAsync(ct);

        // Monitor + Unmonitor are the v3-only per-scene push (v2 has no POST /episode), so they defer HERE —
        // before the missing-set re-derive and any enqueue — exactly as the single per-scene ops do. Search is
        // version-uniform (both versions offer discovery), so it is NOT gated here; ComputeDiscoveryAsync owns
        // the search version check.
        if (op is DiscoverySceneOp.Monitor or DiscoverySceneOp.Unmonitor
            && AdapterSelector.SelectForVersion(options.SelectedVersion, client) is not IWhisparrScenePush)
        {
            return Results.Json(new VersionUnsupportedResponse("VERSION_UNSUPPORTED", options.SelectedVersion), statusCode: 400);
        }

        // Above both the inline branch and the enqueue, so an incomplete configuration never becomes a job.
        if (RefuseIncompleteConfig(options) is { } refusal)
        {
            return refusal;
        }

        var description = DescribeDiscoveryActionAll(op, sourceIds?.Length);

        // The page a supplied selection's rows were rendered from, clamped once for both the inline and the job
        // path. A whole-entity mark-all carries none: an absent coordinate IS the whole-catalogue re-derive, which
        // is the whole point of marking everything, and no default belongs here. The intersect against the derived
        // set is unchanged, and a selected id the page does not hold is dropped with no mutation.
        var page = ClampedPage(req.Page);

        // The other half of that coordinate. A page index names a set only together with the ordering and the
        // filters the page was rendered under, and both halves reach every derive site or neither does.
        var query = req.Query;

        // No host job service (a unit-test host): re-derive the missing set + run the bulk core inline on the
        // caller principal (a foreground request), returning the aggregate directly.
        if (_jobs is null)
        {
            var computation = await ComputeDiscoveryAsync(kind, coveEntityId, client, stashDbClient, tpdbClient, ct, page, query);
            if (computation.Terminal is { } terminal)
            {
                return terminal;
            }

            var actions = new SceneActions(client, options, EmptyCoveLibraryPort.Instance, CapabilityPort(client));
            // Monitor keeps the shipped MarkScenesWantedAsync spine; unmonitor/search run the shared per-scene unit.
            var result = op == DiscoverySceneOp.Monitor
                ? await DiscoveryActionAllCoreAsync(computation.Missing, sourceIds, actions, ct)
                : await DiscoveryActionAllUnitCoreAsync(op, computation.Missing, sourceIds, computation.MovieIndex, actions, ct);
            LogDiscoveryActionAll(op, result.IsOk ? result.Value!.Succeeded : 0);
            return ToMonitorResult(result);
        }

        // The per-scene fan-out outlives this request, so the job re-opens its OWN scope + clients and reports
        // per-scene progress into the Job Drawer via RunBatchAsync. The clamped page and the query travel as
        // explicit arguments: the request object does not outlive the enqueue, and the job must never reach back
        // for it.
        var jobId = _jobs.Enqueue(
            DiscoveryActionAllJobType, description,
            (progress, jobCt) => RunDiscoveryActionAllJobAsync(
                op, kind, coveEntityId, sourceIds, options, page, query, progress, jobCt),
            exclusive: false);
        return Results.Json(new JobAcceptedResponse(jobId, description), EnumStringResponseJsonOptions);
    }

    // The three discovery scene ops the single + bulk endpoints share. Monitor arms acquisition (monitored:true,
    // no grab); Unmonitor un-marks it (monitored:false, no grab); Search is the sole immediate grab.
    internal enum DiscoverySceneOp { Monitor, Unmonitor, Search }

    // Parses the bulk Op (case-insensitive) into a DiscoverySceneOp; false for anything else (caller 400s).
    private static bool TryParseDiscoverySceneOp(string? op, out DiscoverySceneOp sceneOp)
    {
        switch (op?.Trim().ToLowerInvariant())
        {
            case "monitor": sceneOp = DiscoverySceneOp.Monitor; return true;
            case "unmonitor": sceneOp = DiscoverySceneOp.Unmonitor; return true;
            case "search": sceneOp = DiscoverySceneOp.Search; return true;
            default: sceneOp = default; return false;
        }
    }

    /// <summary>
    /// The bulk mark-wanted core over an already-computed missing set, extracted so it is unit-testable host-free
    /// with a hand-built missing set + a fake-HTTP <see cref="SceneActions"/> (mirroring the videos-batch core). A
    /// supplied <paramref name="sourceIds"/> selection is intersected with the missing set (out-of-set ids
    /// skipped); omitting it targets the whole set. Every targeted scene is marked wanted through the shipped
    /// <see cref="SceneActions.MarkScenesWantedAsync"/> spine (monitored:true, searchForMovie:false, origin-tagged,
    /// 409-idempotent) — the bulk run issues NO grab command.
    /// </summary>
    internal static Task<WhisparrResult<BulkActionResult>> DiscoveryActionAllCoreAsync(
        IReadOnlyList<MissingScene> missing, string[]? sourceIds, SceneActions actions, CancellationToken ct)
        => actions.MarkScenesWantedAsync(TargetedSceneRefs(missing, sourceIds), ct);

    // The targeted scene refs for a bulk mark-wanted: a supplied SourceIds selection intersected with the missing
    // set (out-of-set ids dropped, missing-set order preserved — an action can only ever target a scene Cove does
    // NOT own), or the whole missing set when omitted. Each ref carries a Cove-derived non-empty title (Whisparr
    // Eros rejects an empty title) + the release year (fuzzy-match aid). Case-insensitive on the source id.
    internal static IReadOnlyList<SceneRef> TargetedSceneRefs(
        IReadOnlyList<MissingScene> missing, string[]? sourceIds)
    {
        IEnumerable<MissingScene> targeted = missing;
        if (sourceIds is not null)
        {
            var selection = new HashSet<string>(sourceIds, StringComparer.OrdinalIgnoreCase);
            targeted = missing.Where(m => selection.Contains(m.SourceId));
        }

        return
        [
            .. targeted.Select(m => new SceneRef(
                m.SourceId, SceneActions.ResolveTitle(m.Title, null, m.SourceId), YearOf(m.ReleaseDate))),
        ];
    }

    // The bulk unmonitor/search core over an already-computed missing set, extracted host-free (mirroring the
    // monitor DiscoveryActionAllCoreAsync). A supplied sourceIds selection is intersected with the missing set
    // (out-of-set ids skipped); omitting it targets the whole set. Each targeted scene runs the SAME per-scene
    // unit the job uses. A not-added search unit is Skipped (no command), never a failure; only search issues a grab.
    internal static async Task<WhisparrResult<BulkActionResult>> DiscoveryActionAllUnitCoreAsync(
        DiscoverySceneOp op, IReadOnlyList<MissingScene> missing, string[]? sourceIds,
        IReadOnlyDictionary<string, WhisparrMovie> movieIndex, SceneActions actions, CancellationToken ct)
    {
        var refs = TargetedSceneRefs(missing, sourceIds);
        int succeeded = 0, failed = 0;
        foreach (var sceneRef in refs)
        {
            switch (await RunDiscoverySceneUnitAsync(op, sceneRef, movieIndex, actions, ct))
            {
                case BatchUnitOutcome.Succeeded: succeeded++; break;
                case BatchUnitOutcome.Failed: failed++; break;
                default: break; // Skipped (a not-added search) — no work done, not counted as a failure
            }
        }

        return WhisparrResult<BulkActionResult>.Ok(new BulkActionResult(refs.Count, succeeded, failed));
    }

    // One scene's discovery op — the single source both the inline unit core and the background job run. Monitor
    // arms acquisition through the shipped MarkScenesWantedAsync spine (monitored:true, no grab); Unmonitor flips
    // monitored:false through SetSceneMonitorAsync's un-path (a bare PUT, no add, no grab); Search resolves the
    // scene's ADDED Whisparr movie id from the raw index (a not-added scene is Skipped — no command) and issues
    // the one grab. Only Search can reach a command — the immediate-grab boundary is LOCKED here.
    private static async Task<BatchUnitOutcome> RunDiscoverySceneUnitAsync(
        DiscoverySceneOp op, SceneRef sceneRef, IReadOnlyDictionary<string, WhisparrMovie> movieIndex,
        SceneActions actions, CancellationToken ct)
    {
        switch (op)
        {
            case DiscoverySceneOp.Unmonitor:
                return (await actions.SetSceneMonitorAsync(sceneRef.StashId, sceneRef.Title, monitored: false, ct)).IsOk
                    ? BatchUnitOutcome.Succeeded
                    : BatchUnitOutcome.Failed;
            case DiscoverySceneOp.Search:
                if (ResolveAddedMovieId(movieIndex, sceneRef.StashId) is not { } movieId)
                {
                    return BatchUnitOutcome.Skipped; // not an added Whisparr movie — nothing to grab, no command
                }

                return await actions.SearchSceneAsync(movieId, ct) is { IsOk: true, Value.Succeeded: >= 1 }
                    ? BatchUnitOutcome.Succeeded
                    : BatchUnitOutcome.Failed;
            default: // Monitor
                // A scene Whisparr's import exclusions cover is Skipped, mirroring the not-added search unit:
                // the refusal is the user's own configuration, never a failure to report.
                return await actions.MarkScenesWantedAsync([sceneRef], ct) switch
                {
                    { IsOk: true, Value.Succeeded: >= 1 } => BatchUnitOutcome.Succeeded,
                    { IsOk: true, Value.AllSkipped: true } => BatchUnitOutcome.Skipped,
                    _ => BatchUnitOutcome.Failed,
                };
        }
    }

    // The Job-Drawer description: an imperative summary per op. A selection names its count; a whole-entity run
    // (SourceIds omitted) reads "all missing scenes".
    private static string DescribeDiscoveryActionAll(DiscoverySceneOp op, int? selectionCount)
    {
        var scope = selectionCount is { } n ? $"{n} missing {(n == 1 ? "scene" : "scenes")}" : "all missing scenes";
        return op switch
        {
            DiscoverySceneOp.Unmonitor => $"Whisparr: unmonitor {scope}",
            DiscoverySceneOp.Search => $"Whisparr: search {scope}",
            _ => selectionCount is { } m
                ? $"Whisparr: mark {m} missing {(m == 1 ? "scene" : "scenes")} wanted"
                : "Whisparr: mark all missing scenes wanted",
        };
    }

    // Runs the bulk discovery op as a background job: a fresh scope + clients (this outlives the request), then the
    // whole re-derive + fan-out under CovePrincipal.System() via the RunAsSystem seam. The seam is required because
    // a background scope carries no request principal (Anonymous), under which CoveContext's authz filters skip
    // every owned scene — so the re-derived missing set (catalogue MINUS owned) would wrongly include owned scenes.
    // The System elevation is only for the owned-scenes read inside the diff, never a caller-gate bypass (the
    // endpoint already gated the caller). One Job-Drawer unit per targeted scene via RunBatchAsync, each running
    // the SAME per-scene unit the single/inline paths use — only a search unit can reach a grab.
    private async Task RunDiscoveryActionAllJobAsync(
        DiscoverySceneOp op, EntityKind kind, int coveEntityId, string[]? sourceIds, WhisparrOptions options,
        int? page, DiscoveryQueryRequest? query, IJobProgress progress, CancellationToken ct)
    {
        await using var dbScope = ScopeFactory.CreateAsyncScope();
        var client = dbScope.ServiceProvider.GetRequiredService<WhisparrClient>();
        var stashDbClient = dbScope.ServiceProvider.GetRequiredService<StashDbGraphQlClient>();
        var tpdbClient = dbScope.ServiceProvider.GetRequiredService<TpdbClient>();

        // Monitor + Unmonitor are the v3-only per-scene push; search is version-uniform, so it is not gated here
        // (a v2 search resolves each episode id from the re-derived catalogue and issues the EpisodeSearch grab).
        if (op is DiscoverySceneOp.Monitor or DiscoverySceneOp.Unmonitor
            && AdapterSelector.SelectForVersion(options.SelectedVersion, client) is not IWhisparrScenePush)
        {
            progress.Report(1d, "Whisparr v3 is required for this action.");
            return;
        }

        await RunAsSystemAsync(dbScope.ServiceProvider, async () =>
        {
            var computation = await ComputeDiscoveryAsync(kind, coveEntityId, client, stashDbClient, tpdbClient, ct, page, query);
            if (computation.Terminal is not null)
            {
                // A version/outage terminal state: nothing to act on. The drawer shows a completed no-op summary.
                progress.Report(1d, "No missing scenes to act on.");
                return;
            }

            var refs = TargetedSceneRefs(computation.Missing, sourceIds);
            var movieIndex = computation.MovieIndex;
            var actions = new SceneActions(client, options, EmptyCoveLibraryPort.Instance, CapabilityPort(client));
            var batch = await _jobs!.RunBatchAsync(
                refs,
                maxInFlight: 1, // sequential — one origin-tag get-or-create / one grab at a time, gentle on Whisparr
                async (sceneRef, unit, unitCt) =>
                    unit.Complete(ToJobUnitOutcome(
                        await RunDiscoverySceneUnitAsync(op, sceneRef, movieIndex, actions, unitCt))),
                progress,
                unitIdFactory: (sceneRef, _) => $"scene:{sceneRef.StashId}",
                labelFactory: sceneRef => sceneRef.Title ?? sceneRef.StashId,
                ct: ct);

            LogDiscoveryActionAll(op, batch.SucceededUnits);
        });
    }

    // The 4-digit leading year of a release-date string (the fuzzy-match aid the add body carries), or null when
    // the row reports no parseable date.
    private static int? YearOf(string? releaseDate)
    {
        if (releaseDate is not null && releaseDate.Length >= 4
            && int.TryParse(releaseDate.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var year))
        {
            return year;
        }

        return null;
    }
}
