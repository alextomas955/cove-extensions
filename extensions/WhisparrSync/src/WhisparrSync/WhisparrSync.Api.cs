using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
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
using Microsoft.Extensions.Logging;
using WhisparrSync.Addressing;
using WhisparrSync.Connection;
using WhisparrSync.Contracts;
using WhisparrSync.Import;
using WhisparrSync.Jobs;
using WhisparrSync.Library;
using WhisparrSync.Missing;
using WhisparrSync.Monitoring;
using WhisparrSync.Options;
using WhisparrSync.Providers;
using WhisparrSync.Whisparr;
// The SDK declares a job-progress interface of its own, and the one the host's job service hands a
// work delegate is the core's. An unqualified reference compiles and means the other one.
using CoreJobProgress = Cove.Core.Interfaces.IJobProgress;

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
             ILibraryStatusPort cards, ILibraryCardIdentityPort sceneCards, CancellationToken ct)
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
    /// on the video detail page, and one bulk action per selection bar, the scene one on the newer
    /// generation alone.
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
    /// registered on v3 alone, because no verb it offers reaches anything on the
    /// older one. The reader on v2 therefore meets a studio or performer selection
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

            // Both unconditional. A studio monitors as a series matched by ThePornDB on the older
            // generation too, and MonitorStudio is in both capability tables, so the studio surfaces
            // work whichever generation is connected.
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

    /// <summary>Reads how the connected instance monitors one Cove entity, right now.</summary>
    /// <remarks>
    /// Live on every read, holding nothing: one request per entity page view, no cache and no stored
    /// per-entity row. A stored answer would be a table growing with the library, and a stale one
    /// would paint a state the instance no longer reports.
    /// <para>
    /// The read tier, which is the tier a caller already needs to see the entity page this answers
    /// for. The gate is checked before the store, so a principal without it causes no read.
    /// </para>
    /// <para>
    /// The answer names the capabilities the connected generation holds, so the browser reads its
    /// menu from the server rather than carrying a generation table of its own.
    /// </para>
    /// </remarks>
    internal static async Task<Results<Ok<EntityMonitoringView>, BadRequest, ForbiddenCode>>
        ReadEntityMonitoringAsync(
            string kind,
            int coveId,
            ICurrentPrincipalAccessor principal,
            OptionsStore options,
            ICredentialPort credentials,
            IWhisparrClient client,
            IEntityIdentityPort identities,
            ILogger log,
            CancellationToken ct)
    {
        if (!HasReadPermission(principal))
        {
            return new ForbiddenCode();
        }

        // Both halves, in ONE expression, at every entity route. The parse succeeds for an integer
        // naming no member, and every arm below classifies a kind by switching on it and throwing for
        // one it cannot express - by design, because a kind resolving to a default arm would act on
        // the wrong table. So the parse alone lets untrusted route input reach a throw inside a
        // handler whose declared results hold no failure. Splitting the two into separate statements
        // is what lets a later edit take one away.
        if (!Enum.TryParse<WhisparrEntityKind>(kind, ignoreCase: true, out var entityKind)
            || !Enum.IsDefined(entityKind))
        {
            return TypedResults.BadRequest();
        }

        ArgumentNullException.ThrowIfNull(identities);

        if (await ResolveTargetAsync(options, credentials, client, ct).ConfigureAwait(false)
            is not { } target)
        {
            return TypedResults.Ok(EntityMonitoringView.NotConfigured(entityKind));
        }

        var reading = await ReadResolvedAsync(
            entityKind, coveId, target, identities, log, ReadingEntity(entityKind, target), ct)
            .ConfigureAwait(false);

        return TypedResults.Ok(reading);
    }

    /// <summary>Monitors one Cove entity on the connected instance, in one gesture.</summary>
    /// <remarks>
    /// The request carries a scope and nothing else. Which entity the instance is asked about is read
    /// from the stored identity row for the Cove entity the route names, so an identifier a caller put
    /// in the body reaches nothing and there is no value to validate.
    /// <para>
    /// The configure tier, the same tier the connection test takes: this route aims this extension's
    /// stored credential at a third party, so it is deliberately out of reach of a caller who cannot
    /// configure the extension. The gate is checked before the body is read.
    /// </para>
    /// <para>
    /// The order is load-bearing. Identity first, so a refusal happens before any outbound request.
    /// Then the entity itself, because one the instance already holds keeps its own add defaults and
    /// reading them would only invite sending them over values a user chose. Only then the defaults,
    /// which are the instance's own, and each empty answer is a stop taken before anything is sent.
    /// </para>
    /// <para>
    /// An accepted monitor starts the reflect-owned run by itself, so a user who asked for one thing
    /// is not left a second gesture to discover. It is ENQUEUED rather than awaited: the run reads
    /// one folder of the entity at a time, and awaiting it would make the length of the click the
    /// length of the entity. Nothing is asked of the caller for it, and no dialog appears.
    /// </para>
    /// </remarks>
    internal async Task<Results<Ok<EntityMonitoringView>, BadRequest, ForbiddenCode>>
        MonitorEntityAsync(
            string kind,
            int coveId,
            MonitorEntityRequest request,
            ICurrentPrincipalAccessor principal,
            OptionsStore options,
            ICredentialPort credentials,
            IWhisparrClient client,
            IEntityIdentityPort identities,
            IJobService jobs,
            IServiceScopeFactory scopes,
            ILogger log,
            CancellationToken ct)
    {
        if (!HasConfigurePermission(principal))
        {
            return new ForbiddenCode();
        }

        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(identities);

        if (!Enum.TryParse<WhisparrEntityKind>(kind, ignoreCase: true, out var entityKind)
            || !Enum.IsDefined(entityKind))
        {
            return TypedResults.BadRequest();
        }

        if (await ResolveTargetAsync(options, credentials, client, ct).ConfigureAwait(false)
            is not { } target)
        {
            return TypedResults.Ok(EntityMonitoringView.NotConfigured(entityKind));
        }

        // The stored default rather than the instance's own, and read off the load that resolved the
        // connection rather than through a second one. There is no literal beside it: with a
        // non-nullable stored member there is nothing to fall back from, and a second fallback would
        // be a second answer to one question.
        var scope = request.Scope ?? target.DefaultMonitorScope;

        // No scope reaches the performer arm. The field a future-only scope is expressed through
        // exists on the studio resource and on no other, so a scope a caller named for a performer
        // names nothing the request could carry.
        var monitoring = await MonitorResolvedAsync(
            entityKind,
            coveId,
            target,
            identities,
            log,
            ActingFor(entityKind, target, scope),
            EntityRootThrough(scopes, target, FilesOfEntity(entityKind, coveId)),
            ct).ConfigureAwait(false);

        // From HERE and not from the resolved member the bulk path also reaches: a selection of a
        // thousand entities must not become a thousand background runs. One reflect step per entity
        // inside the batch is the bulk gesture's own shape.
        if (monitoring is { Refusal: MonitorRefusalKind.None, Monitored: true })
        {
            EnqueueReflectOwned(jobs, scopes, entityKind, coveId);
        }

        return TypedResults.Ok(monitoring);
    }

    /// <summary>
    /// Asks the connected instance to link the files the library already holds for one entity into
    /// place, in the background.
    /// </summary>
    /// <remarks>
    /// Takes no body at all. Which entity is named by the route, and nothing about the outbound
    /// request is a value a caller could supply: the folders are read from the library and the
    /// identity that admits the entity at all is read from its own stored rows.
    /// <para>
    /// The order is the monitor route's own. Identity first, so an entity the connected generation
    /// cannot name is refused before anything is sent — even though the run itself names folders
    /// rather than the entity, acting for an entity this product could not identify would be acting
    /// on a link the library does not hold.
    /// </para>
    /// <para>
    /// The hard-link setting is read before anything else leaves, and a skip is ANSWERED rather than
    /// enqueued: with that setting off the instance has no mode that links, so every matched file
    /// would be copied in full. The reader is told at the control instead, and the run is read again
    /// when it starts, because the setting is the instance's to change in between.
    /// </para>
    /// <para>
    /// Enqueued rather than awaited, so a caller cannot hold a request thread for the length of an
    /// entity's folder set.
    /// </para>
    /// </remarks>
    internal async Task<Results<Ok<ReflectOwnedEnqueued>, Accepted<ReflectOwnedEnqueued>, BadRequest, ForbiddenCode>>
        ReflectOwnedEntityAsync(
            string kind,
            int coveId,
            ICurrentPrincipalAccessor principal,
            OptionsStore options,
            ICredentialPort credentials,
            IWhisparrClient client,
            IEntityIdentityPort identities,
            IJobService jobs,
            IServiceScopeFactory scopes,
            CancellationToken ct)
    {
        if (!HasConfigurePermission(principal))
        {
            return new ForbiddenCode();
        }

        ArgumentNullException.ThrowIfNull(identities);
        ArgumentNullException.ThrowIfNull(jobs);

        if (!Enum.TryParse<WhisparrEntityKind>(kind, ignoreCase: true, out var entityKind)
            || !Enum.IsDefined(entityKind))
        {
            return TypedResults.BadRequest();
        }

        if (await ResolveTargetAsync(options, credentials, client, ct).ConfigureAwait(false)
            is not { } target)
        {
            return TypedResults.Ok(Refusing(MonitorRefusalKind.NotConfigured));
        }

        var identity = await identities.ResolveAsync(entityKind, coveId, target.Generation, ct)
            .ConfigureAwait(false);
        var acting = ReflectOwnedActingOn(target);
        if (acting is null || identity.ForeignId is null)
        {
            return TypedResults.Ok(Refusing(RefusalAmong(acting is null, identity.Refusal)));
        }

        var decision = await ReflectOwnedDecisionAsync(target, acting, ct).ConfigureAwait(false);
        if (!decision.Act)
        {
            return TypedResults.Ok(
                new ReflectOwnedEnqueued(decision.Reason, null, MonitorRefusalKind.None));
        }

        return TypedResults.Accepted(
            (string?)null,
            new ReflectOwnedEnqueued(
                null, EnqueueReflectOwned(jobs, scopes, entityKind, coveId), MonitorRefusalKind.None));

        static ReflectOwnedEnqueued Refusing(MonitorRefusalKind refusal)
            => new(null, null, refusal);
    }

    /// <summary>Starts one entity's reflect-owned run in the background.</summary>
    /// <remarks>
    /// Enqueued EXCLUSIVE. Two entities can hold files in one folder — a video carries a studio and
    /// its performers at once — so overlapping runs would issue overlapping attaches for the same
    /// directory. What exclusivity costs when that does not happen is that the runs go one after the
    /// other, against a third party this product should not be issuing parallel work to anyway.
    /// </remarks>
    private string EnqueueReflectOwned(
        IJobService jobs, IServiceScopeFactory scopes, WhisparrEntityKind kind, int coveId)
    {
        var parameters = ReflectOwnedJob.Encode(kind, coveId);

        return jobs.Enqueue(
            OwnJobTypePrefix + ReflectOwnedJob.JobId,
            $"[{Name}] Reflect owned, one {kind}",
            (progress, ct) => RunReflectOwnedAsync(parameters, scopes, progress, ct),
            exclusive: true);
    }

    /// <summary>Runs one enqueued reflect-owned pass.</summary>
    /// <remarks>
    /// Everything the run acts through is resolved when it STARTS. A cancellation is rethrown after
    /// the summary is written, so the host classifies the run as cancelled rather than completed
    /// while the reader is still told what it managed to link.
    /// </remarks>
    private async Task RunReflectOwnedAsync(
        IReadOnlyDictionary<string, string> parameters,
        IServiceScopeFactory scopes,
        CoreJobProgress progress,
        CancellationToken ct)
    {
        var run = await ReflectOwnedJob.RunAsync(
            ReflectOwnedJob.Decode(parameters), scopes, AimAsync, ct).ConfigureAwait(false);

        await RecordRootReadingsAsync(scopes, run.AddressRefusals, run.AddressedRoots)
            .ConfigureAwait(false);

        // The host's progress carries no summary field, so the run's one line rides the final
        // report's sub-task.
        progress.Report(1d, ReflectOwnedJob.SummaryOf(run));
        ct.ThrowIfCancellationRequested();

        // Nothing this product could not resolve names a skip reason. A reader sent to the instance's
        // hard-link setting because no connection was configured would be sent to a value nobody read.
        async Task<ReflectOwnedAim> AimAsync(IServiceProvider services, CancellationToken runCt)
        {
            if (await ResolveTargetAsync(
                    services.GetRequiredService<OptionsStore>(),
                    services.GetRequiredService<ICredentialPort>(),
                    services.GetRequiredService<IWhisparrClient>(),
                    runCt).ConfigureAwait(false) is not { } target)
            {
                return new ReflectOwnedAim(null, null);
            }

            if (ReflectOwnedActingOn(target) is not { } acting)
            {
                return new ReflectOwnedAim(null, null);
            }

            var decision = await ReflectOwnedDecisionAsync(target, acting, runCt).ConfigureAwait(false);

            return decision.Act
                ? new ReflectOwnedAim(
                    AimedAt(target, acting, services.GetRequiredService<IFolderAddressPort>()), null)
                : new ReflectOwnedAim(null, decision.Reason);
        }
    }

    /// <summary>What a reflect-owned run needs from <paramref name="target"/>, already aimed at it.</summary>
    /// <remarks>
    /// The ONE statement of the work, reached by the entity's own enqueued run and by a selection's
    /// per-entity step alike. Two statements of one gesture is how a selection comes to behave
    /// differently from a click.
    /// </remarks>
    private ReflectOwnedAiming AimedAt(
        MonitoringTarget target, IWhisparrReflectOwnedActing acting, IFolderAddressPort addressing)
        => new(
            target.Generation,
            AddressingThrough(target, addressing),
            async (folder, readCt) => (await ContainedAsync(
                    () => acting.ListImportableFilesAsync(
                        target.BaseAddress, target.ApiKey, folder, readCt),
                    target,
                    _log,
                    readCt).ConfigureAwait(false))
                is { } parsed && MonitoringProjector.Accepted(parsed) == MonitorRefusalKind.None
                    ? ImportableListing.Listed(parsed.Body)
                    : ImportableListing.Refused,
            async (files, attachCt) => (await ContainedAsync(
                    () => acting.AttachOwnedFilesAsync(
                        target.BaseAddress, target.ApiKey, files, attachCt),
                    target,
                    _log,
                    attachCt).ConfigureAwait(false))
                is { } attached
                && MonitoringProjector.Accepted(attached) == MonitorRefusalKind.None);

    /// <summary>
    /// How a run turns a folder the library names into the path <paramref name="target"/> can open.
    /// </summary>
    /// <remarks>
    /// Where the connected generation holds no filesystem role, every folder answers that the instance
    /// cannot be asked. The run then states that rather than handing over a path nobody checked, which
    /// on a mismatched root reads back as a clean pass over an empty folder.
    /// </remarks>
    private static Func<string, CancellationToken, Task<AddressedFolder>> AddressingThrough(
        MonitoringTarget target, IFolderAddressPort addressing)
    {
        if (FilesystemReadingOn(target) is not { } role)
        {
            return (_, _) => Task.FromResult(
                new AddressedFolder(
                    null,
                    FolderAgreementRefusal.InstanceCannotBeAsked,
                    string.Empty,
                    Array.Empty<string>()));
        }

        var aimed = new FolderAddressTarget(
            target.Generation, target.BaseAddress, target.ApiKey, role);

        return (folder, addressCt) => addressing.AddressAsync(aimed, folder, addressCt);
    }

    /// <summary>
    /// The role that answers what <paramref name="target"/> holds at a path of its own, or null where
    /// the connected generation holds none.
    /// </summary>
    private static IWhisparrInstanceFilesystemReading? FilesystemReadingOn(MonitoringTarget target)
        => target.Capabilities.Obtain<IWhisparrInstanceFilesystemReading>()
            .Match<IWhisparrInstanceFilesystemReading?>(filesystem => filesystem, _ => null);

    /// <summary>
    /// The role that links owned files into place on <paramref name="target"/>, or null where the
    /// connected generation holds none.
    /// </summary>
    private static IWhisparrReflectOwnedActing? ReflectOwnedActingOn(MonitoringTarget target)
        => target.Capabilities.Obtain<IWhisparrReflectOwnedActing>()
            .Match<IWhisparrReflectOwnedActing?>(acting => acting, _ => null);

    /// <summary>Whether <paramref name="target"/> links a file into place rather than copying it.</summary>
    /// <remarks>
    /// Read on the route AND again when the run starts. The two are minutes apart, and the value
    /// decides whether every matched file is linked or duplicated in full.
    /// </remarks>
    private async Task<ReflectOwnedDecision> ReflectOwnedDecisionAsync(
        MonitoringTarget target, IWhisparrReflectOwnedActing acting, CancellationToken ct)
    {
        var setting = await ContainedAsync(
            () => acting.ReadHardlinkSettingAsync(target.BaseAddress, target.ApiKey, ct),
            target,
            _log,
            ct).ConfigureAwait(false);

        return ReflectOwnedPlanner.Decide(setting?.Body);
    }

    /// <summary>
    /// Offers the connected instance every scene the library holds under one entity that its own
    /// catalogue does not, in the background.
    /// </summary>
    /// <remarks>
    /// Takes no body at all. Which entity is named by the route, and nothing outbound is a value a
    /// caller could supply: the entity's identifier and every scene's are read from the library's
    /// own stored rows, and the instance-side id the catalogue refresh names comes from the
    /// instance's own record of the entity.
    /// <para>
    /// The order is the monitor route's own, and every step of it is a stop taken before anything
    /// is created. Identity first, so an entity the connected generation cannot name is refused
    /// with no outbound request. Then the scene-registration role, whose ABSENCE is the whole of
    /// v2's refusal - no route there adds a catalogue item, so the role is not
    /// registered and nothing here compares a generation. Then the entity itself, because an
    /// instance that does not hold it has no catalogue to add to. Then the profile and the root,
    /// each empty answer a stop taken before the first scene is composed.
    /// </para>
    /// <para>
    /// The profile and the root are read HERE rather than at any earlier point, and read again when
    /// the run starts: they are the instance's own and are its to change in between, and they decide
    /// what every scene this verb creates is filed under.
    /// </para>
    /// <para>
    /// Enqueued rather than awaited, so a caller cannot hold a request thread for the length of an
    /// entity's catalogue.
    /// </para>
    /// </remarks>
    internal async Task<Results<Ok<AddAllMissingEnqueued>, Accepted<AddAllMissingEnqueued>, BadRequest, ForbiddenCode>>
        AddAllMissingEntityAsync(
            string kind,
            int coveId,
            ICurrentPrincipalAccessor principal,
            OptionsStore options,
            ICredentialPort credentials,
            IWhisparrClient client,
            IEntityIdentityPort identities,
            IJobService jobs,
            IServiceScopeFactory scopes,
            CancellationToken ct)
    {
        if (!HasConfigurePermission(principal))
        {
            return new ForbiddenCode();
        }

        ArgumentNullException.ThrowIfNull(identities);
        ArgumentNullException.ThrowIfNull(jobs);

        if (!Enum.TryParse<WhisparrEntityKind>(kind, ignoreCase: true, out var entityKind)
            || !Enum.IsDefined(entityKind))
        {
            return TypedResults.BadRequest();
        }

        if (await ResolveTargetAsync(options, credentials, client, ct).ConfigureAwait(false)
            is not { } target)
        {
            return TypedResults.Ok(new AddAllMissingEnqueued(null, MonitorRefusalKind.NotConfigured));
        }

        var resolved = await ResolveAddAllMissingAsync(
            entityKind,
            coveId,
            target,
            identities,
            EntityRootThrough(scopes, target, FilesOfEntity(entityKind, coveId)),
            ct).ConfigureAwait(false);
        if (resolved.Aiming is null)
        {
            return TypedResults.Ok(new AddAllMissingEnqueued(null, resolved.Refusal));
        }

        return TypedResults.Accepted(
            (string?)null,
            new AddAllMissingEnqueued(
                EnqueueAddAllMissing(jobs, scopes, entityKind, coveId), MonitorRefusalKind.None));
    }

    /// <summary>What one entity's registration run needs, or why it cannot be started.</summary>
    /// <param name="Aiming">What the run acts through, or null on a refusal.</param>
    /// <param name="Refusal">Why there is none, or <see cref="MonitorRefusalKind.None"/>.</param>
    private sealed record AddAllMissingResolution(
        AddAllMissingAiming? Aiming, MonitorRefusalKind Refusal);

    /// <summary>
    /// Resolves everything one registration run acts through, in the order that makes each refusal
    /// cost as little as it can.
    /// </summary>
    /// <remarks>
    /// Reached from the route AND again when the run starts, so the two cannot come to disagree
    /// about what a registration carries. The values it reads are the instance's own and are minutes
    /// apart on the two paths.
    /// </remarks>
    private async Task<AddAllMissingResolution> ResolveAddAllMissingAsync(
        WhisparrEntityKind kind,
        int coveId,
        MonitoringTarget target,
        IEntityIdentityPort identities,
        Func<AddDefaults, CancellationToken, Task<EntityAddDefaultsResolution>> composeAdd,
        CancellationToken ct)
    {
        var identity = await identities.ResolveAsync(kind, coveId, target.Generation, ct)
            .ConfigureAwait(false);
        var acting = target.Capabilities.Obtain<IWhisparrMissingSceneActing>()
            .Match<IWhisparrMissingSceneActing?>(held => held, _ => null);
        var reading = HeldActingFor(kind, target);

        if (acting is null || reading is not { } actingFor || identity.ForeignId is not { } named)
        {
            return new AddAllMissingResolution(
                null, RefusalAmong(acting is null || reading is null, identity.Refusal));
        }

        var read = await ContainedAsync(() => actingFor(named).ReadEntity(ct), target, _log, ct)
            .ConfigureAwait(false);
        if (read is null)
        {
            return new AddAllMissingResolution(null, MonitorRefusalKind.InstanceRefused);
        }

        var answer = MonitoringProjector.Classify(read);
        if (answer.Reading != MonitoringProjector.EntityReading.Held
            || MonitoringProjector.EntityIdIn(read.Body) is not { } entityId)
        {
            return new AddAllMissingResolution(null, RefusalIn(answer));
        }

        var profiles = await ContainedAsync(
            () => target.Reads.ReadQualityProfilesAsync(target.BaseAddress, target.ApiKey, ct),
            target,
            _log,
            ct).ConfigureAwait(false);
        var roots = profiles is null
            ? null
            : await ContainedAsync(
                () => target.Reads.ReadRootFoldersAsync(target.BaseAddress, target.ApiKey, ct),
                target,
                _log,
                ct).ConfigureAwait(false);
        if (profiles is null || roots is null)
        {
            return new AddAllMissingResolution(null, MonitorRefusalKind.InstanceRefused);
        }

        var defaults = AddDefaultsProjector.From(profiles.Body, roots.Body);
        if (defaults.Defaults is not { } runWide)
        {
            return new AddAllMissingResolution(null, defaults.Refusal);
        }

        // Composed once for the run rather than once per scene. The agreement is cached per library
        // root, but a per-scene composition would still repeat the counts for every scene in a page.
        var composed = await composeAdd(runWide, ct).ConfigureAwait(false);
        if (composed.Defaults is not { } composeWith)
        {
            return new AddAllMissingResolution(null, composed.Refusal);
        }

        return new AddAllMissingResolution(
            new AddAllMissingAiming(
                target.Generation,
                (foreignId, registerCt) => ContainedAsync(
                    () => acting.AddSceneAsync(
                        target.BaseAddress, target.ApiKey, foreignId, composeWith, registerCt),
                    target,
                    _log,
                    registerCt),
                async refreshCt =>
                {
                    await ContainedAsync(
                        () => acting.RefreshCatalogueAsync(
                            target.BaseAddress, target.ApiKey, kind, entityId, refreshCt),
                        target,
                        _log,
                        refreshCt).ConfigureAwait(false);
                }),
            MonitorRefusalKind.None);
    }

    /// <summary>Starts one entity's registration run in the background.</summary>
    /// <remarks>
    /// Enqueued EXCLUSIVE, for the reason the reflect-owned run is: two entities can name one scene
    /// - a video carries a studio and its performers at once - so overlapping runs would offer the
    /// same scene twice. What exclusivity costs when that does not happen is that the runs go one
    /// after the other, against a third party this product should not be issuing parallel work to.
    /// </remarks>
    private string EnqueueAddAllMissing(
        IJobService jobs, IServiceScopeFactory scopes, WhisparrEntityKind kind, int coveId)
    {
        var parameters = AddAllMissingJob.Encode(kind, coveId);

        return jobs.Enqueue(
            OwnJobTypePrefix + AddAllMissingJob.JobId,
            $"[{Name}] Add all missing, one {kind}",
            (progress, ct) => RunAddAllMissingAsync(parameters, scopes, progress, ct),
            exclusive: true);
    }

    /// <summary>Runs one enqueued registration pass.</summary>
    /// <remarks>
    /// Everything the run acts through is resolved when it STARTS. A cancellation is rethrown after
    /// the summary is written, so the host classifies the run as cancelled rather than completed
    /// while the reader is still told what it managed to register.
    /// </remarks>
    private async Task RunAddAllMissingAsync(
        IReadOnlyDictionary<string, string> parameters,
        IServiceScopeFactory scopes,
        CoreJobProgress progress,
        CancellationToken ct)
    {
        var batch = AddAllMissingJob.Decode(parameters);
        var run = await AddAllMissingJob.RunAsync(batch, scopes, AimAsync, ct).ConfigureAwait(false);

        // The host's progress carries no summary field, so the run's one line rides the final
        // report's sub-task.
        progress.Report(1d, AddAllMissingJob.SummaryOf(run));
        ct.ThrowIfCancellationRequested();

        async Task<AddAllMissingAiming?> AimAsync(IServiceProvider services, CancellationToken runCt)
        {
            if (batch.Kind is not { } kind
                || await ResolveTargetAsync(
                    services.GetRequiredService<OptionsStore>(),
                    services.GetRequiredService<ICredentialPort>(),
                    services.GetRequiredService<IWhisparrClient>(),
                    runCt).ConfigureAwait(false) is not { } target)
            {
                return null;
            }

            return (await ResolveAddAllMissingAsync(
                kind,
                batch.CoveId,
                target,
                services.GetRequiredService<IEntityIdentityPort>(),
                EntityRootIn(services, target, FilesOfEntity(kind, batch.CoveId)),
                runCt).ConfigureAwait(false)).Aiming;
        }
    }

    /// <summary>Stops the connected instance monitoring one Cove entity.</summary>
    /// <remarks>
    /// Takes no body at all. There is nothing for a caller to say: which entity is named by the
    /// route, and the identifier the instance is given is read from the stored identity row, so the
    /// same order holds as for the monitor route and a refusal happens before any outbound request.
    /// <para>
    /// Setting the flag false governs what a later catalogue addition does and retracts nothing
    /// already wanted. An entity the instance does not hold is already not monitored, so that answers
    /// the current state rather than a refusal, and nothing is sent.
    /// </para>
    /// </remarks>
    internal static async Task<Results<Ok<EntityMonitoringView>, BadRequest, ForbiddenCode>>
        UnmonitorEntityAsync(
            string kind,
            int coveId,
            ICurrentPrincipalAccessor principal,
            OptionsStore options,
            ICredentialPort credentials,
            IWhisparrClient client,
            IEntityIdentityPort identities,
            ILogger log,
            CancellationToken ct)
    {
        if (!HasConfigurePermission(principal))
        {
            return new ForbiddenCode();
        }

        ArgumentNullException.ThrowIfNull(identities);

        if (!Enum.TryParse<WhisparrEntityKind>(kind, ignoreCase: true, out var entityKind)
            || !Enum.IsDefined(entityKind))
        {
            return TypedResults.BadRequest();
        }

        if (await ResolveTargetAsync(options, credentials, client, ct).ConfigureAwait(false)
            is not { } target)
        {
            return TypedResults.Ok(EntityMonitoringView.NotConfigured(entityKind));
        }

        return TypedResults.Ok(
            await UnmonitorResolvedAsync(entityKind, coveId, target, identities, log, ct)
                .ConfigureAwait(false));
    }

    /// <summary>Stops <paramref name="target"/> monitoring one entity it is known to be able to.</summary>
    /// <remarks>
    /// Separate from the route so the bulk path reaches the SAME statement of the verb. Two
    /// statements of one gesture is how a selection comes to behave differently from a click.
    /// </remarks>
    private static Task<EntityMonitoringView> UnmonitorResolvedAsync(
        WhisparrEntityKind kind,
        int coveId,
        MonitoringTarget target,
        IEntityIdentityPort identities,
        ILogger log,
        CancellationToken ct)
    {
        return ChangingHeldEntityAsync(kind, coveId, target, identities, log, Unmonitoring, ct);

        async Task<EntityMonitoringView> Unmonitoring(
            HeldActing acting, int entityId, bool monitored, CancellationToken changeCt)
        {
            // Present, in both arms. This runs only where the read above classified the entity as
            // held, so the instance holding it is established here rather than assumed.
            if (!monitored)
            {
                return State(kind, target, present: true, monitored: false, scope: null);
            }

            var flipped = await ContainedAsync(
                () => acting.SetMonitored(entityId, false, changeCt), target, log, changeCt)
                .ConfigureAwait(false);

            if (flipped is null)
            {
                return Refused(kind, target, MonitorRefusalKind.InstanceRefused);
            }

            var refused = MonitoringProjector.Accepted(flipped);
            return refused != MonitorRefusalKind.None
                ? Refused(kind, target, refused)
                : State(kind, target, present: true, monitored: false, scope: null);
        }
    }

    /// <summary>
    /// Asks the connected instance to search for what it monitors for one Cove entity.
    /// </summary>
    /// <remarks>
    /// The ONE route of this extension whose effect spends the reader's bandwidth and disk, and the
    /// one place in this product that obtains <see cref="IWhisparrSearchGrabbing"/>. Everything else
    /// here sets flags and tells the instance where files already are.
    /// <para>
    /// Takes NO request body at all, like the unmonitor route. There is no verb member, no scope
    /// member and no identifier member anywhere in its input, so a body omitting a field and binding
    /// to a permissive default is not expressible on this route by construction rather than by a
    /// check. Which entity is named by the route segment, and the identifier the instance is given
    /// comes from the stored identity row and then from the instance's own record.
    /// </para>
    /// <para>
    /// Given its own path from identity to call rather than routed through the shared delegate seam
    /// the monitor, unmonitor and scope verbs go through. A shared flow that can carry a grabbing verb
    /// is exactly the shape "one gesture grows into acquisition" describes, and the seam's value is
    /// that no delegate it takes can express this one.
    /// </para>
    /// <para>
    /// The entity is read before anything is asked for. An entity the instance does not hold monitors
    /// nothing there, so the command would name a row that does not exist and the read is what turns
    /// that into a refusal instead of a request.
    /// </para>
    /// </remarks>
    internal static async Task<Results<Ok<EntityMonitoringView>, BadRequest, ForbiddenCode>>
        SearchAllMonitoredEntityAsync(
            string kind,
            int coveId,
            ICurrentPrincipalAccessor principal,
            OptionsStore options,
            ICredentialPort credentials,
            IWhisparrClient client,
            IEntityIdentityPort identities,
            ILogger log,
            CancellationToken ct)
    {
        if (!HasConfigurePermission(principal))
        {
            return new ForbiddenCode();
        }

        ArgumentNullException.ThrowIfNull(identities);

        if (!Enum.TryParse<WhisparrEntityKind>(kind, ignoreCase: true, out var entityKind)
            || !Enum.IsDefined(entityKind))
        {
            return TypedResults.BadRequest();
        }

        if (await ResolveTargetAsync(options, credentials, client, ct).ConfigureAwait(false)
            is not { } target)
        {
            return TypedResults.Ok(EntityMonitoringView.NotConfigured(entityKind));
        }

        var grabbing = SearchGrabbingOn(target);
        var reading = HeldActingFor(entityKind, target);

        // Identity first, so a refusal costs no outbound request. SEC-4: the outbound identifier is
        // resolved server-side from the stored rows, and nothing a caller supplied reaches it.
        var identity = await identities.ResolveAsync(entityKind, coveId, target.Generation, ct)
            .ConfigureAwait(false);

        if (grabbing is null || reading is not { } actingFor || identity.ForeignId is not { } named)
        {
            return TypedResults.Ok(
                Refused(
                    entityKind,
                    target,
                    RefusalAmong(grabbing is null || reading is null, identity.Refusal)));
        }

        var read = await ContainedAsync(() => actingFor(named).ReadEntity(ct), target, log, ct)
            .ConfigureAwait(false);

        if (read is null)
        {
            return TypedResults.Ok(Refused(entityKind, target, MonitorRefusalKind.InstanceRefused));
        }

        var answer = MonitoringProjector.Classify(read);
        if (answer.Reading != MonitoringProjector.EntityReading.Held
            || MonitoringProjector.EntityIdIn(read.Body) is not { } entityId)
        {
            return TypedResults.Ok(Refused(entityKind, target, RefusalIn(answer)));
        }

        // Read before the search and answered after it: a search changes what the instance goes
        // looking for and never the flag, so reporting the flag the search itself set would report
        // something that did not happen.
        var monitored = MonitoringProjector.MonitoredIn(read.Body);

        var searched = await ContainedAsync(
            () => grabbing.SearchMonitoredAsync(
                target.BaseAddress, target.ApiKey, target.Generation, entityKind, [entityId], ct),
            target,
            log,
            ct).ConfigureAwait(false);

        return TypedResults.Ok(
            searched is null
                || MonitoringProjector.Accepted(searched) != MonitorRefusalKind.None
                    ? Refused(entityKind, target, MonitorRefusalKind.InstanceRefused)
                    : State(
                        entityKind,
                        target,
                        present: true,
                        monitored,
                        ScopeHeld(entityKind, target, monitored, read.Body)));
    }

    /// <summary>Changes the monitor scope the connected instance holds for one Cove entity.</summary>
    /// <remarks>
    /// A kind expressing no scope answers a bad request rather than a refusal. The field a scope is
    /// carried in exists on one resource only, so a scope named for any other kind is a request the
    /// contract cannot express at all, which is what an unparsable kind answers too.
    /// <para>
    /// The flag is left exactly as the instance reports it. Widening a scope is not the same gesture
    /// as monitoring, and answering this with a monitored state the caller did not ask for would
    /// report something that did not happen.
    /// </para>
    /// </remarks>
    internal static async Task<Results<Ok<EntityMonitoringView>, BadRequest, ForbiddenCode>>
        SetMonitorScopeAsync(
            string kind,
            int coveId,
            MonitorEntityRequest request,
            ICurrentPrincipalAccessor principal,
            OptionsStore options,
            ICredentialPort credentials,
            IWhisparrClient client,
            IEntityIdentityPort identities,
            ILogger log,
            CancellationToken ct)
    {
        if (!HasConfigurePermission(principal))
        {
            return new ForbiddenCode();
        }

        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(identities);

        if (!Enum.TryParse<WhisparrEntityKind>(kind, ignoreCase: true, out var entityKind)
            || !Enum.IsDefined(entityKind)
            || !ExpressesAScope(entityKind)
            || request.Scope is not { } scope)
        {
            return TypedResults.BadRequest();
        }

        if (await ResolveTargetAsync(options, credentials, client, ct).ConfigureAwait(false)
            is not { } target)
        {
            return TypedResults.Ok(EntityMonitoringView.NotConfigured(entityKind));
        }

        return TypedResults.Ok(
            await ChangingHeldEntityAsync(entityKind, coveId, target, identities, log, Scoping, ct)
                .ConfigureAwait(false));

        async Task<EntityMonitoringView> Scoping(
            HeldActing acting, int entityId, bool monitored, CancellationToken changeCt)
        {
            if (acting.SetScope is not { } setScope)
            {
                throw new InvalidOperationException(
                    $"A {entityKind} expresses no monitor scope, so this route must not reach it.");
            }

            var applied = await ContainedAsync(
                () => setScope(entityId, scope, changeCt), target, log, changeCt).ConfigureAwait(false);

            // The scope the instance just took, not one read back: this is the one path where the
            // product knows what was applied because it applied it. An entity nothing monitors has
            // no scope in force whatever was written, so that answers none.
            MonitorScope? inForce = monitored ? scope : null;

            if (applied is null)
            {
                return Refused(entityKind, target, MonitorRefusalKind.InstanceRefused);
            }

            var refused = MonitoringProjector.Accepted(applied);
            return refused != MonitorRefusalKind.None
                ? Refused(entityKind, target, refused)
                : State(entityKind, target, present: true, monitored, inForce);
        }
    }

    /// <summary>
    /// How many Cove ids one bulk request may carry.
    /// </summary>
    /// <remarks>
    /// Each id is fanned out into per-entity requests against a third party, so a caller-supplied
    /// array is an unbounded fan-out. The bound is applied before anything is encoded or enqueued,
    /// and it sits far above any selection a page can make. A larger job is the caller's to split.
    /// </remarks>
    private const int MaxEntityIdsPerRequest = 1000;

    /// <summary>
    /// How many Cove ids one scene selection may carry for the search verb.
    /// </summary>
    /// <remarks>
    /// One press of that verb becomes one search per scene against every indexer the instance has,
    /// so its cost multiplies outside Cove in a way the other four verbs' does not, and it takes a
    /// lower bound of its own. The bound is applied before anything is encoded or enqueued, and it
    /// is answered under its own code so a caller can state the limit that applied.
    /// </remarks>
    private const int MaxSceneSearchIdsPerRequest = 100;

    /// <summary>The prefix the host mints onto every job type this extension enqueues.</summary>
    private string OwnJobTypePrefix => "ext:" + Id + ":";

    /// <summary>Enqueues one bulk monitoring gesture over a whole selection.</summary>
    /// <remarks>
    /// The gate is re-checked here, in the first statement, because the host's own permission filter
    /// is inert on a minimal-API endpoint - and the required permission the manifest declares beside
    /// the action is a UI affordance only, which hides a button and enforces nothing.
    /// <para>
    /// The id array is capped BEFORE anything is encoded or enqueued, and an oversized one is refused
    /// with the bound named so a caller can split rather than guess.
    /// </para>
    /// <para>
    /// An empty selection is refused rather than enqueued. A job that does nothing still appears in
    /// the host's Job Drawer, where it reads as work that happened.
    /// </para>
    /// <para>
    /// Enqueued EXCLUSIVE. A monitor batch mutates only Whisparr's own flags, so exclusivity is not
    /// required for correctness; what it prevents is two batches over overlapping selections issuing
    /// overlapping adds. This is reasoned rather than measured, and the cost if it is wrong is that
    /// two batches run one after the other.
    /// </para>
    /// </remarks>
    internal Results<Accepted<JobEnqueued>, BadRequest<ErrorCode>, ForbiddenCode> BulkMonitorEnqueue(
        MonitorBulkRequest request,
        ICurrentPrincipalAccessor principal,
        IJobService jobs,
        IServiceScopeFactory scopes)
    {
        if (!HasConfigurePermission(principal))
        {
            return new ForbiddenCode();
        }

        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(jobs);

        if (!TryParseSelectionType(request.EntityType, out _))
        {
            return TypedResults.BadRequest(new ErrorCode("UNSUPPORTED_ENTITY_TYPE"));
        }

        // Before the id guards rather than beside them. The verb decides what the request IS, so a
        // body naming none is refused without the size of the selection mattering: a caller told to
        // split an over-cap selection would send two halves, each still naming no verb.
        if (request.Verb is not { } verb)
        {
            return TypedResults.BadRequest(new ErrorCode("MISSING_VERB"));
        }

        if (request.EntityIds is not { } entityIds)
        {
            return TypedResults.BadRequest(new ErrorCode("MISSING_ENTITY_IDS"));
        }

        if (entityIds.Length > MaxEntityIdsPerRequest)
        {
            return TypedResults.BadRequest(new ErrorCode("TOO_MANY_IDS", MaxEntityIdsPerRequest));
        }

        if (entityIds.Length == 0)
        {
            return TypedResults.BadRequest(new ErrorCode("NOTHING_SELECTED"));
        }

        var parameters = MonitoringBulkJob.Encode(
            request.EntityType!, verb, request.Scope, entityIds);

        var jobId = jobs.Enqueue(
            OwnJobTypePrefix + MonitoringBulkJob.JobId,
            $"[{Name}] Monitoring, {entityIds.Length} selected",
            (progress, ct) => RunBulkMonitorAsync(parameters, scopes, progress, ct),
            exclusive: true);

        return TypedResults.Accepted((string?)null, new JobEnqueued(jobId));
    }

    /// <summary>Where one of this extension's own runs has got to.</summary>
    /// <remarks>
    /// This extension serves it because Cove gates its own job route on unrestricted read, so a
    /// scoped account is refused there even for a run it started itself.
    /// <para>
    /// A job whose type does not carry this extension's own prefix is answered NOT FOUND rather than
    /// forbidden. Answering forbidden would confirm that the id names a real job, which is exactly the
    /// fact the host's own gate withholds, and would make this route a way around that gate rather
    /// than a replacement for the part of it this extension owns.
    /// </para>
    /// </remarks>
    internal Results<Ok<BulkJobStatus>, NotFound, ForbiddenCode> BulkJobStatusOf(
        string jobId, ICurrentPrincipalAccessor principal, IJobService jobs)
    {
        if (!HasConfigurePermission(principal))
        {
            return new ForbiddenCode();
        }

        ArgumentNullException.ThrowIfNull(jobs);

        var job = jobs.GetJob(jobId);
        return job is null || !job.Type.StartsWith(OwnJobTypePrefix, StringComparison.Ordinal)
            ? TypedResults.NotFound()
            : TypedResults.Ok(BulkJobStatus.From(job));
    }

    /// <summary>Runs one enqueued batch.</summary>
    /// <remarks>
    /// The parameters are decoded tolerantly, so a batch nobody can read does nothing rather than
    /// faulting inside the host's job runner. A verb or a selection type the map does not name is
    /// that same case.
    /// <para>
    /// The target is resolved once, on the first entity's turn, and reused for the rest: it is one
    /// stored read and one credential read, and taking them per entity would be a batch of them.
    /// </para>
    /// <para>
    /// A cancellation is rethrown after the summary is written, so the host classifies the run as
    /// cancelled rather than completed while the reader is still told what it managed to do.
    /// </para>
    /// </remarks>
    private async Task RunBulkMonitorAsync(
        IReadOnlyDictionary<string, string> parameters,
        IServiceScopeFactory scopes,
        CoreJobProgress progress,
        CancellationToken ct)
    {
        var batch = MonitoringBulkJob.Decode(parameters);

        MonitoringTarget? target = null;
        var targetResolved = false;

        ReflectOwnedAiming? linkingThrough = null;
        ReflectOwnedSkipReason? linkingSkipped = null;
        var linkingResolved = false;
        var foldersAttached = 0;
        var foldersRefused = 0;
        var linkingReached = false;

        // One line per library root for the whole selection. Every entity under one root reaches the
        // same reason, and a line per entity would grow with the selection.
        var addressRefusals = new Dictionary<string, FolderAddressRefusal>(StringComparer.Ordinal);
        var addressedRoots = new HashSet<string>(StringComparer.Ordinal);

        var run = TryParseSelectionType(batch.EntityType, out var kind) && batch.Verb is { } verb
            ? await UnderTheVerbAsync().ConfigureAwait(false)
            : MonitorBulkRun.NothingSelected;

        await RecordRootReadingsAsync(scopes, [.. addressRefusals.Values], [.. addressedRoots])
            .ConfigureAwait(false);

        // The host's progress carries no summary field, so the run's one line rides the final
        // report's sub-task.
        progress.Report(
            1d,
            MonitoringBulkJob.SummaryOf(
                run,
                linkingReached
                    ? new MonitorBulkLinking(
                        linkingSkipped, foldersAttached, foldersRefused, [.. addressRefusals.Values])
                    : null));
        ct.ThrowIfCancellationRequested();

        // The search verb's instance-side command names an id array, so the whole selection is one
        // call. Every other verb here is one instance call per entity, and loops.
        Task<MonitorBulkRun> UnderTheVerbAsync()
            => verb == MonitorBulkVerb.SearchAllMonitored
                ? MonitoringBulkJob.RunOneCallAsync(
                    batch.EntityIds, scopes, AimOneAsync, SearchNamedAsync, progress, ct)
                : MonitoringBulkJob.RunAsync(batch.EntityIds, scopes, ActOnOneAsync, progress, ct);

        // Resolved once for the whole batch: it is one stored read and one credential read, and
        // taking them per entity would be a batch of them.
        async Task<MonitoringTarget?> ResolvedAsync(
            IServiceProvider services, CancellationToken runCt)
        {
            if (!targetResolved)
            {
                target = await ResolveTargetAsync(
                    services.GetRequiredService<OptionsStore>(),
                    services.GetRequiredService<ICredentialPort>(),
                    services.GetRequiredService<IWhisparrClient>(),
                    runCt).ConfigureAwait(false);
                targetResolved = true;
            }

            return target;
        }

        async Task<MonitoringBulkJob.MonitorBulkAim> AimOneAsync(
            IServiceProvider services, int coveId, CancellationToken entityCt)
            => await ResolvedAsync(services, entityCt).ConfigureAwait(false) is not { } resolved
                ? new MonitoringBulkJob.MonitorBulkAim(null, MonitorRefusalKind.NotConfigured)
                : await AimSearchAsync(
                    kind,
                    coveId,
                    resolved,
                    services.GetRequiredService<IEntityIdentityPort>(),
                    SearchGrabbingOn(resolved) is not null,
                    _log,
                    entityCt).ConfigureAwait(false);

        // One command naming every entity the resolution step established the instance holds. Its
        // answer is each of those entities' outcome, because the instance answers the command and
        // not the ids inside it.
        async Task<MonitorRefusalKind> SearchNamedAsync(
            IServiceProvider services, IReadOnlyList<int> entityIds, CancellationToken runCt)
        {
            if (await ResolvedAsync(services, runCt).ConfigureAwait(false) is not { } resolved)
            {
                return MonitorRefusalKind.NotConfigured;
            }

            if (SearchGrabbingOn(resolved) is not { } grabbing)
            {
                return MonitorRefusalKind.CapabilityAbsentOnThisGeneration;
            }

            var searched = await ContainedAsync(
                () => grabbing.SearchMonitoredAsync(
                    resolved.BaseAddress, resolved.ApiKey, resolved.Generation, kind, entityIds, runCt),
                resolved,
                _log,
                runCt).ConfigureAwait(false);

            return searched is null
                ? MonitorRefusalKind.InstanceRefused
                : MonitoringProjector.Accepted(searched);
        }

        async Task<MonitorRefusalKind> ActOnOneAsync(
            IServiceProvider services, int coveId, CancellationToken entityCt)
        {
            if (await ResolvedAsync(services, entityCt).ConfigureAwait(false) is not { } resolved)
            {
                return MonitorRefusalKind.NotConfigured;
            }

            var identities = services.GetRequiredService<IEntityIdentityPort>();

            // The same statement of each verb the single-entity route reaches, so a selection cannot
            // behave differently from a click. A verb the connected generation cannot honour is
            // answered per entity by that shared path rather than failing the batch.
            var view = verb switch
            {
                MonitorBulkVerb.Monitor => await MonitorResolvedAsync(
                    kind,
                    coveId,
                    resolved,
                    identities,
                    _log,
                    ActingFor(kind, resolved, batch.Scope ?? resolved.DefaultMonitorScope),
                    EntityRootIn(services, resolved, FilesOfEntity(kind, coveId)),
                    entityCt).ConfigureAwait(false),
                MonitorBulkVerb.Unmonitor => await UnmonitorResolvedAsync(
                    kind, coveId, resolved, identities, _log, entityCt).ConfigureAwait(false),
                _ => throw new InvalidOperationException(
                    $"{verb} is not a verb the bulk surface carries."),
            };

            // Inline rather than enqueued, and only for a monitor a read confirmed. The click
            // enqueues so the request does not wait for an entity's folder set; a selection is
            // already inside a run, and enqueuing per entity would make one gesture a run per entity.
            if (verb == MonitorBulkVerb.Monitor
                && view is { Refusal: MonitorRefusalKind.None, Monitored: true })
            {
                await LinkOwnedAsync(services, resolved, coveId, entityCt).ConfigureAwait(false);
            }

            return view.Refusal;
        }

        async Task LinkOwnedAsync(
            IServiceProvider services, MonitoringTarget resolved, int coveId, CancellationToken entityCt)
        {
            if (!linkingResolved)
            {
                linkingResolved = true;

                // The hard-link setting is a property of the INSTANCE, resolved once for the batch
                // the way the target is. A selection of a thousand entities must not read one value
                // a thousand times.
                if (ReflectOwnedActingOn(resolved) is { } acting)
                {
                    linkingReached = true;
                    var decision = await ReflectOwnedDecisionAsync(resolved, acting, entityCt)
                        .ConfigureAwait(false);
                    linkingSkipped = decision.Reason;
                    linkingThrough = decision.Act
                        ? AimedAt(
                            resolved, acting, services.GetRequiredService<IFolderAddressPort>())
                        : null;
                }
            }

            if (linkingThrough is not { } aimed)
            {
                return;
            }

            var linked = await ReflectOwnedJob
                .RunOneAsync(services, aimed, kind, coveId, entityCt).ConfigureAwait(false);
            foldersAttached += linked.FoldersAttached;
            foldersRefused += linked.FoldersRefused;
            foreach (var root in linked.AddressedRoots ?? [])
            {
                addressedRoots.Add(root);
            }

            foreach (var refusal in linked.AddressRefusals ?? [])
            {
                addressRefusals.TryAdd(refusal.CoveRoot, refusal);
            }
        }
    }

    /// <summary>
    /// The entity kind <paramref name="entityType"/> names, in the spelling the selection bar passes.
    /// </summary>
    /// <remarks>
    /// Matched against the same constants the registration declares, so what the bar has to send to
    /// see the button and what the route accepts cannot drift apart.
    /// </remarks>
    private static bool TryParseSelectionType(string? entityType, out WhisparrEntityKind kind)
    {
        switch (entityType)
        {
            case StudiosSelectionType:
                kind = WhisparrEntityKind.Studio;
                return true;
            case PerformersSelectionType:
                kind = WhisparrEntityKind.Performer;
                return true;
            default:
                kind = default;
                return false;
        }
    }

    /// <summary>What the connected instance is, and what its generation can honour.</summary>
    /// <remarks>
    /// The stored default scope rides here because the same load that resolved the connection read
    /// it, so an acting path takes it without issuing a second read.
    /// </remarks>
    private sealed record MonitoringTarget(
        WhisparrGeneration Generation,
        Uri BaseAddress,
        string ApiKey,
        WhisparrCapabilitySet Capabilities,
        IWhisparrClient Reads,
        MonitorScope DefaultMonitorScope);

    /// <summary>The instance to act against, or null when none is configured.</summary>
    /// <summary>Records what one run established about the Cove library roots it reached.</summary>
    /// <remarks>
    /// A run that reached no folder at all writes nothing: it established nothing about any root, and
    /// its zero counts are the run never having been aimed rather than the roots disagreeing.
    /// <para>
    /// Written outside the run's own cancellation. What a run established about a root holds whether
    /// or not the run went on to finish, and a stopped run that dropped its readings would leave the
    /// settings page asking about roots it had just agreed with.
    /// </para>
    /// </remarks>
    private static async Task RecordRootReadingsAsync(
        IServiceScopeFactory scopes,
        IReadOnlyList<FolderAddressRefusal>? refused,
        IReadOnlyList<string>? addressed)
    {
        if (refused is not { Count: > 0 } && addressed is not { Count: > 0 })
        {
            return;
        }

        using var scope = scopes.CreateScope();
        var services = scope.ServiceProvider;

        await services.GetRequiredService<OptionsWriteGate>().MutateAsync(
            services.GetRequiredService<OptionsStore>(),
            stored => stored with
            {
                OutboundRefusals = OutboundRefusalProjector.Fold(
                    stored.OutboundRefusals, refused, addressed),
            },
            CancellationToken.None).ConfigureAwait(false);
    }

    private static async Task<MonitoringTarget?> ResolveTargetAsync(
        OptionsStore options, ICredentialPort credentials, IWhisparrClient client, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(credentials);

        var stored = await options.LoadAsync(ct).ConfigureAwait(false);
        var generation = stored.SelectedGeneration;
        var apiKey = await credentials.ReadAsync(generation, ct).ConfigureAwait(false);

        // Refused here rather than by handing an empty pair to the client, so an unconfigured
        // connection reaches nothing that could make a request.
        return ConnectionTester.TryReadConnection(
                stored.ConnectionFor(generation)?.Address, apiKey, out var baseAddress, out _)
            ? new MonitoringTarget(
                generation,
                baseAddress,
                apiKey,
                GenerationCapabilities.For(generation, WhisparrRoleSet.From(client)),
                client,
                stored.DefaultMonitorScope)
            : null;
    }

    /// <summary>The acting verbs one entity kind is monitored through, already aimed.</summary>
    /// <remarks>
    /// One kind's members reduced to what the shared flow needs, so the flow is written once and the
    /// difference between the two kinds is confined to where each is built. The scope is bound where
    /// the studio's verbs are, so no shared step can carry a scope to a kind that expresses none.
    /// </remarks>
    private sealed record KindActing(
        HeldActing Held,
        Func<AddDefaults, CancellationToken, Task<WhisparrResponse>> AddMonitored);

    /// <summary>The verbs an entity the instance ALREADY holds is changed through.</summary>
    /// <remarks>
    /// Separate from the add, and reachable without naming a scope, because unmonitoring names none.
    /// A shared record carrying the add's bound scope would hand every verb a scope its caller never
    /// chose.
    /// <para>
    /// <see cref="SetScope"/> is null for a kind expressing no scope, so a scope cannot reach one
    /// through this seam at all rather than reaching a member that refuses once it is called.
    /// </para>
    /// </remarks>
    private sealed record HeldActing(
        Func<CancellationToken, Task<WhisparrResponse>> ReadEntity,
        Func<int, bool, CancellationToken, Task<WhisparrResponse>> SetMonitored,
        Func<int, MonitorScope, CancellationToken, Task<WhisparrResponse>>? SetScope);

    private static KindActing ActingOn(
        IWhisparrStudioActing acting, MonitoringTarget target, string foreignId, MonitorScope scope)
        => new(
            HeldOn(acting, target, foreignId),
            (defaults, addCt) => acting.AddMonitoredStudioAsync(
                target.BaseAddress, target.ApiKey, target.Generation, foreignId, scope, defaults, addCt));

    private static KindActing ActingOn(
        IWhisparrPerformerActing acting, MonitoringTarget target, string foreignId)
        => new(
            HeldOn(acting, target, foreignId),
            (defaults, addCt) => acting.AddMonitoredPerformerAsync(
                target.BaseAddress, target.ApiKey, foreignId, defaults, addCt));

    private static HeldActing HeldOn(
        IWhisparrStudioActing acting, MonitoringTarget target, string foreignId)
        => new(
            readCt => acting.ReadStudioAsync(
                target.BaseAddress, target.ApiKey, target.Generation, foreignId, readCt),
            (entityId, monitored, flipCt) => acting.SetStudioMonitoredAsync(
                target.BaseAddress, target.ApiKey, target.Generation, entityId, monitored, flipCt),
            (entityId, scope, scopeCt) => acting.SetStudioScopeAsync(
                target.BaseAddress, target.ApiKey, target.Generation, entityId, scope, scopeCt));

    private static HeldActing HeldOn(
        IWhisparrPerformerActing acting, MonitoringTarget target, string foreignId)
        => new(
            readCt => acting.ReadPerformerAsync(target.BaseAddress, target.ApiKey, foreignId, readCt),
            (entityId, monitored, flipCt) => acting.SetPerformerMonitoredAsync(
                target.BaseAddress, target.ApiKey, entityId, monitored, flipCt),
            SetScope: null);

    private static async Task<EntityMonitoringView> ReadResolvedAsync(
        WhisparrEntityKind kind,
        int coveId,
        MonitoringTarget target,
        IEntityIdentityPort identities,
        ILogger log,
        Func<string, CancellationToken, Task<WhisparrResponse>>? readEntity,
        CancellationToken ct)
    {
        var identity = await identities.ResolveAsync(kind, coveId, target.Generation, ct)
            .ConfigureAwait(false);

        if (readEntity is not { } reading || identity.ForeignId is not { } foreignId)
        {
            return Refused(kind, target, RefusalAmong(readEntity is null, identity.Refusal));
        }

        var read = await ContainedAsync(
            () => reading(foreignId, ct), target, log, ct).ConfigureAwait(false);

        if (read is null)
        {
            return Refused(kind, target, MonitorRefusalKind.InstanceRefused);
        }

        var answer = MonitoringProjector.Classify(read);
        return answer.Reading switch
        {
            // Not held is not a refusal: the instance holds no entry, and the entity is simply not
            // monitored yet. The two are answered as separate members, so a reader is not left to
            // infer an absence from an unmonitored flag.
            MonitoringProjector.EntityReading.NotHeld
                => State(kind, target, present: false, monitored: false, scope: null),
            MonitoringProjector.EntityReading.Held => Held(read.Body),
            _ => Refused(kind, target, RefusalIn(answer)),
        };

        EntityMonitoringView Held(string body)
        {
            var presence = MonitoringProjector.PresenceOf(
                MonitoringProjector.EntityReading.Held, body);
            var monitored = presence.Monitored ?? false;
            return State(
                kind, target, present: true, monitored, ScopeHeld(kind, target, monitored, body));
        }
    }

    /// <summary>How many of one entity's own files sit under one library root.</summary>
    private static Func<IEntityFolderPort, string, CancellationToken, Task<int>> FilesOfEntity(
        WhisparrEntityKind kind, int coveId)
        => (files, coveRoot, ct) => files.FilesUnderAsync(kind, coveId, coveRoot, ct);

    /// <summary>How many of one video's own files sit under one library root.</summary>
    private static Func<IEntityFolderPort, string, CancellationToken, Task<int>> FilesOfVideo(
        int videoId)
        => (files, coveRoot, ct) => files.VideoFilesUnderAsync(videoId, coveRoot, ct);

    /// <summary>
    /// Composes an add's root per entity from a path holding no elevated services of its own.
    /// </summary>
    /// <remarks>
    /// The count is taken inside a System scope. Cove's per-principal query filters answer a reader
    /// with the rows that reader can see, so a count taken as the caller would report an entity that
    /// holds files as holding none, and the add would then go to the wrong root with no error.
    /// </remarks>
    private static Func<AddDefaults, CancellationToken, Task<EntityAddDefaultsResolution>>
        EntityRootThrough(
            IServiceScopeFactory scopes,
            MonitoringTarget target,
            Func<IEntityFolderPort, string, CancellationToken, Task<int>> countUnder)
        => (runWide, ct) => RunAsSystem.RunInSystemScopeAsync(
            scopes, services => ComposeWithEntityRootAsync(services, target, countUnder, runWide, ct));

    /// <summary>Composes an add's root per entity inside a run's own elevated services.</summary>
    private static Func<AddDefaults, CancellationToken, Task<EntityAddDefaultsResolution>>
        EntityRootIn(
            IServiceProvider services,
            MonitoringTarget target,
            Func<IEntityFolderPort, string, CancellationToken, Task<int>> countUnder)
        => (runWide, ct) => ComposeWithEntityRootAsync(services, target, countUnder, runWide, ct);

    /// <summary>The one place every add body's root is composed, whatever doorway reached it.</summary>
    private static Task<EntityAddDefaultsResolution> ComposeWithEntityRootAsync(
        IServiceProvider services,
        MonitoringTarget target,
        Func<IEntityFolderPort, string, CancellationToken, Task<int>> countUnder,
        AddDefaults runWide,
        CancellationToken ct)
    {
        var files = services.GetRequiredService<IEntityFolderPort>();
        return EntityAddDefaults.ComposeAsync(
            runWide,
            services.GetRequiredService<ICoveLibraryPort>().LibraryRoots,
            (coveRoot, countCt) => countUnder(files, coveRoot, countCt),
            AgreedRootThrough(target, services.GetRequiredService<IFolderAddressPort>()),
            ct);
    }

    /// <summary>Monitors one entity, at the scope <paramref name="actingFor"/> was armed with.</summary>
    /// <remarks>
    /// The scope reaches the instance through the arm and is never answered from here: every branch
    /// answers a read, for the reason <see cref="ScopeHeld"/> states. So a caller composing a scope
    /// supplies it once, to <paramref name="actingFor"/>, and reads the result back off the
    /// instance's own answer.
    /// </remarks>
    private static async Task<EntityMonitoringView> MonitorResolvedAsync(
        WhisparrEntityKind kind,
        int coveId,
        MonitoringTarget target,
        IEntityIdentityPort identities,
        ILogger log,
        Func<string, KindActing>? actingFor,
        Func<AddDefaults, CancellationToken, Task<EntityAddDefaultsResolution>> composeAdd,
        CancellationToken ct)
    {
        var identity = await identities.ResolveAsync(kind, coveId, target.Generation, ct)
            .ConfigureAwait(false);

        if (actingFor is not { } aiming || identity.ForeignId is not { } foreignId)
        {
            return Refused(kind, target, RefusalAmong(actingFor is null, identity.Refusal));
        }

        var acting = aiming(foreignId);
        var read = await ContainedAsync(() => acting.Held.ReadEntity(ct), target, log, ct)
            .ConfigureAwait(false);
        if (read is null)
        {
            return Refused(kind, target, MonitorRefusalKind.InstanceRefused);
        }

        var answer = MonitoringProjector.Classify(read);
        switch (answer.Reading)
        {
            case MonitoringProjector.EntityReading.Held:
                return await MonitorHeldEntityAsync(kind, read.Body, target, acting, log, ct)
                    .ConfigureAwait(false);
            case MonitoringProjector.EntityReading.NotHeld:
                break;
            default:
                return Refused(kind, target, RefusalIn(answer));
        }

        var profiles = await ContainedAsync(
            () => target.Reads.ReadQualityProfilesAsync(target.BaseAddress, target.ApiKey, ct),
            target,
            log,
            ct).ConfigureAwait(false);
        var roots = profiles is null
            ? null
            : await ContainedAsync(
                () => target.Reads.ReadRootFoldersAsync(target.BaseAddress, target.ApiKey, ct),
                target,
                log,
                ct).ConfigureAwait(false);
        if (profiles is null || roots is null)
        {
            return Refused(kind, target, MonitorRefusalKind.InstanceRefused);
        }

        var defaults = AddDefaultsProjector.From(profiles.Body, roots.Body);
        if (defaults.Defaults is not { } runWide)
        {
            return Refused(kind, target, defaults.Refusal);
        }

        var composed = await composeAdd(runWide, ct).ConfigureAwait(false);
        if (composed.Defaults is not { } composeWith)
        {
            return Refused(kind, target, composed.Refusal);
        }

        var added = await ContainedAsync(
            () => acting.AddMonitored(composeWith, ct), target, log, ct).ConfigureAwait(false);

        if (added is null)
        {
            return Refused(kind, target, MonitorRefusalKind.InstanceRefused);
        }

        var refused = MonitoringProjector.Accepted(added);
        return refused != MonitorRefusalKind.None
            ? Refused(kind, target, refused)
            : await ReadBackMonitoredAsync(kind, target, acting, log, ct)
                .ConfigureAwait(false);
    }

    /// <summary>Reads the entity again and answers the state that read reports.</summary>
    /// <remarks>
    /// The evidence a write took effect is a later read rather than the write's own status. This
    /// generation answers an add it did not understand with a created status and an echo showing the
    /// monitored field dropped, so an accepted write the instance then reports unmonitored is a
    /// refusal.
    /// <para>
    /// The scope answered is the read's own, for the reason <see cref="ScopeHeld"/> states: what an
    /// add composed and what a later read reports are different facts, and this generation answers a
    /// body whose fields it dropped with a success. So a composed scope cannot stand in for one.
    /// </para>
    /// <para>
    /// A read that cannot be classified is a refusal too: what the instance holds is then unknown,
    /// and unknown is not evidence.
    /// </para>
    /// <para>
    /// The refusal it answers is <see cref="MonitorRefusalKind.InstanceDidNotReportTheChange"/> and
    /// not the refusal a read's own classification names. This site is reached only after a write was
    /// accepted, so an absence here is not "there was nothing to act on" and a reading this product
    /// rejected is not "nothing was changed": the sentences those kinds carry would tell a reader
    /// that nothing happened when something may well have. A refusal the answering seam read for
    /// itself is kept, because that names this product's own limit rather than what the instance now
    /// holds.
    /// </para>
    /// <para>
    /// The cost is one more outbound read per entity, which a batch pays per selected entity. It
    /// already issues a read and a write for each, so this is what turns a reported outcome into an
    /// observed one for a third of an increase.
    /// </para>
    /// </remarks>
    private static async Task<EntityMonitoringView> ReadBackMonitoredAsync(
        WhisparrEntityKind kind,
        MonitoringTarget target,
        KindActing acting,
        ILogger log,
        CancellationToken ct)
    {
        var read = await ContainedAsync(() => acting.Held.ReadEntity(ct), target, log, ct)
            .ConfigureAwait(false);

        if (read is null)
        {
            return Refused(kind, target, MonitorRefusalKind.InstanceRefused);
        }

        var answer = MonitoringProjector.Classify(read);
        if (answer.Reading == MonitoringProjector.EntityReading.Held
            && MonitoringProjector.MonitoredIn(read.Body))
        {
            return State(
                kind,
                target,
                present: true,
                monitored: true,
                ScopeHeld(kind, target, monitored: true, read.Body));
        }

        var refusal = answer.Refusal is MonitorRefusalKind.None
            ? MonitorRefusalKind.InstanceDidNotReportTheChange
            : answer.Refusal;

        return Refused(kind, target, refusal);
    }

    /// <summary>Whether <paramref name="kind"/> expresses a monitor scope at all.</summary>
    /// <remarks>
    /// Transcribed rather than derived from the acting seam, so a kind added later is classified by
    /// whoever adds it. The field a narrower scope is carried in exists on one resource only.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="kind"/> is not a kind this product expresses.
    /// </exception>
    private static bool ExpressesAScope(WhisparrEntityKind kind)
        => kind switch
        {
            WhisparrEntityKind.Studio => true,
            WhisparrEntityKind.Performer => false,
            _ => throw new ArgumentOutOfRangeException(
                nameof(kind), kind, "This is not an entity kind this product expresses."),
        };

    /// <summary>
    /// Resolves identity, reads the entity, and applies <paramref name="change"/> to one the instance
    /// holds.
    /// </summary>
    /// <remarks>
    /// The order is the monitor route's own: identity first, so a refusal costs no outbound request,
    /// then the entity itself. Nothing the instance holds is nothing to change, and it is not
    /// monitored either, so that answers the current state and sends nothing.
    /// <para>
    /// The instance-side row id is read from the entity's own record rather than substituted. It
    /// exists only for an entity the instance holds, so an absent one is refused rather than guessed.
    /// </para>
    /// </remarks>
    private static async Task<EntityMonitoringView> ChangingHeldEntityAsync(
        WhisparrEntityKind kind,
        int coveId,
        MonitoringTarget target,
        IEntityIdentityPort identities,
        ILogger log,
        Func<HeldActing, int, bool, CancellationToken, Task<EntityMonitoringView>> change,
        CancellationToken ct)
    {
        var acting = HeldActingFor(kind, target);
        var identity = await identities.ResolveAsync(kind, coveId, target.Generation, ct)
            .ConfigureAwait(false);

        if (acting is not { } actingFor || identity.ForeignId is not { } named)
        {
            return Refused(kind, target, RefusalAmong(acting is null, identity.Refusal));
        }

        var held = actingFor(named);
        var read = await ContainedAsync(() => held.ReadEntity(ct), target, log, ct)
            .ConfigureAwait(false);
        if (read is null)
        {
            return Refused(kind, target, MonitorRefusalKind.InstanceRefused);
        }

        var answer = MonitoringProjector.Classify(read);
        switch (answer.Reading)
        {
            case MonitoringProjector.EntityReading.Held:
                break;
            case MonitoringProjector.EntityReading.NotHeld:
                return State(kind, target, present: false, monitored: false, scope: null);
            default:
                return Refused(kind, target, RefusalIn(answer));
        }

        return MonitoringProjector.EntityIdIn(read.Body) is { } entityId
            ? await change(held, entityId, MonitoringProjector.MonitoredIn(read.Body), ct)
                .ConfigureAwait(false)
            : Refused(kind, target, MonitorRefusalKind.InstanceRefused);
    }

    /// <summary>Turns monitoring on for an entity the instance already holds.</summary>
    /// <remarks>
    /// A held entity keeps its own profile, root folder, tags and date gate: only the flag is sent,
    /// and every other field of the editor resource is left unset because an unset field is not
    /// applied. Reporting the click as done without sending the flip would be a success for something
    /// that did not happen.
    /// </remarks>
    private static async Task<EntityMonitoringView> MonitorHeldEntityAsync(
        WhisparrEntityKind kind,
        string body,
        MonitoringTarget target,
        KindActing acting,
        ILogger log,
        CancellationToken ct)
    {
        if (MonitoringProjector.MonitoredIn(body))
        {
            // Nothing is sent, so the read in hand IS the state: both the flag and the date gate it
            // reports are what the entity is left at.
            return State(
                kind,
                target,
                present: true,
                monitored: true,
                ScopeHeld(kind, target, monitored: true, body));
        }

        if (MonitoringProjector.EntityIdIn(body) is not { } entityId)
        {
            return Refused(kind, target, MonitorRefusalKind.InstanceRefused);
        }

        var flipped = await ContainedAsync(
            () => acting.Held.SetMonitored(entityId, true, ct), target, log, ct).ConfigureAwait(false);

        // Classified from a read for the same reason the add branch is, stated at ReadBackMonitoredAsync.
        if (flipped is null)
        {
            return Refused(kind, target, MonitorRefusalKind.InstanceRefused);
        }

        var refused = MonitoringProjector.Accepted(flipped);
        return refused != MonitorRefusalKind.None
            ? Refused(kind, target, refused)
            : await ReadBackMonitoredAsync(kind, target, acting, log, ct)
                .ConfigureAwait(false);
    }

    /// <summary>Which refusal a classified answer states.</summary>
    /// <remarks>
    /// Total over both members of the answer, in this order. A refusal the answering seam read for
    /// itself outranks the status, for the reason <see cref="MonitoringProjector.Classify"/> states.
    /// A not-held reading is its own fact: the instance reported an absence rather than declining, and
    /// a reader acts on the two differently. Everything left is the instance refusing, which includes
    /// a held entity the flow rejected for a reason of its own, such as one carrying no
    /// instance-side id.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="answer"/> carries a reading this product does not express. Every reading is
    /// named, so one added later stops here rather than arriving under whichever arm a fallthrough
    /// chose.
    /// </exception>
    private static MonitorRefusalKind RefusalIn(MonitoringProjector.EntityAnswer answer)
        => (answer.Refusal, answer.Reading) switch
        {
            (not MonitorRefusalKind.None, _) => answer.Refusal,
            (_, MonitoringProjector.EntityReading.NotHeld)
                => MonitorRefusalKind.InstanceHoldsNoSuchEntity,
            (_, MonitoringProjector.EntityReading.Held or MonitoringProjector.EntityReading.Refused)
                => MonitorRefusalKind.InstanceRefused,
            _ => throw new ArgumentOutOfRangeException(
                nameof(answer), answer.Reading, "This reading has no refusal written down for it."),
        };

    /// <summary>
    /// Which refusal to answer, given the two reasons a resolved flow can observe.
    /// </summary>
    /// <remarks>
    /// A connection has already been established wherever this is reached, so the first reason of the
    /// precedence cannot hold here. The order among the rest is not restated: it is read from the one
    /// place that states it, so a change there moves every route at once.
    /// </remarks>
    private static MonitorRefusalKind RefusalAmong(
        bool capabilityAbsent, MonitorRefusalKind identityRefusal)
        => MonitoringProjector.FirstRefusal(new MonitoringProjector.MonitorReasons(
            NoConnectionConfigured: false,
            CapabilityAbsentOnThisGeneration: capabilityAbsent,
            IdentityRefusal: identityRefusal));

    /// <summary>How one entity is read, or null where the generation cannot read that kind.</summary>
    private static Func<string, CancellationToken, Task<WhisparrResponse>>? ReadingEntity(
        WhisparrEntityKind kind, MonitoringTarget target)
        => kind switch
        {
            WhisparrEntityKind.Studio => target.Capabilities.Obtain<IWhisparrStudioActing>()
                .Match<Func<string, CancellationToken, Task<WhisparrResponse>>?>(
                    acting => (foreignId, readCt) => acting.ReadStudioAsync(
                        target.BaseAddress, target.ApiKey, target.Generation, foreignId, readCt),
                    _ => null),
            WhisparrEntityKind.Performer => target.Capabilities.Obtain<IWhisparrPerformerActing>()
                .Match<Func<string, CancellationToken, Task<WhisparrResponse>>?>(
                    acting => (foreignId, readCt) => acting.ReadPerformerAsync(
                        target.BaseAddress, target.ApiKey, foreignId, readCt),
                    _ => null),
            _ => NoArmFor<Func<string, CancellationToken, Task<WhisparrResponse>>>(kind, target),
        };

    /// <summary>How one entity is monitored, or null where the generation cannot monitor that kind.</summary>
    private static Func<string, KindActing>? ActingFor(
        WhisparrEntityKind kind, MonitoringTarget target, MonitorScope scope)
        => kind switch
        {
            WhisparrEntityKind.Studio => target.Capabilities.Obtain<IWhisparrStudioActing>()
                .Match<Func<string, KindActing>?>(
                    acting => foreignId => ActingOn(acting, target, foreignId, scope), _ => null),
            WhisparrEntityKind.Performer => target.Capabilities.Obtain<IWhisparrPerformerActing>()
                .Match<Func<string, KindActing>?>(
                    acting => foreignId => ActingOn(acting, target, foreignId), _ => null),
            _ => NoArmFor<Func<string, KindActing>>(kind, target),
        };

    /// <summary>
    /// How one entity the instance holds is changed, or null where the generation cannot.
    /// </summary>
    /// <summary>
    /// How <paramref name="target"/> is asked to search, or null where its generation holds no search
    /// at all.
    /// </summary>
    /// <remarks>
    /// Obtained BY NAME, and this is the only place in the product that does so, which is what makes
    /// "a call site that never asks for the role cannot express the request" a property of the type
    /// set rather than a habit. A generation holding no search has no implementation to hand over,
    /// which is a refusal at the caller rather than a member that accepts the call and declines it.
    /// <para>
    /// Both gestures that can reach a search come through here: the one over a single entity and the
    /// one over a selection. A third would have to be written against this same member.
    /// </para>
    /// </remarks>
    private static IWhisparrSearchGrabbing? SearchGrabbingOn(MonitoringTarget target)
        => target.Capabilities.Obtain<IWhisparrSearchGrabbing>()
            .Match<IWhisparrSearchGrabbing?>(held => held, _ => null);

    /// <summary>
    /// The instance's own identifier for one selected entity, or why the instance cannot be told to
    /// search it.
    /// </summary>
    /// <remarks>
    /// The same order the single-entity route takes: identity first, so an entity carrying no link
    /// costs no outbound request, then the instance's own record, which is what establishes that it
    /// holds the entity at all. An entity it does not hold monitors nothing there.
    /// </remarks>
    private static async Task<MonitoringBulkJob.MonitorBulkAim> AimSearchAsync(
        WhisparrEntityKind kind,
        int coveId,
        MonitoringTarget target,
        IEntityIdentityPort identities,
        bool searchHeld,
        ILogger log,
        CancellationToken ct)
    {
        var reading = HeldActingFor(kind, target);
        var identity = await identities.ResolveAsync(kind, coveId, target.Generation, ct)
            .ConfigureAwait(false);

        if (!searchHeld || reading is not { } actingFor || identity.ForeignId is not { } named)
        {
            return new MonitoringBulkJob.MonitorBulkAim(
                null, RefusalAmong(!searchHeld || reading is null, identity.Refusal));
        }

        var read = await ContainedAsync(() => actingFor(named).ReadEntity(ct), target, log, ct)
            .ConfigureAwait(false);
        if (read is null)
        {
            return new MonitoringBulkJob.MonitorBulkAim(null, MonitorRefusalKind.InstanceRefused);
        }

        var answer = MonitoringProjector.Classify(read);
        return answer.Reading != MonitoringProjector.EntityReading.Held
            || MonitoringProjector.EntityIdIn(read.Body) is not { } entityId
                ? new MonitoringBulkJob.MonitorBulkAim(null, RefusalIn(answer))
                : new MonitoringBulkJob.MonitorBulkAim(entityId, MonitorRefusalKind.None);
    }

    private static Func<string, HeldActing>? HeldActingFor(
        WhisparrEntityKind kind, MonitoringTarget target)
        => kind switch
        {
            WhisparrEntityKind.Studio => target.Capabilities.Obtain<IWhisparrStudioActing>()
                .Match<Func<string, HeldActing>?>(
                    acting => foreignId => HeldOn(acting, target, foreignId), _ => null),
            WhisparrEntityKind.Performer => target.Capabilities.Obtain<IWhisparrPerformerActing>()
                .Match<Func<string, HeldActing>?>(
                    acting => foreignId => HeldOn(acting, target, foreignId), _ => null),
            _ => NoArmFor<Func<string, HeldActing>>(kind, target),
        };

    /// <summary>Nothing to act through for a kind no route has an arm for.</summary>
    /// <remarks>
    /// The capability table is the authority, so a generation that HOLDS the capability while no
    /// route can act on it is a fault rather than a refusal: a capability is registered with the
    /// member that honours it, never ahead of it, and reporting a gap that does not exist would send
    /// the user to a sentence about their instance.
    /// </remarks>
    private static T? NoArmFor<T>(WhisparrEntityKind kind, MonitoringTarget target)
        where T : class
    {
        var capability = MonitoringProjector.CapabilityFor(kind);
        return target.Capabilities.Held.Contains(capability)
            ? throw new InvalidOperationException(
                $"{target.Generation} holds {capability}, but no route has an arm acting on a {kind}.")
            : null;
    }

    private static EntityMonitoringView Refused(
        WhisparrEntityKind kind, MonitoringTarget target, MonitorRefusalKind refusal)
        => EntityMonitoringView.Refused(kind, target.Generation, target.Capabilities.Held, refusal);

    private static EntityMonitoringView State(
        WhisparrEntityKind kind,
        MonitoringTarget target,
        bool present,
        bool monitored,
        MonitorScope? scope)
        => EntityMonitoringView.State(
            kind, target.Generation, target.Capabilities.Held, present, monitored, scope);

    /// <summary>The scope the entity <paramref name="body"/> describes is held at.</summary>
    /// <remarks>
    /// The instance's own answer is the only source. Neither acting path may substitute the scope it
    /// asked for here: what an add composed and what a later read reports are different facts, and
    /// this generation answers a body whose fields it dropped with a success.
    /// </remarks>
    private static MonitorScope? ScopeHeld(
        WhisparrEntityKind kind, MonitoringTarget target, bool monitored, string? body)
        => MonitoringProjector.ScopeIn(kind, target.Generation, monitored, body);

    /// <summary>
    /// <paramref name="request"/>'s answer, or null when it produced none.
    /// </summary>
    /// <remarks>
    /// Contained rather than propagated: it is raised into a route whose declared results hold no
    /// failure. Exactly one line is emitted, from a filter naming every exception it contains, and a
    /// named outcome is returned. A shutdown rethrows, because it is not a verdict about the
    /// instance.
    /// <para>
    /// The filter names an I/O failure as well as a request one. The client reads a body out of the
    /// response stream, so a connection dropped part way through an answer raises
    /// <see cref="IOException"/> rather than <see cref="HttpRequestException"/>; a batch that let one
    /// escape would lose the record of every entity it had already acted on.
    /// </para>
    /// </remarks>
    private static async Task<WhisparrResponse?> ContainedAsync(
        Func<Task<WhisparrResponse>> request,
        MonitoringTarget target,
        ILogger log,
        CancellationToken ct)
    {
        try
        {
            return await request().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception failure)
            when (failure is HttpRequestException or IOException or TaskCanceledException)
        {
            WhisparrSyncLog.MonitoringRequestContained(
                log, target.Generation, WhisparrSyncLog.Classify(failure), target.BaseAddress.Host);
            return null;
        }
    }

    /// <summary>Receives one callback from Whisparr and answers whether it was this product's.</summary>
    /// <remarks>
    /// Authenticated by a secret this product minted, not by a Cove permission, because the caller is
    /// another application rather than a Cove user. The secret is accepted from either position: a
    /// registration this product made carries it out of band, and an address a user pasted by hand has
    /// nowhere else to put one.
    /// <para>
    /// Runs as System. The caller carries no principal, and Cove's per-principal query filters answer
    /// an Anonymous reader with zero rows and no error, which would report the stored secret as absent
    /// and refuse every delivery.
    /// </para>
    /// <para>
    /// The body is read ONCE and only after the secret matches, so an unauthenticated delivery
    /// reaches no allocation, no filesystem probe and no host call. It is bounded by this
    /// extension's own cap rather than the framework's, which Cove configures nowhere and which
    /// defaults far above anything an instance sends.
    /// </para>
    /// <para>
    /// The answer names no path and does not say whether a file was found. The caller is anonymous,
    /// and an answer that varied with what is on disk would make this route a filesystem probe.
    /// </para>
    /// <para>
    /// Neither generation signs a delivery, so the secret is the whole of the authentication here. On
    /// a Cove whose own authentication is disabled, nothing else stands in front of this route: the
    /// host answers an unauthenticated in-network caller on privileged reads, issues no authentication
    /// challenge, and consults no proxy or trusted-host allow-list. Nothing here may imply a host-side
    /// failsafe.
    /// </para>
    /// </remarks>
    internal static async Task<Results<Ok<ImportAcknowledgement>, BadRequest, UnauthorizedHttpResult>> CallbackAsync(
        HttpContext http,
        IServiceScopeFactory scopes,
        ILogger log,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(scopes);

        var presented = CallbackSecret.PresentedIn(
            http.Request.Headers[CallbackSecret.CustomHeaderName],
            http.Request.Headers.Authorization,
            http.Request.Query[CallbackAddress.SecretQueryParameter]);

        var authenticated = presented is not null
            && await RunAsSystem.RunInSystemScopeAsync(scopes, async services =>
            {
                var stored = await services.GetRequiredService<ICallbackSecretPort>()
                    .ReadAsync(ct)
                    .ConfigureAwait(false);
                return CallbackSecret.Matches(stored, presented.Value);
            }).ConfigureAwait(false);

        if (!authenticated)
        {
            return TypedResults.Unauthorized();
        }

        if (await ReadBoundedBodyAsync(http.Request, ct).ConfigureAwait(false) is not { } body)
        {
            return TypedResults.BadRequest();
        }

        var generation = WebhookProjector.GenerationOf(http.Request.Headers.UserAgent);
        if (generation is null)
        {
            return TypedResults.BadRequest();
        }

        // Read outside the scope: the projection is pure, and a body that produces no candidate must
        // not open one.
        var reading = WebhookProjector.Read(generation.Value, body);
        if (reading.Outcome == WebhookProjectionOutcome.Unreadable)
        {
            return TypedResults.BadRequest();
        }

        var position = presented!.Position;
        var outcome = await RunAsSystem.RunInSystemScopeAsync(scopes, async services =>
        {
            await RecordSecretPositionAsync(
                services.GetRequiredService<OptionsStore>(),
                services.GetRequiredService<OptionsWriteGate>(),
                generation.Value,
                position,
                ct).ConfigureAwait(false);

            if (reading.Outcome == WebhookProjectionOutcome.Ignored)
            {
                NoteIgnoredEventType(log, generation.Value, reading.EventType);
                return ImportEventOutcome.Ignored;
            }

            // An act-list event carrying no readable path never reaches the core, and is recorded as
            // its own refusal rather than as an ignore: this product handles the event and did not
            // understand the body, which is a different fact from not handling the event.
            if (reading.Candidate is not { } candidate)
            {
                WhisparrSyncLog.ImportRefused(
                    log,
                    generation.Value,
                    ImportOutcome.RefusedUnreadablePayload,
                    ImportRefusalProjector.NoReportedRoot);
                return ImportEventOutcome.Accepted;
            }

            await services.GetRequiredService<IImportCore>()
                .IngestAsync(candidate, ct)
                .ConfigureAwait(false);
            return ImportEventOutcome.Accepted;
        }).ConfigureAwait(false);

        return TypedResults.Ok(new ImportAcknowledgement(position, outcome));
    }

    /// <summary>
    /// The request body as a JSON object, or null when it is too long, unparseable, or not an object.
    /// </summary>
    /// <remarks>
    /// Read once, into a buffer one byte longer than the cap, so a body past the cap is detected
    /// without being materialised. A declared length over the cap is refused before the stream is
    /// touched at all, and an undeclared one is caught by the buffer.
    /// <para>
    /// Parsed as a node rather than bound to a record. One generation publishes no contract, so what
    /// a body IS gets established by parsing it, and a record would assume a shape the other
    /// generation does not send.
    /// </para>
    /// </remarks>
    private static async Task<JsonObject?> ReadBoundedBodyAsync(HttpRequest request, CancellationToken ct)
    {
        if (request.ContentLength is > MaxCallbackBodyBytes)
        {
            return null;
        }

        var buffer = new byte[MaxCallbackBodyBytes + 1];
        var read = await request.Body
            .ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, ct)
            .ConfigureAwait(false);
        if (read > MaxCallbackBodyBytes)
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(buffer.AsSpan(0, read)) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// How long a delivery this product sent for may be.
    /// </summary>
    /// <remarks>
    /// This extension's own bound, not the framework's: Cove configures no maximum request body
    /// size, so the framework default applies and is orders of magnitude above anything an instance
    /// sends. The committed payload captures are the evidence for the scale.
    /// </remarks>
    internal const int MaxCallbackBodyBytes = 64 * 1024;

    /// <summary>
    /// Records an event type this product does not act on, once per distinct type.
    /// </summary>
    /// <remarks>
    /// Once per type rather than once per delivery: several of the triggers a registration subscribes
    /// to fire per file, and a line each would bury the one that named a type nobody expected.
    /// </remarks>
    private static void NoteIgnoredEventType(ILogger log, WhisparrGeneration generation, string? eventType)
    {
        if (eventType is null)
        {
            return;
        }

        // Shortened before it reaches either the set or the log. The value is a string an
        // authenticated caller chose, and both a log sink and this set are durable.
        var named = eventType.Length <= EventTypeChars ? eventType : eventType[..EventTypeChars];

        if (IgnoredEventTypes.Count >= IgnoredEventTypeCeiling
            || !IgnoredEventTypes.TryAdd(generation + ":" + named, true))
        {
            return;
        }

        WhisparrSyncLog.ImportEventTypeIgnored(log, generation, named);
    }

    /// <summary>How much of a caller-supplied event type is recorded.</summary>
    /// <remarks>
    /// Long enough for every event type either generation declares, short enough that no single
    /// delivery can write a page of caller-chosen text into the host's log.
    /// </remarks>
    private const int EventTypeChars = 64;

    /// <summary>How many distinct ignored event types are remembered.</summary>
    /// <remarks>
    /// The set exists so each type is reported once, and its size is what a caller would otherwise
    /// control: the event types the two generations declare are a fixed handful, but the string
    /// arrives in a request body. Past the ceiling the repeats simply stop being reported.
    /// </remarks>
    private const int IgnoredEventTypeCeiling = 64;

    /// <summary>The ignored event types already reported, so each is reported once.</summary>
    /// <remarks>
    /// Concurrent because deliveries arrive in parallel and the whole value of the set is that
    /// exactly one of them logs.
    /// </remarks>
    private static readonly ConcurrentDictionary<string, bool> IgnoredEventTypes = new(StringComparer.Ordinal);

    /// <summary>Registers this product's callback in the connected instance, in place.</summary>
    /// <remarks>
    /// The answer reports what a re-read of the instance's notification list FOUND, not what the write
    /// answered. A write being accepted says the request was well formed; it does not say the
    /// notification now points anywhere.
    /// <para>
    /// An edited address contributes only its scheme, host, port and path prefix, and it is stored so
    /// the edit survives a refresh. The route and the secret are always this product's own.
    /// </para>
    /// </remarks>
    internal static async Task<Results<Ok<CallbackView>, ForbiddenCode>> RegisterCallbackAsync(
        RegisterCallbackRequest request,
        HttpContext http,
        ICurrentPrincipalAccessor principal,
        string extensionId,
        OptionsStore options,
        OptionsWriteGate gate,
        ICredentialPort credentials,
        ICallbackSecretPort secrets,
        IWhisparrNotificationPort notifications,
        RegistrationGate registrations,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (!HasConfigurePermission(principal))
        {
            return new ForbiddenCode();
        }

        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(notifications);
        ArgumentNullException.ThrowIfNull(registrations);

        // Stored even when it equals the host this request arrived on. What storing it buys is that a
        // later request from a different host does not move the address.
        var edited = CallbackAddress.HostPartOf(request.CallbackAddress, extensionId);
        var stored = edited.Length > 0
            ? await gate.MutateAsync(options, current => current with { CallbackHost = edited }, ct)
                .ConfigureAwait(false)
            : await options.LoadAsync(ct).ConfigureAwait(false);

        var generation = stored.SelectedGeneration;
        var connection = stored.ConnectionFor(generation) ?? new WhisparrSyncGenerationConnection();
        var apiKey = await credentials.ReadAsync(generation, ct).ConfigureAwait(false);
        var secret = await secrets.EnsureAsync(clock.GetUtcNow(), ct).ConfigureAwait(false);
        var host = CallbackAddress.ResolveHost(stored.CallbackHost, RequestHostOf(http));

        // Refused here rather than by handing an empty pair to the port, so an unconfigured
        // connection reaches nothing that could make a request.
        if (!ConnectionTester.TryReadConnection(connection.Address, apiKey, out var baseAddress, out var missing))
        {
            return TypedResults.Ok(ProjectCallback(stored, extensionId, secret, host, missing, null));
        }

        // Gated, because the port finds this product's notification and then creates or updates it:
        // two registrations overlapping that pair both find none and both create one.
        var outcome = await registrations.RunAsync(
            token => notifications.RegisterAsync(
                generation,
                baseAddress,
                apiKey,
                TravelsOutOfBand(generation)
                    ? CallbackAddress.WithoutSecret(host, extensionId)
                    : CallbackAddress.WithSecret(host, extensionId, secret),
                secret,
                token),
            ct).ConfigureAwait(false);

        // The status is folded onto the connection the gate loads, and the answer is projected from
        // what the gate persisted. The registration is an outbound round trip, which is the longest
        // window another writer of this same record has to commit inside - and the secret-position
        // write is one such writer, on every delivery.
        var persisted = await gate.MutateAsync(
            options,
            fresh => fresh.WithConnectionFor(
                generation,
                (fresh.ConnectionFor(generation) ?? new WhisparrSyncGenerationConnection())
                    with
                { CallbackRegistration = outcome.Status }),
            ct).ConfigureAwait(false);

        return TypedResults.Ok(
            ProjectCallback(persisted, extensionId, secret, host, null, outcome.Refusal));
    }

    /// <summary>Reads the callback as it stands, without asking the instance anything.</summary>
    /// <remarks>
    /// The status is the one a registration attempt recorded, so a generation nothing has checked
    /// answers that it has not been checked rather than borrowing the other generation's answer. It is
    /// deliberately not re-derived by contacting Whisparr: opening the page would then make an
    /// outbound request whose failure is indistinguishable from an absent registration.
    /// <para>
    /// The secret is minted on the first read that needs one, which is what lets an address be shown
    /// before any registration exists.
    /// </para>
    /// </remarks>
    internal static async Task<Results<Ok<CallbackView>, ForbiddenCode>> ReadCallbackStatusAsync(
        HttpContext http,
        ICurrentPrincipalAccessor principal,
        string extensionId,
        OptionsStore options,
        ICallbackSecretPort secrets,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (!HasConfigurePermission(principal))
        {
            return new ForbiddenCode();
        }

        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(clock);

        var stored = await options.LoadAsync(ct).ConfigureAwait(false);
        var secret = await secrets.EnsureAsync(clock.GetUtcNow(), ct).ConfigureAwait(false);

        return TypedResults.Ok(
            ProjectCallback(
                stored,
                extensionId,
                secret,
                CallbackAddress.ResolveHost(stored.CallbackHost, RequestHostOf(http)),
                null,
                null));
    }

    private static CallbackView ProjectCallback(
        WhisparrSyncOptions stored,
        string extensionId,
        string secret,
        string host,
        ConnectionSetting? missing,
        string? refusal)
    {
        var generation = stored.SelectedGeneration;
        var connection = stored.ConnectionFor(generation);
        return new CallbackView(
            generation,
            connection?.CallbackRegistration ?? RegistrationStatus.NotCheckedYet,
            CallbackAddress.WithSecret(host, extensionId, secret),
            CallbackAddress.WithoutSecret(host, extensionId),
            TravelsOutOfBand(generation),
            connection?.LastCallbackSecretPosition,
            missing,
            refusal);
    }

    /// <summary>Whether <paramref name="generation"/> can carry a secret off the address it registers.</summary>
    private static bool TravelsOutOfBand(WhisparrGeneration generation)
        => GenerationCapabilities.For(generation)
            .Obtain<IOutOfBandSecretRegistration>()
            .Match(_ => true, _ => false);

    /// <summary>The scheme, host, port and path prefix this request arrived on.</summary>
    /// <remarks>
    /// The default the address is built on before a user has corrected one. It is the host the BROWSER
    /// reached Cove at, which is not necessarily one Whisparr can reach — which is exactly why the
    /// address is editable.
    /// </remarks>
    private static string RequestHostOf(HttpContext http)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"{http.Request.Scheme}://{http.Request.Host}{http.Request.PathBase}").TrimEnd('/');

    // The transition is the whole content of the reading: the note about the less private form is
    // shown while it reads Address and clears when it reads OutOfBand.
    //
    // The generation selects which connection carries it, and it is the generation the delivery was
    // read as rather than the one the settings page has selected: the reading is the page's tell that
    // an instance is registered AND delivering, so recorded against another instance it says that
    // about one which has not delivered.
    internal static Task RecordSecretPositionAsync(
        OptionsStore options,
        OptionsWriteGate gate,
        WhisparrGeneration generation,
        CallbackSecretPosition position,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(gate);

        return gate.MutateAsync(
            options,
            stored => stored.ConnectionFor(generation) is { } connection
                ? stored.WithConnectionFor(
                    generation, connection with { LastCallbackSecretPosition = position })
                : stored,
            ct);
    }

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
