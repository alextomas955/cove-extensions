using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cove.Core.Auth;
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
using WhisparrSync.Options;
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
             OptionsStore options, ICredentialPort credentials, TimeProvider clock, CancellationToken ct)
                => SaveSettingsAsync(request, principal, options, credentials, clock, ct))
            .WithTags(WireTag)
            .RequireCovePermission(PermissionMode.Any, ConfigurePermissions);

        endpoints.MapGet(ImportBannerRoute,
            (ICurrentPrincipalAccessor principal, OptionsStore options, CancellationToken ct)
                => ReadImportBannerAsync(principal, options, ct))
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
             OptionsStore options, ICredentialPort credentials, ICallbackSecretPort secrets,
             IWhisparrNotificationPort notifications, TimeProvider clock, CancellationToken ct)
                => RegisterCallbackAsync(
                    request, http, principal, Id, options, credentials, secrets, notifications, clock, ct))
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

    /// <summary>
    /// The settings surface the host mounts: one dedicated tab under the Extensions settings group.
    /// </summary>
    /// <remarks>
    /// Page layout, so the host renders the panel full-width with no card chrome and this extension
    /// draws its own. <c>componentName</c> must be byte-identical to the key in the bundle's
    /// <c>defineExtension</c> component map: the host resolves one to the other by exact string and
    /// renders nothing, with no error, when they differ.
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
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(clock);

        var now = clock.GetUtcNow();
        await credentials.ApplyAsync(
            WhisparrGeneration.V3, SettingsProjector.CredentialWriteFor(request.V3), now, ct)
            .ConfigureAwait(false);
        await credentials.ApplyAsync(
            WhisparrGeneration.V2, SettingsProjector.CredentialWriteFor(request.V2), now, ct)
            .ConfigureAwait(false);

        var stored = await options.LoadAsync(ct).ConfigureAwait(false);
        await options.SaveAsync(SettingsProjector.Apply(stored, request), ct).ConfigureAwait(false);

        return TypedResults.Ok(await ProjectSettingsAsync(options, credentials, ct).ConfigureAwait(false));
    }

    /// <summary>Reads the refusals outstanding, one line per Whisparr root that has any.</summary>
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
        return TypedResults.Ok(ImportBannerView.From(stored.ImportRefusals));
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
                services.GetRequiredService<OptionsStore>(), position, ct)
                .ConfigureAwait(false);

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
        ICredentialPort credentials,
        ICallbackSecretPort secrets,
        IWhisparrNotificationPort notifications,
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
        ArgumentNullException.ThrowIfNull(notifications);

        var stored = await options.LoadAsync(ct).ConfigureAwait(false);

        // Stored even when it equals the host this request arrived on. What storing it buys is that a
        // later request from a different host does not move the address.
        var edited = CallbackAddress.HostPartOf(request.CallbackAddress, extensionId);
        if (edited.Length > 0 && !string.Equals(edited, stored.CallbackHost, StringComparison.Ordinal))
        {
            stored = stored with { CallbackHost = edited };
            await options.SaveAsync(stored, ct).ConfigureAwait(false);
        }

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

        var outcome = await notifications.RegisterAsync(
            generation,
            baseAddress,
            apiKey,
            TravelsOutOfBand(generation)
                ? CallbackAddress.WithoutSecret(host, extensionId)
                : CallbackAddress.WithSecret(host, extensionId, secret),
            secret,
            ct).ConfigureAwait(false);

        stored = stored.WithConnectionFor(
            generation, connection with { CallbackRegistration = outcome.Status });
        await options.SaveAsync(stored, ct).ConfigureAwait(false);

        return TypedResults.Ok(
            ProjectCallback(stored, extensionId, secret, host, null, outcome.Refusal));
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

    // Written only when the position changes, so a delivery stream does not put every event into a
    // read-modify-write over one stored value. The transition is the whole content of the reading: the
    // note about the less private form is shown while it reads Address and clears when it reads
    // OutOfBand.
    private static async Task RecordSecretPositionAsync(
        OptionsStore options, CallbackSecretPosition position, CancellationToken ct)
    {
        var stored = await options.LoadAsync(ct).ConfigureAwait(false);
        var connection = stored.ConnectionFor(stored.SelectedGeneration);
        if (connection is null || connection.LastCallbackSecretPosition == position)
        {
            return;
        }

        await options.SaveAsync(
            stored.WithConnectionFor(
                stored.SelectedGeneration,
                connection with { LastCallbackSecretPosition = position }),
            ct).ConfigureAwait(false);
    }

    private static async Task<WhisparrSyncSettingsView> ProjectSettingsAsync(
        OptionsStore options, ICredentialPort credentials, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(credentials);

        var stored = await options.LoadAsync(ct).ConfigureAwait(false);
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
