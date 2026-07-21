using Cove.Core.Auth;
using Cove.Plugins;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using WhisparrSync.Client;
using WhisparrSync.Ingest;

namespace WhisparrSync;

/// <summary>
/// The Cove-facing surface of the extension: the full-page settings tab (contributed through
/// <see cref="GetUIManifest"/>) and the <c>/test-connection</c> minimal-API endpoint. Stays disjoint
/// from <c>WhisparrSync.cs</c> (identity + DI) and <c>WhisparrSync.Logging.cs</c>.
/// </summary>
public sealed partial class WhisparrSync
{
    // The route prefix mirrors how the host mounts an extension's endpoints: /api/extensions/{id}/…
    private const string RouteBase = "/api/extensions/com.alextomas955.whisparrsync";
    private const string TestConnectionRoute = RouteBase + "/test-connection";
    private const string StatusRoute = RouteBase + "/status";
    private const string OptionsRoute = RouteBase + "/options";
    private const string RootFoldersRoute = RouteBase + "/rootfolders";
    private const string QualityProfilesRoute = RouteBase + "/qualityprofiles";
    private const string WebhookUrlRoute = RouteBase + "/webhook-url";
    private const string RegisterWebhookRoute = RouteBase + "/register-webhook";

    // The ONE anonymous route: inbound Whisparr On-Import events. It carries no Cove principal, so
    // it deliberately OMITS the Forbidden(principal,…) gate every other route uses — the shared-secret token
    // validated inside WebhookReceiver is its auth.
    private const string WebhookRoute = RouteBase + "/webhook";

    // The read-only import-activity log (review half): a pure read of the extension's own audit
    // journal, read-gated (extensions.read) exactly like /reconciliation.
    private const string ImportLogRoute = RouteBase + "/import-log";

    // The read-only root-overlap advisory: compares the Whisparr roots against the Cove library
    // roots and warns when they overlap (a re-grab-loop risk). Read-gated exactly like /reconciliation.
    private const string RootOverlapRoute = RouteBase + "/root-overlap";

    // The read-only reconciliation surface. /preview-sync computes the live diff (configure-gated —
    // it reaches the stored creds to call Whisparr); /reconciliation is a pure match-map read
    // (read-gated); /match/confirm|reject validate a submitted pair against the fresh diff, then write ONLY
    // the extension's own match store (configure-gated).
    private const string PreviewSyncRoute = RouteBase + "/preview-sync";
    private const string ReconciliationRoute = RouteBase + "/reconciliation";
    private const string MatchConfirmRoute = RouteBase + "/match/confirm";
    private const string MatchRejectRoute = RouteBase + "/match/reject";

    // The studio/performer monitor surface. /monitor toggles the monitored state
    // (add-then-flip via EntityMonitor); /monitor-status projects the quiet-status counts. Both are
    // mutating-posture (they reach the stored creds to call Whisparr), so both are configure-gated + stored-
    // creds-only: the request body carries the entity's OWN Cove remoteIds, never a url/key.
    private const string MonitorRoute = RouteBase + "/monitor";
    private const string MonitorStatusRoute = RouteBase + "/monitor-status";

    // The read-only scene Whisparr-status surface. All three are
    // configure-gated + stored-creds-only: the body carries NO url/key, the stored key is never echoed.
    // /scene-status-summary is the toolbar's library-wide 4-state count (GET, no body); /scene-detail projects
    // one scene's Whisparr-owned facts (POST {coveId}) — a read that grabs nothing.
    private const string SceneStatusSummaryRoute = RouteBase + "/scene-status-summary";
    // POST {CoveIds:[...]} → { states } for a visible grid page — one DB read + one Whisparr fetch, not per card.
    private const string SceneStatusBatchRoute = RouteBase + "/scene-status-batch";
    // POST {Kind, CoveEntityIds:[...]} → { states } for a studios/performers page — two Whisparr fetches, not per card.
    private const string EntityStatusBatchRoute = RouteBase + "/entity-status-batch";
    private const string EntityLibrarySummaryRoute = RouteBase + "/entity-library-summary";
    private const string SceneDetailRoute = RouteBase + "/scene-detail";
    // The library-wide identity-health count for the guided-setup banner: total scenes + how many carry no
    // provider id on the connected version. A pure Cove read (no outbound call), read-gated (extensions.read).
    private const string IdentityHealthRoute = RouteBase + "/identity-health";

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

