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
using WhisparrSync.Connection;
using WhisparrSync.Contracts;
using WhisparrSync.Import;
using WhisparrSync.Jobs;
using WhisparrSync.Monitoring;
using WhisparrSync.Options;
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
    private string MonitoringReadRoute => RouteBase + "/entity/{kind}/{coveId}/monitoring";
    private string MonitorRoute => RouteBase + "/entity/{kind}/{coveId}/monitor";
    private string UnmonitorRoute => RouteBase + "/entity/{kind}/{coveId}/unmonitor";
    private string MonitorScopeRoute => RouteBase + "/entity/{kind}/{coveId}/scope";
    private string ReflectOwnedRoute => RouteBase + "/entity/{kind}/{coveId}/reflect-owned";
    private string AddAllMissingRoute => RouteBase + "/entity/{kind}/{coveId}/add-all-missing";
    private string SearchAllMonitoredRoute =>
        RouteBase + "/entity/{kind}/{coveId}/search-all-monitored";
    private string BulkMonitorRoute => RouteBase + "/entities/bulk-monitor";
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
    /// The surfaces the host mounts: one dedicated settings tab, one control in each of the studio
    /// and performer pages' own action rows, and one bulk action per selection bar.
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
    /// The manifest is built once and cannot vary by generation, so the buttons are always
    /// registered: the button's PRESENCE is a manifest fact and a verb's AVAILABILITY is a runtime
    /// one, enforced in the handler and again at the route.
    /// </para>
    /// </remarks>
    public override UIManifest GetUIManifest()
        => ManifestBuilder()
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
            .AddAction(
                id: "whisparr-monitor-selected-studios",
                label: "Monitor in Whisparr",
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
                label: "Monitor in Whisparr",
                actionType: "bulk",
                entityTypes: [PerformersSelectionType],
                icon: "eye",
                apiEndpoint: null,
                handlerName: BulkHandlerName,
                order: 100,
                requiredPermission: Permissions.ExtensionsConfigure,
                suppressSuccessAlert: true)
            .WithJsBundle("index.mjs")
            .Build();

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
    internal static async Task<Results<Ok<WhisparrSyncSettingsView>, ForbiddenCode>> SaveSettingsAsync(
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
                ? new ReflectOwnedAim(AimedAt(target, acting), null)
                : new ReflectOwnedAim(null, decision.Reason);
        }
    }

    /// <summary>What a reflect-owned run needs from <paramref name="target"/>, already aimed at it.</summary>
    /// <remarks>
    /// The ONE statement of the work, reached by the entity's own enqueued run and by a selection's
    /// per-entity step alike. Two statements of one gesture is how a selection comes to behave
    /// differently from a click.
    /// </remarks>
    private ReflectOwnedAiming AimedAt(MonitoringTarget target, IWhisparrReflectOwnedActing acting)
        => new(
            target.Generation,
            async (folder, readCt) => (await ContainedAsync(
                    () => acting.ListImportableFilesAsync(
                        target.BaseAddress, target.ApiKey, folder, readCt),
                    target,
                    _log,
                    readCt).ConfigureAwait(false))
                is { } parsed && MonitoringProjector.Accepted(parsed) == MonitorRefusalKind.None
                    ? parsed.Body
                    : null,
            async (files, attachCt) => (await ContainedAsync(
                    () => acting.AttachOwnedFilesAsync(
                        target.BaseAddress, target.ApiKey, files, attachCt),
                    target,
                    _log,
                    attachCt).ConfigureAwait(false))
                is { } attached
                && MonitoringProjector.Accepted(attached) == MonitorRefusalKind.None);

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
    /// the older generation's refusal - no route there adds a catalogue item, so the role is not
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

        var resolved = await ResolveAddAllMissingAsync(entityKind, coveId, target, identities, ct)
            .ConfigureAwait(false);
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
        if (defaults.Defaults is not { } composeWith)
        {
            return new AddAllMissingResolution(null, defaults.Refusal);
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
            if (!monitored)
            {
                return State(kind, target, monitored: false, scope: null);
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
                : State(kind, target, monitored: false, scope: null);
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

        // Obtained BY NAME, and this is the only call site in the product that does so. A generation
        // holding no search has no implementation to hand over, which is the refusal below rather
        // than a member that accepts the call and declines it.
        var grabbing = target.Capabilities.Obtain<IWhisparrSearchGrabbing>()
            .Match<IWhisparrSearchGrabbing?>(held => held, _ => null);
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
                target.BaseAddress, target.ApiKey, target.Generation, entityKind, entityId, ct),
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
                : State(entityKind, target, monitored, inForce);
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

        var run = TryParseSelectionType(batch.EntityType, out var kind) && batch.Verb is { } verb
            ? await MonitoringBulkJob.RunAsync(
                batch.EntityIds, scopes, ActOnOneAsync, progress, ct).ConfigureAwait(false)
            : MonitorBulkRun.NothingSelected;

        // The host's progress carries no summary field, so the run's one line rides the final
        // report's sub-task.
        progress.Report(
            1d,
            MonitoringBulkJob.SummaryOf(
                run,
                linkingReached
                    ? new MonitorBulkLinking(linkingSkipped, foldersAttached, foldersRefused)
                    : null));
        ct.ThrowIfCancellationRequested();

        async Task<MonitorRefusalKind> ActOnOneAsync(
            IServiceProvider services, int coveId, CancellationToken entityCt)
        {
            if (!targetResolved)
            {
                target = await ResolveTargetAsync(
                    services.GetRequiredService<OptionsStore>(),
                    services.GetRequiredService<ICredentialPort>(),
                    services.GetRequiredService<IWhisparrClient>(),
                    entityCt).ConfigureAwait(false);
                targetResolved = true;
            }

            if (target is not { } resolved)
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
                    linkingThrough = decision.Act ? AimedAt(resolved, acting) : null;
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
            // Not held is not a refusal: the entity is simply not monitored yet.
            MonitoringProjector.EntityReading.NotHeld
                => State(kind, target, monitored: false, scope: null),
            MonitoringProjector.EntityReading.Held => Held(read.Body),
            _ => Refused(kind, target, RefusalIn(answer)),
        };

        EntityMonitoringView Held(string body)
        {
            var monitored = MonitoringProjector.MonitoredIn(body);
            return State(kind, target, monitored, ScopeHeld(kind, target, monitored, body));
        }
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
        if (defaults.Defaults is not { } composeWith)
        {
            return Refused(kind, target, defaults.Refusal);
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
        return answer.Reading == MonitoringProjector.EntityReading.Held
            && MonitoringProjector.MonitoredIn(read.Body)
                ? State(
                    kind,
                    target,
                    monitored: true,
                    ScopeHeld(kind, target, monitored: true, read.Body))
                : Refused(kind, target, RefusalIn(answer));
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
                return State(kind, target, monitored: false, scope: null);
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
            return State(kind, target, monitored: true, ScopeHeld(kind, target, monitored: true, body));
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
        WhisparrEntityKind kind, MonitoringTarget target, bool monitored, MonitorScope? scope)
        => EntityMonitoringView.State(
            kind, target.Generation, target.Capabilities.Held, monitored, scope);

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
