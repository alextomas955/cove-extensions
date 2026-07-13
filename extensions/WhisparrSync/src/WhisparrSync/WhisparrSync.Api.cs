using System.Text.Json;
using Cove.Core.Auth;
using Cove.Plugins;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using WhisparrSync.Adapters;
using WhisparrSync.Client;
using WhisparrSync.Options;
using WhisparrSync.Webhook;

namespace WhisparrSync;

/// <summary>
/// The Cove-facing surface of the extension: the full-page settings tab (contributed through
/// <see cref="GetUIManifest"/>) and the <c>/test-connection</c> minimal-API endpoint. Stays disjoint
/// from <c>WhisparrSync.cs</c> (identity + DI) and <c>WhisparrSync.Logging.cs</c>.
/// </summary>
public sealed partial class WhisparrSync
{
    // The route prefix mirrors how the host mounts an extension's endpoints: /api/extensions/{id}/…
    private const string RouteBase = "/api/extensions/com.alextomas955.whisparrsync";
    private const string TestConnectionRoute = RouteBase + "/test-connection";
    private const string StatusRoute = RouteBase + "/status";
    private const string OptionsRoute = RouteBase + "/options";
    private const string RootFoldersRoute = RouteBase + "/rootfolders";
    private const string QualityProfilesRoute = RouteBase + "/qualityprofiles";
    private const string WebhookUrlRoute = RouteBase + "/webhook-url";
    private const string RegisterWebhookRoute = RouteBase + "/register-webhook";

    /// <summary>
    /// Contributes the Whisparr Sync settings tab as a full-page layout (like Renamer): the host renders
    /// the tab's panel full-width with no card chrome and the extension owns the canvas. The section's
    /// <c>componentName</c> ("WhisparrSyncPage") MUST equal the key in the bundle's <c>defineExtension</c>
    /// components map (see the UI <c>index.ts</c>).
    /// </summary>
    public override UIManifest GetUIManifest()
        => ManifestBuilder()
            .AddSettingsTab(
                key: "whisparr-sync",
                label: "Whisparr Sync",
                description: "Connect Cove to your Whisparr instance.",
                order: 100,
                layout: SettingsTabLayout.Page)
            .AddSettingsSection(targetTab: "whisparr-sync", label: "Whisparr Sync", componentName: "WhisparrSyncPage")
            .WithJsBundle("index.mjs")
            .Build();

    /// <summary>
    /// Maps the settings endpoints. The lambda immediately delegates to an extracted instance method so
    /// the handler is unit-testable without an HTTP host. The host resolves the typed
    /// <see cref="WhisparrClient"/> and <see cref="ICurrentPrincipalAccessor"/> from the request scope.
    /// </summary>
    public override void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(TestConnectionRoute,
            (TestConnectionRequest req, WhisparrClient client, ICurrentPrincipalAccessor principal, CancellationToken ct)
                => TestConnectionAsync(req, client, principal, ct));

        endpoints.MapGet(StatusRoute,
            (ICurrentPrincipalAccessor principal, CancellationToken ct) => StatusAsync(principal, ct));

        endpoints.MapGet(OptionsRoute,
            (ICurrentPrincipalAccessor principal, CancellationToken ct) => GetOptionsAsync(principal, ct));

        endpoints.MapPost(OptionsRoute,
            (OptionsSaveRequest req, ICurrentPrincipalAccessor principal, CancellationToken ct)
                => SaveOptionsAsync(req, principal, ct));

        // Root-folder / quality-profile lists are POSTs carrying the connect creds in the BODY (never a
        // query string — CONN-06): a just-tested (unsaved) key populates the dropdowns immediately, and an
        // empty submitted key on reload reuses the stored key ONLY against the stored host (CR-01 — the
        // stored key is never sent to a caller-supplied foreign host). These require extensions.configure.
        endpoints.MapPost(RootFoldersRoute,
            (TestConnectionRequest req, WhisparrClient client, ICurrentPrincipalAccessor principal, CancellationToken ct)
                => ListRootFoldersAsync(req, client, principal, ct));

        endpoints.MapPost(QualityProfilesRoute,
            (TestConnectionRequest req, WhisparrClient client, ICurrentPrincipalAccessor principal, CancellationToken ct)
                => ListQualityProfilesAsync(req, client, principal, ct));

        // The Cove host base for the webhook URL is derived from the inbound request (scheme + host) — the
        // extension backend has no other authoritative view of its own public address.
        endpoints.MapGet(WebhookUrlRoute,
            (HttpRequest http, ICurrentPrincipalAccessor principal, CancellationToken ct)
                => WebhookUrlAsync($"{http.Scheme}://{http.Host}", principal, ct));

