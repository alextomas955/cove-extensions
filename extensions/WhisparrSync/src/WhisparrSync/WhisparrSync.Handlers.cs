using System.Globalization;
using Cove.Core.Auth;
using Microsoft.AspNetCore.Http;
using WhisparrSync.Adapters;
using WhisparrSync.Client;
using WhisparrSync.Library;
using WhisparrSync.Monitor;
using WhisparrSync.Options;
using WhisparrSync.Push;
using WhisparrSync.Safety;
using WhisparrSync.Scene;
using static Cove.Extensions.Shared.MinimalApiPermissions;

namespace WhisparrSync;

/// <summary>
/// The outward per-scene and per-entity endpoint handlers: root-overlap advisory, monitor +
/// monitor-status, the scene/entity status projections, and the single-item add / search / monitor /
/// exclude / reflect-owned surface. Splits from <c>WhisparrSync.Api.cs</c> (wiring) by concern.
/// </summary>
public sealed partial class WhisparrSync
{
    /// <summary>
    /// Reports whether any Cove library root overlaps a Whisparr root — a re-grab-loop risk. Reads
    /// the Whisparr roots (via the stored creds, cached) and the Cove roots (from <c>CoveConfiguration</c> if
    /// the host injects it into the extension scope, otherwise derived from the
    /// library's own file folders), then composes them with <see cref="RootOverlapDetector"/>. The result is
    /// a best-effort ADVISORY only: cross-mount / cross-container deployments legitimately see the same
    /// library at different paths, so the warning says so and is never a hard gate. 403-first on
    /// <c>extensions.read</c> — a pure read that mutates nothing.
    /// </summary>
    internal async Task<IResult> RootOverlapAsync(
        WhisparrClient client, ICurrentPrincipalAccessor principal, CancellationToken ct)
    {
        if (Forbidden(principal, Permissions.ExtensionsRead) is { } denied)
        {
            return denied;
        }

        var whisparrRoots = await GetWhisparrRootsAsync(client, ct);
        var coveRoots = await GetCoveRootsAsync(ct);
        var overlaps = RootOverlapDetector.Detect(whisparrRoots, coveRoots);

        var warning = overlaps.Count == 0
            ? null
            : "One or more Cove library roots overlap a Whisparr root. Because Cove imports files in place " +
              "(never moving or deleting them), a shared root can let an import echo back to Whisparr as a new " +
              "grab. This is a best-effort advisory: cross-mount or containerized deployments may legitimately " +
              "see the same library at different paths, so verify against your own layout.";

        return Results.Json(new { overlaps, warning }, ImportLogResponseJsonOptions);
    }

    /// <summary>
    /// Toggles the monitor state of a studio or performer in Whisparr via the
    /// add-then-flip semantics (<see cref="EntityMonitor.SetMonitorAsync"/>). Configure-gated: this
    /// mutation reaches the stored credentials, so a read-only principal must not reach it. Stored creds ONLY:
    /// the request body carries NO url/key — <see cref="ResolveCredsAsync"/> with an empty
    /// request resolves the stored host+key, so the stored key is never paired with a caller-supplied host and
    /// is never echoed. The Whisparr lookup id is resolved server-side from the entity's forwarded Cove
    /// <c>remoteIds</c> by the CONNECTED version's endpoint (StashDB on v3, ThePornDB on v2 —
    /// <see cref="WhisparrOptions.IdentityEndpoint"/>), so a caller cannot point the toggle at an arbitrary id.
    /// A v2 studio routes to the real SITE add-then-flip; a v2 performer defers (VersionMismatch → a clear 400,
    /// never a 500). Never triggers a Whisparr search (loop-safety is enforced in the adapter).
    /// </summary>
    internal async Task<IResult> MonitorAsync(
        MonitorRequest req, WhisparrClient client, ICurrentPrincipalAccessor principal, CancellationToken ct)
    {
        if (Forbidden(principal, Permissions.ExtensionsConfigure) is { } denied)
        {
            return denied;
        }

        if (!TryParseEntityKind(req.Kind, out var kind))
        {
            return Results.Json(new { code = "UNKNOWN_KIND" }, statusCode: 400);
        }

        var (options, _, _) = await ResolveCredsAsync(new TestConnectionRequest(null, null), ct);
        if (AdapterSelector.SelectForVersion(options.SelectedVersion, client) is not { } adapter)
        {
            return Results.Json(new { code = "VERSION_UNSUPPORTED" }, statusCode: 400);
        }

        // Capability precedes identity (matches MonitorStatusAsync): a performer on v2 is unsupported id or not.
        if (!adapter.SupportsEntityMonitor(kind))
        {
            return Results.Json(new { code = "VERSION_UNSUPPORTED", detected = options.SelectedVersion }, statusCode: 400);
        }

        if (ResolveRemoteId(req.RemoteIds, options) is not { } stashId)
        {
            // The entity carries no identity on the connected version's endpoint — it cannot be monitored, and
            // we must NOT call Whisparr with a wrong id. A clear handled outcome, not an error. A v2 studio that
            // DOES resolve routes to the real SITE add-then-flip (the adapter's VersionMismatch, if any, maps to
            // a clear 400 via ToMonitorResult — never a 500).
            return Results.Json(new { code = "NO_STASHDB_IDENTITY", provider = ProviderNameFor(options) }, MonitorResponseJsonOptions);
        }

        // Scope follows the request when supplied, else the stored default. Unparseable → the default (never a
        // throw). Ignored when turning monitor off (the adapter unmonitors the scenes regardless of scope).
        var scope = ParseMonitorScope(req.Scope, options.DefaultMonitorScope);
        var result = await new EntityMonitor(client, options).SetMonitorAsync(kind, stashId, req.Monitored, scope, ct);
        if (result.IsOk)
        {
            LogMonitorToggled(kind, req.Monitored, result.Value!.Added);
        }

        return ToMonitorResult(result);
    }

