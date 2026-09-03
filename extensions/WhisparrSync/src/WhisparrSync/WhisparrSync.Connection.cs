using Cove.Core.Auth;
using Microsoft.AspNetCore.Http;
using WhisparrSync.Adapters;
using WhisparrSync.Client;
using WhisparrSync.Matching;
using WhisparrSync.Options;
using WhisparrSync.Webhook;
using static Cove.Extensions.Shared.MinimalApiPermissions;

namespace WhisparrSync;

/// <summary>
/// The connection + configuration surface: status/options read-write, root-folder and quality-profile
/// lists, credential resolution, the webhook URL + registration + secret, reconciliation endpoints, and
/// the test-connection probe.
/// </summary>
public sealed partial class WhisparrSync
{
    /// <summary>
    /// Returns whether the extension is configured (a base URL and a stored key are present) — the
    /// redaction-safe status projection (never the raw key). 403-first on <c>extensions.read</c>.
    /// </summary>
    /// <remarks>
    /// <c>detectedVersion</c> is intentionally NOT projected here. Nothing currently persists
    /// <see cref="WhisparrOptions.DetectedVersion"/> (a successful test returns it to the UI in the
    /// test-connection response but never writes it), so exposing it on <c>/status</c> would advertise a
    /// permanently-empty field a downstream consumer could wrongly trust. The field is re-added to this
    /// projection once its persistence is wired.
    /// </remarks>
    internal async Task<IResult> StatusAsync(ICurrentPrincipalAccessor principal, CancellationToken ct)
    {
        if (Forbidden(principal, Permissions.ExtensionsRead) is { } denied)
        {
            return denied;
        }

        var options = await new OptionsStore(Store, _log).LoadAsync(ct);
        var configured = !string.IsNullOrWhiteSpace(options.BaseUrl) && !string.IsNullOrEmpty(options.ApiKey);
        return Results.Json(
            new { configured, hasApiKey = !string.IsNullOrEmpty(options.ApiKey) },
            OptionsResponseJsonOptions);
    }

    /// <summary>
    /// Returns the persisted options as a redaction-safe <see cref="OptionsView"/> — every field except the
    /// API key, which is projected to a <c>hasApiKey</c> boolean. 403-first on <c>extensions.read</c>.
    /// </summary>
    internal async Task<IResult> GetOptionsAsync(ICurrentPrincipalAccessor principal, CancellationToken ct)
    {
        if (Forbidden(principal, Permissions.ExtensionsRead) is { } denied)
        {
            return denied;
        }

        var options = await new OptionsStore(Store, _log).LoadAsync(ct);
        return Results.Json(OptionsView.From(options), OptionsResponseJsonOptions);
    }

    /// <summary>
    /// Persists the submitted URL / API key / version / quality profile / path translation. Write-only key
    /// semantics: an empty submitted key preserves the stored one (<see cref="WhisparrOptions.WithSubmitted"/>),
    /// so saving from a UI that never held the key does not blank it. The server-managed
    /// <c>DetectedVersion</c>/<c>WebhookSecret</c> are left untouched. 403-first on <c>extensions.configure</c>.
    /// </summary>
    internal async Task<IResult> SaveOptionsAsync(
        OptionsSaveRequest req, ICurrentPrincipalAccessor principal, CancellationToken ct)
    {
        if (Forbidden(principal, Permissions.ExtensionsConfigure) is { } denied)
        {
            return denied;
        }

        var store = new OptionsStore(Store, _log);
        var current = await store.LoadAsync(ct);
        var updated = current.WithSubmitted(
            req.BaseUrl, req.ApiKey, req.SelectedVersion, req.QualityProfileId,
            pathTranslation: req.PathTranslation,
            tagsOnAdd: req.TagsOnAdd,
            monitorNewByDefault: req.MonitorNewByDefault,
            allowQualityUpgrades: req.AllowQualityUpgrades);
        await store.SaveAsync(updated, ct);
        _selectedVersion = updated.SelectedVersion; // keep the sync GetUIManifest gate current after a version change

        return Results.Json(OptionsView.From(updated), OptionsResponseJsonOptions);
    }

