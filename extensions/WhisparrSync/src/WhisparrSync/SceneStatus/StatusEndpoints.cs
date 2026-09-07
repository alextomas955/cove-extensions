using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using WhisparrSync.Adapters;
using WhisparrSync.Client;
using WhisparrSync.Contracts;
using WhisparrSync.Library;
using WhisparrSync.SceneStatus;
using static WhisparrSync.Contracts.WireSerializers;

namespace WhisparrSync;

/// <summary>
/// The read-only scene Whisparr-status slice: the library-wide summary count, the per-card batch classify,
/// and one scene's detail-rail facts. All configure-gated + stored-creds-only (the body carries no url/key)
/// and grab nothing — every projection reads through <see cref="SceneStatusProjector"/> from the fetched
/// movie/exclusion sets.
/// </summary>
public sealed partial class WhisparrSync
{
    // The read-only scene Whisparr-status surface. All three are
    // configure-gated + stored-creds-only: the body carries NO url/key, the stored key is never echoed.
    // /scene-status-summary is the toolbar's library-wide 4-state count (GET, no body); /scene-detail projects
    // one scene's Whisparr-owned facts (POST {coveId}) — a read that grabs nothing.
    private const string SceneStatusSummaryRoute = RouteBase + "/scene-status-summary";
    // POST {CoveIds:[...]} → { states } for a visible grid page — one DB read + one Whisparr fetch, not per card.
    private const string SceneStatusBatchRoute = RouteBase + "/scene-status-batch";
    private const string SceneDetailRoute = RouteBase + "/scene-detail";

    /// <summary>
    /// Registers the scene-status slice's routes. Summary is a bodiless GET; batch + detail POST only the Cove
    /// ids (never a url/key). All three declare the configure tier.
    /// </summary>
    private void MapStatusEndpoints(IEndpointRouteBuilder endpoints)
    {
        // The read-only scene Whisparr-status endpoints. Summary is a bodiless GET; detail + releases
        // POST only the Cove entity id (never a url/key). Each lambda delegates immediately to an
        // extracted instance handler so it is unit-testable without an HTTP host.
        endpoints.MapGet(SceneStatusSummaryRoute,
            (WhisparrClient client, CancellationToken ct)
                => SceneStatusSummaryAsync(client, ct)).ConfigureGated();

        endpoints.MapPost(SceneStatusBatchRoute,
            (SceneStatusBatchRequest req, WhisparrClient client, CancellationToken ct)
                => SceneStatusBatchAsync(req, client, ct)).ConfigureGated();

        endpoints.MapPost(SceneDetailRoute,
            (SceneDetailRequest req, WhisparrClient client, CancellationToken ct)
                => SceneDetailAsync(req, client, ct)).ConfigureGated();
    }

    /// <summary>
    /// Library-wide Whisparr-status counts for the videos toolbar. Loads ALL Cove videos
    /// once, fetches the Whisparr movie set + exclusion set once, and returns the by-state partition from
    /// <see cref="SceneStatusProjector.SummaryCounts"/> — the honest library-level affordance the toolbar slot
    /// paints (the host exposes no per-video-card decorator — the FALLBACK). Configure-gated
    /// + stored-creds-only: the request has no body, and <see cref="ResolveCredsAsync"/> with an empty
    /// request resolves the stored host+key so the key is never paired with a caller value and never echoed.
    /// Degrades quietly per dimension: a non-Ok movie/exclusion read yields empty inputs for that dimension
    /// rather than failing the whole summary.
    /// </summary>
    /// <remarks>
    /// A generation that cannot key a scene-status index is refused BEFORE the transport, so it costs zero wire
    /// calls — the same shape as the rest of the per-scene surface, and for the same missing scene-level id. The
    /// four states partition a library, so a generation whose index is empty whatever it reads would report every
    /// scene as one Whisparr does not have: a confident answer that is uniformly wrong, which is worse than no
    /// answer. Capability is presence of <see cref="IWhisparrStatusIndexSource"/>; the check names no version, so
    /// a generation that gains the role is served with no change here.
    /// </remarks>
    internal async Task<IResult> SceneStatusSummaryAsync(
        WhisparrClient client, CancellationToken ct)
    {
        var (options, baseUrl, apiKey) = await StoredCredsAsync(ct);
        if (AdapterSelector.SelectForVersion(options.SelectedVersion, client) is not IWhisparrStatusIndexSource adapter)
        {
            return VersionUnsupported();
        }

        var index = await StatusIndexAsync(adapter, baseUrl, apiKey, ct);
        var exclusions = adapter is IWhisparrExclusions exclusionsAdapter
            ? await exclusionsAdapter.ListExclusionsAsync(baseUrl, apiKey, ct)
            : WhisparrResult<WhisparrExclusion[]>.VersionMismatch(options.SelectedVersion);

        var excluded = SceneStatusProjector.BuildExcludedSet(exclusions.IsOk ? exclusions.Value! : []);

        // Folded inside the scope because the stream is only valid while its DbContext is.
        var counts = await WithScopedLibraryAsync(
            options.StashDbEndpoint,
            options.TpdbEndpoint,
            library => SceneStatusProjector.SummaryCountsAsync(library.StreamAllVideosAsync(ct), index, excluded, ct));

        LogSceneStatusRead(counts.Total);
        return Results.Json(new SceneStatusSummaryResponse(counts), EnumStringResponseJsonOptions);
    }