    /// <summary>
    /// Projects the quiet-status ("added / monitored / X of Y grabbed") for a studio/performer via
    /// <see cref="EntityMonitor.GetStatusAsync"/>. Same security posture as <see cref="MonitorAsync"/>:
    /// configure-gated, stored creds only (it reaches the stored credentials to read the Whisparr movie set),
    /// server-side stashId resolution, and a graceful v2 deferral. Reads only — no Cove or Whisparr mutation.
    /// </summary>
    internal async Task<IResult> MonitorStatusAsync(
        MonitorStatusRequest req, WhisparrClient client, ICurrentPrincipalAccessor principal, CancellationToken ct)
    {
        if (Forbidden(principal, Permissions.ExtensionsConfigure) is { } denied)
        {
            return denied;
        }

        if (!TryParseEntityKind(req.Kind, out var kind))
        {
            return Results.Json(new { code = "UNKNOWN_KIND" }, statusCode: 400);
        }

        var (options, _, _) = await ResolveCredsAsync(new TestConnectionRequest(null, null), ct);
        if (AdapterSelector.SelectForVersion(options.SelectedVersion, client) is not { } adapter)
        {
            return Results.Json(new { code = "VERSION_UNSUPPORTED" }, statusCode: 400);
        }

        // Capability precedes identity. A performer on v2 has no entity in the version at all; a missing id is
        // beside the point. Without this guard an id-less performer returns NO_STASHDB_IDENTITY (a disabled
        // "add a metadata link" control) when it should return VERSION_UNSUPPORTED (the state the UI hides on).
        if (!adapter.SupportsEntityMonitor(kind))
        {
            return Results.Json(new { code = "VERSION_UNSUPPORTED", detected = options.SelectedVersion }, statusCode: 400);
        }

        if (ResolveRemoteId(req.RemoteIds, options) is not { } stashId)
        {
            return Results.Json(new { code = "NO_STASHDB_IDENTITY", provider = ProviderNameFor(options) }, MonitorResponseJsonOptions);
        }

        var result = await new EntityMonitor(client, options).GetStatusAsync(kind, stashId, ct);
        if (result.State is not WhisparrResultState.Ok)
        {
            return ToMonitorResult(result);
        }

        // The bulk menu gates on these: "Add all missing" needs SupportsSceneAdd (v3), "Reflect owned" needs
        // SupportsOwnedImport (both versions). Without them the menu would offer an action the connected version
        // answers with 400.
        var status = result.Value!;
        return Results.Json(
            new
            {
                status.Added,
                status.Monitored,
                status.ScenesPresent,
                status.ScenesTotal,
                status.HasCounts,
                addSupported = adapter.SupportsSceneAdd,
                ownedImportSupported = adapter.SupportsOwnedImport,
            },
            MonitorResponseJsonOptions);
    }

    /// <summary>
    /// Library-wide Whisparr-status counts for the videos toolbar. Loads ALL Cove videos
    /// once, fetches the Whisparr movie set + exclusion set once, and returns the by-state partition from
    /// <see cref="SceneStatusProjector.SummaryCounts"/> — the honest library-level affordance the toolbar slot
    /// paints (the host exposes no per-video-card decorator — the FALLBACK). Configure-gated
    /// + stored-creds-only: the request has no body, and <see cref="ResolveCredsAsync"/> with an empty
    /// request resolves the stored host+key so the key is never paired with a caller value and never echoed.
    /// Degrades quietly per dimension: a non-Ok movie/exclusion read yields empty inputs for that dimension
    /// (a v2 instance defers exclusions → <c>excluded=0</c>) rather than failing the whole summary.
    /// </summary>
    internal async Task<IResult> SceneStatusSummaryAsync(
        WhisparrClient client, ICurrentPrincipalAccessor principal, CancellationToken ct)
    {
        if (Forbidden(principal, Permissions.ExtensionsConfigure) is { } denied)
        {
            return denied;
        }

        var (options, baseUrl, apiKey) = await ResolveCredsAsync(new TestConnectionRequest(null, null), ct);
        if (AdapterSelector.SelectForVersion(options.SelectedVersion, client) is not { } adapter)
        {
            return Results.Json(new { code = "VERSION_UNSUPPORTED" }, statusCode: 400);
        }

        var videos = await LoadAllVideosSafeAsync(options.StashDbEndpoint, options.TpdbEndpoint, ct);
        var movies = await adapter.ListMoviesAsync(baseUrl, apiKey, ct);
        var exclusions = await adapter.ListExclusionsAsync(baseUrl, apiKey, ct);

        var index = SceneStatusProjector.BuildMovieIndex(movies.IsOk ? movies.Value! : []);
        var excluded = SceneStatusProjector.BuildExcludedSet(exclusions.IsOk ? exclusions.Value! : []);
        var counts = SceneStatusProjector.SummaryCounts(videos, index, excluded);

        LogSceneStatusRead(counts.Total);
        return Results.Json(new { counts }, MonitorResponseJsonOptions);
    }

    /// <summary>
    /// Classifies Cove video ids into their Whisparr status for the per-card badges — one page costs one DB read +
    /// one movie/exclusion fetch, not one call per card. Configure-gated, stored creds only, v3-only (a v2 instance
    /// has no per-scene identity → <c>VERSION_UNSUPPORTED</c>). An unresolvable id is absent from the map. Reads only.
    /// </summary>
    internal async Task<IResult> SceneStatusBatchAsync(
        SceneStatusBatchRequest req, WhisparrClient client, ICurrentPrincipalAccessor principal, CancellationToken ct)
    {
        if (Forbidden(principal, Permissions.ExtensionsConfigure) is { } denied)
        {
            return denied;
        }

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

        if (coveIds.Length == 0)
        {
            return Results.Json(new { states = new Dictionary<int, SceneCardStatus>() }, SceneStatusBatchJsonOptions);
        }

        return await WithScopedLibraryAsync(options.StashDbEndpoint, options.TpdbEndpoint, async library =>
        {
            // Cached list reads: paging a large library re-uses one fetch per TTL window, not one per page.
            var movies = await CachedMoviesAsync(adapter, baseUrl, apiKey, ct);
            var exclusions = await CachedExclusionsAsync(adapter, baseUrl, apiKey, ct);
            var states = await SceneStatusBatchCoreAsync(
                coveIds, movies.IsOk ? movies.Value! : [], exclusions.IsOk ? exclusions.Value! : [], library, ct);
            LogSceneStatusRead(states.Count);
            return Results.Json(new { states }, SceneStatusBatchJsonOptions);
        });
    }

    /// <summary>
    /// The classify half of <see cref="SceneStatusBatchAsync"/> (pure over the pre-fetched movie + exclusion sets,
    /// so it is unit-testable host-free and re-uses the cached lists). Projects each scene with the SAME
    /// <see cref="SceneStatusProjector.CardStatus"/> the summary + scene panel key on, carrying the primary state
    /// plus the secondary <c>hasFile</c> fact, so the card badge, toolbar count, and scene tab never disagree.
    /// Empty inputs (a failed upstream read) classify every scene as <c>notAdded</c> with <c>hasFile:false</c>.
    /// </summary>
    internal static async Task<IReadOnlyDictionary<int, SceneCardStatus>> SceneStatusBatchCoreAsync(
        IReadOnlyList<int> coveIds, WhisparrMovie[] movies, WhisparrExclusion[] exclusions,
        ICoveLibraryPort library, CancellationToken ct)
    {
        var videos = await library.LoadVideosByIdsAsync(coveIds, ct);
        var index = SceneStatusProjector.BuildMovieIndex(movies);
        var excluded = SceneStatusProjector.BuildExcludedSet(exclusions);

        var states = new Dictionary<int, SceneCardStatus>();
        foreach (var video in videos)
        {
            states[video.CoveId] = SceneStatusProjector.CardStatus(video.StashIds, index, excluded);
        }

        return states;
    }

