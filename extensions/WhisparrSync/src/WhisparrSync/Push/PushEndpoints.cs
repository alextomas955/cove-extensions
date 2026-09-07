using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using WhisparrSync.Adapters;
using WhisparrSync.Client;
using WhisparrSync.Contracts;
using WhisparrSync.Push;
using static WhisparrSync.Contracts.WireSerializers;

namespace WhisparrSync;

/// <summary>
/// The scene push/search/bulk mutation slice: per-scene add/search/monitor, the studio/performer bulk
/// add-missing / search-monitored / reflect-owned, the exclusion / interactive-grab / releases-list /
/// upgrade-search surface, and the Whisparr file-settings read+write. Every route is configure-gated +
/// stored-creds-only, the scene identity is resolved SERVER-SIDE, and only Search / Search-upgrades /
/// Grab-release may grab (loop-safety is LOCKED in <see cref="SceneActions"/>).
/// </summary>
public sealed partial class WhisparrSync
{
    // The scene push/search/bulk mutation surface. Every route
    // is configure-gated + stored-creds-only: the body carries NO url/key, the stored key is never
    // echoed, and the Whisparr identity is resolved SERVER-SIDE — per-scene routes from the forwarded Cove id
    // (via LoadVideoByIdSafeAsync, like /scene-detail), bulk routes from the entity's kind + Cove id / remoteIds.
    // Scenes are a v3-only capability, so a v2 instance defers to a clear VERSION_UNSUPPORTED 400
    // (never a 500 — v2 does not support scenes). Only /scene-search + /bulk-search-monitored may grab.
    private const string SceneAddRoute = RouteBase + "/scene-add";
    private const string SceneSearchRoute = RouteBase + "/scene-search";
    private const string SceneMonitorRoute = RouteBase + "/scene-monitor";
    private const string BulkAddMissingRoute = RouteBase + "/bulk-add-missing";
    private const string BulkSearchMonitoredRoute = RouteBase + "/bulk-search-monitored";

    // The owned-scene import: attach files Cove already owns to their matching fileless Whisparr scenes without
    // moving/deleting Cove's file and without grabbing. Live on BOTH versions: v3 adopts in place (re-points the
    // movie path to Cove's folder + rescans, falling back to a copy import for a flat layout), v2 registers its
    // episode in place. Same posture as bulk-add-missing: configure-gated + stored-creds-only, entity resolved
    // SERVER-SIDE from the forwarded kind + Cove id.
    private const string ReflectOwnedRoute = RouteBase + "/reflect-owned";

    // The exclusion / interactive-grab / upgrade surface. Same posture as the
    // other scene routes: configure-gated + stored-creds-only (body carries NO url/key, the stored key
    // is never echoed), v3-only (v2 → VERSION_UNSUPPORTED 400), and the scene identity resolved SERVER-SIDE
    // from the forwarded Cove id (LoadVideoByIdSafeAsync). Of the four, only /scene-grab-release and
    // /scene-search-upgrades may grab; /scene-exclusion never searches and /scene-releases-list is a pure read.
    private const string SceneExclusionRoute = RouteBase + "/scene-exclusion";
    private const string SceneGrabReleaseRoute = RouteBase + "/scene-grab-release";
    private const string SceneReleasesListRoute = RouteBase + "/scene-releases-list";
    private const string SceneSearchUpgradesRoute = RouteBase + "/scene-search-upgrades";

    // The Whisparr file-settings surface: read (GET) + write (POST) the four file-affecting toggles. Both are
    // configure-gated + stored-creds-only (the body carries NO url/key); the write is read-modify-write, honoring
    // ONLY the four whitelisted booleans, never a client-supplied config object. v3-only (v2 → VERSION_UNSUPPORTED 400).
    private const string FileSettingsRoute = RouteBase + "/file-settings";