    // Videos bulk route: a configure-gated, v3-only op (add / search / search-upgrades / exclude) over a capped id
    // selection, each id resolved to a scene server-side.
    private const string VideosBatchRoute = RouteBase + "/videos-batch";

    // Fan-out guard: an oversized caller-supplied id array is rejected 400 before any read or outbound call.
    private const int MaxEntityIdsPerRequest = 1000;

    // Dispatched by the host under this name (no ApiEndpoint — the JS handler POSTs /videos-batch itself); MUST
    // equal the JS bundle's actionHandlers key byte-for-byte.
    private const string VideosBatchHandlerName = "whisparrBatchSelected";

    // Studios/performers bulk route (monitor / unmonitor / add-all-missing / search / reflect-owned). NOT v3-only:
    // it defers per version+kind like the per-entity menu (studio on both; performer + add-all-missing v3-only).
    private const string EntitiesBatchRoute = RouteBase + "/entities-batch";
    private const string EntitiesBatchHandlerName = "whisparrEntitiesBatchSelected";

    // A bulk action enqueues a background IJobService job so the Job Drawer (not a window.alert) carries the
    // progress + summary; the queued-success alert is suppressed on the action.
    private const string VideosBatchJobType = "whisparr-videos-batch";
    private const string EntitiesBatchJobType = "whisparr-entities-batch";

    /// <summary>
    /// Contributes the Whisparr Sync settings tab as a full-page layout (like Renamer): the host renders
    /// the tab's panel full-width with no card chrome and the extension owns the canvas. The section's
    /// <c>componentName</c> ("WhisparrSyncPage") MUST equal the key in the bundle's <c>defineExtension</c>
    /// components map (see the UI <c>index.ts</c>).
    /// </summary>
    public override UIManifest GetUIManifest() => BuildManifest(_selectedVersion);