    /// <summary>
    /// Studio/performer status for the per-card badges — a studios/performers page costs ONE Whisparr fetch
    /// (the entity list), not one per card. Configure-gated, stored creds only, v3-only (a v2 instance
    /// returns <c>VERSION_UNSUPPORTED</c>, and the entity card slots are not registered on v2 anyway). An
    /// unresolvable id, or one absent from Whisparr, is simply missing from the map (the card shows no badge).
    /// </summary>
    internal async Task<IResult> EntityStatusBatchAsync(
        EntityStatusBatchRequest req, WhisparrClient client, ICurrentPrincipalAccessor principal, CancellationToken ct)
    {
        if (Forbidden(principal, Permissions.ExtensionsConfigure) is { } denied)
        {
            return denied;
        }

        if (!TryParseEntityKind(req.Kind, out var kind))
        {
            return Results.Json(new { code = "UNKNOWN_KIND" }, statusCode: 400);
        }

        var coveIds = req.CoveEntityIds ?? [];
        if (coveIds.Length > MaxEntityIdsPerRequest)
        {
            return Results.Json(new { code = "TOO_MANY_IDS", max = MaxEntityIdsPerRequest }, statusCode: 400);
        }

        var (options, baseUrl, apiKey) = await ResolveCredsAsync(new TestConnectionRequest(null, null), ct);
        var adapter = AdapterSelector.SelectForVersion(options.SelectedVersion, client);
        if (adapter is null || !adapter.SupportsEntityMonitor(kind))
        {
            return Results.Json(new { code = "VERSION_UNSUPPORTED" }, statusCode: 400);
        }

        if (coveIds.Length == 0)
        {
            return Results.Json(new { states = new Dictionary<int, EntityStatus>() }, MonitorResponseJsonOptions);
        }

        return await WithScopedLibraryAsync(options.StashDbEndpoint, options.TpdbEndpoint, async library =>
        {
            // Cached entity-list read: paging re-uses one fetch per TTL window, not one per page. A failed
            // read degrades to empty (no badges), never a thrown page. No movie set — the count is Whisparr's own.
            IReadOnlyDictionary<int, EntityStatus> states;
            if (adapter is V2Adapter)
            {
                // v2: studio-only (guarded above), matched by TPDB against the site (series) list.
                var series = await CachedSeriesAsync(baseUrl, apiKey, client, ct);
                states = await V2EntityStatusBatchCoreAsync(
                    coveIds, library, series is { IsOk: true } ? series.Value! : [], ct);
            }
            else
            {
                var studios = kind == EntityKind.Studio ? await CachedStudiosAsync(baseUrl, apiKey, client, ct) : null;
                var performers = kind == EntityKind.Performer ? await CachedPerformersAsync(baseUrl, apiKey, client, ct) : null;
                states = await EntityStatusBatchCoreAsync(
                    kind, coveIds, library,
                    studios is { IsOk: true } ? studios.Value! : [],
                    performers is { IsOk: true } ? performers.Value! : [],
                    ct);
            }

            return Results.Json(new { states }, MonitorResponseJsonOptions);
        });
    }

    /// <summary>
    /// The classify half of <see cref="EntityStatusBatchAsync"/> (pure over the pre-fetched entity list, so it is
    /// unit-testable host-free and re-uses the cached list). Resolves each Cove id to its StashDB id via the
    /// library, then <see cref="V3Adapter.ClassifyEntityStatusBatch"/> yields every entity's monitored +
    /// scenesPresent/scenesTotal. An id with no StashDB id, or no Whisparr entity, is absent from the map.
    /// </summary>
    internal static async Task<IReadOnlyDictionary<int, EntityStatus>> EntityStatusBatchCoreAsync(
        EntityKind kind, IReadOnlyList<int> coveEntityIds, ICoveLibraryPort library,
        WhisparrStudio[] studios, WhisparrPerformer[] performers, CancellationToken ct)
    {
        var coveToStash = new Dictionary<int, string>();
        foreach (var id in coveEntityIds)
        {
            var identity = await library.LoadEntityIdentityAsync(kind, id, ct);
            if (identity?.StashIds.FirstOrDefault(x => !string.IsNullOrEmpty(x)) is { } stashId)
            {
                coveToStash[id] = stashId;
            }
        }

        var result = new Dictionary<int, EntityStatus>();
        if (coveToStash.Count == 0)
        {
            return result;
        }

        var byStash = V3Adapter.ClassifyEntityStatusBatch(
            kind, [.. coveToStash.Values.Distinct(StringComparer.OrdinalIgnoreCase)], studios, performers);
        foreach (var (coveId, stashId) in coveToStash)
        {
            if (byStash.TryGetValue(stashId, out var status))
            {
                result[coveId] = status;
            }
        }

        return result;
    }

    /// <summary>
    /// The v2 studio half of <see cref="EntityStatusBatchAsync"/>: resolves each Cove studio to its ThePornDB id
    /// (v2's identity key, not StashDB) and classifies against the pre-fetched site (series) list via
    /// <see cref="V2Adapter.ClassifyStudioStatusBatch"/>. Studio-only — v2 has no performer entity.
    /// </summary>
    internal static async Task<IReadOnlyDictionary<int, EntityStatus>> V2EntityStatusBatchCoreAsync(
        IReadOnlyList<int> coveEntityIds, ICoveLibraryPort library, WhisparrSeries[] series, CancellationToken ct)
    {
        var coveToTpdb = new Dictionary<int, string>();
        foreach (var id in coveEntityIds)
        {
            var identity = await library.LoadEntityIdentityAsync(EntityKind.Studio, id, ct);
            if (identity?.TpdbIds.FirstOrDefault(x => !string.IsNullOrEmpty(x)) is { } tpdbId)
            {
                coveToTpdb[id] = tpdbId;
            }
        }

        var result = new Dictionary<int, EntityStatus>();
        if (coveToTpdb.Count == 0)
        {
            return result;
        }

        var byTpdb = V2Adapter.ClassifyStudioStatusBatch(
            [.. coveToTpdb.Values.Distinct(StringComparer.OrdinalIgnoreCase)], series);
        foreach (var (coveId, tpdbId) in coveToTpdb)
        {
            if (byTpdb.TryGetValue(tpdbId, out var status))
            {
                result[coveId] = status;
            }
        }

        return result;
    }

    /// <summary>
    /// The library-wide monitored summary for the studios/performers toolbar row: how many Cove entities of the
    /// kind are monitored in Whisparr, over the total in the library. Enumerates every Cove entity of the kind
    /// ONCE (id + StashDB ids) and matches against the cached Whisparr entity list — one entity-list fetch per TTL
    /// window, so it stays cheap for a library with thousands of studios/performers. An entity with no StashDB id,
    /// or absent from Whisparr, counts toward the total but never <c>monitored</c>.
    /// </summary>
    internal static async Task<EntityLibrarySummary> EntityLibrarySummaryCoreAsync(
        EntityKind kind, ICoveLibraryPort library,
        WhisparrStudio[] studios, WhisparrPerformer[] performers, CancellationToken ct)
    {
        var identities = await library.LoadAllEntityIdentitiesAsync(kind, ct);
        if (identities.Count == 0)
        {
            return new EntityLibrarySummary(Total: 0, Monitored: 0);
        }

        var monitoredForeignIds = kind == EntityKind.Studio
            ? studios.Where(s => s.Monitored).Select(s => s.ForeignId)
            : performers.Where(p => p.Monitored).Select(p => p.ForeignId);
        var monitored = new HashSet<string>(
            monitoredForeignIds.Where(id => !string.IsNullOrEmpty(id))!, StringComparer.OrdinalIgnoreCase);

        var monitoredCount = identities.Count(
            identity => identity.StashIds.Any(id => !string.IsNullOrEmpty(id) && monitored.Contains(id)));
        return new EntityLibrarySummary(Total: identities.Count, Monitored: monitoredCount);
    }

