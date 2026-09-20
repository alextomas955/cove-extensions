using Cove.Core.Auth;
using Cove.Core.Interfaces;
using Cove.Extensions.Shared;
using Cove.Plugins;
using Cove.Sdk;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
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

    /// <summary>The settings tab this extension mounts, and the tab its one section targets.</summary>
    private const string SettingsTabKey = "whisparr-sync";

    /// <summary>The tab this extension mounts on the studio, performer and tag pages.</summary>
    private const string MissingTabKey = "whisparr-missing";

    /// <summary>The name the bundle registers this extension's catalogue tab under.</summary>
    /// <remarks>
    /// Byte-identical to the key in the bundle's own component map. The host resolves one to the
    /// other by exact string and renders nothing, with no error, when they differ.
    /// </remarks>
    private const string MissingTabComponentName = "WhisparrMissingTab";

    /// <summary>How the catalogue tab reads on every page it is mounted on.</summary>
    private const string MissingTabLabel = "Missing";

    /// <summary>Where the catalogue tab sits among a page's own tabs.</summary>
    private const int MissingTabOrder = 150;

    /// <summary>The count route the host fetches for a <paramref name="kind"/> page.</summary>
    /// <remarks>
    /// Composed from the same base the route is mapped under, so the endpoint the host calls and the
    /// endpoint this extension mounts cannot drift apart.
    /// </remarks>
    private string MissingCountEndpointFor(string kind)
        => RouteBase + "/entity/" + kind + "/{entityId}/missing/count";

    /// <summary>The tab this extension mounts on the video detail page.</summary>
    private const string SceneTabKey = "whisparr-scene";

    /// <inheritdoc cref="MissingTabComponentName"/>
    private const string SceneTabComponentName = "WhisparrSceneTab";

    /// <summary>How the scene tab reads on the video detail page.</summary>
    private const string SceneTabLabel = "Whisparr";

    /// <summary>Where the scene tab sits among the video page's own tabs.</summary>
    private const int SceneTabOrder = 150;

    /// <summary>The name the bundle registers this extension's bulk action handler under.</summary>
    /// <remarks>
    /// Byte-identical to the key in the bundle's own handler map. The host resolves one to the other
    /// by exact string and dispatches nothing, with no error, when they differ.
    /// </remarks>
    private const string BulkHandlerName = "whisparrMonitorSelected";

    /// <summary>
    /// The spelling the host's selection bar passes for a studio selection.
    /// </summary>
    /// <remarks>
    /// The bar normalizes only the two media plurals; every studio and performer call site passes the
    /// RAW PLURAL, and the host matches an action's declared types by exact string membership. A
    /// singular spelling makes the button simply not appear, with no error anywhere, which is why the
    /// registration and the route's own parse read the same constant.
    /// </remarks>
    private const string StudiosSelectionType = "studios";

    /// <inheritdoc cref="StudiosSelectionType"/>
    private const string PerformersSelectionType = "performers";

    /// <summary>
    /// The spelling the host's selection bar passes for a video selection.
    /// </summary>
    /// <remarks>
    /// SINGULAR, unlike the studio and performer spellings: the bar's own normalizer maps the videos
    /// plural to the singular and every video call site passes the singular already. The host matches
    /// an action's declared types by exact string membership, so the plural here would make the
    /// button simply not appear, with no error anywhere.
    /// </remarks>
    private const string VideosSelectionType = "video";

    /// <summary>The id the host resolves this extension's scene selection action by.</summary>
    private const string SceneBatchActionId = "whisparr-scene-batch";

    /// <summary>
    /// The name the bundle registers this extension's scene selection handler under.
    /// </summary>
    /// <inheritdoc cref="BulkHandlerName" path="/remarks"/>
    private const string SceneBatchHandlerName = "whisparrSceneBatch";

    /// <summary>
    /// The surfaces the host mounts: one dedicated settings tab, one control in each of the studio
    /// and performer pages' own action rows, one catalogue tab per entity page type, one scene tab
    /// on the video detail page, and one bulk action per selection bar, the scene one on v3 alone.
    /// </summary>
    /// <remarks>
    /// Page layout, so the host renders the panel full-width with no card chrome and this extension
    /// draws its own. Every <c>componentName</c> must be byte-identical to the key in the bundle's
    /// <c>defineExtension</c> component map: the host resolves one to the other by exact string and
    /// renders nothing, with no error, when they differ.
    /// <para>
    /// The action-row slot is the only position an extension can reach on either page. The host's
    /// own entity-action contribution point answers with an empty list for anything but a video or an
    /// image, so a control registered there would never render at all.
    /// </para>
    /// <para>
    /// The bulk action is registered ONCE PER ENTITY KIND rather than once carrying both types: the
    /// host allows a single required permission per action and filters visibility by both the entity
    /// type in context and that permission, so one action covering both kinds would still be one
    /// visibility gate. Each declares a handler and NO api endpoint, because the handler has to ask
    /// for a verb and a scope before anything is sent.
    /// </para>
    /// <para>
    /// The manifest is rebuilt on every aggregation and reads the generation the extension last
    /// stored, so the videos-view registrations are absent on v2 and no surface
    /// renders empty there. The browser fetches the manifest, so a generation change takes effect on
    /// the next page load.
    /// </para>
    /// <para>
    /// The studio and performer bulk actions stay registered on both generations: an action's
    /// PRESENCE is a manifest fact and a verb's AVAILABILITY is a runtime one, enforced in the
    /// handler and again at the route. The scene selection action departs from that principle and is
    /// registered on v3 alone, because no verb it offers reaches anything on v2. The reader on v2
    /// therefore meets a studio or performer selection
    /// that offers a Whisparr button explaining itself, and a scene selection that offers no button
    /// at all.
    /// </para>
    /// </remarks>
    public override UIManifest GetUIManifest()
    {
        var manifest = ManifestBuilder()
            .AddSettingsTab(
                key: SettingsTabKey,
                label: "Whisparr Sync",
                description: "Keep Cove in step with the Whisparr instance you configure.",
                order: 100,
                layout: SettingsTabLayout.Page)
            .AddSettingsSection(
                targetTab: SettingsTabKey,
                label: "Whisparr Sync",
                componentName: "WhisparrSyncPage")
            .AddSlot("studio-detail-actions", componentName: "WhisparrStudioActions", order: 100)
            .AddSlot("performer-detail-actions", componentName: "WhisparrPerformerActions", order: 100)

            // Both unconditional. A studio monitors as a series matched by ThePornDB on v2 too, and
            // MonitorStudio is in both capability tables, so the studio surfaces work whichever
            // generation is connected.
            .AddSlot("studios-list-toolbar-end", componentName: "WhisparrLibraryToggle", order: 100)
            .AddSlot("studio-card-footer", componentName: "WhisparrStudioCardBadge", order: 100)
            .AddSlot("studios-list-row", componentName: "WhisparrStudioLibraryRow", order: 100)

            // One component, registered once per page type. The host passes a tab component only the
            // entity id and a navigate callback, so the component reads its own kind from its route.
            //
            // Each countEndpoint bakes its own kind: the host substitutes the literal {entityId} and
            // nothing else, so the kind cannot travel as a second placeholder. It is fetched in an
            // effect on page load, before the tab is opened, and a badge is drawn only for a numeric
            // count.
            .AddTab(
                pageType: "studio",
                key: MissingTabKey,
                label: MissingTabLabel,
                componentName: MissingTabComponentName,
                order: MissingTabOrder,
                countEndpoint: MissingCountEndpointFor("studio"))
            .AddTab(
                pageType: "performer",
                key: MissingTabKey,
                label: MissingTabLabel,
                componentName: MissingTabComponentName,
                order: MissingTabOrder,
                countEndpoint: MissingCountEndpointFor("performer"))
            .AddTab(
                pageType: "tag",
                key: MissingTabKey,
                label: MissingTabLabel,
                componentName: MissingTabComponentName,
                order: MissingTabOrder,
                countEndpoint: MissingCountEndpointFor("tag"))
            .AddAction(
                id: "whisparr-monitor-selected-studios",
                label: "Whisparr",
                actionType: "bulk",
                entityTypes: [StudiosSelectionType],
                icon: "eye",
                apiEndpoint: null,
                handlerName: BulkHandlerName,
                order: 100,
                requiredPermission: Permissions.ExtensionsConfigure,
                // The work reports into the host's own Job Drawer, so its queued-success alert would
                // say the same thing twice.
                suppressSuccessAlert: true)
            .AddAction(
                id: "whisparr-monitor-selected-performers",
                label: "Whisparr",
                actionType: "bulk",
                entityTypes: [PerformersSelectionType],
                icon: "eye",
                apiEndpoint: null,
                handlerName: BulkHandlerName,
                order: 100,
                requiredPermission: Permissions.ExtensionsConfigure,
                suppressSuccessAlert: true)
            .WithJsBundle("index.mjs");

        // Whisparr v2 publishes no per-scene identity and holds no performer entity, so
        // these surfaces have no meaning there and are hidden by omission. The full-width row below
        // a list toolbar goes with the badges it counts, so it is registered wherever they are.
        //
        // The scene tab carries neither a countEndpoint nor an icon: the video detail page maps a
        // contributed tab into its own list keeping only the key, the label and the manual contexts,
        // so either would be fetched and drawn by nothing. Nothing else is registered on that page,
        // so opening a video costs no Whisparr request until the tab is pressed.
        if (!SelectedGenerationIsOlder)
        {
            manifest
                .AddSlot("videos-list-toolbar-end", componentName: "WhisparrLibraryToggle", order: 100)
                .AddSlot("video-card-content", componentName: "WhisparrVideoCardBadge", order: 100)
                .AddSlot("videos-list-row", componentName: "WhisparrVideoLibraryRow", order: 100)
                .AddSlot(
                    "performers-list-toolbar-end", componentName: "WhisparrLibraryToggle", order: 100)
                .AddSlot(
                    "performer-card-footer", componentName: "WhisparrPerformerCardBadge", order: 100)
                .AddSlot(
                    "performers-list-row", componentName: "WhisparrPerformerLibraryRow", order: 100)
                .AddTab(
                    pageType: "video",
                    key: SceneTabKey,
                    label: SceneTabLabel,
                    componentName: SceneTabComponentName,
                    order: SceneTabOrder)

                // The label is the whole of what this extension supplies to the button. The host
                // owns its layout and draws its own glyph there whatever an action declares, so a
                // glyph named here would be a claim nothing renders, and the selected count beside
                // it is the host's own.
                //
                // No api endpoint, because the handler asks which verb before anything is sent.
                .AddAction(
                    id: SceneBatchActionId,
                    label: "Whisparr",
                    actionType: "bulk",
                    entityTypes: [VideosSelectionType],
                    icon: null,
                    apiEndpoint: null,
                    handlerName: SceneBatchHandlerName,
                    order: 100,
                    requiredPermission: Permissions.ExtensionsConfigure,
                    // The work reports into the host's own Job Drawer, so its queued-success alert
                    // would say the same thing twice.
                    suppressSuccessAlert: true);
        }

        return manifest.Build();
    }

    /// <summary>Whether the stored generation is positively v2.</summary>
    /// <remarks>
    /// False for anything else, a generation nothing established included, so a store that could not
    /// be read and a blob the model could not bind both keep every surface.
    /// </remarks>
    private bool SelectedGenerationIsOlder
        => string.Equals(
            _selectedGeneration,
            nameof(WhisparrGeneration.V2),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// What this extension can see of the host's own configuration, of the host services it can
    /// obtain, and of its worker's lifecycle, from inside its container.
    /// </summary>
    /// <remarks>
    /// Opens no scope and touches no database: every member is a reading taken at load or an instant
    /// in this extension's own lifecycle, rather than library data.
    /// </remarks>
    internal Results<Ok<HostConfigurationView>, ForbiddenCode> HostConfiguration(
        ICurrentPrincipalAccessor principal)
        => HasReadPermission(principal)
            ? TypedResults.Ok(new HostConfigurationView(
                ConfigurationResolved,
                LibraryRootCount,
                WorkerStartedAtUtc,
                WorkerCancelledAtUtc,
                ScanServiceResolved,
                MetadataServerServiceResolved))
            : new ForbiddenCode();

    /// <summary>
    /// Tests one Whisparr connection, and reports which of the six outcomes it produced.
    /// </summary>
    /// <remarks>
    /// A request naming neither an address nor a key tests the STORED connection, which is the one
    /// call allowed to record what it read. A request naming either tests that pair and records
    /// nothing about a version, because the instance it reaches may not be the stored one.
    /// <para>
    /// The gate is checked BEFORE the body is read, so a principal without it causes no outbound
    /// request. Without that ordering the route would forward a request on behalf of a caller who is
    /// not allowed to configure this extension, and the classified answer would tell them what sits
    /// at an address they chose.
    /// </para>
    /// </remarks>
    internal static async Task<Results<Ok<ConnectionTestView>, ForbiddenCode>> ConnectionTestAsync(
        ConnectionTestRequest request,
        ICurrentPrincipalAccessor principal,
        IConnectionTestRunner runner,
        CancellationToken ct)
    {
        if (!HasConfigurePermission(principal))
        {
            return new ForbiddenCode();
        }

        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(runner);

        return TypedResults.Ok(
            string.IsNullOrWhiteSpace(request.Address) && string.IsNullOrWhiteSpace(request.ApiKey)
                ? await runner.TestStoredAsync(ct).ConfigureAwait(false)
                : await runner.TestTransientAsync(request.Address, request.ApiKey, ct).ConfigureAwait(false));
    }

    /// <summary>Reads the stored settings.</summary>
    /// <remarks>
    /// The answer cannot carry an API key: <see cref="WhisparrSyncSettingsView"/> has no member that
    /// could hold one, and the key is never read here — only its presence is.
    /// </remarks>
    internal static async Task<Results<Ok<WhisparrSyncSettingsView>, ForbiddenCode>> ReadSettingsAsync(
        ICurrentPrincipalAccessor principal,
        OptionsStore options,
        ICredentialPort credentials,
        CancellationToken ct)
        => HasConfigurePermission(principal)
            ? TypedResults.Ok(await ProjectSettingsAsync(options, credentials, ct).ConfigureAwait(false))
            : new ForbiddenCode();

    /// <summary>Applies one settings save and answers with the settings as they now stand.</summary>
    /// <remarks>
    /// The gate is checked before the body is read, so a principal without it writes nothing.
    /// <para>
    /// The key is written before the options blob. The two are separate stores with no transaction
    /// between them, so a save interrupted between the two leaves a stored key beside the address it
    /// was entered against rather than beside an address nothing was entered for.
    /// </para>
    /// </remarks>
    internal async Task<Results<Ok<WhisparrSyncSettingsView>, ForbiddenCode>> SaveSettingsAsync(
        WhisparrSyncSettingsSaveRequest request,
        ICurrentPrincipalAccessor principal,
        OptionsStore options,
        OptionsWriteGate gate,
        ICredentialPort credentials,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (!HasConfigurePermission(principal))
        {
            return new ForbiddenCode();
        }

        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(clock);

        var now = clock.GetUtcNow();
        await credentials.ApplyAsync(
            WhisparrGeneration.V3, SettingsProjector.CredentialWriteFor(request.V3), now, ct)
            .ConfigureAwait(false);
        await credentials.ApplyAsync(
            WhisparrGeneration.V2, SettingsProjector.CredentialWriteFor(request.V2), now, ct)
            .ConfigureAwait(false);

        var persisted = await gate
            .MutateAsync(options, stored => SettingsProjector.Apply(stored, request), ct)
            .ConfigureAwait(false);

        // After both writes: the manifest reads this, and a value refreshed between them would name
        // a generation only one of the two stores had been given.
        _selectedGeneration = persisted.SelectedGeneration.ToString();

        return TypedResults.Ok(
            await ProjectSettingsAsync(persisted, credentials, ct).ConfigureAwait(false));
    }

    /// <summary>
    /// Reads the refusals outstanding, one line per Whisparr root that has any, and how many records
    /// the backstop could not take.
    /// </summary>
    /// <remarks>
    /// The configure tier, which is the tier Cove's own bulk extension-data route already requires to
    /// read these same values, so this route exposes nothing a caller could not already read. The gate
    /// is checked before the store, so a principal without it causes no read.
    /// <para>
    /// The answer holds recorded filesystem paths. Its size is the stored aggregate's, which the
    /// library's size does not enter into.
    /// </para>
    /// </remarks>
    internal static async Task<Results<Ok<ImportBannerView>, ForbiddenCode>> ReadImportBannerAsync(
        ICurrentPrincipalAccessor principal,
        OptionsStore options,
        CancellationToken ct)
    {
        if (!HasConfigurePermission(principal))
        {
            return new ForbiddenCode();
        }

        ArgumentNullException.ThrowIfNull(options);

        var stored = await options.LoadAsync(ct).ConfigureAwait(false);
        return TypedResults.Ok(ImportBannerView.From(stored.ImportRefusals, stored.ImportHealth));
    }

    /// <summary>
    /// Reads the Cove library roots the connected instance established no path for, one line each.
    /// </summary>
    /// <remarks>
    /// The configure tier, which is the tier Cove's own bulk extension-data route already requires to
    /// read these same stored values, so this route exposes nothing a caller could not already read.
    /// The gate is checked before the store, so a principal without it causes no read.
    /// <para>
    /// The answer holds recorded filesystem paths. Its size is the stored aggregate's, which the
    /// library's size does not enter into.
    /// </para>
    /// </remarks>
    internal static async Task<Results<Ok<FolderAgreementView>, ForbiddenCode>>
        ReadFolderMappingsAsync(
            ICurrentPrincipalAccessor principal, OptionsStore options, CancellationToken ct)
    {
        if (!HasConfigurePermission(principal))
        {
            return new ForbiddenCode();
        }

        ArgumentNullException.ThrowIfNull(options);

        var stored = await options.LoadAsync(ct).ConfigureAwait(false);
        return TypedResults.Ok(
            FolderAgreementView.From(stored.OutboundRefusals, stored.OutboundMappings));
    }

    /// <summary>Stores where an operator says one library root is, once a probe has resolved it.</summary>
    /// <remarks>
    /// The probe is the authority. A mapping typed into this route is built into a candidate and put
    /// through the same reading a run takes, and it is stored only where the instance reported the
    /// library's own sample file at the size the library holds. A path taken on trust would attach the
    /// wrong file to a scene with nothing downstream to reveal it.
    /// <para>
    /// A save that resolved also clears that root's stored refusal, and the reading is held on the
    /// spot, so the next run neither reports a refusal that no longer holds nor waits out the previous
    /// reading's expiry.
    /// </para>
    /// </remarks>
    internal static async Task<Results<Ok<FolderMappingSaveResult>, ForbiddenCode>>
        SaveFolderMappingAsync(
            FolderMappingSaveRequest request,
            ICurrentPrincipalAccessor principal,
            OptionsStore options,
            OptionsWriteGate gate,
            ICredentialPort credentials,
            IWhisparrClient client,
            ICoveLibraryPort library,
            IFolderAddressPort addressing,
            CancellationToken ct)
    {
        if (!HasConfigurePermission(principal))
        {
            return new ForbiddenCode();
        }

        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(addressing);

        // The host's own spelling of the root, not the caller's: what is stored has to key the same
        // way the sample-file read and the folder loop key it.
        if (library.LibraryRoots.FirstOrDefault(root => SameRoot(root, request.CoveRoot))
            is not { } coveRoot)
        {
            return TypedResults.Ok(Answering(FolderMappingSaveOutcome.NotALibraryRoot));
        }

        if (string.IsNullOrWhiteSpace(request.InstancePath))
        {
            await StoreAsync(mapping: null, clearRefusal: false).ConfigureAwait(false);
            return TypedResults.Ok(Answering(FolderMappingSaveOutcome.Removed));
        }

        if (await ResolveTargetAsync(options, credentials, client, ct).ConfigureAwait(false)
            is not { } target)
        {
            return TypedResults.Ok(Answering(FolderMappingSaveOutcome.NotConfigured));
        }

        if (FilesystemReadingOn(target) is not { } role)
        {
            return TypedResults.Ok(
                Answering(
                    FolderMappingSaveOutcome.Refused,
                    FolderAgreementRefusal.InstanceCannotBeAsked));
        }

        var aimed = new FolderAddressTarget(
            target.Generation, target.BaseAddress, target.ApiKey, role);
        var addressed = await addressing
            .AddressAsync(aimed, coveRoot, request.InstancePath, ct).ConfigureAwait(false);

        if (addressed.InstancePath is not { } agreed)
        {
            return TypedResults.Ok(
                Answering(
                    FolderMappingSaveOutcome.Refused, addressed.Refusal, addressed.Tried));
        }

        // The spelling the probe verified rather than the one that was typed, so what is stored is
        // what the instance answered to.
        await StoreAsync(agreed, clearRefusal: true).ConfigureAwait(false);
        return TypedResults.Ok(
            Answering(FolderMappingSaveOutcome.Stored, refusal: null, addressed.Tried));

        Task<WhisparrSyncOptions> StoreAsync(string? mapping, bool clearRefusal)
            => gate.MutateAsync(
                options,
                stored => stored with
                {
                    OutboundMappings = OutboundRefusalProjector.WithMapping(
                        stored.OutboundMappings, coveRoot, mapping),
                    OutboundRefusals = clearRefusal
                        ? OutboundRefusalProjector.Fold(
                            stored.OutboundRefusals, refused: null, [coveRoot])
                        : stored.OutboundRefusals,
                },
                ct);

        static FolderMappingSaveResult Answering(
            FolderMappingSaveOutcome outcome,
            FolderAgreementRefusal? refusal = null,
            IReadOnlyList<string>? tried = null)
            => new(outcome, refusal, tried ?? []);
    }

    /// <summary>Whether two spellings name one configured library root.</summary>
    private static bool SameRoot(string? left, string? right)
        => string.Equals(
            ImportRootRefusals.NormaliseRoot(left),
            ImportRootRefusals.NormaliseRoot(right),
            StringComparison.Ordinal);

    private static async Task<WhisparrSyncSettingsView> ProjectSettingsAsync(
        OptionsStore options, ICredentialPort credentials, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);

        return await ProjectSettingsAsync(
            await options.LoadAsync(ct).ConfigureAwait(false), credentials, ct).ConfigureAwait(false);
    }

    private static async Task<WhisparrSyncSettingsView> ProjectSettingsAsync(
        WhisparrSyncOptions stored, ICredentialPort credentials, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(credentials);

        return SettingsProjector.ToView(
            stored,
            await credentials.HasKeyAsync(WhisparrGeneration.V3, ct).ConfigureAwait(false),
            await credentials.HasKeyAsync(WhisparrGeneration.V2, ct).ConfigureAwait(false));
    }

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