    /// <summary>
    /// Builds the UI manifest for the connected Whisparr <paramref name="selectedVersion"/>. The settings tab
    /// and the studio/performer monitor surfaces are version-neutral; the per-scene / library-video surfaces
    /// (scene detail tab, videos-list status toolbar, "Whisparr" bulk action) are StashDB-keyed and are omitted
    /// on v2 (Sonarr), which has no per-scene identity — those surfaces are host-drawn from the manifest, so
    /// omission is the only way to hide them (a component cannot render a host-drawn tab/action away).
    /// </summary>
    internal UIManifest BuildManifest(string? selectedVersion)
    {
        var builder = ManifestBuilder()
            .AddSettingsTab(
                key: "whisparr-sync",
                label: "Whisparr Sync",
                description: "Connect Cove to your Whisparr instance.",
                order: 100,
                icon: "WhisparrLogo",
                layout: SettingsTabLayout.Page)
            .AddSettingsSection(targetTab: "whisparr-sync", label: "Whisparr Sync", componentName: "WhisparrSyncPage")
            // The monitor button rides the native action-row slot on both entity
            // detail pages; the quiet status line rides the native *-detail-bottom slot. One
            // control per entity (no OverrideComponent / context-menu — both are silent host no-ops).
            // The componentName literals MUST match the UI index.ts component keys byte-for-byte. These stay on
            // both versions — a v2 studio monitors as a SITE; the performer control self-hides on v2 in the JS.
            .AddSlot(slot: "studio-detail-actions", componentName: "WhisparrMonitorButton")
            .AddSlot(slot: "performer-detail-actions", componentName: "WhisparrMonitorButton")
            .AddSlot(slot: "studio-detail-bottom", componentName: "WhisparrStatusLine")
            .AddSlot(slot: "performer-detail-bottom", componentName: "WhisparrStatusLine")
            // Studio library affordances ride BOTH versions — a v2 studio monitors as a SITE (series) matched by
            // ThePornDB, and its count batches off the series-list statistics, just as v3 batches off the studio
            // list. The pill gates the row + card badge (shared on/off state); the host contains each slot so a bad
            // extension can't break the card. Performers + scenes are v3-only (added in the v3 block below).
            .AddSlot(slot: "studios-list-toolbar-end", componentName: "WhisparrLibraryToggle")
            .AddSlot(slot: "studios-list-row", componentName: "WhisparrEntityLibraryRow")
            .AddSlot(slot: "studio-card-footer", componentName: "WhisparrEntityCardBadge");

        // v2 (Sonarr) scenes are episodes with no StashDB identity, and v2 has no performer entity: the per-scene
        // status surface, the videos/performers library affordances, and the per-scene bulk action have no v2
        // meaning (they defer or mislead). Being host-drawn from the manifest, they are hidden by omission here.
        if (!string.Equals(selectedVersion, "v2", StringComparison.OrdinalIgnoreCase))
        {
            builder
                // The read-only scene Whisparr-status surface. The per-scene panel rides the
                // ONLY native per-video surface — the detail-rail TAB (the video page exposes no
                // *-detail-bottom slot) — and the library affordance rides the native videos toolbar slot.
                // OverrideComponent("video.card") and actionType:"context-menu" are silent host no-ops; per-scene
                // status instead renders in the card CONTENT area (video-card-content), the detail-rail tab, and
                // the reconciliation column. Every componentName MUST equal the UI index.ts key byte-for-byte.
                // icon is a bare name the host resolves to a built-in icon, else a component the bundle
                // registered under that name: "WhisparrLogo" is our own brand mark (registered in index.ts),
                // rendered as the tab icon inheriting the host's currentColor like a native icon.
                .AddTab(pageType: "video", key: "whisparr", label: "Whisparr", componentName: "WhisparrScenePanel", icon: "WhisparrLogo")
                // The videos + performers "show Whisparr status" pills (the studios pill is registered above, on both
                // versions); shared on/off state. On the videos list the pill also reveals the per-state count row.
                .AddSlot(slot: "videos-list-toolbar-end", componentName: "WhisparrLibraryToggle")
                .AddSlot(slot: "performers-list-toolbar-end", componentName: "WhisparrLibraryToggle")
                // The count row on its OWN row below each list's toolbar: scenes-by-state on videos, monitored-of-total
                // on performers. Shares the pill's on/off state; the host renders it only when filled.
                .AddSlot(slot: "videos-list-row", componentName: "WhisparrLibraryRow")
                .AddSlot(slot: "performers-list-row", componentName: "WhisparrEntityLibraryRow")
                // Per-card badges, gated by the pill. The host contains each slot so a bad extension can't break the card.
                .AddSlot(slot: "video-card-content", componentName: "WhisparrCardBadge")
                .AddSlot(slot: "performer-card-footer", componentName: "WhisparrEntityCardBadge")
                // The videos selection-bar "Whisparr" action: no ApiEndpoint, dispatched by HandlerName
                // (byte-for-byte with the JS bundle) which presents the op submenu then POSTs /videos-batch.
                // requiredPermission is a UI affordance only — /videos-batch re-checks extensions.configure.
                .AddAction(
                    id: "whisparr-batch-video",
                    label: "Whisparr",
                    actionType: "bulk",
                    entityTypes: ["video"],
                    icon: "WhisparrLogo",
                    apiEndpoint: null,
                    handlerName: VideosBatchHandlerName,
                    order: 100,
                    requiredPermission: Permissions.VideosWrite,
                    suppressSuccessAlert: true) // job feedback via the Job Drawer, so suppress the queued alert
                                                // entityTypes uses the host's PLURAL list keys: ExtensionSelectionActions normalizes only
                                                // video/image to singular, so studios/performers must pass through as-is. Configure-gated (it calls
                                                // Whisparr with stored creds).
                .AddAction(
                    id: "whisparr-batch-entity",
                    label: "Whisparr",
                    actionType: "bulk",
                    entityTypes: ["studios", "performers"],
                    icon: "WhisparrLogo",
                    apiEndpoint: null,
                    handlerName: EntitiesBatchHandlerName,
                    order: 100,
                    requiredPermission: Permissions.ExtensionsConfigure,
                    suppressSuccessAlert: true);
        }

        return builder.WithJsBundle("index.mjs").Build();
    }