    /// <summary>
    /// The v2 studio variant of <see cref="EntityLibrarySummaryCoreAsync"/>: counts monitored sites over every
    /// Cove studio, matching each studio's ThePornDB id against the monitored series' <c>tvdbId</c>. Studio-only.
    /// </summary>
    internal static async Task<EntityLibrarySummary> V2EntityLibrarySummaryCoreAsync(
        ICoveLibraryPort library, WhisparrSeries[] series, CancellationToken ct)
    {
        var identities = await library.LoadAllEntityIdentitiesAsync(EntityKind.Studio, ct);
        if (identities.Count == 0)
        {
            return new EntityLibrarySummary(Total: 0, Monitored: 0);
        }

        var monitored = new HashSet<string>(
            series.Where(s => s.Monitored && s.TvdbId is not null)
                .Select(s => s.TvdbId!.Value.ToString(CultureInfo.InvariantCulture)),
            StringComparer.OrdinalIgnoreCase);

        var monitoredCount = identities.Count(
            identity => identity.TpdbIds.Any(id => !string.IsNullOrEmpty(id) && monitored.Contains(id)));
        return new EntityLibrarySummary(Total: identities.Count, Monitored: monitoredCount);
    }

    /// <summary>
    /// The studios/performers toolbar row's library-wide monitored count. Configure-gated, stored creds only.
    /// Studios work on both versions (v2 matches by ThePornDB against the site list); performers are v3-only, so a
    /// v2 performer returns <c>VERSION_UNSUPPORTED</c>. <c>available</c> is false when the Whisparr entity-list
    /// read failed, so the client shows "Whisparr unavailable" rather than a misleading "0 monitored".
    /// </summary>
    internal async Task<IResult> EntityLibrarySummaryAsync(
        string? kind, WhisparrClient client, ICurrentPrincipalAccessor principal, CancellationToken ct)
    {
        if (Forbidden(principal, Permissions.ExtensionsConfigure) is { } denied)
        {
            return denied;
        }

        if (!TryParseEntityKind(kind, out var entityKind))
        {
            return Results.Json(new { code = "UNKNOWN_KIND" }, statusCode: 400);
        }

        var (options, baseUrl, apiKey) = await ResolveCredsAsync(new TestConnectionRequest(null, null), ct);
        var adapter = AdapterSelector.SelectForVersion(options.SelectedVersion, client);
        if (adapter is null || !adapter.SupportsEntityMonitor(entityKind))
        {
            return Results.Json(new { code = "VERSION_UNSUPPORTED" }, statusCode: 400);
        }

        return await WithScopedLibraryAsync(options.StashDbEndpoint, options.TpdbEndpoint, async library =>
        {
            if (adapter is V2Adapter)
            {
                var series = await CachedSeriesAsync(baseUrl, apiKey, client, ct);
                var v2Summary = await V2EntityLibrarySummaryCoreAsync(
                    library, series is { IsOk: true } ? series.Value! : [], ct);
                return Results.Json(
                    new { available = series is { IsOk: true }, total = v2Summary.Total, monitored = v2Summary.Monitored },
                    MonitorResponseJsonOptions);
            }

            var studios = entityKind == EntityKind.Studio ? await CachedStudiosAsync(baseUrl, apiKey, client, ct) : null;
            var performers = entityKind == EntityKind.Performer ? await CachedPerformersAsync(baseUrl, apiKey, client, ct) : null;
            var available = entityKind == EntityKind.Studio ? studios is { IsOk: true } : performers is { IsOk: true };
            var summary = await EntityLibrarySummaryCoreAsync(
                entityKind, library,
                studios is { IsOk: true } ? studios.Value! : [],
                performers is { IsOk: true } ? performers.Value! : [],
                ct);
            return Results.Json(
                new { available, total = summary.Total, monitored = summary.Monitored }, MonitorResponseJsonOptions);
        });
    }

    /// <summary>
    /// One scene's Whisparr-owned status facts for the native detail-rail tab. The scene is
    /// resolved SERVER-SIDE from its Cove entity id via <see cref="CoveLibraryPort.LoadVideoByIdAsync"/> (the
    /// tab forwards only the Cove id — never a remote id), so a caller cannot point the lookup at an arbitrary
    /// StashDB id. A scene with no row, or no StashDB id on the stored endpoint, is the handled
    /// <c>NO_STASHDB_IDENTITY</c> outcome (a 200, never a 500) and makes NO outbound Whisparr call. Otherwise it
    /// returns <see cref="SceneStatusProjector.Detail"/> — Whisparr-owned facts ONLY (state/added/monitored/
    /// hasFile/quality/cutoff); it deliberately reads/returns no Cove-owned field (title/date/path/size).
    /// Same security posture as the summary (configure-gated, stored creds only).
    /// </summary>
    internal async Task<IResult> SceneDetailAsync(
        SceneDetailRequest req, WhisparrClient client, ICurrentPrincipalAccessor principal, CancellationToken ct)
    {
        if (Forbidden(principal, Permissions.ExtensionsConfigure) is { } denied)
        {
            return denied;
        }

        var (options, baseUrl, apiKey) = await ResolveCredsAsync(new TestConnectionRequest(null, null), ct);
        if (AdapterSelector.SelectForVersion(options.SelectedVersion, client) is not { } adapter)
        {
            return Results.Json(new { code = "VERSION_UNSUPPORTED" }, statusCode: 400);
        }

        // Resolve the scene FIRST (a Cove read, no Whisparr call). No row / no StashDB id => handled outcome
        // before any outbound call, so a not-identifiable scene never reaches Whisparr.
        var video = await LoadVideoByIdSafeAsync(req.CoveId, options.StashDbEndpoint, options.TpdbEndpoint, ct);
        if (video is null || video.StashIds.Count == 0)
        {
            return Results.Json(new { code = "NO_STASHDB_IDENTITY", provider = ProviderNameFor(options) }, MonitorResponseJsonOptions);
        }

        var movies = await adapter.ListMoviesAsync(baseUrl, apiKey, ct);
        var exclusions = await adapter.ListExclusionsAsync(baseUrl, apiKey, ct);
        var index = SceneStatusProjector.BuildMovieIndex(movies.IsOk ? movies.Value! : []);
        var excluded = SceneStatusProjector.BuildExcludedSet(exclusions.IsOk ? exclusions.Value! : []);

        var detail = SceneStatusProjector.Detail(video.StashIds, index, excluded)
            with
        { ActionsSupported = adapter.SupportsSceneAdd };
        return Results.Json(detail, MonitorResponseJsonOptions);
    }

