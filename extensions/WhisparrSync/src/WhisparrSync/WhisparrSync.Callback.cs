using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cove.Core.Auth;
using Cove.Extensions.Shared;
using Cove.Sdk;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
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
    private void MapCallbackEndpoints(IEndpointRouteBuilder endpoints)
    {
        // The one route of this extension that answers a caller holding no Cove permission, declared
        // with the SDK's own convention. An endpoint declaring no convention also admits an anonymous
        // caller, but silently and with a host warning.
        endpoints.MapPost(CallbackRoute,
            (HttpContext http, IServiceScopeFactory scopes, CancellationToken ct)
                => CallbackAsync(http, scopes, _log, ct))
            .WithTags(WireTag)
            .AllowCoveAnonymous();

        endpoints.MapPost(CallbackRegisterRoute,
            (RegisterCallbackRequest request, HttpContext http, ICurrentPrincipalAccessor principal,
             OptionsStore options, OptionsWriteGate gate, ICredentialPort credentials,
             ICallbackSecretPort secrets, IWhisparrNotificationPort notifications,
             RegistrationGate registrations,
             [FromServices] IHostLockdownPort lockdown,
             TimeProvider clock, CancellationToken ct)
                => RegisterCallbackAsync(
                    request, http, principal, Id, options, gate, credentials, secrets, notifications,
                    registrations, lockdown, clock, ct))
            .WithTags(WireTag)
            .RequireCovePermission(PermissionMode.Any, ConfigurePermissions);

        endpoints.MapGet(CallbackStatusRoute,
            (HttpContext http, ICurrentPrincipalAccessor principal, OptionsStore options,
             ICallbackSecretPort secrets,
             [FromServices] IHostLockdownPort lockdown,
             TimeProvider clock, CancellationToken ct)
                => ReadCallbackStatusAsync(
                    http, principal, Id, options, secrets, lockdown, clock, ct))
            .WithTags(WireTag)
            .RequireCovePermission(PermissionMode.Any, ConfigurePermissions);
    }

    // Authenticated by a secret this product minted, not by a Cove permission: the caller is another
    // application rather than a Cove user. The secret is accepted from either position, because an
    // address a user pasted by hand has nowhere but the query to carry one.
    //
    // Runs as System. The caller carries no principal, and Cove's per-principal query filters answer
    // an Anonymous reader with zero rows and no error, which would report the stored secret as absent
    // and refuse every delivery.
    //
    // The body is read once and only after the secret matches, so an unauthenticated delivery reaches
    // no allocation, no filesystem probe and no host call.
    //
    // The answer names no path and does not say whether a file was found. The caller is anonymous,
    // and an answer that varied with what is on disk would make this route a filesystem probe.
    //
    // Neither generation signs a delivery, so the secret is the whole of the authentication. On a
    // Cove whose own authentication is disabled nothing else stands in front of this route: the host
    // issues no authentication challenge and consults no proxy or trusted-host allow-list.
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

            // Recorded as a refusal rather than an ignore: this product handles the event and could
            // not read the body, which is a different fact from not handling the event.
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

    // Read into a buffer one byte longer than the cap, so a body past the cap is detected without
    // being materialised. A declared length over the cap is refused before the stream is touched.
    //
    // Parsed as a node rather than bound to a record: one generation publishes no contract, and a
    // record would assume a shape the other generation does not send.
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

    // This extension's own bound, not the framework's: Cove configures no maximum request body size,
    // so the framework default applies and is far above anything an instance sends.
    internal const int MaxCallbackBodyBytes = 64 * 1024;

    // Once per distinct type rather than once per delivery: several of the triggers a registration
    // subscribes to fire per file.
    private static void NoteIgnoredEventType(ILogger log, WhisparrGeneration generation, string? eventType)
    {
        if (eventType is null)
        {
            return;
        }

        // Shortened before it reaches the set or the log: the value is caller-chosen, and both the
        // set and a log sink are durable.
        var named = eventType.Length <= EventTypeChars ? eventType : eventType[..EventTypeChars];

        if (IgnoredEventTypes.Count >= IgnoredEventTypeCeiling
            || !IgnoredEventTypes.TryAdd(generation + ":" + named, true))
        {
            return;
        }

        WhisparrSyncLog.ImportEventTypeIgnored(log, generation, named);
    }

    // Long enough for every event type either generation declares, short enough that no single
    // delivery can write a page of caller-chosen text into the host's log.
    private const int EventTypeChars = 64;

    // Without a ceiling a caller sets the size of the set: the event types the two generations
    // declare are a fixed handful, but the string arrives in a request body. Past the ceiling the
    // repeats stop being reported.
    private const int IgnoredEventTypeCeiling = 64;

    // Concurrent: deliveries arrive in parallel and exactly one of them must log.
    private static readonly ConcurrentDictionary<string, bool> IgnoredEventTypes = new(StringComparer.Ordinal);

    // The answer reports what a re-read of the instance's notification list found, not what the write
    // answered. An accepted write says the request was well formed; it does not say the notification
    // points anywhere.
    //
    // An edited address contributes only its scheme, host, port and path prefix. The route and the
    // secret are always this product's own.
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
        IHostLockdownPort lockdown,
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
        ArgumentNullException.ThrowIfNull(lockdown);

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
            return TypedResults.Ok(
                ProjectCallback(
                    stored,
                    extensionId,
                    secret,
                    host,
                    missing,
                    null,
                    !await lockdown.WouldLockDownAsync(ct).ConfigureAwait(false)));
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

        // Folded onto the connection the gate reloads, not onto the copy read before the call. The
        // registration is an outbound round trip, the longest window another writer of this record
        // has to commit in, and the secret-position write is one such writer on every delivery.
        var persisted = await gate.MutateAsync(
            options,
            fresh => fresh.WithConnectionFor(
                generation,
                (fresh.ConnectionFor(generation) ?? new WhisparrSyncGenerationConnection())
                    with
                { CallbackRegistration = outcome.Status }),
            ct).ConfigureAwait(false);

        return TypedResults.Ok(
            ProjectCallback(
                persisted,
                extensionId,
                secret,
                host,
                null,
                outcome.Refusal,
                !await lockdown.WouldLockDownAsync(ct).ConfigureAwait(false)));
    }

    // The status is the one a registration attempt recorded, per generation, and is not re-derived
    // by contacting Whisparr: opening the page would then make an outbound request whose failure is
    // indistinguishable from an absent registration.
    //
    // The secret is minted on the first read that needs one, so an address can be shown before any
    // registration exists.
    internal static async Task<Results<Ok<CallbackView>, ForbiddenCode>> ReadCallbackStatusAsync(
        HttpContext http,
        ICurrentPrincipalAccessor principal,
        string extensionId,
        OptionsStore options,
        ICallbackSecretPort secrets,
        IHostLockdownPort lockdown,
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
        ArgumentNullException.ThrowIfNull(lockdown);
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
                null,
                !await lockdown.WouldLockDownAsync(ct).ConfigureAwait(false)));
    }

    private static CallbackView ProjectCallback(
        WhisparrSyncOptions stored,
        string extensionId,
        string secret,
        string host,
        ConnectionSetting? missing,
        string? refusal,
        bool registrationIsSafe)
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
            refusal,
            registrationIsSafe);
    }

    private static bool TravelsOutOfBand(WhisparrGeneration generation)
        => GenerationCapabilities.For(generation)
            .Obtain<IOutOfBandSecretRegistration>()
            .Match(_ => true, _ => false);

    // The host the browser reached Cove at, which Whisparr cannot necessarily reach. That is why the
    // address is editable.
    private static string RequestHostOf(HttpContext http)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"{http.Request.Scheme}://{http.Request.Host}{http.Request.PathBase}").TrimEnd('/');

    // Recorded against the generation the delivery was read as, not the one the settings page has
    // selected: the reading is the page's evidence that an instance is registered and delivering, so
    // against another connection it would claim that of an instance which has not delivered.
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
}
