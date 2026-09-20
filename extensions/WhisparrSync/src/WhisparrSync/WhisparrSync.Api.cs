using Cove.Core.Auth;
using Cove.Core.Interfaces;
using Cove.Sdk;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using WhisparrSync.Addressing;
using WhisparrSync.Connection;
using WhisparrSync.Contracts;
using WhisparrSync.Import;
using WhisparrSync.Library;
using WhisparrSync.Missing;
using WhisparrSync.Monitoring;
using WhisparrSync.Options;
using WhisparrSync.Providers;
using WhisparrSync.Whisparr;

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

        endpoints.MapGet(HostConfigurationRoute,
            (ICurrentPrincipalAccessor principal) => HostConfiguration(principal))
            .WithTags(WireTag)
            .RequireCovePermission(PermissionMode.Any, ReadPermissions);

        endpoints.MapPost(ConnectionTestRoute,
            (ConnectionTestRequest request, ICurrentPrincipalAccessor principal,
             IConnectionTestRunner runner, CancellationToken ct)
                => ConnectionTestAsync(request, principal, runner, ct))
            .WithTags(WireTag)
            .RequireCovePermission(PermissionMode.Any, ConfigurePermissions);

        endpoints.MapGet(SettingsRoute,
            (ICurrentPrincipalAccessor principal, OptionsStore options, ICredentialPort credentials,
             CancellationToken ct)
                => ReadSettingsAsync(principal, options, credentials, ct))
            .WithTags(WireTag)
            .RequireCovePermission(PermissionMode.Any, ConfigurePermissions);

        endpoints.MapPut(SettingsRoute,
            (WhisparrSyncSettingsSaveRequest request, ICurrentPrincipalAccessor principal,
             OptionsStore options, OptionsWriteGate gate, ICredentialPort credentials,
             TimeProvider clock, CancellationToken ct)
                => SaveSettingsAsync(request, principal, options, gate, credentials, clock, ct))
            .WithTags(WireTag)
            .RequireCovePermission(PermissionMode.Any, ConfigurePermissions);

        endpoints.MapGet(ImportBannerRoute,
            (ICurrentPrincipalAccessor principal, OptionsStore options, CancellationToken ct)
                => ReadImportBannerAsync(principal, options, ct))
            .WithTags(WireTag)
            .RequireCovePermission(PermissionMode.Any, ConfigurePermissions);

        // The same tier as the settings routes, and for the same reason: both read and write stored
        // configuration, and the save aims this extension's stored credential at a third party.
        endpoints.MapGet(FolderMappingsRoute,
            (ICurrentPrincipalAccessor principal, OptionsStore options, CancellationToken ct)
                => ReadFolderMappingsAsync(principal, options, ct))
            .WithTags(WireTag)
            .RequireCovePermission(PermissionMode.Any, ConfigurePermissions);

        endpoints.MapPut(FolderMappingsRoute,
            (FolderMappingSaveRequest request, ICurrentPrincipalAccessor principal,
             OptionsStore options, OptionsWriteGate gate, ICredentialPort credentials,
             IWhisparrClient client, ICoveLibraryPort library, IFolderAddressPort addressing,
             CancellationToken ct)
                => SaveFolderMappingAsync(
                    request, principal, options, gate, credentials, client, library, addressing, ct))
            .WithTags(WireTag)
            .RequireCovePermission(PermissionMode.Any, ConfigurePermissions);

        endpoints.MapGet(MonitoringReadRoute,
            (string kind, int coveId, ICurrentPrincipalAccessor principal, OptionsStore options,
             ICredentialPort credentials, IWhisparrClient client, IEntityIdentityPort identities,
             CancellationToken ct)
                => ReadEntityMonitoringAsync(
                    kind, coveId, principal, options, credentials, client, identities, _log, ct))
            .WithTags(WireTag)
            .RequireCovePermission(PermissionMode.Any, ReadPermissions);

        endpoints.MapPost(MonitorRoute,
            (string kind, int coveId, MonitorEntityRequest request,
             ICurrentPrincipalAccessor principal, OptionsStore options, ICredentialPort credentials,
             IWhisparrClient client, IEntityIdentityPort identities, IJobService jobs,
             IServiceScopeFactory scopes, CancellationToken ct)
                => MonitorEntityAsync(
                    kind, coveId, request, principal, options, credentials, client, identities, jobs,
                    scopes, _log, ct))
            .WithTags(WireTag)
            .RequireCovePermission(PermissionMode.Any, ConfigurePermissions);

        // The same tier as the monitor route, and for the reason that route's own remark gives: it
        // aims this extension's stored credential at a third party. Its reach is the one Cove entity
        // the route segment names, so it is neither a whole-library read nor a body-named
        // no-content call, and neither lesser tier expresses it.
        endpoints.MapPost(ReflectOwnedRoute,
            (string kind, int coveId, ICurrentPrincipalAccessor principal, OptionsStore options,
             ICredentialPort credentials, IWhisparrClient client, IEntityIdentityPort identities,
             IJobService jobs, IServiceScopeFactory scopes, CancellationToken ct)
                => ReflectOwnedEntityAsync(
                    kind, coveId, principal, options, credentials, client, identities, jobs, scopes, ct))
            .WithTags(WireTag)
            .RequireCovePermission(PermissionMode.Any, ConfigurePermissions);

        // The configure tier, and the reach decision is this route's own. Its reach is one Cove
        // entity's own catalogue, named by the route segment, so it is neither a whole-library verb
        // nor a body-named one. The tier is the configure tier because the route aims this
        // extension's stored credential at a third party AND creates items in the reader's own
        // Whisparr, which is not something a caller who cannot configure the extension may do.
        endpoints.MapPost(AddAllMissingRoute,
            (string kind, int coveId, ICurrentPrincipalAccessor principal, OptionsStore options,
             ICredentialPort credentials, IWhisparrClient client, IEntityIdentityPort identities,
             IJobService jobs, IServiceScopeFactory scopes, CancellationToken ct)
                => AddAllMissingEntityAsync(
                    kind, coveId, principal, options, credentials, client, identities, jobs, scopes, ct))
            .WithTags(WireTag)
            .RequireCovePermission(PermissionMode.Any, ConfigurePermissions);

        // The same tier as the monitor route, and for the same reason: each aims this extension's
        // stored credential at a third party.
        endpoints.MapPost(UnmonitorRoute,
            (string kind, int coveId, ICurrentPrincipalAccessor principal, OptionsStore options,
             ICredentialPort credentials, IWhisparrClient client, IEntityIdentityPort identities,
             CancellationToken ct)
                => UnmonitorEntityAsync(
                    kind, coveId, principal, options, credentials, client, identities, _log, ct))
            .WithTags(WireTag)
            .RequireCovePermission(PermissionMode.Any, ConfigurePermissions);

        // The configure tier, and the reach decision is this route's own rather than the monitor
        // route's borrowed. Its reach is one Cove entity named by the route segment and its effect is
        // bounded by what that entity already monitors, so it is neither a whole-library verb nor a
        // body-named one. The tier is the configure tier for two reasons rather than one: the route
        // aims this extension's stored credential at a third party AND it spends the reader's
        // bandwidth and disk. It is the most consequential route this extension mounts, and it must
        // not sit at a tier a caller who cannot configure the extension can reach.
        endpoints.MapPost(SearchAllMonitoredRoute,
            (string kind, int coveId, ICurrentPrincipalAccessor principal, OptionsStore options,
             ICredentialPort credentials, IWhisparrClient client, IEntityIdentityPort identities,
             CancellationToken ct)
                => SearchAllMonitoredEntityAsync(
                    kind, coveId, principal, options, credentials, client, identities, _log, ct))
            .WithTags(WireTag)
            .RequireCovePermission(PermissionMode.Any, ConfigurePermissions);

        endpoints.MapPost(MonitorScopeRoute,
            (string kind, int coveId, MonitorEntityRequest request,
             ICurrentPrincipalAccessor principal, OptionsStore options, ICredentialPort credentials,
             IWhisparrClient client, IEntityIdentityPort identities, CancellationToken ct)
                => SetMonitorScopeAsync(
                    kind, coveId, request, principal, options, credentials, client, identities, _log, ct))
            .WithTags(WireTag)
            .RequireCovePermission(PermissionMode.Any, ConfigurePermissions);

        // The same tier again: one gesture aiming this extension's stored credential at a third party
        // for every entity in a selection is not a lesser act than doing it for one.
        endpoints.MapPost(BulkMonitorRoute,
            (MonitorBulkRequest request, ICurrentPrincipalAccessor principal, IJobService jobs,
             IServiceScopeFactory scopes)
                => BulkMonitorEnqueue(request, principal, jobs, scopes))
            .WithTags(WireTag)
            .RequireCovePermission(PermissionMode.Any, ConfigurePermissions);

        // Entity-scoped reads: the reach of each is the one Cove entity the route segment names, so
        // the read tier expresses it.
        endpoints.MapGet(MissingPageRoute,
            (string kind, int coveId, int? page, int? perPage, string? sort, string? q,
             string? filters, bool? menusHeld, ICurrentPrincipalAccessor principal,
             OptionsStore options, ICredentialPort credentials, IWhisparrClient client,
             ProviderEndpointPort endpoints, MissingPagePlanner planner, CancellationToken ct)
                => ReadMissingPageAsync(
                    kind, coveId, page, perPage, sort, q, filters, menusHeld, principal, options,
                    credentials, client, endpoints, planner, _log, ct))
            .WithTags(WireTag)
            .RequireCovePermission(PermissionMode.Any, ReadPermissions);

        // The read tier. This route names its cards in the body, the way the bulk route names its
        // scenes, so its reach is the set the caller sent and never the library. It composes no
        // write, and a caller who may see the library may see a read-only status over it.
        endpoints.MapPost(LibraryStatusRoute,
            (string kind, LibraryStatusRequest request, ICurrentPrincipalAccessor principal,
             OptionsStore options, ICredentialPort credentials, IWhisparrClient client,
             LibraryStatusPort cards, ILibraryCardIdentityPort sceneCards, CancellationToken ct)
                => ReadLibraryStatusAsync(
                    kind, request, principal, options, credentials, client, cards, sceneCards, ct))
            .WithTags(WireTag)
            .RequireCovePermission(PermissionMode.Any, ReadPermissions);

        // The read tier, not the configure one: the route names one scene as a path segment and
        // composes no write, so a caller who may see the library may read what Whisparr holds for a
        // scene in it.
        endpoints.MapGet(SceneDetailRoute,
            (int coveId, ICurrentPrincipalAccessor principal, OptionsStore options,
             ICredentialPort credentials, IWhisparrClient client,
             ILibraryCardIdentityPort sceneCards, CancellationToken ct)
                => SceneDetailAsync(
                    coveId, principal, options, credentials, client, sceneCards, _log, ct))
            .WithTags(WireTag)
            .RequireCovePermission(PermissionMode.Any, ReadPermissions);

        // The configure tier for each of the five: one gesture aiming this extension's stored
        // credential at a third party, creating or removing items in the reader's own Whisparr, is
        // not something a caller who cannot configure the extension may do. Which scene a request
        // touches is a path segment, so a caller cannot name one in a body the route would otherwise
        // have to refuse.
        endpoints.MapPost(SceneAddRoute,
            (int coveId, ICurrentPrincipalAccessor principal, OptionsStore options,
             ICredentialPort credentials, IWhisparrClient client,
             ILibraryCardIdentityPort sceneCards, IServiceScopeFactory scopes,
             CancellationToken ct)
                => AddSceneAsync(
                    coveId, principal, options, credentials, client, sceneCards, scopes, _log, ct))
            .WithTags(WireTag)
            .RequireCovePermission(PermissionMode.Any, ConfigurePermissions);

        endpoints.MapPost(SceneMonitorRoute,
            (int coveId, ICurrentPrincipalAccessor principal, OptionsStore options,
             ICredentialPort credentials, IWhisparrClient client,
             ILibraryCardIdentityPort sceneCards, CancellationToken ct)
                => MonitorSceneAsync(
                    coveId, principal, options, credentials, client, sceneCards, _log, ct))
            .WithTags(WireTag)
            .RequireCovePermission(PermissionMode.Any, ConfigurePermissions);

        endpoints.MapPost(SceneUnmonitorRoute,
            (int coveId, ICurrentPrincipalAccessor principal, OptionsStore options,
             ICredentialPort credentials, IWhisparrClient client,
             ILibraryCardIdentityPort sceneCards, CancellationToken ct)
                => UnmonitorSceneAsync(
                    coveId, principal, options, credentials, client, sceneCards, _log, ct))
            .WithTags(WireTag)
            .RequireCovePermission(PermissionMode.Any, ConfigurePermissions);

        endpoints.MapPost(SceneExcludeRoute,
            (int coveId, ICurrentPrincipalAccessor principal, OptionsStore options,
             ICredentialPort credentials, IWhisparrClient client,
             ILibraryCardIdentityPort sceneCards, CancellationToken ct)
                => ExcludeSceneAsync(
                    coveId, principal, options, credentials, client, sceneCards, _log, ct))
            .WithTags(WireTag)
            .RequireCovePermission(PermissionMode.Any, ConfigurePermissions);

        endpoints.MapPost(SceneRemoveExclusionRoute,
            (int coveId, ICurrentPrincipalAccessor principal, OptionsStore options,
             ICredentialPort credentials, IWhisparrClient client,
             ILibraryCardIdentityPort sceneCards, CancellationToken ct)
                => RemoveSceneExclusionAsync(
                    coveId, principal, options, credentials, client, sceneCards, _log, ct))
            .WithTags(WireTag)
            .RequireCovePermission(PermissionMode.Any, ConfigurePermissions);

        // The configure tier for two reasons rather than one: the route aims this extension's stored
        // credential at a third party AND it spends the reader's indexer traffic and disk. It is the
        // most consequential route this surface mounts, and it must not sit at a tier a caller who
        // cannot configure the extension can reach.
        endpoints.MapPost(SceneSearchRoute,
            (int coveId, ICurrentPrincipalAccessor principal, OptionsStore options,
             ICredentialPort credentials, IWhisparrClient client,
             ILibraryCardIdentityPort sceneCards, CancellationToken ct)
                => SearchSceneNowAsync(
                    coveId, principal, options, credentials, client, sceneCards, _log, ct))
            .WithTags(WireTag)
            .RequireCovePermission(PermissionMode.Any, ConfigurePermissions);

        // The same tier again: one gesture aiming this extension's stored credential at a third
        // party for every scene in a selection is not a lesser act than doing it for one. The reach
        // is what the body names, and the verb it names decides which bound applies.
        endpoints.MapPost(SceneBatchRoute,
            (SceneBatchRequest request, ICurrentPrincipalAccessor principal, IJobService jobs,
             IServiceScopeFactory scopes)
                => EnqueueSceneBatch(request, principal, jobs, scopes))
            .WithTags(WireTag)
            .RequireCovePermission(PermissionMode.Any, ConfigurePermissions);

        endpoints.MapGet(MissingCountRoute,
            (string kind, int coveId, string? q, string? filters,
             ICurrentPrincipalAccessor principal, OptionsStore options, ICredentialPort credentials,
             IWhisparrClient client, ProviderEndpointPort endpoints, MissingPagePlanner planner,
             CancellationToken ct)
                => ReadMissingCountAsync(
                    kind, coveId, q, filters, principal, options, credentials, client, endpoints,
                    planner, _log, ct))
            .WithTags(WireTag)
            .RequireCovePermission(PermissionMode.Any, ReadPermissions);

        // The same read tier as the page and the count beside it: the reach is the one Cove entity
        // the route segment names, and the answer is a list of values the metadata source already
        // publishes. It composes no write and asks the connected instance nothing.
        endpoints.MapGet(MissingFacetValuesRoute,
            (string kind, int coveId, string facetKey, string? q,
             ICurrentPrincipalAccessor principal, OptionsStore options, MissingPagePlanner planner,
             CancellationToken ct)
                => ReadMissingFacetValuesAsync(
                    kind, coveId, facetKey, q, principal, options, planner, _log, ct))
            .WithTags(WireTag)
            .RequireCovePermission(PermissionMode.Any, ReadPermissions);

        // The configure tier, matching the whole-entity registration route above: one gesture over a
        // page's selection aims this extension's stored credential at a third party and creates items
        // in the reader's own Whisparr, which is not something a caller who cannot configure the
        // extension may do.
        endpoints.MapPost(MissingBulkMonitorRoute,
            (string kind, int coveId, MissingBulkRequest request,
             ICurrentPrincipalAccessor principal, IJobService jobs, IServiceScopeFactory scopes,
             OptionsStore options, ICredentialPort credentials, IWhisparrClient client,
             CancellationToken ct)
                => EnqueueMissingBulkMonitorAsync(
                    kind, coveId, request, principal, jobs, scopes, options, credentials, client, ct))
            .WithTags(WireTag)
            .RequireCovePermission(PermissionMode.Any, ConfigurePermissions);

        // The same tier again, and the same act over a wider reach. It names no scene at all: the
        // narrowing rides the query string and the run re-derives its own set, so what a caller can
        // reach is one entity's catalogue and never a set it composed itself.
        endpoints.MapPost(MissingMonitorAllRoute,
            (string kind, int coveId, string? q, string? filters,
             ICurrentPrincipalAccessor principal, IJobService jobs, IServiceScopeFactory scopes,
             OptionsStore options, ICredentialPort credentials, IWhisparrClient client,
             CancellationToken ct)
                => EnqueueMissingMonitorAllAsync(
                    kind, coveId, q, filters, principal, jobs, scopes, options, credentials, client,
                    ct))
            .WithTags(WireTag)
            .RequireCovePermission(PermissionMode.Any, ConfigurePermissions);

        // The configure tier for the same reason as the bulk route: one scene is not a lesser act
        // than a selection of them. Which scene a request touches is a route segment, so a caller
        // cannot name one in a body the route would otherwise have to refuse.
        endpoints.MapPost(MissingSceneMonitorRoute,
            (string kind, int coveId, string providerSceneId, ICurrentPrincipalAccessor principal,
             OptionsStore options, ICredentialPort credentials, IWhisparrClient client,
             IServiceScopeFactory scopes, CancellationToken ct)
                => MonitorMissingSceneAsync(
                    kind, coveId, providerSceneId, principal, options, credentials, client, scopes,
                    _log, ct))
            .WithTags(WireTag)
            .RequireCovePermission(PermissionMode.Any, ConfigurePermissions);

        // The configure tier for two reasons rather than one: the route aims this extension's stored
        // credential at a third party AND it spends the reader's indexer traffic and disk. It is the
        // most consequential route this surface mounts, and it must not sit at a tier a caller who
        // cannot configure the extension can reach.
        endpoints.MapPost(MissingSceneSearchRoute,
            (string kind, int coveId, string providerSceneId, ICurrentPrincipalAccessor principal,
             OptionsStore options, ICredentialPort credentials, IWhisparrClient client,
             CancellationToken ct)
                => SearchMissingSceneAsync(
                    kind, coveId, providerSceneId, principal, options, credentials, client, _log, ct))
            .WithTags(WireTag)
            .RequireCovePermission(PermissionMode.Any, ConfigurePermissions);

        // The configure tier because it aims this extension's stored credential at a third party and
        // reads the whole library to do it. It names nothing at all: what is counted is the reader's
        // own library, so a caller can compose no set of its own here.
        endpoints.MapPost(SyncPreviewRoute,
            (ICurrentPrincipalAccessor principal, IJobService jobs, IServiceScopeFactory scopes,
             OptionsStore options, ICredentialPort credentials, IWhisparrClient client,
             CancellationToken ct)
                => EnqueueSyncPreviewAsync(
                    principal, jobs, scopes, options, credentials, client, ct))
            .WithTags(WireTag)
            .RequireCovePermission(PermissionMode.Any, ConfigurePermissions);

        // The same tier for the read half, which answers what the count above left. Not a lesser
        // tier than the start: it reports how much of the reader's library a third party holds, and
        // that is the same fact whichever route answered it.
        endpoints.MapGet(SyncPreviewRoute,
            (ICurrentPrincipalAccessor principal, IJobService jobs, SyncPreviewCache counts,
             OptionsStore options, ICredentialPort credentials, IWhisparrClient client,
             CancellationToken ct)
                => ReadSyncPreviewAsync(
                    principal, jobs, counts, options, credentials, client, ct))
            .WithTags(WireTag)
            .RequireCovePermission(PermissionMode.Any, ConfigurePermissions);

        // The same tier as the count, and for a stronger reason: this one writes into a third
        // party's catalogue on behalf of the whole library.
        endpoints.MapPost(SyncRunRoute,
            (SyncRunRequest? request, ICurrentPrincipalAccessor principal, IJobService jobs,
             IServiceScopeFactory scopes, OptionsStore options, ICredentialPort credentials,
             IWhisparrClient client, CancellationToken ct)
                => EnqueueSyncRunAsync(
                    request, principal, jobs, scopes, options, credentials, client, ct))
            .WithTags(WireTag)
            .RequireCovePermission(PermissionMode.Any, ConfigurePermissions);

        endpoints.MapGet(JobStatusRoute,
            (string jobId, ICurrentPrincipalAccessor principal, IJobService jobs)
                => BulkJobStatusOf(jobId, principal, jobs))
            .WithTags(WireTag)
            .RequireCovePermission(PermissionMode.Any, ConfigurePermissions);

        // The ONE route of this extension that answers a caller holding no Cove permission, and it
        // says so with the SDK's own convention rather than by declaring nothing. An endpoint
        // declaring no convention also admits an anonymous caller, but silently and with a host
        // warning, which is an access tier nothing states.
        endpoints.MapPost(CallbackRoute,
            (HttpContext http, IServiceScopeFactory scopes, CancellationToken ct)
                => CallbackAsync(http, scopes, _log, ct))
            .WithTags(WireTag)
            .AllowCoveAnonymous();

        endpoints.MapPost(CallbackRegisterRoute,
            (RegisterCallbackRequest request, HttpContext http, ICurrentPrincipalAccessor principal,
             OptionsStore options, OptionsWriteGate gate, ICredentialPort credentials,
             ICallbackSecretPort secrets, IWhisparrNotificationPort notifications,
             RegistrationGate registrations, TimeProvider clock, CancellationToken ct)
                => RegisterCallbackAsync(
                    request, http, principal, Id, options, gate, credentials, secrets, notifications,
                    registrations, clock, ct))
            .WithTags(WireTag)
            .RequireCovePermission(PermissionMode.Any, ConfigurePermissions);

        endpoints.MapGet(CallbackStatusRoute,
            (HttpContext http, ICurrentPrincipalAccessor principal, OptionsStore options,
             ICallbackSecretPort secrets, TimeProvider clock, CancellationToken ct)
                => ReadCallbackStatusAsync(http, principal, Id, options, secrets, clock, ct))
            .WithTags(WireTag)
            .RequireCovePermission(PermissionMode.Any, ConfigurePermissions);
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