    /// <summary>
    /// The library-wide identity-health count for the guided-setup banner: total Cove scenes and how many
    /// carry no provider id on the connected version's endpoint (StashDB on v3, ThePornDB on v2). A PURE Cove
    /// read — it makes NO outbound Whisparr call, reading only the stored endpoint config to pick which id list
    /// to check. The count runs under <c>CovePrincipal.System()</c> (see <see cref="CountIdentityHealthAsync"/>)
    /// so the total is the whole library, not the calling principal's slice. 403-first on <c>extensions.read</c>.
    /// </summary>
    internal async Task<IResult> IdentityHealthAsync(ICurrentPrincipalAccessor principal, CancellationToken ct)
    {
        if (Forbidden(principal, Permissions.ExtensionsRead) is { } denied)
        {
            return denied;
        }

        var options = await new OptionsStore(Store).LoadAsync(ct);
        var (total, unidentified) = await CountIdentityHealthAsync(options, ct);
        return Results.Json(new { totalScenes = total, unidentifiedScenes = unidentified }, MonitorResponseJsonOptions);
    }

    /// <summary>
    /// Adds a scene to Whisparr as a monitored, non-grabbing movie
    /// (<c>searchForMovie:false</c>, origin-tagged). The scene is resolved SERVER-SIDE from its Cove entity id
    /// via <see cref="LoadVideoByIdSafeAsync"/> (the body forwards only the Cove id — never a StashDB id), so a
    /// caller cannot point the add at an arbitrary id. A scene with no row or no StashDB id on the stored
    /// endpoint is the handled <c>NO_STASHDB_IDENTITY</c> outcome (a 200, never a 500) and makes NO outbound
    /// call. Configure-gated + stored-creds-only: the body carries no url/key and the stored key is
    /// never echoed. Scenes are v3-only, so a v2 instance returns <c>VERSION_UNSUPPORTED</c> (400)
    /// before any Cove read. Never triggers a Whisparr search (loop-safety is enforced in <see cref="SceneActions"/>).
    /// </summary>
    internal async Task<IResult> SceneAddAsync(
        SceneAddRequest req, WhisparrClient client, ICurrentPrincipalAccessor principal, CancellationToken ct)
    {
        if (Forbidden(principal, Permissions.ExtensionsConfigure) is { } denied)
        {
            return denied;
        }

        var (options, _, _) = await ResolveCredsAsync(new TestConnectionRequest(null, null), ct);
        if (AdapterSelector.SelectForVersion(options.SelectedVersion, client) is not V3Adapter)
        {
            return Results.Json(new { code = "VERSION_UNSUPPORTED" }, statusCode: 400);
        }

        var video = await LoadVideoByIdSafeAsync(req.CoveId, options.StashDbEndpoint, options.TpdbEndpoint, ct);
        if (video is null || video.StashIds.Count == 0)
        {
            return Results.Json(new { code = "NO_STASHDB_IDENTITY", provider = ProviderNameFor(options) }, MonitorResponseJsonOptions);
        }

        var result = await new SceneActions(client, options, EmptyCoveLibraryPort.Instance)
            .AddSceneAsync(video.StashIds[0], SceneActions.ResolveTitle(video.Title, video.FilePaths, video.StashIds[0]), ct);
        if (result.IsOk)
        {
            LogScenePushed(result.Value!.Added, result.Value.Monitored);
        }

        return ToMonitorResult(result);
    }

    /// <summary>
    /// Searches now for a single scene. Resolves the scene server-side (like <see cref="SceneAddAsync"/>),
    /// finds its Whisparr movie via the movie index (<see cref="ResolveMovieForScene"/>), and — only when the
    /// scene is already an added movie — issues one <c>MoviesSearch</c> over that movie id. A scene with no
    /// StashDB id is <c>NO_STASHDB_IDENTITY</c>; a scene not yet added to Whisparr is a handled
    /// <c>{ searched:false }</c> (nothing to search). Configure-gated + stored-creds-only; v2 → 400.
    /// This is the ONLY per-scene route that may grab.
    /// </summary>
    internal async Task<IResult> SceneSearchAsync(
        SceneSearchRequest req, WhisparrClient client, ICurrentPrincipalAccessor principal, CancellationToken ct)
    {
        if (Forbidden(principal, Permissions.ExtensionsConfigure) is { } denied)
        {
            return denied;
        }

        var (options, baseUrl, apiKey) = await ResolveCredsAsync(new TestConnectionRequest(null, null), ct);
        if (AdapterSelector.SelectForVersion(options.SelectedVersion, client) is not V3Adapter adapter)
        {
            return Results.Json(new { code = "VERSION_UNSUPPORTED" }, statusCode: 400);
        }

        var video = await LoadVideoByIdSafeAsync(req.CoveId, options.StashDbEndpoint, options.TpdbEndpoint, ct);
        if (video is null || video.StashIds.Count == 0)
        {
            return Results.Json(new { code = "NO_STASHDB_IDENTITY", provider = ProviderNameFor(options) }, MonitorResponseJsonOptions);
        }

        var movies = await adapter.ListMoviesAsync(baseUrl, apiKey, ct);
        if (!movies.IsOk)
        {
            return Results.Json(new { result = FailureDiscriminator(movies.State) }, statusCode: 502);
        }

        var movie = ResolveMovieForScene(video.StashIds, SceneStatusProjector.BuildMovieIndex(movies.Value!));
        if (movie is null)
        {
            // The scene has a StashDB id but is not yet an added Whisparr movie — nothing to search.
            LogSceneSearched(false);
            return Results.Json(new { searched = false }, MonitorResponseJsonOptions);
        }

        var result = await new SceneActions(client, options, EmptyCoveLibraryPort.Instance)
            .SearchSceneAsync(movie.Id, ct);
        if (result.IsOk)
        {
            LogSceneSearched(true);
        }

        return ToMonitorResult(result);
    }

    /// <summary>
    /// Sets a scene's monitor state (add-then-flip when turning ON an absent scene). The scene is
    /// resolved server-side from its Cove id; no StashDB id → <c>NO_STASHDB_IDENTITY</c> (no outbound call).
    /// Configure-gated + stored-creds-only; v2 → 400. The add leg registers <c>searchForMovie:false</c>
    /// (never grabs) — only <see cref="SceneSearchAsync"/> may grab.
    /// </summary>
    internal async Task<IResult> SceneMonitorAsync(
        SceneMonitorRequest req, WhisparrClient client, ICurrentPrincipalAccessor principal, CancellationToken ct)
    {
        if (Forbidden(principal, Permissions.ExtensionsConfigure) is { } denied)
        {
            return denied;
        }

        var (options, _, _) = await ResolveCredsAsync(new TestConnectionRequest(null, null), ct);
        if (AdapterSelector.SelectForVersion(options.SelectedVersion, client) is not V3Adapter)
        {
            return Results.Json(new { code = "VERSION_UNSUPPORTED" }, statusCode: 400);
        }

        var video = await LoadVideoByIdSafeAsync(req.CoveId, options.StashDbEndpoint, options.TpdbEndpoint, ct);
        if (video is null || video.StashIds.Count == 0)
        {
            return Results.Json(new { code = "NO_STASHDB_IDENTITY", provider = ProviderNameFor(options) }, MonitorResponseJsonOptions);
        }

        var result = await new SceneActions(client, options, EmptyCoveLibraryPort.Instance)
            .SetSceneMonitorAsync(
                video.StashIds[0], SceneActions.ResolveTitle(video.Title, video.FilePaths, video.StashIds[0]),
                req.Monitored, ct);
        if (result.IsOk)
        {
            LogScenePushed(result.Value!.Added, result.Value.Monitored);
        }

        return ToMonitorResult(result);
    }

