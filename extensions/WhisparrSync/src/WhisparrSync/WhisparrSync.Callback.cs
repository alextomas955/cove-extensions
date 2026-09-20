using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cove.Core.Auth;
using Cove.Extensions.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
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
}