        endpoints.MapPost(RegisterWebhookRoute,
            (HttpRequest http, WhisparrClient client, ICurrentPrincipalAccessor principal, CancellationToken ct)
                => RegisterWebhookAsync($"{http.Scheme}://{http.Host}", client, principal, ct));
    }

    /// <summary>
    /// The 403-first permission gate every settings handler shares: returns a <c>403 FORBIDDEN</c> result
    /// when the principal is null or lacks <paramref name="permission"/>, otherwise <c>null</c> (proceed).
    /// The host <c>[RequiresPermission]</c> filter is inert on minimal-API endpoints, so each handler must
    /// re-check itself.
    /// </summary>
    private static IResult? Forbidden(ICurrentPrincipalAccessor principal, string permission)
        => principal.Current is null || !principal.Current.Has(permission)
            ? Results.Json(new { code = "FORBIDDEN" }, statusCode: 403)
            : null;

    /// <summary>
    /// Returns whether the extension is configured (a base URL and a stored key are present) — the
    /// redaction-safe status projection (CONN-06: never the raw key). 403-first on <c>extensions.read</c>.
    /// </summary>
    /// <remarks>
    /// WR-03: <c>detectedVersion</c> is intentionally NOT projected here. Nothing in this phase ever persists
    /// <see cref="WhisparrOptions.DetectedVersion"/> (a successful test returns it to the UI in the
    /// test-connection response but never writes it), so exposing it on <c>/status</c> would advertise a
    /// permanently-empty field a downstream consumer could wrongly trust. The field is re-added to this
    /// projection once a later phase wires its persistence.
    /// </remarks>
    internal async Task<IResult> StatusAsync(ICurrentPrincipalAccessor principal, CancellationToken ct)
    {
        if (Forbidden(principal, Permissions.ExtensionsRead) is { } denied)
        {
            return denied;
        }

        var options = await new OptionsStore(Store).LoadAsync(ct);
        var configured = !string.IsNullOrWhiteSpace(options.BaseUrl) && !string.IsNullOrEmpty(options.ApiKey);
        return Results.Json(
            new { configured, hasApiKey = !string.IsNullOrEmpty(options.ApiKey) },
            OptionsResponseJsonOptions);
    }

    /// <summary>
    /// Returns the persisted options as a redaction-safe <see cref="OptionsView"/> — every field except the
    /// API key, which is projected to a <c>hasApiKey</c> boolean (CONN-06). 403-first on <c>extensions.read</c>.
    /// </summary>
    internal async Task<IResult> GetOptionsAsync(ICurrentPrincipalAccessor principal, CancellationToken ct)
    {
        if (Forbidden(principal, Permissions.ExtensionsRead) is { } denied)
        {
            return denied;
        }

        var options = await new OptionsStore(Store).LoadAsync(ct);
        return Results.Json(OptionsView.From(options), OptionsResponseJsonOptions);
    }

    /// <summary>
    /// Persists the submitted URL / API key / version / root folder / quality profile. Write-only key
    /// semantics: an empty submitted key preserves the stored one (<see cref="WhisparrOptions.WithSubmitted"/>),
    /// so saving from a UI that never held the key does not blank it (CONN-06). The server-managed
    /// <c>DetectedVersion</c>/<c>WebhookSecret</c> are left untouched. 403-first on <c>extensions.configure</c>.
    /// </summary>
    internal async Task<IResult> SaveOptionsAsync(
        OptionsSaveRequest req, ICurrentPrincipalAccessor principal, CancellationToken ct)
    {
        if (Forbidden(principal, Permissions.ExtensionsConfigure) is { } denied)
        {
            return denied;
        }

        var store = new OptionsStore(Store);
        var current = await store.LoadAsync(ct);
        var updated = current.WithSubmitted(
            req.BaseUrl, req.ApiKey, req.SelectedVersion, req.RootFolderId, req.QualityProfileId);
        await store.SaveAsync(updated, ct);

        return Results.Json(OptionsView.From(updated), OptionsResponseJsonOptions);
    }

    /// <summary>
    /// Lists the instance's root folders for the settings dropdown (CONN-05). Resolves the connect creds
    /// (submitted, or the stored key only against the stored host — see <see cref="ResolveCredsAsync"/>),
    /// selects the adapter from the persisted version, and returns the fetched <c>RootFolder[]</c> — or the
    /// classified error on a non-Ok transport result. 403-first on <c>extensions.configure</c> (CR-01):
    /// populating the dropdowns is part of the configure flow and reaches the stored credentials, so a
    /// read-only principal must not reach it.
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
    /// Lists the instance's quality profiles for the settings dropdown (CONN-05). Same shape as
    /// <see cref="ListRootFoldersAsync"/>. 403-first on <c>extensions.configure</c> (CR-01).
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
    /// Loads the stored options and resolves the effective connect creds. Security invariant (CR-01): the
    /// server-stored API key is NEVER sent to a caller-chosen host. A submitted key is always used as-is; an
    /// empty submitted key falls back to the stored key ONLY when the effective base URL is the stored one
    /// (the caller did not override it, or overrode it with the same host). If the caller overrides the base
    /// URL with a different host and supplies no key, the stored key is withheld (empty) — so a low-privilege
    /// request can never exfiltrate the stored key to <c>http://attacker</c>. This preserves the CONN-05
    /// dropdown UX: on reload the UI sends the stored URL + an empty key (stored key reused against the stored
    /// host), and after a test it sends the form URL + the form key (its own key used directly).
    /// </summary>
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
        else
        {
            apiKey = string.Empty; // refuse to send the stored key to a caller-chosen foreign host
        }

        return (options, baseUrl, apiKey);
    }

    /// <summary>Maps a non-Ok transport state to the UI's error discriminator (never leaks the key/reason).</summary>
    private static string FailureDiscriminator(WhisparrResultState state) => state switch
    {
        WhisparrResultState.BadKey => "badKey",
        WhisparrResultState.NotWhisparr => "notWhisparr",
        _ => "unreachable",
    };

    /// <summary>
    /// Returns the ready-to-use webhook URL with the embedded secret (CONN-07). The secret is minted via
    /// <c>RandomNumberGenerator</c> and persisted once so the URL is stable across calls. 403-first on
    /// <c>extensions.configure</c> (CR-01): minting/persisting the webhook secret is part of the configure
    /// flow, so a read-only principal must not reach it. The secret is shown for the user to paste; it is
    /// never logged.
    /// </summary>
    internal async Task<IResult> WebhookUrlAsync(
        string coveBaseUrl, ICurrentPrincipalAccessor principal, CancellationToken ct)
    {
        if (Forbidden(principal, Permissions.ExtensionsConfigure) is { } denied)
        {
            return denied;
        }

        var (_, secret) = await EnsureWebhookSecretAsync(ct);
        return Results.Json(new { url = WebhookUrlBuilder.BuildUrl(coveBaseUrl, secret) }, OptionsResponseJsonOptions);
    }

    /// <summary>
    /// Best-effort auto-register of the Cove webhook in Whisparr (CONN-07). Mints/persists the secret,
    /// builds the URL, and posts the v3 Notification via the adapter. A non-2xx (or a refused version)
    /// returns <c>registered:false</c> — the UI falls back to copy-paste, and the connect flow never fails.
    /// 403-first on <c>extensions.configure</c>. The secret is never logged.
    /// </summary>
    internal async Task<IResult> RegisterWebhookAsync(
        string coveBaseUrl, WhisparrClient client, ICurrentPrincipalAccessor principal, CancellationToken ct)
    {
        if (Forbidden(principal, Permissions.ExtensionsConfigure) is { } denied)
        {
            return denied;
        }

        var (options, secret) = await EnsureWebhookSecretAsync(ct);
        var url = WebhookUrlBuilder.BuildUrl(coveBaseUrl, secret);

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
    /// Loads the options, minting + persisting a webhook secret when one is absent (so the URL is stable
    /// across calls). Returns the effective options and the secret.
    /// </summary>
    private async Task<(WhisparrOptions Options, string Secret)> EnsureWebhookSecretAsync(CancellationToken ct)
    {
        var store = new OptionsStore(Store);
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
    /// result the UI branches on (CONN-02): <c>ok</c> (with version + instance name), <c>badKey</c>,
    /// <c>unreachable</c> (with a short reason), <c>notWhisparr</c> (HTML/502), or <c>versionMismatch</c>
    /// (with the detected version — the fail-closed VER-04 refusal when the major version is not 3). The
    /// adapter is chosen from the parsed status via <see cref="AdapterSelector"/>, never from the status
    /// code (both v2 and v3 answer <c>/api/v3</c>). 403-first on <c>extensions.configure</c> (the host
    /// <c>[RequiresPermission]</c> filter is inert on minimal-API). The API key is used server-side only
    /// and is never included in the response (CONN-06).
    /// </summary>
    internal async Task<IResult> TestConnectionAsync(
        TestConnectionRequest req, WhisparrClient client, ICurrentPrincipalAccessor principal, CancellationToken ct)
    {
        if (principal.Current is null || !principal.Current.Has(Permissions.ExtensionsConfigure))
        {
            return Results.Json(new { code = "FORBIDDEN" }, statusCode: 403);
        }

        var result = await client.GetStatusAsync(req.BaseUrl ?? string.Empty, req.ApiKey ?? string.Empty, ct);

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

    // Serialize responses with the extension's own web-convention options so the wire shape (camelCase)
    // matches what the UI reads; the host's default minimal-API serializer is not relied on here.
    private static readonly JsonSerializerOptions TestConnectionResponseJsonOptions = new(JsonSerializerDefaults.Web);

    // The options / list responses keep property names AS DECLARED (PascalCase — the stored-blob spelling
    // the UI models), with the one deliberate exception the [JsonPropertyName] on OptionsView.HasApiKey
    // renders as `hasApiKey`. A default (non-Web) options instance applies no naming policy.
    private static readonly JsonSerializerOptions OptionsResponseJsonOptions = new();

    /// <summary>The Test-connection request body: the URL + key the user typed on the settings page.</summary>
    internal sealed record TestConnectionRequest(string? BaseUrl, string? ApiKey);

    /// <summary>
    /// The options-save request body. Case-insensitive minimal-API binding maps the UI's PascalCase JSON
    /// onto these. An empty/absent <see cref="ApiKey"/> preserves the stored key (write-only; CONN-06).
    /// </summary>
    internal sealed record OptionsSaveRequest(
        string? BaseUrl, string? ApiKey, string? SelectedVersion, int RootFolderId, int QualityProfileId);
}
