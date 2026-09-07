using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using WhisparrSync.Adapters;
using WhisparrSync.Client;
using WhisparrSync.Contracts;
using WhisparrSync.Ingest;
using WhisparrSync.Options;
using WhisparrSync.State;
using static WhisparrSync.Contracts.WireSerializers;

namespace WhisparrSync;

/// <summary>
/// The connection + configuration slice: status/options read-write, credential resolution, the webhook URL +
/// registration + secret, reconciliation endpoint, and the test-connection probe.
/// </summary>
public sealed partial class WhisparrSync
{
    private const string TestConnectionRoute = RouteBase + "/test-connection";
    private const string StatusRoute = RouteBase + "/status";
    private const string OptionsRoute = RouteBase + "/options";
    private const string WebhookUrlRoute = RouteBase + "/webhook-url";
    private const string RegisterWebhookRoute = RouteBase + "/register-webhook";

    /// <summary>
    /// Registers the connection/configuration slice's routes. The read projections (status/options) declare the
    /// read tier; anything reaching the stored creds (test-connection, webhook admin) declares the configure
    /// tier. Each lambda immediately delegates to an extracted instance handler so the handler is unit-testable
    /// without an HTTP host.
    /// </summary>
    private void MapConnectionEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(TestConnectionRoute,
            (TestConnectionRequest req, WhisparrClient client, CancellationToken ct)
                => TestConnectionAsync(req, client, ct)).ConfigureGated();

        endpoints.MapGet(StatusRoute, (CancellationToken ct) => StatusAsync(ct)).ReadGated();

        endpoints.MapGet(OptionsRoute, (CancellationToken ct) => GetOptionsAsync(ct)).ReadGated();

        endpoints.MapPost(OptionsRoute,
            (OptionsSaveRequest req, CancellationToken ct) => SaveOptionsAsync(req, ct)).ConfigureGated();

        // The Cove host base for the webhook URL is derived from the inbound request (scheme + host) — the
        // extension backend has no other authoritative view of its own public address.
        endpoints.MapGet(WebhookUrlRoute,
            (HttpRequest http, WhisparrClient client, CancellationToken ct)
                => WebhookUrlAsync($"{http.Scheme}://{http.Host}", client, ct)).ConfigureGated();