    // The summary's Whisparr half, taken through the role rather than the aggregate so a generation that cannot
    // key an index cannot be passed here at all. The set is folded into the index as it is read and the rows are
    // never held. A non-Ok read yields an empty index — the summary degrades per dimension rather than failing
    // whole, which is sound only because a generation whose index is empty on a SUCCESSFUL read is refused
    // upstream; otherwise the two cases would be indistinguishable to the caller.
    internal static async Task<IReadOnlyDictionary<string, WhisparrMovieFacts>> StatusIndexAsync(
        IWhisparrStatusIndexSource adapter, string baseUrl, string apiKey, CancellationToken ct)
    {
        var index = await adapter.LoadStatusIndexAsync(baseUrl, apiKey, ct);
        return index.IsOk ? index.Value! : SceneStatusProjector.BuildMovieIndex<WhisparrMovieFacts>([]);
    }

    /// <summary>
    /// Classifies Cove video ids into their Whisparr status for the per-card badges — one page costs one DB read +
    /// one movie/exclusion fetch, not one call per card. Configure-gated, stored creds only, v3-only (a v2 instance
    /// has no per-scene identity → <c>VERSION_UNSUPPORTED</c>). An unresolvable id is absent from the map. Reads only.
    /// </summary>
    internal async Task<IResult> SceneStatusBatchAsync(
        SceneStatusBatchRequest req, WhisparrClient client, CancellationToken ct)
    {
        var coveIds = req.CoveIds ?? [];
        if (coveIds.Length > MaxEntityIdsPerRequest)
        {
            return Results.Json(new TooManyIdsResponse("TOO_MANY_IDS", MaxEntityIdsPerRequest), statusCode: 400);
        }

        var (options, baseUrl, apiKey) = await StoredCredsAsync(ct);
        if (V3Only(options, client) is not { } adapter)
        {
            return VersionUnsupported();
        }

        if (coveIds.Length == 0)
        {
            return Results.Json(new SceneStatusBatchResponse(new Dictionary<int, SceneCardStatus>()), EnumStringResponseJsonOptions);
        }

        return await WithScopedLibraryAsync(options.StashDbEndpoint, options.TpdbEndpoint, async library =>
        {
            // Cached list reads: paging a large library re-uses one fetch per TTL window, not one per page.
            var movies = await CachedMoviesAsync(adapter, options.SelectedVersion ?? string.Empty, baseUrl, apiKey, ct);
            var exclusions = await CachedExclusionsAsync(adapter, options.SelectedVersion ?? string.Empty, baseUrl, apiKey, ct);
            var states = await SceneStatusBatchCoreAsync(
                coveIds, movies.IsOk ? movies.Value! : [], exclusions.IsOk ? exclusions.Value! : [], library, ct);
            LogSceneStatusRead(states.Count);
            return Results.Json(new SceneStatusBatchResponse(states), EnumStringResponseJsonOptions);
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
        SceneDetailRequest req, WhisparrClient client, CancellationToken ct)
    {
        var (options, baseUrl, apiKey) = await StoredCredsAsync(ct);

        // A per-scene surface, so it is v3-only like every other one: v2 has no scene-level id, which is why
        // no v2 adapter carries the scene-lookup role. It used to gate only on the version being manageable,
        // so it ran on v2 and reached a whole-set read there. The refusal precedes the transport, so a v2
        // caller costs zero wire calls and leaves no origin tag behind.
        if (V3Only(options, client) is not { } adapter)
        {
            return VersionUnsupported();
        }

        // Resolve the scene FIRST (a Cove read, no Whisparr call). No row / no StashDB id => handled outcome
        // before any outbound call, so a not-identifiable scene never reaches Whisparr.
        var video = await LoadVideoByIdSafeAsync(req.CoveId, options.StashDbEndpoint, options.TpdbEndpoint, ct);
        if (video is null || video.StashIds.Count == 0)
        {
            return Results.Json(new NoIdentityResponse("NO_STASHDB_IDENTITY", ProviderNameFor(options)), EnumStringResponseJsonOptions);
        }

        var scene = await ((IWhisparrSceneLookup)adapter).FindSceneMovieAsync(baseUrl, apiKey, video.StashIds, ct);
        var exclusions = await adapter.ListExclusionsAsync(baseUrl, apiKey, ct);
        var index = SceneStatusProjector.BuildMovieIndex(
            scene is { IsOk: true, Value: { } row } ? [row] : []);
        var excluded = SceneStatusProjector.BuildExcludedSet(exclusions.IsOk ? exclusions.Value! : []);

        var detail = SceneStatusProjector.Detail(video.StashIds, index, excluded)
            with
        { ActionsSupported = adapter is IWhisparrScenePush };
        return Results.Json(detail, EnumStringResponseJsonOptions);
    }
}