    /// <summary>
    /// Registers every scene a studio/performer owns in Cove but Whisparr does not yet track, as
    /// non-grabbing movies (the local-diff "add all missing"). The diff is computed SERVER-SIDE from the entity's
    /// OWN Cove scenes (enumerated by <see cref="ICoveLibraryPort.LoadVideosForEntityAsync"/> for the forwarded
    /// <see cref="BulkAddMissingRequest.Kind"/> + <see cref="BulkAddMissingRequest.CoveEntityId"/>) diffed against
    /// the fetched Whisparr movie set — NO StashDB call. Runs inside a fresh DB scope so the scoped port has the
    /// correct lifetime (mirrors <see cref="LoadVideoByIdSafeAsync"/>'s null-scope-safe pattern). Configure-gated +
    /// stored-creds-only; v2 → 400. Every registration is <c>searchForMovie:false</c> — never grabs.
    /// </summary>
    /// <remarks>
    /// The request deliberately carries NO <c>remoteIds</c>. The missing-set diff is keyed by the
    /// Cove <c>Studio.Id</c>/<c>Performer.Id</c> (the local enumeration), never by a caller-forwarded stashId, so
    /// a <c>remoteIds</c> field would be a dead input the handler never consumes.
    /// </remarks>
    internal async Task<IResult> BulkAddMissingAsync(
        BulkAddMissingRequest req, WhisparrClient client, ICurrentPrincipalAccessor principal, CancellationToken ct)
    {
        if (Forbidden(principal, Permissions.ExtensionsConfigure) is { } denied)
        {
            return denied;
        }

        if (!TryParseEntityKind(req.Kind, out var kind))
        {
            return Results.Json(new { code = "UNKNOWN_KIND" }, statusCode: 400);
        }

        var (options, _, _) = await ResolveCredsAsync(new TestConnectionRequest(null, null), ct);
        if (AdapterSelector.SelectForVersion(options.SelectedVersion, client) is not V3Adapter)
        {
            return Results.Json(new { code = "VERSION_UNSUPPORTED" }, statusCode: 400);
        }

        return await WithScopedLibraryAsync(options.StashDbEndpoint, options.TpdbEndpoint, async library =>
        {
            var result = await new SceneActions(client, options, library)
                .AddAllMissingAsync(kind, req.CoveEntityId, ct);
            if (result.IsOk)
            {
                LogBulkAction(kind, result.Value!.Total, result.Value.Succeeded, result.Value.Failed);
            }

            return ToMonitorResult(result);
        });
    }

    /// <summary>
    /// Searches all monitored scenes of a studio/performer. The entity's id is resolved
    /// SERVER-SIDE from the forwarded Cove <see cref="BulkSearchMonitoredRequest.RemoteIds"/> by the connected
    /// version's endpoint (<see cref="WhisparrOptions.IdentityEndpoint"/> — the same rule <see cref="MonitorAsync"/>
    /// uses), so a caller cannot point the search at an arbitrary id. No matching endpoint →
    /// <c>NO_STASHDB_IDENTITY</c> (no outbound call). <see cref="SceneActions.SearchAllMonitoredAsync"/> resolves
    /// the entity's monitored attributed ids from the already-fetched set (no StashDB call) and issues one search
    /// command (v3 <c>MoviesSearch</c> / v2 <c>EpisodeSearch</c>) over them.
    /// Configure-gated + stored-creds-only.
    /// </summary>
    internal async Task<IResult> BulkSearchMonitoredAsync(
        BulkSearchMonitoredRequest req, WhisparrClient client, ICurrentPrincipalAccessor principal, CancellationToken ct)
    {
        if (Forbidden(principal, Permissions.ExtensionsConfigure) is { } denied)
        {
            return denied;
        }

        if (!TryParseEntityKind(req.Kind, out var kind))
        {
            return Results.Json(new { code = "UNKNOWN_KIND" }, statusCode: 400);
        }

        var (options, _, _) = await ResolveCredsAsync(new TestConnectionRequest(null, null), ct);
        if (AdapterSelector.SelectForVersion(options.SelectedVersion, client) is null)
        {
            return Results.Json(new { code = "VERSION_UNSUPPORTED" }, statusCode: 400);
        }

        if (ResolveRemoteId(req.RemoteIds, options) is not { } stashId)
        {
            return Results.Json(new { code = "NO_STASHDB_IDENTITY", provider = ProviderNameFor(options) }, MonitorResponseJsonOptions);
        }

        // A v2 studio routes to the SITE episode search (the sole grab-capable v2 verb); a v2 performer has no
        // attributed set and the adapter defers → a clear 400 via ToMonitorResult, never a 500.
        var result = await new SceneActions(client, options, EmptyCoveLibraryPort.Instance)
            .SearchAllMonitoredAsync(kind, stashId, ct);
        if (result.IsOk)
        {
            LogBulkAction(kind, result.Value!.Total, result.Value.Succeeded, result.Value.Failed);
        }

        return ToMonitorResult(result);
    }

    /// <summary>
    /// Imports every scene a studio/performer OWNS in Cove into Whisparr: each owned file that matches a fileless
    /// Whisparr scene by the connected version's identity id (StashDB on v3, TPDB on v2) is attached to that scene
    /// via a targeted <c>ManualImport</c> — Cove's own file is never moved or deleted (v2 in place; v3 copies) and
    /// it never searches. The entity's own Cove scenes are enumerated SERVER-SIDE from the forwarded
    /// <see cref="ReflectOwnedRequest.Kind"/> +
    /// <see cref="ReflectOwnedRequest.CoveEntityId"/> (runs in a fresh DB scope, like <see cref="BulkAddMissingAsync"/>).
    /// Configure-gated + stored-creds-only. An unmanageable version (no adapter) returns <c>VERSION_UNSUPPORTED</c>
    /// (400) BEFORE any wire call.
    /// </summary>
    internal async Task<IResult> ReflectOwnedAsync(
        ReflectOwnedRequest req, WhisparrClient client, ICurrentPrincipalAccessor principal, CancellationToken ct)
    {
        if (Forbidden(principal, Permissions.ExtensionsConfigure) is { } denied)
        {
            return denied;
        }

        if (!TryParseEntityKind(req.Kind, out var kind))
        {
            return Results.Json(new { code = "UNKNOWN_KIND" }, statusCode: 400);
        }

        var (options, _, _) = await ResolveCredsAsync(new TestConnectionRequest(null, null), ct);
        if (AdapterSelector.SelectForVersion(options.SelectedVersion, client) is not { SupportsOwnedImport: true })
        {
            // Both v3 and v2 support owned-import (v3 in-place adopt, v2 in-place register); only an unmanageable
            // version has no adapter — it refuses BEFORE resolving the DB scope or reading any Cove scene (wire-free).
            return Results.Json(new { code = "VERSION_UNSUPPORTED" }, statusCode: 400);
        }

        return await WithScopedLibraryAsync(options.StashDbEndpoint, options.TpdbEndpoint, async library =>
        {
            var result = await new SceneActions(client, options, library)
                .ReflectOwnedAsync(kind, req.CoveEntityId, ct);
            if (result.IsOk)
            {
                LogBulkAction(kind, result.Value!.Total, result.Value.Succeeded, result.Value.Failed);
            }

            return ToMonitorResult(result);
        });
    }

