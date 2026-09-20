using Cove.Core.Auth;
using Microsoft.AspNetCore.Routing;
using WhisparrSync.Connection;

namespace WhisparrSync;

public sealed partial class WhisparrSync
{
    // The endpoint reference and the mapped route MUST be the same literal, so derive both from one
    // base. Instance members because Id comes from extension.json: reading a route before the host has
    // applied the manifest throws instead of mounting the endpoints under the wrong id.
    private string RouteBase => "/api/extensions/" + Id;
    private string HostConfigurationRoute => RouteBase + "/host-configuration";
    private string ConnectionTestRoute => RouteBase + "/connection/test";
    private string SettingsRoute => RouteBase + "/settings";
    private string ImportBannerRoute => RouteBase + "/import/banner";
    private string FolderMappingsRoute => RouteBase + "/addressing/folder-mappings";
    private string MonitoringReadRoute => RouteBase + "/entity/{kind}/{coveId}/monitoring";
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

    /// <summary>
    /// Registers every endpoint, each DECLARING the gate its own handler re-checks.
    /// </summary>
    /// <remarks>
    /// The declaration is what the host reads and audits; the in-handler check stays because the
    /// host's <c>[RequiresPermission]</c> filter is MVC-only and inert on a minimal-API endpoint, so
    /// the declaration alone enforces nothing on a host predating policy enforcement.
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
        MapMissingMonitorAllEndpoints(endpoints);
        MapMissingCardEndpoints(endpoints);
        MapSceneEndpoints(endpoints);
        MapSceneBatchEndpoints(endpoints);
        MapLibraryStatusEndpoints(endpoints);
        MapSyncLibraryEndpoints(endpoints);
        MapCallbackEndpoints(endpoints);
    }

    /// <summary>The tag every route of this extension carries in the emitted wire document.</summary>
    /// <remarks>
    /// Stated rather than inferred. The inferred tag comes from the handler's declaring type, and
    /// falls back to the ENTRY assembly for a handler that captures nothing — which is whichever
    /// process emitted the document, so an inferred tag moves the committed document the day the test
    /// runner changes.
    /// </remarks>
    private const string WireTag = "WhisparrSync";

    /// <summary>The gates this extension's routes declare, and the ones their handlers re-check.</summary>
    /// <remarks>
    /// ONE array per tier, read by both, because the divergence is what would go unnoticed: an
    /// endpoint advertising one gate to the host while enforcing another still passes every test that
    /// drives the handler directly.
    /// </remarks>
    private static readonly string[] ReadPermissions = [Permissions.VideosRead];

    /// <inheritdoc cref="ReadPermissions"/>
    /// <remarks>
    /// The configure tier. No default Viewer or Member role holds it, which is what keeps the
    /// connection test out of reach of a caller who could otherwise aim it at an internal address.
    /// </remarks>
    private static readonly string[] ConfigurePermissions = [Permissions.ExtensionsConfigure];

    private static bool HasReadPermission(ICurrentPrincipalAccessor principal)
        => principal.Current is { } current && Array.Exists(ReadPermissions, current.Has);

    private static bool HasConfigurePermission(ICurrentPrincipalAccessor principal)
        => principal.Current is { } current && Array.Exists(ConfigurePermissions, current.Has);
}