        endpoints.MapPost(RegisterWebhookRoute,
            (WebhookRegisterRequest? req, HttpRequest http, WhisparrClient client, CancellationToken ct)
                => RegisterWebhookAsync($"{http.Scheme}://{http.Host}", client, ct, req?.Url)).ConfigureGated();
    }

    /// <summary>
    /// Returns whether the extension is configured (a base URL and a stored key are present) plus the keys of
    /// any required option that is unset — the redaction-safe status projection (never the raw key). Declares the read tier.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>missingRequiredOptions</c> carries option KEYS only, never a URL, a key value, or anything adjacent to
    /// one — a read-gated caller learns which setting is unset without learning what is stored.
    /// </para>
    /// <para>
    /// <c>detectedVersion</c> is empty until a probe against the stored host succeeds, which is an honest
    /// not-yet-verified state rather than a failed detection — every install reads empty until its first
    /// successful test.
    /// </para>
    /// </remarks>
    internal async Task<IResult> StatusAsync(CancellationToken ct)
    {
        var options = await new OptionsStore(Store).LoadAsync(ct);
        var missingRequiredOptions = ConfigCompletenessGuard.MissingRequiredOptions(options);
        return Results.Json(
            new ConfigStatusResponse(
                Configured: missingRequiredOptions.Count == 0,
                HasApiKey: !string.IsNullOrEmpty(options.ApiKey),
                DetectedVersion: options.DetectedVersion,
                MissingRequiredOptions: missingRequiredOptions),
            WebResponseJsonOptions);
    }

    /// <summary>
    /// Returns the persisted options as a redaction-safe <see cref="OptionsView"/> — every field except the
    /// API key, which is projected to a <c>hasApiKey</c> boolean. Declares the read tier.
    /// </summary>
    internal async Task<IResult> GetOptionsAsync(CancellationToken ct)
    {
        var options = await new OptionsStore(Store).LoadAsync(ct);
        return Results.Json(OptionsView.From(options), WebResponseJsonOptions);
    }

    /// <summary>
    /// Persists the submitted URL / API key / version / path translation. Write-only key
    /// semantics: an empty submitted key preserves the stored one (<see cref="WhisparrOptions.WithSubmitted"/>),
    /// so saving from a UI that never held the key does not blank it. The server-managed <c>WebhookSecret</c> is left
    /// untouched; the server-managed <c>DetectedVersion</c> is dropped when the submitted address repoints the
    /// connection, since the reading described the instance at the old address. Declares the configure tier.
    /// </summary>
    internal async Task<IResult> SaveOptionsAsync(
        OptionsSaveRequest req, CancellationToken ct)
    {
        var updated = await new OptionsStore(Store).UpdateAsync(
            current => current.WithSubmitted(
                req.BaseUrl, req.ApiKey, req.SelectedVersion,
                pathTranslation: req.PathTranslation,
                tagsOnAdd: req.TagsOnAdd,
                monitorNewByDefault: req.MonitorNewByDefault,
                allowQualityUpgrades: req.AllowQualityUpgrades),
            ct);
        _selectedVersion = updated.SelectedVersion; // keep the sync GetUIManifest gate current after a version change

        return Results.Json(OptionsView.From(updated), WebResponseJsonOptions);
    }

    /// <summary>
    /// The one configuration refusal every route reaching Whisparr shares: the pure unset check over the stored
    /// address and key. Returns the refusal to send back, or <c>null</c> when the action may proceed.
    /// </summary>
    /// <remarks>
    /// It issues ZERO outbound calls, so a route refusing here has not reached the orchestration seam whose first
    /// acts create the origin tag. There is nothing to check against the instance: the add's root folder and its
    /// quality profile are both derived from the instance at add time, so neither can hold a stored value the
    /// instance would refuse.
    /// </remarks>
    private static IResult? RefuseIncompleteConfig(WhisparrOptions options)
        => ConfigCompletenessGuard.MissingRequiredOptions(options) is { Count: > 0 } unset
            ? ConfigRefusal(unset)
            : null;

    /// <summary>
    /// The single construction of the stored-configuration refusal body. The option keys are the whole answer —
    /// every one of them is unset, which is the only fault this guard can now report.
    /// </summary>
    private static IResult ConfigRefusal(IReadOnlyList<string> keys)
        => Results.Json(new ConfigIncompleteResponse("CONFIG_INCOMPLETE", keys), statusCode: 400);

    /// <summary>The refusal a route sends when the connected generation cannot honor it.</summary>
    private static IResult VersionUnsupported()
        => Results.Json(new ErrorResponse("VERSION_UNSUPPORTED"), statusCode: 400);

    /// <summary>
    /// The refusal a route sends when the connected instance's own API description does not declare the routes
    /// the operation needs.
    /// </summary>
    /// <remarks>
    /// Deliberately a different code from <see cref="VersionUnsupported"/>: a v3 instance missing a route is not
    /// a version mismatch, and a reader looking at a refusal must be able to tell "this generation never offers
    /// it" from "this build does not expose it". Fail-closed — the refusal is the answer, never a wider read.
    /// </remarks>
    private static IResult CapabilityUnavailable()
        => Results.Json(new ErrorResponse("CAPABILITY_UNAVAILABLE"), statusCode: 400);

    /// <summary>
    /// The connected adapter narrowed to <see cref="V3Adapter"/>, or <c>null</c> on any other generation.
    /// </summary>
    /// <remarks>
    /// The guard stays an explicit early return at each call site rather than being folded into a helper that
    /// returns the refusal: a reader checking whether a mutating route defers on v2 should see the refusal in
    /// the handler, not have to follow a call. This only removes the repeated cast and status literal.
    /// </remarks>
    private static V3Adapter? V3Only(WhisparrOptions options, WhisparrClient client)
        => AdapterSelector.SelectForVersion(options.SelectedVersion, client) as V3Adapter;

    /// <summary>
    /// Loads the stored options and resolves the effective connect creds. Security invariant: the
    /// server-stored API key is NEVER sent to a caller-chosen host. A submitted key is always used as-is; an
    /// empty submitted key falls back to the stored key ONLY when the effective base URL is the stored one
    /// (the caller did not override it, or overrode it with the same host). If the caller overrides the base
    /// URL with a different host and supplies no key, the stored key is withheld (empty) — so a low-privilege
    /// request can never exfiltrate the stored key to <c>http://attacker</c>. This preserves the
    /// dropdown UX: on reload the UI sends the stored URL + an empty key (stored key reused against the stored
    /// host), and after a test it sends the form URL + the form key (its own key used directly).
    /// </summary>
    /// <summary>
    /// The stored connection, for the routes that take no url/key from the caller at all.
    /// </summary>
    /// <remarks>
    /// Every mutating route is stored-creds-only, so each one resolved through
    /// <see cref="ResolveCredsAsync"/> with an empty request — a placeholder argument repeated at more than
    /// twenty call sites, where the shape of the call implied a caller-supplied credential that none of them
    /// has. Naming the stored case removes the placeholder and makes "this route cannot be pointed at another
    /// host" readable at the call site.
    /// </remarks>
    private Task<(WhisparrOptions Options, string BaseUrl, string ApiKey)> StoredCredsAsync(CancellationToken ct)
        => ResolveCredsAsync(new TestConnectionRequest(null, null), ct);

    private async Task<(WhisparrOptions Options, string BaseUrl, string ApiKey)> ResolveCredsAsync(
        TestConnectionRequest req, CancellationToken ct)
    {
        var options = await new OptionsStore(Store).LoadAsync(ct);
        var overrodeUrl = !string.IsNullOrWhiteSpace(req.BaseUrl);
        var baseUrl = overrodeUrl ? req.BaseUrl! : options.BaseUrl;

        string apiKey;
        if (!string.IsNullOrEmpty(req.ApiKey))
        {
            apiKey = req.ApiKey!; // caller supplied its own key — use it as-is
        }
        else if (!overrodeUrl ||
                 string.Equals(baseUrl.TrimEnd('/'), options.BaseUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
        {
            apiKey = options.ApiKey; // stored key only ever paired with the stored host
        }
        else if (options.SavedConnections.Values.FirstOrDefault(
                     c => string.Equals(c.BaseUrl.TrimEnd('/'), baseUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
                 is { } savedConnection)
        {
            // A saved per-version connection's key is bound to its OWN host, so pairing them is not exfiltration —
            // this is what lets the settings toggle repopulate the other version's root/profile dropdowns without
            // the user re-typing that instance's key.
            apiKey = savedConnection.ApiKey;
        }
        else
        {
            apiKey = string.Empty; // refuse to send a stored key to a caller-chosen foreign host
        }

        return (options, baseUrl, apiKey);
    }

    /// <summary>
    /// Stamps the detected version and the tick at which it was detected — but only when the tested host IS the
    /// stored host. A blank version writes nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The probe accepts a caller-supplied base URL, so without the host gate a test against a foreign host
    /// would stamp that instance's version onto the active connection.
    /// </para>
    /// <para>
    /// Rewriting the COMPLETE record and changing only these two fields is what keeps this from DROPPING the
    /// fields it does not know about: constructing a fresh options object, or reusing
    /// <see cref="WhisparrOptions.WithSubmitted"/> (which reconstructs the saved-connection map), would destroy
    /// that map. It does nothing about a STALE read — a record loaded before a concurrent save and written after
    /// it reverts every field that save changed, the whole connection included.
    /// </para>
    /// <para>
    /// The contract that covers that: the write runs under the store's writer gate, so no other writer in this
    /// process can save between the load and the save. Two writers outside it remain — the host's own
    /// <c>PUT /api/extensions/{id}/data/{key}</c> route reaches the same blob without passing through here, and
    /// a semaphore says nothing about a second process.
    /// </para>
    /// <para>
    /// The version and its tick are written together or not at all: a version carrying a stale tick would be a
    /// smaller copy of the freshness claim this exists to remove.
    /// </para>
    /// </remarks>
    internal static async Task PersistDetectedVersionAsync(
        OptionsStore store, string testedBaseUrl, string? detectedVersion, long observedAt, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(detectedVersion))
        {
            return;
        }

        await store.UpdateAsync(
            // The same trimmed, case-insensitive comparison ResolveCredsAsync pairs a stored key with a host by.
            current => string.Equals(
                    testedBaseUrl.TrimEnd('/'), current.BaseUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)
                ? current with { DetectedVersion = detectedVersion, DetectedVersionTicks = observedAt }
                : null,
            ct);
    }

    /// <summary>Maps a non-Ok transport state to the UI's error discriminator (never leaks the key/reason).</summary>
    internal static string FailureDiscriminator(WhisparrResultState state) => state switch
    {
        WhisparrResultState.BadKey => "badKey",
        WhisparrResultState.NotWhisparr => "notWhisparr",
        WhisparrResultState.Rejected => "rejected",
        // Kept off the "unreachable" catch-all: nothing was sent, so reporting an outage would name a fault
        // in Whisparr for a value this side never set.
        WhisparrResultState.NotAsked => "notAsked",
        _ => "unreachable",
    };

    /// <summary>
    /// Returns the webhook URL + an authoritative <c>registered</c> flag sourced from Whisparr's own
    /// "Cove Whisparr Sync" connection: present → its url + <c>registered:true</c>; absent → the derived default
    /// (persisted <see cref="WhisparrOptions.WebhookHost"/>, else the request host) + <c>registered:false</c>.
    /// The same question is answered for every saved connection in <c>connections</c>.
    /// Declares the configure tier — it mints the secret and reaches the stored creds to call
    /// Whisparr. The secret is never logged.
    /// </summary>
    /// <remarks>
    /// The token-bearing URL is returned for the ACTIVE connection only, so widening the answer to two instances
    /// does not widen the token surface.
    /// </remarks>
    internal async Task<IResult> WebhookUrlAsync(
        string coveBaseUrl, WhisparrClient client, CancellationToken ct)
    {
        var (options, secret) = await EnsureWebhookSecretAsync(ct);
        var origin = string.IsNullOrWhiteSpace(options.WebhookHost) ? coveBaseUrl : options.WebhookHost;
        var derivedUrl = WebhookUrlBuilder.BuildUrl(origin, secret);

        // Captured from the scan rather than re-read: the scan already costs one read per saved connection.
        string? activeConnectorUrl = null;
        var connections = await WebhookConnectionsAsync(
            options,
            async (version, versionBaseUrl, versionApiKey, token) =>
            {
                if (AdapterSelector.SelectForVersion(version, client) is not IWhisparrWebhookAdmin admin)
                {
                    return WhisparrResult<WebhookConnection?>.VersionMismatch(version);
                }

                var found = await admin.FindWebhookConnectionAsync(versionBaseUrl, versionApiKey, token);
                if (found is { IsOk: true, Value: { } row }
                    && string.Equals(version, options.SelectedVersion, StringComparison.OrdinalIgnoreCase))
                {
                    activeConnectorUrl = row.Url;
                }

                return found;
            },
            ct);

        var registered = connections.Any(
            c => c.Registered && string.Equals(c.Version, options.SelectedVersion, StringComparison.OrdinalIgnoreCase));
        var url = string.IsNullOrWhiteSpace(activeConnectorUrl) ? derivedUrl : activeConnectorUrl;
        return Results.Json(new WebhookUrlResponse(url, registered, connections), WebResponseJsonOptions);
    }

    // The generations with an adapter, in the order the settings page lists them. A stored key outside the set
    // cannot be probed, and an entry that could only ever read "not registered" is the answer being removed.
    private static readonly string[] ManageableVersions = ["v3", "v2"];

    /// <summary>
    /// Answers the registration question per saved connection, off the per-version base URL and key already
    /// stored. Returns one entry per answerable connection; a version with no saved connection has none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The ACTIVE connection is answered from the resolved top-level pair, so it has an entry even before its
    /// first per-version save (<see cref="OptionsView.From"/> folds it in the same way).
    /// </para>
    /// <para>
    /// A non-Ok read degrades to not-registered for that entry ALONE: a down instance must not fail the response
    /// or block the settings page from offering copy-paste and register.
    /// </para>
    /// <para>
    /// Each key is presented only to its own connection's host — the pairing rule
    /// <see cref="ResolveCredsAsync"/> documents, here structural rather than checked.
    /// </para>
    /// </remarks>
    internal static async Task<IReadOnlyList<WebhookConnectionView>> WebhookConnectionsAsync(
        WhisparrOptions options,
        Func<string, string, string, CancellationToken, Task<WhisparrResult<WebhookConnection?>>> findConnector,
        CancellationToken ct)
    {
        var views = new List<WebhookConnectionView>(ManageableVersions.Length);
        foreach (var version in ManageableVersions)
        {
            var active = string.Equals(version, options.SelectedVersion, StringComparison.OrdinalIgnoreCase);
            var (baseUrl, apiKey) = active
                ? (options.BaseUrl, options.ApiKey)
                : options.SavedConnections.TryGetValue(version, out var saved)
                    ? (saved.BaseUrl, saved.ApiKey)
                    : ("", "");

            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                continue;
            }

            var found = await findConnector(version, baseUrl, apiKey, ct);
            views.Add(new WebhookConnectionView(version, baseUrl, Registered: found is { IsOk: true, Value: not null }));
        }

        return views;
    }

    /// <summary>
    /// Idempotent auto-register of the Cove webhook in Whisparr, persisting the resolved host. Mints/persists
    /// the secret, resolves + stores the origin, and delegates to the update-or-create adapter register. An
    /// already-existing connection (including a unique-name 400/409) resolves to <c>registered:true</c> — a
    /// re-register never errors and never falsely reports "not registered". A refused version or a non-Ok
    /// transport returns <c>registered:false</c> — the UI falls back to copy-paste, and the connect flow never
    /// fails. Declares the configure tier. The secret is never logged.
    /// </summary>
    /// <remarks>
    /// A containerized Whisparr cannot reach the <c>localhost</c> the admin browses Cove at, so when the UI
    /// forwards a hand-edited URL in <paramref name="overrideUrl"/> ONLY its origin is honored — the token is
    /// always re-minted from the stored secret via <see cref="WebhookUrlBuilder.BuildUrl"/>, so an edited host
    /// can never carry a wrong or forged token. That origin is persisted to
    /// <see cref="WhisparrOptions.WebhookHost"/> so a pre-connector edit survives a refresh; persisting only the
    /// origin (not the token) keeps a forged token out of the stored host.
    /// </remarks>
    internal async Task<IResult> RegisterWebhookAsync(
        string coveBaseUrl, WhisparrClient client, CancellationToken ct,
        string? overrideUrl = null)
    {
        var (options, secret) = await EnsureWebhookSecretAsync(ct);
        var origin = coveBaseUrl;
        if (!string.IsNullOrWhiteSpace(overrideUrl)
            && Uri.TryCreate(overrideUrl, UriKind.Absolute, out var parsed)
            && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps))
        {
            origin = parsed.GetLeftPart(UriPartial.Authority);
        }

        // Unconditional even when the loaded host already matches: the update re-reads under the gate, so
        // what it returns is the record every field below must be read from. The value loaded before the
        // secret mint may have been superseded by a save in between.
        options = await new OptionsStore(Store).UpdateAsync(
            current => string.Equals(origin, current.WebhookHost, StringComparison.Ordinal)
                ? null
                : current with { WebhookHost = origin },
            ct);

        var url = WebhookUrlBuilder.BuildUrl(origin, secret);

        if (AdapterSelector.SelectForVersion(options.SelectedVersion, client) is not { } adapter)
        {
            LogWebhookRegistered(false);
            return Results.Json(new WebhookRegistrationResponse(false), WebResponseJsonOptions);
        }

        var result = await adapter.RegisterWebhookAsync(options.BaseUrl, options.ApiKey, url, ct);
        LogWebhookRegistered(result.IsOk);
        return Results.Json(new WebhookRegistrationResponse(result.IsOk), WebResponseJsonOptions);
    }

    /// <summary>
    /// Loads the options, minting + persisting a webhook secret when one is absent (so the URL is stable
    /// across calls). Returns the effective options and the secret.
    /// </summary>
    private async Task<(WhisparrOptions Options, string Secret)> EnsureWebhookSecretAsync(CancellationToken ct)
    {
        var options = await new OptionsStore(Store).UpdateAsync(
            current =>
            {
                var minted = WebhookUrlBuilder.EnsureSecret(current.WebhookSecret);
                return minted == current.WebhookSecret ? null : current with { WebhookSecret = minted };
            },
            ct);

        return (options, options.WebhookSecret);
    }

    /// <summary>
    /// Runs the full connect flow against the supplied Whisparr URL + API key and returns a discriminated
    /// result the UI branches on: <c>ok</c> (with version + instance name), <c>badKey</c>,
    /// <c>unreachable</c> (with a short reason), <c>notWhisparr</c> (HTML/502), or <c>versionMismatch</c>
    /// (with the detected version — the fail-closed refusal when the major version is not 3). The
    /// adapter is chosen from the parsed status via <see cref="AdapterSelector"/>, never from the status
    /// code (both v2 and v3 answer <c>/api/v3</c>). Declares the configure tier. The API key is used server-side only
    /// and is never included in the response.
    /// </summary>
    internal async Task<IResult> TestConnectionAsync(
        TestConnectionRequest req, WhisparrClient client, CancellationToken ct)
    {
        // Once a key is saved the settings field is masked ("Key is set — type to replace"), so a user
        // re-testing a stored connection sends a BLANK key. Resolve through ResolveCredsAsync so the typed
        // key wins when present, else the STORED key is used — but only when the typed host matches the
        // stored host, never leaking the stored key to a caller-chosen foreign host.
        var (_, baseUrl, apiKey) = await ResolveCredsAsync(req, ct);
        var result = await client.GetStatusAsync(baseUrl, apiKey, ct);

        var health = new HealthStore(Store);
        var observedAt = DateTime.UtcNow.Ticks;

        switch (result.State)
        {
            case WhisparrResultState.Ok:
                var status = result.Value!;
                // Branch on the parsed version, never the 200 status: a v2 instance also answers /api/v3.
                if (AdapterSelector.Select(status, client) is null)
                {
                    LogVersionRefused(AdapterSelector.ParseMajor(status.Version));
                    await health.RecordAsync(
                        HealthDependency.Acquisition,
                        HealthOutcome.FromAcquisition(WhisparrResultState.VersionMismatch, status.Version),
                        observedAt,
                        ct);
                    return Results.Json(
                        new TestConnectionVersionMismatchResponse("versionMismatch", status.Version),
                        WebResponseJsonOptions);
                }

                LogConnectTested(status.Version ?? "unknown", status.InstanceName ?? "unknown");
                await PersistDetectedVersionAsync(
                    new OptionsStore(Store), baseUrl, status.Version, observedAt, ct);
                await health.RecordAsync(
                    HealthDependency.Acquisition,
                    HealthOutcome.FromAcquisition(WhisparrResultState.Ok, null),
                    observedAt,
                    ct);
                return Results.Json(
                    new TestConnectionSuccessResponse("success", status.Version, status.InstanceName),
                    WebResponseJsonOptions);

            case WhisparrResultState.BadKey:
                await health.RecordAsync(
                    HealthDependency.Acquisition,
                    HealthOutcome.FromAcquisition(WhisparrResultState.BadKey, null),
                    observedAt,
                    ct);
                return Results.Json(new ResultDiscriminatorResponse("badKey"), WebResponseJsonOptions);

            case WhisparrResultState.NotWhisparr:
                await health.RecordAsync(
                    HealthDependency.Acquisition,
                    HealthOutcome.FromAcquisition(WhisparrResultState.NotWhisparr, null),
                    observedAt,
                    ct);
                return Results.Json(new ResultDiscriminatorResponse("notWhisparr"), WebResponseJsonOptions);

            default:
                LogWhisparrUnreachable(result.Reason ?? result.State.ToString());
                await health.RecordAsync(
                    HealthDependency.Acquisition,
                    HealthOutcome.FromAcquisition(result.State, result.Reason),
                    observedAt,
                    ct);
                return Results.Json(
                    new TestConnectionUnreachableResponse("unreachable", result.Reason),
                    WebResponseJsonOptions);
        }
    }
}