    /// <summary>
    /// Toggles a scene's Whisparr import-list exclusion by the <see cref="SceneExclusionRequest.Exclude"/>
    /// flag (true = add the exclusion, false = remove it). The scene is resolved SERVER-SIDE from its Cove id
    /// (<see cref="LoadVideoByIdSafeAsync"/>); a scene with no row or no StashDB id is the handled
    /// <c>NO_STASHDB_IDENTITY</c> outcome (200, no outbound call). The un-exclude leg resolves the exclusion's
    /// Whisparr id server-side by foreignId match (the adapter — never a caller id). Configure-gated +
    /// stored-creds-only; v2 → <c>VERSION_UNSUPPORTED</c> (400). Never triggers a search (an exclusion
    /// never grabs — loop-safety is LOCKED).
    /// </summary>
    internal async Task<IResult> SceneExclusionAsync(
        SceneExclusionRequest req, WhisparrClient client, ICurrentPrincipalAccessor principal, CancellationToken ct)
    {
        if (Forbidden(principal, Permissions.ExtensionsConfigure) is { } denied)
        {
            return denied;
        }

        var (options, _, _) = await ResolveCredsAsync(new TestConnectionRequest(null, null), ct);
        if (AdapterSelector.SelectForVersion(options.SelectedVersion, client) is not V3Adapter)
        {
            return Results.Json(new { code = "VERSION_UNSUPPORTED" }, statusCode: 400);
        }

        var video = await LoadVideoByIdSafeAsync(req.CoveId, options.StashDbEndpoint, options.TpdbEndpoint, ct);
        if (video is null || video.StashIds.Count == 0)
        {
            return Results.Json(new { code = "NO_STASHDB_IDENTITY", provider = ProviderNameFor(options) }, MonitorResponseJsonOptions);
        }

        var actions = new SceneActions(client, options, EmptyCoveLibraryPort.Instance);
        var stashId = video.StashIds[0];
        var result = req.Exclude
            ? await actions.ExcludeSceneAsync(
                stashId, SceneActions.ResolveTitle(video.Title, video.FilePaths, stashId), video.Date?.Year, ct)
            : await actions.UnExcludeSceneAsync(stashId, ct);

        if (result.IsOk)
        {
            LogSceneExclusionToggled(req.Exclude);
            return Results.Json(new { excluded = req.Exclude }, MonitorResponseJsonOptions);
        }

        return ToMonitorResult(result);
    }

    /// <summary>
    /// Grabs one specific indexer release for a scene — the sole interactive grab. The scene is resolved
    /// SERVER-SIDE from its Cove id (so the grab is bound to a real Cove scene the caller may view); the
    /// <see cref="SceneGrabReleaseRequest.Guid"/> + <see cref="SceneGrabReleaseRequest.IndexerId"/> are the
    /// release handles the picker obtained from this extension's own <c>/scene-releases-list</c> read.
    /// Configure-gated + stored-creds-only; v2 → 400. A scene with no StashDB id is
    /// <c>NO_STASHDB_IDENTITY</c> (no outbound call). The guid is never logged.
    /// </summary>
    internal async Task<IResult> SceneGrabReleaseAsync(
        SceneGrabReleaseRequest req, WhisparrClient client, ICurrentPrincipalAccessor principal, CancellationToken ct)
    {
        if (Forbidden(principal, Permissions.ExtensionsConfigure) is { } denied)
        {
            return denied;
        }

        if (string.IsNullOrWhiteSpace(req.Guid))
        {
            return Results.Json(new { code = "MISSING_RELEASE" }, statusCode: 400);
        }

        var (options, baseUrl, apiKey) = await ResolveCredsAsync(new TestConnectionRequest(null, null), ct);
        if (AdapterSelector.SelectForVersion(options.SelectedVersion, client) is not V3Adapter adapter)
        {
            return Results.Json(new { code = "VERSION_UNSUPPORTED" }, statusCode: 400);
        }

        var video = await LoadVideoByIdSafeAsync(req.CoveId, options.StashDbEndpoint, options.TpdbEndpoint, ct);
        if (video is null || video.StashIds.Count == 0)
        {
            return Results.Json(new { code = "NO_STASHDB_IDENTITY", provider = ProviderNameFor(options) }, MonitorResponseJsonOptions);
        }

        // The interactive-search release row carries movieId:null; without a movieId in the grab body
        // Whisparr answers 404 "Unable to find matching movie". Supply the movie resolved for this scene.
        var movies = await adapter.ListMoviesAsync(baseUrl, apiKey, ct);
        var movie = movies.IsOk
            ? ResolveMovieForScene(video.StashIds, SceneStatusProjector.BuildMovieIndex(movies.Value!))
            : null;
        if (movie is null)
        {
            return Results.Json(new { code = "NOT_ADDED", provider = ProviderNameFor(options) }, MonitorResponseJsonOptions);
        }

        var result = await new SceneActions(client, options, EmptyCoveLibraryPort.Instance)
            .GrabReleaseAsync(req.Guid, req.IndexerId, movie.Id, ct);
        if (result.IsOk)
        {
            LogSceneReleaseGrabbed();
            return Results.Json(new { grabbed = true }, MonitorResponseJsonOptions);
        }

        return ToMonitorResult(result);
    }

