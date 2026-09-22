using Cove.Core.Auth;
using Microsoft.AspNetCore.Routing;
using WhisparrSync.Connection;

namespace WhisparrSync;

public sealed partial class WhisparrSync
{
    // Both the endpoint reference and the mapped route come from one base so they cannot drift.
    // Instance members because Id comes from extension.json: reading a route before the host applies
    // the manifest throws instead of mounting the endpoints under the wrong id.
    private string RouteBase => "/api/extensions/" + Id;
    private string HostConfigurationRoute => RouteBase + "/host-configuration";
    private string ConnectionTestRoute => RouteBase + "/connection/test";
    private string SettingsRoute => RouteBase + "/settings";
    private string ImportBannerRoute => RouteBase + "/import/banner";
    private string FolderMappingsRoute => RouteBase + "/addressing/folder-mappings";
    private string MonitoringReadRoute => RouteBase + "/entity/{kind}/{coveId}/monitoring";

    private string ConnectionOfferRoute => RouteBase + "/connection/offer";
    private string MonitorRoute => RouteBase + "/entity/{kind}/{coveId}/monitor";
    private string UnmonitorRoute => RouteBase + "/entity/{kind}/{coveId}/unmonitor";
    private string MonitorScopeRoute => RouteBase + "/entity/{kind}/{coveId}/scope";
    private string ReflectOwnedRoute => RouteBase + "/entity/{kind}/{coveId}/reflect-owned";
    private string AddAllMissingRoute => RouteBase + "/entity/{kind}/{coveId}/add-all-missing";
    private string SearchAllMonitoredRoute =>
        RouteBase + "/entity/{kind}/{coveId}/search-all-monitored";
    private string MissingPageRoute => RouteBase + "/entity/{kind}/{coveId}/missing";
    private string MissingCountRoute => RouteBase + "/entity/{kind}/{coveId}/missing/count";
    private string MissingFacetValuesRoute =>
        RouteBase + "/entity/{kind}/{coveId}/missing/facet/{facetKey}";
    private string MissingTrackRoute => RouteBase + "/entity/{kind}/{coveId}/missing/track";
    private string MissingBulkMonitorRoute =>
        RouteBase + "/entity/{kind}/{coveId}/missing/bulk-monitor";
    private string MissingMonitorAllRoute =>
        RouteBase + "/entity/{kind}/{coveId}/missing/monitor-all";
    private string MissingSceneMonitorRoute =>
        RouteBase + "/entity/{kind}/{coveId}/missing/{providerSceneId}/monitor";
    private string MissingSceneSearchRoute =>
        RouteBase + "/entity/{kind}/{coveId}/missing/{providerSceneId}/search";
    private string SceneDetailRoute => RouteBase + "/scene/{coveId}";
    private string SceneAddRoute => RouteBase + "/scene/{coveId}/add";
    private string SceneMonitorRoute => RouteBase + "/scene/{coveId}/monitor";
    private string SceneUnmonitorRoute => RouteBase + "/scene/{coveId}/unmonitor";
    private string SceneExcludeRoute => RouteBase + "/scene/{coveId}/exclude";
    private string SceneRemoveExclusionRoute => RouteBase + "/scene/{coveId}/remove-exclusion";
    private string SceneSearchRoute => RouteBase + "/scene/{coveId}/search";
    private string SceneBatchRoute => RouteBase + "/scenes/batch";
    private string LibraryStatusRoute => RouteBase + "/library/{kind}/status";
    private string BulkMonitorRoute => RouteBase + "/entities/bulk-monitor";
    private string SyncPreviewRoute => RouteBase + "/sync/preview";
    private string SyncRunRoute => RouteBase + "/sync/run";
    private string JobStatusRoute => RouteBase + "/job-status/{jobId}";

    // Derived from the same builder the registered address is, so the route Whisparr is told to call
    // and the route this extension mounts cannot drift apart.
    private string CallbackRoute => CallbackAddress.RouteFor(Id);
    private string CallbackRegisterRoute => CallbackRoute + "/register";
    private string CallbackStatusRoute => CallbackRoute + "/status";

    /// <summary>Registers every endpoint, each declaring the gate its own handler re-checks.</summary>
    /// <remarks>
    /// The host reads and audits the declaration. The in-handler check stays because the host's
    /// <c>[RequiresPermission]</c> filter is MVC-only and inert on a minimal-API endpoint, so on a
    /// host predating policy enforcement the declaration enforces nothing.
    /// </remarks>
    public override void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        MapSettingsEndpoints(endpoints);
        MapMonitoringEndpoints(endpoints);
        MapReflectOwnedEndpoints(endpoints);
        MapAddAllMissingEndpoints(endpoints);
        MapMonitoringBulkEndpoints(endpoints);
        MapMissingEndpoints(endpoints);
        MapMissingBulkEndpoints(endpoints);
        MapMissingTrackEndpoints(endpoints);
        MapMissingMonitorAllEndpoints(endpoints);
        MapMissingCardEndpoints(endpoints);
        MapSceneEndpoints(endpoints);
        MapSceneBatchEndpoints(endpoints);
        MapLibraryStatusEndpoints(endpoints);
        MapSyncLibraryEndpoints(endpoints);
        MapCallbackEndpoints(endpoints);
    }

    // Stated, not inferred: an inferred tag falls back to the entry assembly for a handler that
    // captures nothing, so it would change with the process that emits the wire document.
    private const string WireTag = "WhisparrSync";

    // One array per tier, read by both the route declaration and the handler check. An endpoint that
    // advertises one gate and enforces another still passes every test that drives the handler.
    private static readonly string[] ReadPermissions = [Permissions.VideosRead];

    // No default Viewer or Member role holds the configure tier, which keeps the connection test out
    // of reach of a caller who could aim it at an internal address.
    private static readonly string[] ConfigurePermissions = [Permissions.ExtensionsConfigure];

    private static bool HasReadPermission(ICurrentPrincipalAccessor principal)
        => principal.Current is { } current && Array.Exists(ReadPermissions, current.Has);

    private static bool HasConfigurePermission(ICurrentPrincipalAccessor principal)
        => principal.Current is { } current && Array.Exists(ConfigurePermissions, current.Has);
}