    /// <summary>
    /// Maps the settings endpoints. The lambda immediately delegates to an extracted instance method so
    /// the handler is unit-testable without an HTTP host. The host resolves the typed
    /// <see cref="WhisparrClient"/> and <see cref="ICurrentPrincipalAccessor"/> from the request scope.
    /// </summary>
    public override void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(TestConnectionRoute,
            (TestConnectionRequest req, WhisparrClient client, ICurrentPrincipalAccessor principal, CancellationToken ct)
                => TestConnectionAsync(req, client, principal, ct));

        endpoints.MapGet(StatusRoute,
            (ICurrentPrincipalAccessor principal, CancellationToken ct) => StatusAsync(principal, ct));

        endpoints.MapGet(OptionsRoute,
            (ICurrentPrincipalAccessor principal, CancellationToken ct) => GetOptionsAsync(principal, ct));

        endpoints.MapPost(OptionsRoute,
            (OptionsSaveRequest req, ICurrentPrincipalAccessor principal, CancellationToken ct)
                => SaveOptionsAsync(req, principal, ct));

        // Root-folder / quality-profile lists are POSTs carrying the connect creds in the BODY (never a
        // query string): a just-tested (unsaved) key populates the dropdowns immediately, and an
        // empty submitted key on reload reuses the stored key ONLY against the stored host (the stored key
        // is never sent to a caller-supplied foreign host). These require extensions.configure.
        endpoints.MapPost(RootFoldersRoute,
            (TestConnectionRequest req, WhisparrClient client, ICurrentPrincipalAccessor principal, CancellationToken ct)
                => ListRootFoldersAsync(req, client, principal, ct));

        endpoints.MapPost(QualityProfilesRoute,
            (TestConnectionRequest req, WhisparrClient client, ICurrentPrincipalAccessor principal, CancellationToken ct)
                => ListQualityProfilesAsync(req, client, principal, ct));

        // The Cove host base for the webhook URL is derived from the inbound request (scheme + host) — the
        // extension backend has no other authoritative view of its own public address.
        endpoints.MapGet(WebhookUrlRoute,
            (HttpRequest http, ICurrentPrincipalAccessor principal, CancellationToken ct)
                => WebhookUrlAsync($"{http.Scheme}://{http.Host}", principal, ct));

        endpoints.MapPost(RegisterWebhookRoute,
            (WebhookRegisterRequest? req, HttpRequest http, WhisparrClient client, ICurrentPrincipalAccessor principal, CancellationToken ct)
                => RegisterWebhookAsync($"{http.Scheme}://{http.Host}", client, principal, ct, req?.Url));

        // Preview-sync reads Cove + Whisparr and returns the zero-mutation diff; it takes no body (it uses the
        // stored creds only). Confirm/reject carry the {coveId, whisparrMovieId} pair in the body.
        endpoints.MapPost(PreviewSyncRoute,
            (WhisparrClient client, ICurrentPrincipalAccessor principal, CancellationToken ct)
                => PreviewSyncAsync(client, principal, ct));

        endpoints.MapGet(ReconciliationRoute,
            (ICurrentPrincipalAccessor principal, CancellationToken ct) => ReconciliationAsync(principal, ct));

        endpoints.MapPost(MatchConfirmRoute,
            (MatchDecisionRequest req, WhisparrClient client, ICurrentPrincipalAccessor principal, CancellationToken ct)
                => MatchConfirmAsync(req, client, principal, ct));

        endpoints.MapPost(MatchRejectRoute,
            (MatchDecisionRequest req, WhisparrClient client, ICurrentPrincipalAccessor principal, CancellationToken ct)
                => MatchRejectAsync(req, client, principal, ct));