    /// <summary>
    /// Returns the enriched pickable release rows for a scene's interactive picker. Resolves the scene
    /// server-side, finds its Whisparr movie via the movie index, then reads <c>GetReleasesAsync(movieId)</c> —
    /// a READ that grabs/downloads nothing (loop-safe), invoked only on explicit UI expand. A scene with no
    /// StashDB id is <c>NO_STASHDB_IDENTITY</c> (no outbound call); a not-added scene (no matching movie) returns
    /// an empty list. Configure-gated + stored-creds-only; v2 → 400.
    /// </summary>
    internal async Task<IResult> SceneReleasesListAsync(
        SceneReleasesRequest req, WhisparrClient client, ICurrentPrincipalAccessor principal, CancellationToken ct)
    {
        if (Forbidden(principal, Permissions.ExtensionsConfigure) is { } denied)
        {
            return denied;
        }

        var (options, baseUrl, apiKey) = await ResolveCredsAsync(new TestConnectionRequest(null, null), ct);
        if (AdapterSelector.SelectForVersion(options.SelectedVersion, client) is not V3Adapter adapter)
        {
            return Results.Json(new { code = "VERSION_UNSUPPORTED" }, statusCode: 400);
        }

        var video = await LoadVideoByIdSafeAsync(req.CoveId, options.StashDbEndpoint, options.TpdbEndpoint, ct);
        if (video is null || video.StashIds.Count == 0)
        {
            return Results.Json(new { code = "NO_STASHDB_IDENTITY", provider = ProviderNameFor(options) }, MonitorResponseJsonOptions);
        }

        var movies = await adapter.ListMoviesAsync(baseUrl, apiKey, ct);
        if (!movies.IsOk)
        {
            return Results.Json(new { releases = Array.Empty<WhisparrRelease>() }, MonitorResponseJsonOptions);
        }

        var movie = ResolveMovieForScene(video.StashIds, SceneStatusProjector.BuildMovieIndex(movies.Value!));
        if (movie is null)
        {
            return Results.Json(new { releases = Array.Empty<WhisparrRelease>() }, MonitorResponseJsonOptions);
        }

        var releases = await adapter.GetReleasesAsync(baseUrl, apiKey, movie.Id, ct);
        var rows = releases.IsOk ? releases.Value! : [];
        LogSceneReleasesListed(rows.Length);
        return Results.Json(new { releases = rows }, MonitorResponseJsonOptions);
    }

    /// <summary>
    /// Searches a scene's Whisparr movie for a quality upgrade — a grab-capable verb, honoring the
    /// <see cref="WhisparrOptions.AllowQualityUpgrades"/> setting (off = an Ok no-op that issues no command).
    /// Resolves the scene server-side, finds its movie; a not-added scene is a handled <c>{ searched:false }</c>
    /// (nothing to upgrade). A scene with no StashDB id is <c>NO_STASHDB_IDENTITY</c> (no outbound call).
    /// Configure-gated + stored-creds-only; v2 → 400.
    /// </summary>
    internal async Task<IResult> SceneSearchUpgradesAsync(
        SceneSearchRequest req, WhisparrClient client, ICurrentPrincipalAccessor principal, CancellationToken ct)
    {
        if (Forbidden(principal, Permissions.ExtensionsConfigure) is { } denied)
        {
            return denied;
        }

        var (options, baseUrl, apiKey) = await ResolveCredsAsync(new TestConnectionRequest(null, null), ct);
        if (AdapterSelector.SelectForVersion(options.SelectedVersion, client) is not V3Adapter adapter)
        {
            return Results.Json(new { code = "VERSION_UNSUPPORTED" }, statusCode: 400);
        }

        var video = await LoadVideoByIdSafeAsync(req.CoveId, options.StashDbEndpoint, options.TpdbEndpoint, ct);
        if (video is null || video.StashIds.Count == 0)
        {
            return Results.Json(new { code = "NO_STASHDB_IDENTITY", provider = ProviderNameFor(options) }, MonitorResponseJsonOptions);
        }

        var movies = await adapter.ListMoviesAsync(baseUrl, apiKey, ct);
        if (!movies.IsOk)
        {
            return Results.Json(new { result = FailureDiscriminator(movies.State) }, statusCode: 502);
        }

        var movie = ResolveMovieForScene(video.StashIds, SceneStatusProjector.BuildMovieIndex(movies.Value!));
        if (movie is null)
        {
            LogSceneUpgradeSearched(false);
            return Results.Json(new { searched = false }, MonitorResponseJsonOptions);
        }

        var result = await new SceneActions(client, options, EmptyCoveLibraryPort.Instance)
            .SearchForUpgradesAsync(movie.Id, ct);
        if (result.IsOk)
        {
            LogSceneUpgradeSearched(true);
        }

        return ToMonitorResult(result);
    }

    /// <summary>
    /// Reads the four file-affecting Whisparr toggles (rename movies / replace illegal characters / auto-rename
    /// folders / delete empty folders) for the config editor. Configure-gated + stored-creds-only: the request
    /// carries no url/key and <see cref="ResolveCredsAsync"/> with an empty request resolves the stored host+key,
    /// so the stored key is never paired with a caller value and never echoed. Reaching the stored credentials to
    /// make an outbound Whisparr call is a configure operation (the same posture as <see cref="ListRootFoldersAsync"/>),
    /// so a read-only principal must not reach it. v2 defers to a clear <c>VERSION_UNSUPPORTED</c> 400 (its
    /// Sonarr-shaped config field names diverge). Reads only — no Cove or Whisparr mutation.
    /// </summary>
    internal async Task<IResult> FileSettingsGetAsync(
        WhisparrClient client, ICurrentPrincipalAccessor principal, CancellationToken ct)
    {
        if (Forbidden(principal, Permissions.ExtensionsConfigure) is { } denied)
        {
            return denied;
        }

        var (options, baseUrl, apiKey) = await ResolveCredsAsync(new TestConnectionRequest(null, null), ct);
        if (AdapterSelector.SelectForVersion(options.SelectedVersion, client) is not { } adapter)
        {
            return Results.Json(new { code = "VERSION_UNSUPPORTED" }, statusCode: 400);
        }

        return ToMonitorResult(await adapter.GetFileSettingsAsync(baseUrl, apiKey, ct));
    }

    /// <summary>
    /// Writes the four file-affecting Whisparr toggles via the adapter's read-modify-write
    /// (<see cref="IWhisparrAdapter.EditFileSettingsAsync"/>): GET each config singleton, flip only the whitelisted
    /// booleans <paramref name="req"/> carries, PUT the complete object back so unknown Whisparr fields survive.
    /// The server honors ONLY the four booleans — it never accepts or forwards an arbitrary client config body.
    /// Configure-gated + stored-creds-only (the body carries no url/key, the stored key is never echoed). v2 defers
    /// to a clear <c>VERSION_UNSUPPORTED</c> 400. The one mutation is to the four Whisparr toggles, nothing else.
    /// </summary>
    internal async Task<IResult> FileSettingsWriteAsync(
        WhisparrFileSettingsRequest req, WhisparrClient client, ICurrentPrincipalAccessor principal, CancellationToken ct)
    {
        if (Forbidden(principal, Permissions.ExtensionsConfigure) is { } denied)
        {
            return denied;
        }

        var (options, baseUrl, apiKey) = await ResolveCredsAsync(new TestConnectionRequest(null, null), ct);
        if (AdapterSelector.SelectForVersion(options.SelectedVersion, client) is not { } adapter)
        {
            return Results.Json(new { code = "VERSION_UNSUPPORTED" }, statusCode: 400);
        }

        return ToMonitorResult(await adapter.EditFileSettingsAsync(baseUrl, apiKey, req, ct));
    }
}
