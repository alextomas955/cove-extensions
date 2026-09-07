using Cove.Core.Auth;
using Cove.Plugins;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using WhisparrSync.Client;
using WhisparrSync.Contracts;
using static WhisparrSync.Contracts.WireSerializers;

namespace WhisparrSync;

/// <summary>
/// The Cove-facing surface of the extension: the full-page settings tab (contributed through
/// <see cref="GetUIManifest"/>) and the composition root that mounts every per-slice endpoint set. Stays
/// disjoint from <c>WhisparrSync.cs</c> (identity + DI) and <c>WhisparrSync.Logging.cs</c>. The per-capability
/// endpoint bodies live in each slice's <c>*Endpoints.cs</c>; <see cref="MapEndpoints"/> only composes them.
/// </summary>
public sealed partial class WhisparrSync
{
    // The route prefix mirrors how the host mounts an extension's endpoints: /api/extensions/{id}/…
    // Each slice's *Endpoints.cs owns its own route strings off this shared base.
    private const string RouteBase = "/api/extensions/com.alextomas955.whisparrsync";

    // Fan-out guard: an oversized caller-supplied id array is rejected 400 before any read or outbound call.
    // Shared by the videos/entities batch + the per-page status-batch surfaces.
    private const int MaxEntityIdsPerRequest = 1000;

    // Dispatched by the host under this name (no ApiEndpoint — the JS handler POSTs /videos-batch itself); MUST
    // equal the JS bundle's actionHandlers key byte-for-byte. Consumed by BuildManifest below.
    private const string VideosBatchHandlerName = "whisparrBatchSelected";

    // Studios/performers bulk action dispatch name — likewise byte-for-byte with the JS bundle. Consumed by BuildManifest.
    private const string EntitiesBatchHandlerName = "whisparrEntitiesBatchSelected";

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
            .AddSlot(slot: "studio-card-footer", componentName: "WhisparrEntityCardBadge")
            // The per-entity "Missing" tab on the studio, performer + tag detail pages. All version-neutral:
            // discovery reads the metadata source directly (StashDB on v3, ThePornDB on v2), which serves all
            // three axes, so none of these sit in the v2-omission block below. The host draws the count badge from
            // countEndpoint (substituting {entityId}) and renders componentName — the literals MUST match the UI
            // index.ts component keys byte-for-byte. Kind is fixed per contribution in the count endpoint (never
            // client-derived).
            .AddTab(
                pageType: "studio", key: "whisparr-missing", label: "Missing",
                componentName: "WhisparrStudioMissingTab", icon: "WhisparrLogo",
                countEndpoint: RouteBase + "/discovery/count?kind=studio&entityId={entityId}")
            .AddTab(
                pageType: "performer", key: "whisparr-missing", label: "Missing",
                componentName: "WhisparrPerformerMissingTab", icon: "WhisparrLogo",
                countEndpoint: RouteBase + "/discovery/count?kind=performer&entityId={entityId}")
            .AddTab(
                pageType: "tag", key: "whisparr-missing", label: "Missing",
                componentName: "WhisparrTagMissingTab", icon: "WhisparrLogo",
                countEndpoint: RouteBase + "/discovery/count?kind=tag&entityId={entityId}")
            // The global wanted/queue/history surface: a nested settings SUB-PAGE under the Whisparr Sync tab,
            // separate from the connection panel and the owned view. Version-neutral — History reads uniformly
            // on v3 + v2. The icon is a host-drawn lucide-style icon NAME
            // (not a bundle component), so it must be a real lucide glyph name. componentName MUST equal the UI
            // index.ts key byte-for-byte.
            .AddSettingsTab(
                key: "whisparr-discovery",
                label: "Wanted, queue & history",
                layout: SettingsTabLayout.Page,
                order: 110,
                icon: "ListChecks",
                parentTabKey: "whisparr-sync")
            .AddSettingsSection(
                targetTab: "whisparr-discovery",
                label: "Wanted, queue & history",
                componentName: "WhisparrDiscoveryPage");

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
                // status instead renders in the card CONTENT area (video-card-content) and the detail-rail
                // tab. Every componentName MUST equal the UI index.ts key byte-for-byte.
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
    /// The single route-registration surface: composes each per-slice endpoint set. Every route declares its
    /// access tier through <see cref="RouteGates"/>, which the host enforces in middleware before the extension
    /// scope exists — no handler re-checks the principal.
    /// </summary>
    public override void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        // Each per-capability slice owns its own route strings + handler bodies in its *Endpoints.cs; this is
        // purely their composition. Registration order is irrelevant (routes are matched by path, not by order).
        MapConnectionEndpoints(endpoints);
        MapIngestEndpoints(endpoints);
        MapMonitorEndpoints(endpoints);
        MapStatusEndpoints(endpoints);
        MapDiscoveryEndpoints(endpoints);
        MapDiscoveryActionEndpoints(endpoints);
        MapActivityEndpoints(endpoints);
        MapPushEndpoints(endpoints);
        MapBatchEndpoints(endpoints);
        MapSyncEndpoints(endpoints);
    }

    /// <summary>
    /// Maps a monitor / status result to the wire response: <c>Ok</c> serializes the value (camelCase); a v2
    /// deferral (<see cref="WhisparrResultState.VersionMismatch"/>) returns a clear <c>400</c>
    /// <c>VERSION_UNSUPPORTED</c> (never a 500 — Whisparr v2 is unsupported); an add Whisparr's import
    /// exclusions cover returns a 200 <c>EXCLUDED_IN_WHISPARR</c> (a skip, not an error); any other non-Ok
    /// maps to a <c>502</c> with the failure discriminator (never leaking the key or a raw reason).
    /// </summary>
    private static IResult ToMonitorResult<T>(WhisparrResult<T> result) => result.State switch
    {
        WhisparrResultState.Ok => Results.Json(result.Value, EnumStringResponseJsonOptions),
        WhisparrResultState.VersionMismatch => Results.Json(
            new VersionUnsupportedResponse("VERSION_UNSUPPORTED", result.DetectedVersion), statusCode: 400),
        // Rejected carries Whisparr's own error text (safe to surface — not the key/URL), so pass it through.
        WhisparrResultState.Rejected => Results.Json(
            new RejectedResponse("rejected", result.Reason), statusCode: 502),
        // An add the user's own Whisparr import exclusions cover is a handled outcome, not an error — the
        // same 200-with-a-discriminator shape NO_STASHDB_IDENTITY uses.
        WhisparrResultState.Excluded => Results.Json(
            new ExcludedResponse("EXCLUDED_IN_WHISPARR", result.Reason), EnumStringResponseJsonOptions),
        _ => Results.Json(new ResultDiscriminatorResponse(FailureDiscriminator(result.State)), statusCode: 502),
    };
}