    /// <summary>
    /// Registers the push slice's routes. Per-scene routes POST only the Cove entity id (identity resolved
    /// server-side); bulk routes POST the entity kind + Cove id / remoteIds. None carries a url/key. Each
    /// delegates immediately to an extracted instance handler so it is unit-testable without an HTTP host.
    /// </summary>
    private void MapPushEndpoints(IEndpointRouteBuilder endpoints)
    {
        // The scene push/search/bulk mutations. Per-scene routes POST only the Cove entity id (identity
        // resolved server-side); bulk-add-missing POSTs the entity kind + Cove id (the local-diff enumeration
        // source); bulk-search-monitored POSTs the entity kind + its Cove remoteIds (server-side stashId match).
        // None carries a url/key — the handler uses the stored creds only.
        endpoints.MapPost(SceneAddRoute,
            (SceneAddRequest req, WhisparrClient client, CancellationToken ct)
                => SceneAddAsync(req, client, ct)).ConfigureGated();

        endpoints.MapPost(SceneSearchRoute,
            (SceneSearchRequest req, WhisparrClient client, CancellationToken ct)
                => SceneSearchAsync(req, client, ct)).ConfigureGated();

        endpoints.MapPost(SceneMonitorRoute,
            (SceneMonitorRequest req, WhisparrClient client, CancellationToken ct)
                => SceneMonitorAsync(req, client, ct)).ConfigureGated();

        endpoints.MapPost(BulkAddMissingRoute,
            (BulkAddMissingRequest req, WhisparrClient client, CancellationToken ct)
                => BulkAddMissingAsync(req, client, ct)).ConfigureGated();

        endpoints.MapPost(BulkSearchMonitoredRoute,
            (BulkSearchMonitoredRequest req, WhisparrClient client, CancellationToken ct)
                => BulkSearchMonitoredAsync(req, client, ct)).ConfigureGated();

        endpoints.MapPost(ReflectOwnedRoute,
            (ReflectOwnedRequest req, WhisparrClient client, CancellationToken ct)
                => ReflectOwnedAsync(req, client, ct)).ConfigureGated();

        // The exclusion / interactive-grab / upgrade endpoints. Each POSTs only the scene's Cove id (plus,
        // for grab, the picked release's guid + indexerId — release handles the picker got from the server's own
        // releases-list read); the scene identity is resolved server-side. None carries a url/key. Each
        // delegates immediately to an extracted instance handler so it is unit-testable without an HTTP host.
        endpoints.MapPost(SceneExclusionRoute,
            (SceneExclusionRequest req, WhisparrClient client, CancellationToken ct)
                => SceneExclusionAsync(req, client, ct)).ConfigureGated();

        endpoints.MapPost(SceneGrabReleaseRoute,
            (SceneGrabReleaseRequest req, WhisparrClient client, CancellationToken ct)
                => SceneGrabReleaseAsync(req, client, ct)).ConfigureGated();

        endpoints.MapPost(SceneReleasesListRoute,
            (SceneReleasesRequest req, WhisparrClient client, CancellationToken ct)
                => SceneReleasesListAsync(req, client, ct)).ConfigureGated();

        endpoints.MapPost(SceneSearchUpgradesRoute,
            (SceneSearchRequest req, WhisparrClient client, CancellationToken ct)
                => SceneSearchUpgradesAsync(req, client, ct)).ConfigureGated();

        // The Whisparr file-settings read (GET) + write (POST). Neither carries a url/key — the handler uses the
        // stored creds only; the write honors ONLY the four whitelisted booleans (read-modify-write in the adapter).
        endpoints.MapGet(FileSettingsRoute,
            (WhisparrClient client, CancellationToken ct)
                => FileSettingsGetAsync(client, ct)).ConfigureGated();

        endpoints.MapPost(FileSettingsRoute,
            (WhisparrFileSettingsRequest req, WhisparrClient client, CancellationToken ct)
                => FileSettingsWriteAsync(req, client, ct)).ConfigureGated();
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
        SceneAddRequest req, WhisparrClient client, CancellationToken ct)
    {
        var (options, _, _) = await StoredCredsAsync(ct);
        if (V3Only(options, client) is null)
        {
            return VersionUnsupported();
        }

        var (video, noIdentity) = await ResolveSceneIdentityAsync(req.CoveId, options, ct);
        if (video is null)
        {
            return noIdentity!; // the resolver returns a refusal for exactly the null-video case
        }

        // Refuse an incomplete stored configuration BEFORE the orchestration seam is constructed: that seam's
        // first acts are the fallback-root read and the origin-tag find-or-create, so a guard one line later has
        // already reached Whisparr and can leave a stray cove-sync tag behind.
        if (RefuseIncompleteConfig(options) is { } refusal)
        {
            return refusal;
        }

        var result = await new SceneActions(client, options, EmptyCoveLibraryPort.Instance, CapabilityPort(client))
            .AddSceneAsync(video.StashIds[0], SceneActions.ResolveTitle(video.Title, video.FilePaths, video.StashIds[0]), ct);
        if (result.IsOk)
        {
            LogScenePushed(result.Value!.Added, result.Value.Monitored);
        }

        return ToMonitorResult(result);
    }