        // Anonymous inbound webhook: bind the raw HttpContext so the receiver reads the token
        // (X-Cove-Token header preferred; ?token= query is a documented fallback) and body itself. NO principal
        // parameter, NO Forbidden gate — the token is the auth. The coordinator resolves the scoped IScanService
        // from the captured factory and gates every ingest on the cached Whisparr root set (fail-closed
        // when roots are unavailable). WarnQueryTokenChannelOnce logs a one-time warning if the insecure
        // query-token channel is used.
        endpoints.MapPost(WebhookRoute,
            (HttpContext http, WhisparrClient client, CancellationToken ct)
                => new WebhookReceiver(
                        Store,
                        new IngestCoordinator(ScopeFactory, c => GetWhisparrRootsAsync(client, c)),
                        WarnQueryTokenChannelOnce)
                    .HandleAsync(http, ct));

        // Read-only audit log: a pure store read, 403-first on extensions.read.
        endpoints.MapGet(ImportLogRoute,
            (ICurrentPrincipalAccessor principal, CancellationToken ct) => ImportLogAsync(principal, ct));

        // Read-only root-overlap advisory: reads the Whisparr roots (stored creds) + the Cove
        // library roots and reports any overlap. 403-first on extensions.read.
        endpoints.MapGet(RootOverlapRoute,
            (WhisparrClient client, ICurrentPrincipalAccessor principal, CancellationToken ct)
                => RootOverlapAsync(client, principal, ct));

        // Studio/performer monitor toggle + status. Both POST (they carry the entity's
        // Cove remoteIds in the body) and delegate to an extracted instance handler so they are unit-testable
        // host-free. Stored creds only: the body carries NO url/key.
        endpoints.MapPost(MonitorRoute,
            (MonitorRequest req, WhisparrClient client, ICurrentPrincipalAccessor principal, CancellationToken ct)
                => MonitorAsync(req, client, principal, ct));

        endpoints.MapPost(MonitorStatusRoute,
            (MonitorStatusRequest req, WhisparrClient client, ICurrentPrincipalAccessor principal, CancellationToken ct)
                => MonitorStatusAsync(req, client, principal, ct));

        // The read-only scene Whisparr-status endpoints. Summary is a bodiless GET; detail + releases
        // POST only the Cove entity id (never a url/key). Each lambda delegates immediately to an
        // extracted instance handler so it is unit-testable without an HTTP host.
        endpoints.MapGet(SceneStatusSummaryRoute,
            (WhisparrClient client, ICurrentPrincipalAccessor principal, CancellationToken ct)
                => SceneStatusSummaryAsync(client, principal, ct));

        endpoints.MapPost(SceneStatusBatchRoute,
            (SceneStatusBatchRequest req, WhisparrClient client, ICurrentPrincipalAccessor principal, CancellationToken ct)
                => SceneStatusBatchAsync(req, client, principal, ct));

        endpoints.MapPost(EntityStatusBatchRoute,
            (EntityStatusBatchRequest req, WhisparrClient client, ICurrentPrincipalAccessor principal, CancellationToken ct)
                => EntityStatusBatchAsync(req, client, principal, ct));

        endpoints.MapGet(EntityLibrarySummaryRoute,
            (string? kind, WhisparrClient client, ICurrentPrincipalAccessor principal, CancellationToken ct)
                => EntityLibrarySummaryAsync(kind, client, principal, ct));

        endpoints.MapPost(SceneDetailRoute,
            (SceneDetailRequest req, WhisparrClient client, ICurrentPrincipalAccessor principal, CancellationToken ct)
                => SceneDetailAsync(req, client, principal, ct));

        // The library-wide identity-health count: a bodiless GET, read-gated, a pure Cove read under System
        // principal (no outbound Whisparr call).
        endpoints.MapGet(IdentityHealthRoute,
            (ICurrentPrincipalAccessor principal, CancellationToken ct) => IdentityHealthAsync(principal, ct));

        // The scene push/search/bulk mutations. Per-scene routes POST only the Cove entity id (identity
        // resolved server-side); bulk-add-missing POSTs the entity kind + Cove id (the local-diff enumeration
        // source); bulk-search-monitored POSTs the entity kind + its Cove remoteIds (server-side stashId match).
        // None carries a url/key — the handler uses the stored creds only. Each delegates immediately to
        // an extracted instance handler so it is unit-testable without an HTTP host.
        endpoints.MapPost(SceneAddRoute,
            (SceneAddRequest req, WhisparrClient client, ICurrentPrincipalAccessor principal, CancellationToken ct)
                => SceneAddAsync(req, client, principal, ct));