    /// <summary>
    /// Lists the instance's root folders. There is no root-folder setting — the add-time derivation reads the
    /// list server-side — so this endpoint stays for connection diagnostics / a future advanced view. Resolves
    /// the connect creds (submitted, or the stored key only against the stored host — see
    /// <see cref="ResolveCredsAsync"/>), selects the adapter from the persisted version, and returns the fetched
    /// <c>RootFolder[]</c> — or the classified error on a non-Ok transport result. 403-first on
    /// <c>extensions.configure</c>: it reaches the stored credentials, so a read-only principal must not reach it.
    /// </summary>
    internal async Task<IResult> ListRootFoldersAsync(
        TestConnectionRequest req, WhisparrClient client, ICurrentPrincipalAccessor principal, CancellationToken ct)
    {
        if (Forbidden(principal, Permissions.ExtensionsConfigure) is { } denied)
        {
            return denied;
        }

        var (options, baseUrl, apiKey) = await ResolveCredsAsync(req, ct);
        if (AdapterSelector.SelectForVersion(options.SelectedVersion, client) is not { } adapter)
        {
            return Results.Json(new { code = "VERSION_UNSUPPORTED" }, statusCode: 400);
        }

        var result = await adapter.ListRootFoldersAsync(baseUrl, apiKey, ct);
        return result.IsOk
            ? Results.Json(result.Value, OptionsResponseJsonOptions)
            : Results.Json(new { result = FailureDiscriminator(result.State) }, statusCode: 502);
    }