    /// <summary>
    /// Searches now for a single scene. Resolves the scene server-side (like <see cref="SceneAddAsync"/>),
    /// resolves its Whisparr movie by id (<see cref="IWhisparrSceneLookup"/>), and — only when the
    /// scene is already an added movie — issues one <c>MoviesSearch</c> over that movie id. A scene with no
    /// StashDB id is <c>NO_STASHDB_IDENTITY</c>; a scene not yet added to Whisparr is a handled
    /// <c>{ searched:false }</c> (nothing to search). Configure-gated + stored-creds-only; v2 → 400.
    /// This is the ONLY per-scene route that may grab.
    /// </summary>
    internal async Task<IResult> SceneSearchAsync(
        SceneSearchRequest req, WhisparrClient client, CancellationToken ct)
    {
        var (options, baseUrl, apiKey) = await StoredCredsAsync(ct);
        if (V3Only(options, client) is not { } adapter)
        {
            return VersionUnsupported();
        }

        var (video, noIdentity) = await ResolveSceneIdentityAsync(req.CoveId, options, ct);
        if (video is null)
        {
            return noIdentity!; // the resolver returns a refusal for exactly the null-video case
        }

        var lookup = await adapter.FindSceneMovieAsync(baseUrl, apiKey, video.StashIds, ct);
        if (!lookup.IsOk)
        {
            return Results.Json(new ResultDiscriminatorResponse(FailureDiscriminator(lookup.State)), statusCode: 502);
        }

        var movie = lookup.Value;
        if (movie is null)
        {
            // The scene has a StashDB id but is not yet an added Whisparr movie — nothing to search.
            LogSceneSearched(false);
            return Results.Json(new SceneSearchResponse(false), EnumStringResponseJsonOptions);
        }

        var result = await new SceneActions(client, options, EmptyCoveLibraryPort.Instance, CapabilityPort(client))
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
        SceneMonitorRequest req, WhisparrClient client, CancellationToken ct)
    {
        var (options, _, _) = await StoredCredsAsync(ct);
        if (V3Only(options, client) is null)
        {
            return VersionUnsupported();
        }

        var (video, noIdentity) = await ResolveSceneIdentityAsync(req.CoveId, options, ct);
        if (video is null)
        {
            return noIdentity!; // the resolver returns a refusal for exactly the null-video case
        }

        // Same position and reasoning as SceneAddAsync.
        if (RefuseIncompleteConfig(options) is { } refusal)
        {
            return refusal;
        }

        var result = await new SceneActions(client, options, EmptyCoveLibraryPort.Instance, CapabilityPort(client))
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
    /// OWN Cove scenes (enumerated by <see cref="Library.ICoveLibraryPort.LoadVideosForEntityAsync"/> for the forwarded
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
        BulkAddMissingRequest req, WhisparrClient client, CancellationToken ct)
    {
        if (!TryParseEntityKind(req.Kind, out var kind))
        {
            return Results.Json(new ErrorResponse("UNKNOWN_KIND"), statusCode: 400);
        }

        var (options, _, _) = await StoredCredsAsync(ct);
        if (V3Only(options, client) is null)
        {
            return VersionUnsupported();
        }

        // Above the scoped enumeration as well as the seam: every unit of this fan-out is a create, so one
        // incomplete configuration would otherwise fail every scene in the entity after the root read and the tag
        // ensure had fired.
        if (RefuseIncompleteConfig(options) is { } refusal)
        {
            return refusal;
        }

        return await WithScopedLibraryAsync(options.StashDbEndpoint, options.TpdbEndpoint, async library =>
        {
            var result = await new SceneActions(client, options, library, CapabilityPort(client))
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
    /// version's endpoint (<see cref="Options.WhisparrOptions.IdentityEndpoint"/> — the same rule <see cref="MonitorAsync"/>
    /// uses), so a caller cannot point the search at an arbitrary id. No matching endpoint →
    /// <c>NO_STASHDB_IDENTITY</c> (no outbound call). <see cref="SceneActions.SearchAllMonitoredAsync"/> resolves
    /// the entity's monitored attributed ids from the already-fetched set (no StashDB call) and issues one search
    /// command (v3 <c>MoviesSearch</c> / v2 <c>EpisodeSearch</c>) over them.
    /// Configure-gated + stored-creds-only.
    /// </summary>
    internal async Task<IResult> BulkSearchMonitoredAsync(
        BulkSearchMonitoredRequest req, WhisparrClient client, CancellationToken ct)
    {
        if (!TryParseEntityKind(req.Kind, out var kind))
        {
            return Results.Json(new ErrorResponse("UNKNOWN_KIND"), statusCode: 400);
        }

        var (options, _, _) = await StoredCredsAsync(ct);
        if (AdapterSelector.SelectForVersion(options.SelectedVersion, client) is null)
        {
            return VersionUnsupported();
        }

        if (ResolveRemoteId(req.RemoteIds, options) is not { } stashId)
        {
            return Results.Json(new NoIdentityResponse("NO_STASHDB_IDENTITY", ProviderNameFor(options)), EnumStringResponseJsonOptions);
        }

        // A v2 studio routes to the SITE episode search (the sole grab-capable v2 verb); a v2 performer has no
        // attributed set and the adapter defers → a clear 400 via ToMonitorResult, never a 500.
        var result = await new SceneActions(client, options, EmptyCoveLibraryPort.Instance, CapabilityPort(client))
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
        ReflectOwnedRequest req, WhisparrClient client, CancellationToken ct)
    {
        if (!TryParseEntityKind(req.Kind, out var kind))
        {
            return Results.Json(new ErrorResponse("UNKNOWN_KIND"), statusCode: 400);
        }

        var (options, _, _) = await StoredCredsAsync(ct);
        if (AdapterSelector.SelectForVersion(options.SelectedVersion, client) is not IWhisparrOwnedImport)
        {
            // Both v3 and v2 support owned-import (v3 in-place adopt, v2 in-place register); only an unmanageable
            // version has no adapter — it refuses BEFORE resolving the DB scope or reading any Cove scene (wire-free).
            return VersionUnsupported();
        }

        return await WithScopedLibraryAsync(options.StashDbEndpoint, options.TpdbEndpoint, async library =>
        {
            var result = await new SceneActions(client, options, library, CapabilityPort(client))
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
        SceneExclusionRequest req, WhisparrClient client, CancellationToken ct)
    {
        var (options, _, _) = await StoredCredsAsync(ct);
        if (V3Only(options, client) is null)
        {
            return VersionUnsupported();
        }

        var (video, noIdentity) = await ResolveSceneIdentityAsync(req.CoveId, options, ct);
        if (video is null)
        {
            return noIdentity!; // the resolver returns a refusal for exactly the null-video case
        }

        var actions = new SceneActions(client, options, EmptyCoveLibraryPort.Instance, CapabilityPort(client));
        var stashId = video.StashIds[0];
        var result = req.Exclude
            ? await actions.ExcludeSceneAsync(
                stashId, SceneActions.ResolveTitle(video.Title, video.FilePaths, stashId), video.Date?.Year, ct)
            : await actions.UnExcludeSceneAsync(stashId, ct);

        if (result.IsOk)
        {
            LogSceneExclusionToggled(req.Exclude);
            return Results.Json(new SceneExclusionResponse(req.Exclude), EnumStringResponseJsonOptions);
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
        SceneGrabReleaseRequest req, WhisparrClient client, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Guid))
        {
            return Results.Json(new ErrorResponse("MISSING_RELEASE"), statusCode: 400);
        }

        var (options, baseUrl, apiKey) = await StoredCredsAsync(ct);
        if (V3Only(options, client) is not { } adapter)
        {
            return VersionUnsupported();
        }

        var (video, noIdentity) = await ResolveSceneIdentityAsync(req.CoveId, options, ct);
        if (video is null)
        {
            return noIdentity!; // the resolver returns a refusal for exactly the null-video case
        }

        // The interactive-search release row carries movieId:null; without a movieId in the grab body
        // Whisparr answers 404 "Unable to find matching movie". Supply the movie resolved for this scene.
        var lookup = await adapter.FindSceneMovieAsync(baseUrl, apiKey, video.StashIds, ct);
        if (lookup.Value is not { } movie)
        {
            return Results.Json(new NoIdentityResponse("NOT_ADDED", ProviderNameFor(options)), EnumStringResponseJsonOptions);
        }

        var result = await new SceneActions(client, options, EmptyCoveLibraryPort.Instance, CapabilityPort(client))
            .GrabReleaseAsync(req.Guid, req.IndexerId, movie.Id, ct);
        if (result.IsOk)
        {
            LogSceneReleaseGrabbed();
            return Results.Json(new SceneGrabResponse(true), EnumStringResponseJsonOptions);
        }

        return ToMonitorResult(result);
    }

    /// <summary>
    /// Returns the enriched pickable release rows for a scene's interactive picker. Resolves the scene
    /// server-side, resolves its Whisparr movie by id, then reads <c>GetReleasesAsync(movieId)</c> —
    /// a READ that grabs/downloads nothing (loop-safe), invoked only on explicit UI expand. A scene with no
    /// StashDB id is <c>NO_STASHDB_IDENTITY</c> (no outbound call); a not-added scene (no matching movie) returns
    /// an empty list. Configure-gated + stored-creds-only; v2 → 400.
    /// </summary>
    internal async Task<IResult> SceneReleasesListAsync(
        SceneReleasesRequest req, WhisparrClient client, CancellationToken ct)
    {
        var (options, baseUrl, apiKey) = await StoredCredsAsync(ct);
        if (V3Only(options, client) is not { } adapter)
        {
            return VersionUnsupported();
        }

        var (video, noIdentity) = await ResolveSceneIdentityAsync(req.CoveId, options, ct);
        if (video is null)
        {
            return noIdentity!; // the resolver returns a refusal for exactly the null-video case
        }

        var lookup = await adapter.FindSceneMovieAsync(baseUrl, apiKey, video.StashIds, ct);
        if (lookup.Value is not { } movie)
        {
            return Results.Json(new SceneReleasesResponse(Array.Empty<WhisparrRelease>()), EnumStringResponseJsonOptions);
        }

        var releases = await adapter.GetReleasesAsync(baseUrl, apiKey, movie.Id, ct);
        var rows = releases.IsOk ? releases.Value! : [];
        LogSceneReleasesListed(rows.Length);
        return Results.Json(new SceneReleasesResponse(rows), EnumStringResponseJsonOptions);
    }

    /// <summary>
    /// Searches a scene's Whisparr movie for a quality upgrade — a grab-capable verb, honoring the
    /// <see cref="Options.WhisparrOptions.AllowQualityUpgrades"/> setting (off = an Ok no-op that issues no command).
    /// Resolves the scene server-side, finds its movie; a not-added scene is a handled <c>{ searched:false }</c>
    /// (nothing to upgrade). A scene with no StashDB id is <c>NO_STASHDB_IDENTITY</c> (no outbound call).
    /// Configure-gated + stored-creds-only; v2 → 400.
    /// </summary>
    internal async Task<IResult> SceneSearchUpgradesAsync(
        SceneSearchRequest req, WhisparrClient client, CancellationToken ct)
    {
        var (options, baseUrl, apiKey) = await StoredCredsAsync(ct);
        if (V3Only(options, client) is not { } adapter)
        {
            return VersionUnsupported();
        }

        var (video, noIdentity) = await ResolveSceneIdentityAsync(req.CoveId, options, ct);
        if (video is null)
        {
            return noIdentity!; // the resolver returns a refusal for exactly the null-video case
        }

        var lookup = await adapter.FindSceneMovieAsync(baseUrl, apiKey, video.StashIds, ct);
        if (!lookup.IsOk)
        {
            return Results.Json(new ResultDiscriminatorResponse(FailureDiscriminator(lookup.State)), statusCode: 502);
        }

        if (lookup.Value is not { } movie)
        {
            LogSceneUpgradeSearched(false);
            return Results.Json(new SceneSearchResponse(false), EnumStringResponseJsonOptions);
        }

        var result = await new SceneActions(client, options, EmptyCoveLibraryPort.Instance, CapabilityPort(client))
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
    /// make an outbound Whisparr call is a configure operation (the same posture as <see cref="TestConnectionAsync"/>),
    /// so a read-only principal must not reach it. v2 defers to a clear <c>VERSION_UNSUPPORTED</c> 400 (its
    /// Sonarr-shaped config field names diverge). Reads only — no Cove or Whisparr mutation.
    /// </summary>
    internal async Task<IResult> FileSettingsGetAsync(
        WhisparrClient client, CancellationToken ct)
    {
        var (options, baseUrl, apiKey) = await StoredCredsAsync(ct);
        if (AdapterSelector.SelectForVersion(options.SelectedVersion, client) is not { } adapter)
        {
            return VersionUnsupported();
        }

        return ToMonitorResult(adapter is IWhisparrFileSettings fileSettings
            ? await fileSettings.GetFileSettingsAsync(baseUrl, apiKey, ct)
            : WhisparrResult<WhisparrFileSettings>.VersionMismatch(options.SelectedVersion));
    }

    /// <summary>
    /// Writes the four file-affecting Whisparr toggles via the adapter's read-modify-write
    /// (<see cref="IWhisparrFileSettings.EditFileSettingsAsync"/>): GET each config singleton, flip only the whitelisted
    /// booleans <paramref name="req"/> carries, PUT the complete object back so unknown Whisparr fields survive.
    /// The server honors ONLY the four booleans — it never accepts or forwards an arbitrary client config body.
    /// Configure-gated + stored-creds-only (the body carries no url/key, the stored key is never echoed). v2 defers
    /// to a clear <c>VERSION_UNSUPPORTED</c> 400. The one mutation is to the four Whisparr toggles, nothing else.
    /// </summary>
    internal async Task<IResult> FileSettingsWriteAsync(
        WhisparrFileSettingsRequest req, WhisparrClient client, CancellationToken ct)
    {
        var (options, baseUrl, apiKey) = await StoredCredsAsync(ct);
        if (AdapterSelector.SelectForVersion(options.SelectedVersion, client) is not { } adapter)
        {
            return VersionUnsupported();
        }

        return ToMonitorResult(adapter is IWhisparrFileSettings fileSettings
            ? await fileSettings.EditFileSettingsAsync(baseUrl, apiKey, req, ct)
            : WhisparrResult<WhisparrFileSettings>.VersionMismatch(options.SelectedVersion));
    }
}