        endpoints.MapPost(SceneSearchRoute,
            (SceneSearchRequest req, WhisparrClient client, ICurrentPrincipalAccessor principal, CancellationToken ct)
                => SceneSearchAsync(req, client, principal, ct));

        endpoints.MapPost(SceneMonitorRoute,
            (SceneMonitorRequest req, WhisparrClient client, ICurrentPrincipalAccessor principal, CancellationToken ct)
                => SceneMonitorAsync(req, client, principal, ct));

        endpoints.MapPost(BulkAddMissingRoute,
            (BulkAddMissingRequest req, WhisparrClient client, ICurrentPrincipalAccessor principal, CancellationToken ct)
                => BulkAddMissingAsync(req, client, principal, ct));

        endpoints.MapPost(BulkSearchMonitoredRoute,
            (BulkSearchMonitoredRequest req, WhisparrClient client, ICurrentPrincipalAccessor principal, CancellationToken ct)
                => BulkSearchMonitoredAsync(req, client, principal, ct));

        endpoints.MapPost(ReflectOwnedRoute,
            (ReflectOwnedRequest req, WhisparrClient client, ICurrentPrincipalAccessor principal, CancellationToken ct)
                => ReflectOwnedAsync(req, client, principal, ct));

        // The exclusion / interactive-grab / upgrade endpoints. Each POSTs only the scene's Cove id (plus,
        // for grab, the picked release's guid + indexerId — release handles the picker got from the server's own
        // releases-list read); the scene identity is resolved server-side. None carries a url/key. Each
        // delegates immediately to an extracted instance handler so it is unit-testable without an HTTP host.
        endpoints.MapPost(SceneExclusionRoute,
            (SceneExclusionRequest req, WhisparrClient client, ICurrentPrincipalAccessor principal, CancellationToken ct)
                => SceneExclusionAsync(req, client, principal, ct));

        endpoints.MapPost(SceneGrabReleaseRoute,
            (SceneGrabReleaseRequest req, WhisparrClient client, ICurrentPrincipalAccessor principal, CancellationToken ct)
                => SceneGrabReleaseAsync(req, client, principal, ct));

        endpoints.MapPost(SceneReleasesListRoute,
            (SceneReleasesRequest req, WhisparrClient client, ICurrentPrincipalAccessor principal, CancellationToken ct)
                => SceneReleasesListAsync(req, client, principal, ct));

        endpoints.MapPost(SceneSearchUpgradesRoute,
            (SceneSearchRequest req, WhisparrClient client, ICurrentPrincipalAccessor principal, CancellationToken ct)
                => SceneSearchUpgradesAsync(req, client, principal, ct));

        // The videos-list batch route: POSTs {Op, CoveIds} — the op runs over the capped
        // selection, each id resolved to a scene server-side. Configure-gated + stored-creds-only + v3-only.
        endpoints.MapPost(VideosBatchRoute,
            (VideosBatchRequest req, WhisparrClient client, ICurrentPrincipalAccessor principal, CancellationToken ct)
                => VideosBatchAsync(req, client, principal, ct));

        endpoints.MapPost(EntitiesBatchRoute,
            (EntitiesBatchRequest req, WhisparrClient client, ICurrentPrincipalAccessor principal, CancellationToken ct)
                => EntitiesBatchAsync(req, client, principal, ct));

        // The Whisparr file-settings read (GET) + write (POST). Neither carries a url/key — the handler uses the
        // stored creds only; the write honors ONLY the four whitelisted booleans (read-modify-write in the adapter).
        endpoints.MapGet(FileSettingsRoute,
            (WhisparrClient client, ICurrentPrincipalAccessor principal, CancellationToken ct)
                => FileSettingsGetAsync(client, principal, ct));

        endpoints.MapPost(FileSettingsRoute,
            (WhisparrFileSettingsRequest req, WhisparrClient client, ICurrentPrincipalAccessor principal, CancellationToken ct)
                => FileSettingsWriteAsync(req, client, principal, ct));
    }
}