    /// <summary>
    /// Lists the instance's quality profiles for the settings dropdown. Same shape as
    /// <see cref="ListRootFoldersAsync"/>. 403-first on <c>extensions.configure</c>.
    /// </summary>
    internal async Task<IResult> ListQualityProfilesAsync(
        TestConnectionRequest req, WhisparrClient client, ICurrentPrincipalAccessor principal, CancellationToken ct)
    {
        if (Forbidden(principal, Permissions.ExtensionsConfigure) is { } denied)
        {
            return denied;
        }

        var (options, baseUrl, apiKey) = await ResolveCredsAsync(req, ct);
        if (AdapterSelector.SelectForVersion(options.SelectedVersion, client) is not { } adapter)
        {
            return Results.Json(new { code = "VERSION_UNSUPPORTED" }, statusCode: 400);
        }

        var result = await adapter.ListQualityProfilesAsync(baseUrl, apiKey, ct);
        return result.IsOk
            ? Results.Json(result.Value, OptionsResponseJsonOptions)
            : Results.Json(new { result = FailureDiscriminator(result.State) }, statusCode: 502);
    }

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
    private async Task<(WhisparrOptions Options, string BaseUrl, string ApiKey)> ResolveCredsAsync(
        TestConnectionRequest req, CancellationToken ct)
    {
        var options = await new OptionsStore(Store, _log).LoadAsync(ct);
        var overrodeUrl = !string.IsNullOrWhiteSpace(req.BaseUrl);
        var baseUrl = overrodeUrl ? req.BaseUrl! : options.BaseUrl;

        string apiKey;
        if (!string.IsNullOrEmpty(req.ApiKey))
        {
            apiKey = req.ApiKey; // caller supplied its own key — use it as-is
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

    /// <summary>Maps a non-Ok transport state to the UI's error discriminator (never leaks the key/reason).</summary>
    private static string FailureDiscriminator(WhisparrResultState state) => state switch
    {
        WhisparrResultState.BadKey => "badKey",
        WhisparrResultState.NotWhisparr => "notWhisparr",
        WhisparrResultState.Rejected => "rejected",
        _ => "unreachable",
    };

    /// <summary>
    /// Returns the webhook URL + an authoritative <c>registered</c> flag sourced from Whisparr's own
    /// "Cove Whisparr Sync" connection: present → its url + <c>registered:true</c>; absent → the derived default
    /// (persisted <see cref="WhisparrOptions.WebhookHost"/>, else the request host) + <c>registered:false</c>.
    /// 403-first on <c>extensions.configure</c> — it mints the secret and reaches the stored creds to call
    /// Whisparr. The secret is never logged.
    /// </summary>
    internal async Task<IResult> WebhookUrlAsync(
        string coveBaseUrl, WhisparrClient client, ICurrentPrincipalAccessor principal, CancellationToken ct)
    {
        if (Forbidden(principal, Permissions.ExtensionsConfigure) is { } denied)
        {
            return denied;
        }

        var (options, secret) = await EnsureWebhookSecretAsync(ct);
        var origin = string.IsNullOrWhiteSpace(options.WebhookHost) ? coveBaseUrl : options.WebhookHost;
        var derivedUrl = WebhookUrlBuilder.BuildUrl(origin, secret);

        // A non-Ok list must degrade to the derived default (never 500): a down Whisparr cannot block the
        // settings page from loading and offering copy-paste + register.
        if (AdapterSelector.SelectForVersion(options.SelectedVersion, client) is { } adapter)
        {
            var found = await adapter.FindWebhookConnectionAsync(options.BaseUrl, options.ApiKey, ct);
            if (found is { IsOk: true, Value: { } connection })
            {
                var url = string.IsNullOrWhiteSpace(connection.Url) ? derivedUrl : connection.Url;
                return Results.Json(new WebhookUrlResponse(url, Registered: true), OptionsResponseJsonOptions);
            }
        }

        return Results.Json(new WebhookUrlResponse(derivedUrl, Registered: false), OptionsResponseJsonOptions);
    }

    /// <summary>
    /// Idempotent auto-register of the Cove webhook in Whisparr, persisting the resolved host. Mints/persists
    /// the secret, resolves + stores the origin, and delegates to the update-or-create adapter register. An
    /// already-existing connection (including a unique-name 400/409) resolves to <c>registered:true</c> — a
    /// re-register never errors and never falsely reports "not registered". A refused version or a non-Ok
    /// transport returns <c>registered:false</c> — the UI falls back to copy-paste, and the connect flow never
    /// fails. 403-first on <c>extensions.configure</c>. The secret is never logged.
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
        string coveBaseUrl, WhisparrClient client, ICurrentPrincipalAccessor principal, CancellationToken ct,
        string? overrideUrl = null)
    {
        if (Forbidden(principal, Permissions.ExtensionsConfigure) is { } denied)
        {
            return denied;
        }

        var (options, secret) = await EnsureWebhookSecretAsync(ct);
        var origin = coveBaseUrl;
        if (!string.IsNullOrWhiteSpace(overrideUrl)
            && Uri.TryCreate(overrideUrl, UriKind.Absolute, out var parsed)
            && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps))
        {
            origin = parsed.GetLeftPart(UriPartial.Authority);
        }

        if (!string.Equals(origin, options.WebhookHost, StringComparison.Ordinal))
        {
            options = options with { WebhookHost = origin };
            await new OptionsStore(Store, _log).SaveAsync(options, ct);
        }

        var url = WebhookUrlBuilder.BuildUrl(origin, secret);

        if (AdapterSelector.SelectForVersion(options.SelectedVersion, client) is not { } adapter)
        {
            LogWebhookRegistered(false);
            return Results.Json(new { registered = false }, OptionsResponseJsonOptions);
        }

        var result = await adapter.RegisterWebhookAsync(options.BaseUrl, options.ApiKey, url, ct);
        LogWebhookRegistered(result.IsOk);
        return Results.Json(new { registered = result.IsOk }, OptionsResponseJsonOptions);
    }

    /// <summary>
    /// Returns the last persisted match map + status counts — a pure read of the extension's own match store,
    /// reaching no credentials and opening no scope. Read-gated (<c>extensions.read</c>): the only reconciliation
    /// route a read-only principal may reach.
    /// </summary>
    internal async Task<IResult> ReconciliationAsync(ICurrentPrincipalAccessor principal, CancellationToken ct)
    {
        if (Forbidden(principal, Permissions.ExtensionsRead) is { } denied)
        {
            return denied;
        }

        var persisted = await new MatchStateStore(Store).LoadAllAsync(ct);
        var counts = new PersistedCounts(
            Confirmed: persisted.Count(e => e.Status == MatchStatus.Confirmed),
            NeedsReview: persisted.Count(e => e.Status == MatchStatus.NeedsReview),
            Rejected: persisted.Count(e => e.Status == MatchStatus.Rejected),
            Total: persisted.Count);
        return Results.Json(new { entries = persisted, counts }, ReconciliationResponseJsonOptions);
    }

    /// <summary>
    /// Loads the options, minting + persisting a webhook secret when one is absent (so the URL is stable
    /// across calls). Returns the effective options and the secret.
    /// </summary>
    private async Task<(WhisparrOptions Options, string Secret)> EnsureWebhookSecretAsync(CancellationToken ct)
    {
        var store = new OptionsStore(Store, _log);
        var options = await store.LoadAsync(ct);
        var secret = WebhookUrlBuilder.EnsureSecret(options.WebhookSecret);
        if (secret != options.WebhookSecret)
        {
            options = options with { WebhookSecret = secret };
            await store.SaveAsync(options, ct);
        }

        return (options, secret);
    }

    /// <summary>
    /// Runs the full connect flow against the supplied Whisparr URL + API key and returns a discriminated
    /// result the UI branches on: <c>ok</c> (with version + instance name), <c>badKey</c>,
    /// <c>unreachable</c> (with a short reason), <c>notWhisparr</c> (HTML/502), or <c>versionMismatch</c>
    /// (with the detected version — the fail-closed refusal when the major version is not 3). The
    /// adapter is chosen from the parsed status via <see cref="AdapterSelector"/>, never from the status
    /// code (both v2 and v3 answer <c>/api/v3</c>). 403-first on <c>extensions.configure</c> (the host
    /// <c>[RequiresPermission]</c> filter is inert on minimal-API). The API key is used server-side only
    /// and is never included in the response.
    /// </summary>
    internal async Task<IResult> TestConnectionAsync(
        TestConnectionRequest req, WhisparrClient client, ICurrentPrincipalAccessor principal, CancellationToken ct)
    {
        if (Forbidden(principal, Permissions.ExtensionsConfigure) is { } denied)
        {
            return denied;
        }

        // Once a key is saved the settings field is masked ("Key is set — type to replace"), so a user
        // re-testing a stored connection sends a BLANK key. Resolve through ResolveCredsAsync so the typed
        // key wins when present, else the STORED key is used — but only when the typed host matches the
        // stored host, never leaking the stored key to a caller-chosen foreign host.
        var (_, baseUrl, apiKey) = await ResolveCredsAsync(req, ct);
        var result = await client.GetStatusAsync(baseUrl, apiKey, ct);

        switch (result.State)
        {
            case WhisparrResultState.Ok:
                var status = result.Value!;
                // Branch on the parsed version, never the 200 status: a v2 instance also answers /api/v3.
                if (AdapterSelector.Select(status, client) is null)
                {
                    LogVersionRefused(AdapterSelector.ParseMajor(status.Version));
                    return Results.Json(
                        new { result = "versionMismatch", detected = status.Version },
                        TestConnectionResponseJsonOptions);
                }

                LogConnectTested(status.Version ?? "unknown", status.InstanceName ?? "unknown");
                return Results.Json(
                    new { result = "success", version = status.Version, instanceName = status.InstanceName },
                    TestConnectionResponseJsonOptions);

            case WhisparrResultState.BadKey:
                return Results.Json(new { result = "badKey" }, TestConnectionResponseJsonOptions);

            case WhisparrResultState.NotWhisparr:
                return Results.Json(new { result = "notWhisparr" }, TestConnectionResponseJsonOptions);

            default:
                LogWhisparrUnreachable(result.Reason ?? result.State.ToString());
                return Results.Json(
                    new { result = "unreachable", reason = result.Reason },
                    TestConnectionResponseJsonOptions);
        }
    }
}
