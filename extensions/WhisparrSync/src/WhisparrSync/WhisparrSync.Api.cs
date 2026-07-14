using System.Text.Json;
using System.Text.Json.Serialization;
using Cove.Core.Auth;
using Cove.Core.Interfaces;
using Cove.Plugins;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WhisparrSync.Adapters;
using WhisparrSync.Client;
using WhisparrSync.Ingest;
using WhisparrSync.Library;
using WhisparrSync.Matching;
using WhisparrSync.Options;
using WhisparrSync.Reconcile;
using WhisparrSync.Safety;
using WhisparrSync.State;
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

    // The ONE anonymous route (03-01): inbound Whisparr On-Import events. It carries no Cove principal, so
    // it deliberately OMITS the Forbidden(principal,…) gate every other route uses — the shared-secret token
    // validated inside WebhookReceiver is its auth (SEC-01).
    private const string WebhookRoute = RouteBase + "/webhook";

    // The read-only import-activity log (IMPT-04 review half): a pure read of the extension's own audit
    // journal, read-gated (extensions.read) exactly like /reconciliation.
    private const string ImportLogRoute = RouteBase + "/import-log";

    // The read-only root-overlap advisory (SEC-02): compares the Whisparr roots against the Cove library
    // roots and warns when they overlap (a re-grab-loop risk). Read-gated exactly like /reconciliation.
    private const string RootOverlapRoute = RouteBase + "/root-overlap";

    // The read-only reconciliation surface (02-03). /preview-sync computes the live diff (configure-gated —
    // it reaches the stored creds to call Whisparr, CR-01); /reconciliation is a pure match-map read
    // (read-gated); /match/confirm|reject validate a submitted pair against the fresh diff, then write ONLY
    // the extension's own match store (configure-gated).
    private const string PreviewSyncRoute = RouteBase + "/preview-sync";
    private const string ReconciliationRoute = RouteBase + "/reconciliation";
    private const string MatchConfirmRoute = RouteBase + "/match/confirm";
    private const string MatchRejectRoute = RouteBase + "/match/reject";

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

        // Preview-sync reads Cove + Whisparr and returns the zero-mutation diff; it takes no body (it uses the
        // stored creds only — CR-01). Confirm/reject carry the {coveId, whisparrMovieId} pair in the body.
        endpoints.MapPost(PreviewSyncRoute,
            (WhisparrClient client, ICurrentPrincipalAccessor principal, CancellationToken ct)
                => PreviewSyncAsync(client, principal, ct));

        endpoints.MapGet(ReconciliationRoute,
            (ICurrentPrincipalAccessor principal, CancellationToken ct) => ReconciliationAsync(principal, ct));

        endpoints.MapPost(MatchConfirmRoute,
            (MatchDecisionRequest req, WhisparrClient client, ICurrentPrincipalAccessor principal, CancellationToken ct)
                => MatchConfirmAsync(req, client, principal, ct));

        endpoints.MapPost(MatchRejectRoute,
            (MatchDecisionRequest req, WhisparrClient client, ICurrentPrincipalAccessor principal, CancellationToken ct)
                => MatchRejectAsync(req, client, principal, ct));

        // Anonymous inbound webhook (SEC-01): bind the raw HttpContext so the receiver reads the token
        // (X-Cove-Token header or ?token= query) and body itself. NO principal parameter, NO Forbidden gate —
        // the token is the auth. The coordinator resolves the scoped IScanService from the captured factory and
        // gates every ingest on the cached Whisparr root set (T-03-PT, fail-closed when roots are unavailable).
        endpoints.MapPost(WebhookRoute,
            (HttpContext http, WhisparrClient client, CancellationToken ct)
                => new WebhookReceiver(Store, new IngestCoordinator(ScopeFactory, c => GetWhisparrRootsAsync(client, c)))
                    .HandleAsync(http, ct));

        // Read-only audit log (IMPT-04): a pure store read, 403-first on extensions.read.
        endpoints.MapGet(ImportLogRoute,
            (ICurrentPrincipalAccessor principal, CancellationToken ct) => ImportLogAsync(principal, ct));

        // Read-only root-overlap advisory (SEC-02): reads the Whisparr roots (stored creds) + the Cove
        // library roots and reports any overlap. 403-first on extensions.read.
        endpoints.MapGet(RootOverlapRoute,
            (WhisparrClient client, ICurrentPrincipalAccessor principal, CancellationToken ct)
                => RootOverlapAsync(client, principal, ct));
    }

    /// <summary>
    /// Reports whether any Cove library root overlaps a Whisparr root — a re-grab-loop risk (SEC-02). Reads
    /// the Whisparr roots (via the stored creds, cached) and the Cove roots (from <c>CoveConfiguration</c> if
    /// the host injects it into the extension scope — 03-RESEARCH Open Q1 — otherwise derived from the
    /// library's own file folders), then composes them with <see cref="RootOverlapDetector"/>. The result is
    /// a best-effort ADVISORY only: cross-mount / cross-container deployments legitimately see the same
    /// library at different paths, so the warning says so and is never a hard gate. 403-first on
    /// <c>extensions.read</c> — a pure read that mutates nothing.
    /// </summary>
    internal async Task<IResult> RootOverlapAsync(
        WhisparrClient client, ICurrentPrincipalAccessor principal, CancellationToken ct)
    {
        if (Forbidden(principal, Permissions.ExtensionsRead) is { } denied)
        {
            return denied;
        }

        var whisparrRoots = await GetWhisparrRootsAsync(client, ct);
        var coveRoots = await GetCoveRootsAsync(ct);
        var overlaps = RootOverlapDetector.Detect(whisparrRoots, coveRoots);

        var warning = overlaps.Count == 0
            ? null
            : "One or more Cove library roots overlap a Whisparr root. Because Cove imports files in place " +
              "(never moving or deleting them), a shared root can let an import echo back to Whisparr as a new " +
              "grab. This is a best-effort advisory: cross-mount or containerized deployments may legitimately " +
              "see the same library at different paths, so verify against your own layout.";

        return Results.Json(new { overlaps, warning }, ImportLogResponseJsonOptions);
    }

    /// <summary>
    /// Resolves the Cove library roots for the SEC-02 overlap check. Preferred source (03-RESEARCH Open Q1):
    /// <c>CoveConfiguration.CovePaths</c> resolved from a fresh scope when the host injects it. Fallback: the
    /// distinct parent folders of the library's own file paths via <see cref="CoveLibraryPort"/> — enough for
    /// an advisory containment comparison. Returns an empty set (no warning) when neither source is available;
    /// the overlap warning is advisory, so an unavailable source degrades to silence, never an error.
    /// </summary>
    private async Task<IReadOnlyList<string>> GetCoveRootsAsync(CancellationToken ct)
    {
        if (_scopeFactory is null)
        {
            return [];
        }

        await using var scope = _scopeFactory.CreateAsyncScope();

        // Preferred: the host-configured media-library roots ScanService scans (VERIFIED to exist).
        if (scope.ServiceProvider.GetService<CoveConfiguration>() is { CovePaths: { Count: > 0 } covePaths })
        {
            return [.. covePaths
                .Select(p => p.Path)
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)];
        }

        // Fallback: derive distinct folders from the library's own files (needs no host-config access).
        if (scope.ServiceProvider.GetService<DbContext>() is not { } db)
        {
            return [];
        }

        var options = await new OptionsStore(Store).LoadAsync(ct);
        var videos = await new CoveLibraryPort(db, options.StashDbEndpoint).LoadAllVideosAsync(ct);
        return [.. videos
            .SelectMany(v => v.FilePaths)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => Path.GetDirectoryName(p) ?? p)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// Returns the auto-import audit log — every attempt with its result, source, time, path, and Cove item —
    /// plus imported/skipped/flagged/total counts (IMPT-04 review half). A pure read of the extension's own
    /// journal (reaches no credentials, opens no scope). 403-first on <c>extensions.read</c>.
    /// </summary>
    internal async Task<IResult> ImportLogAsync(ICurrentPrincipalAccessor principal, CancellationToken ct)
    {
        if (Forbidden(principal, Permissions.ExtensionsRead) is { } denied)
        {
            return denied;
        }

        var entries = await new ImportLog(Store).LoadAllAsync(ct);
        var counts = new ImportLogCounts(
            Imported: entries.Count(e => e.Result == "Imported"),
            Skipped: entries.Count(e => e.Result == "Skipped"),
            Flagged: entries.Count(e => e.Result == "Flagged"),
            Total: entries.Count);
        return Results.Json(new { entries, counts }, ImportLogResponseJsonOptions);
    }

    /// <summary>
    /// One reconcile pass, run inside the enqueued exclusive job (the scheduler's work delegate). Resolves the
    /// stored creds (CR-01 — stored key only against the stored host) and the request-scoped
    /// <see cref="WhisparrClient"/>, then runs the <see cref="ReconcileJob"/> over the SAME
    /// <see cref="IngestCoordinator"/> + Whisparr-root guard the webhook uses. A no-op until configured.
    /// </summary>
    internal async Task RunReconcileAsync(CancellationToken ct)
    {
        await using var scope = ScopeFactory.CreateAsyncScope();
        var client = scope.ServiceProvider.GetRequiredService<WhisparrClient>();

        var (_, baseUrl, apiKey) = await ResolveCredsAsync(new TestConnectionRequest(null, null), ct);
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrEmpty(apiKey))
        {
            return; // not configured yet — nothing to reconcile against
        }

        var coordinator = new IngestCoordinator(ScopeFactory, c => GetWhisparrRootsAsync(client, c));
        var job = new ReconcileJob(
            Store, coordinator, (page, c) => client.ListHistoryAsync(baseUrl, apiKey, page, ReconcileJob.PageSize, c));
        await job.RunAsync(ct);
    }

    // A short in-memory cache of the Whisparr root folders (they change rarely): the webhook ingest guard
    // consults this per event, so it must never issue an uncached GET per event. Fail-closed — a failed fetch
    // leaves the cache untouched and returns no roots, so the containment guard rejects until roots are known.
    private IReadOnlyList<string>? _cachedWhisparrRoots;
    private DateTime _whisparrRootsCachedAtUtc;
    // Static (process-lifetime, never disposed): the extension instance is a long-lived host singleton, and a
    // static gate avoids owning a disposable instance field (CA1001) while still serializing the cache refill.
    private static readonly SemaphoreSlim WhisparrRootsGate = new(1, 1);
    private static readonly TimeSpan WhisparrRootsCacheTtl = TimeSpan.FromMinutes(5);

    private async ValueTask<IReadOnlyList<string>> GetWhisparrRootsAsync(WhisparrClient client, CancellationToken ct)
    {
        if (_cachedWhisparrRoots is { } fresh && DateTime.UtcNow - _whisparrRootsCachedAtUtc < WhisparrRootsCacheTtl)
        {
            return fresh;
        }

        await WhisparrRootsGate.WaitAsync(ct);
        try
        {
            if (_cachedWhisparrRoots is { } cached && DateTime.UtcNow - _whisparrRootsCachedAtUtc < WhisparrRootsCacheTtl)
            {
                return cached;
            }

            // Stored creds only (CR-01): the root fetch reuses the saved host/key, never a caller-supplied host.
            var (_, baseUrl, apiKey) = await ResolveCredsAsync(new TestConnectionRequest(null, null), ct);
            var result = await client.ListRootFoldersAsync(baseUrl, apiKey, ct);
            if (!result.IsOk || result.Value is not { } rows)
            {
                return []; // fail-closed: no roots → the guard rejects; cache untouched so the next event retries
            }

            var roots = rows
                .Where(r => !string.IsNullOrWhiteSpace(r.Path))
                .Select(r => r.Path!)
                .ToArray();
            _cachedWhisparrRoots = roots;
            _whisparrRootsCachedAtUtc = DateTime.UtcNow;
            return roots;
        }
        finally
        {
            WhisparrRootsGate.Release();
        }
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
    /// Computes the read-only reconciliation diff (MATCH-03): reads Cove via <see cref="CoveLibraryPort"/>,
    /// fetches the Whisparr movie set via the adapter, loads the persisted match map, and composes them with
    /// <see cref="ReconciliationService"/>. Returns the diff as a flat list of rows (matched / needs-review /
    /// unmatched) plus counts. Configure-gated (CR-01): it reaches the stored credentials to call Whisparr, so
    /// a read-only principal must not reach it. ZERO mutation of Cove or Whisparr.
    /// </summary>
    internal async Task<IResult> PreviewSyncAsync(
        WhisparrClient client, ICurrentPrincipalAccessor principal, CancellationToken ct)
    {
        if (Forbidden(principal, Permissions.ExtensionsConfigure) is { } denied)
        {
            return denied;
        }

        var (error, diff) = await ComputeReconciliationAsync(client, ct);
        return error ?? Results.Json(ToReconResponse(diff!), ReconciliationResponseJsonOptions);
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
    /// Promotes a needs-review suggestion to a confirmed link (MATCH-02): validates the submitted pair against
    /// the freshly-computed diff, then upserts it into the match store as <see cref="MatchStatus.Confirmed"/>.
    /// Configure-gated (it recomputes the diff, which reaches the stored creds). The ONLY write is to the
    /// extension's own match map.
    /// </summary>
    internal async Task<IResult> MatchConfirmAsync(
        MatchDecisionRequest req, WhisparrClient client, ICurrentPrincipalAccessor principal, CancellationToken ct)
    {
        if (Forbidden(principal, Permissions.ExtensionsConfigure) is { } denied)
        {
            return denied;
        }

        var (error, diff) = await ComputeReconciliationAsync(client, ct);
        return error ?? await ApplyMatchDecisionAsync(req, diff!, MatchStatus.Confirmed, ct);
    }

    /// <summary>
    /// Marks a needs-review suggestion rejected (MATCH-02) so a re-run suppresses it. Same validate-then-write
    /// shape as <see cref="MatchConfirmAsync"/>; the decision is <see cref="MatchStatus.Rejected"/>. The only
    /// write is to the extension's own match map (never Cove or Whisparr). NOTE: this phase has no un-reject /
    /// un-confirm path — once written, a decision moves the pair out of needs-review (rejected → suppressed to
    /// unmatched, confirmed → matched), so it cannot be reversed from the UI until a later phase adds a
    /// clear/reset endpoint (IN-05).
    /// </summary>
    internal async Task<IResult> MatchRejectAsync(
        MatchDecisionRequest req, WhisparrClient client, ICurrentPrincipalAccessor principal, CancellationToken ct)
    {
        if (Forbidden(principal, Permissions.ExtensionsConfigure) is { } denied)
        {
            return denied;
        }

        var (error, diff) = await ComputeReconciliationAsync(client, ct);
        return error ?? await ApplyMatchDecisionAsync(req, diff!, MatchStatus.Rejected, ct);
    }

    /// <summary>
    /// Validates <paramref name="req"/> against <paramref name="diff"/> and, only on a match, upserts the
    /// decision into the match store. Extracted from the routed handlers so the validation-before-write rule is
    /// unit-testable host-free (no scope / no Whisparr).
    /// </summary>
    /// <remarks>
    /// V5 input validation (T-02-03-C): the submitted <c>(coveId, whisparrMovieId)</c> pair MUST be a live
    /// needs-review suggestion in the freshly-computed diff. A forged/stale id writes NOTHING — confirm/reject
    /// only ever act on a suggestion the chain currently proposes, and the single write target is the
    /// extension's own match map (never Cove or Whisparr).
    /// </remarks>
    internal async Task<IResult> ApplyMatchDecisionAsync(
        MatchDecisionRequest req, ReconciliationDiff diff, MatchStatus decision, CancellationToken ct)
    {
        var row = diff.NeedsReview.FirstOrDefault(r =>
            r.Movie.Id == req.WhisparrMovieId && r.MatchedVideo?.CoveId == req.CoveId);
        if (row is null)
        {
            return Results.Json(new { code = "MATCH_NOT_IN_DIFF" }, statusCode: 400);
        }

        var state = new MatchState(
            CoveId: req.CoveId,
            WhisparrMovieId: req.WhisparrMovieId,
            StashId: row.MatchedVideo!.StashIds.Count > 0 ? row.MatchedVideo.StashIds[0] : string.Empty,
            MatchedBy: row.Leg ?? MatchedBy.Fuzzy,
            MatchedAtUtcTicks: DateTime.UtcNow.Ticks,
            Status: decision);

        var store = new MatchStateStore(Store);
        if (decision == MatchStatus.Confirmed)
        {
            await store.ConfirmAsync(state, ct);
        }
        else
        {
            await store.RejectAsync(state, ct);
        }

        return Results.Json(
            new { ok = true, coveId = state.CoveId, whisparrMovieId = state.WhisparrMovieId, status = decision },
            ReconciliationResponseJsonOptions);
    }

    /// <summary>
    /// Loads Cove + Whisparr behind the 02-01 seams and composes the 02-02 diff. Returns a classified error
    /// <see cref="IResult"/> (400 unsupported version / 502 transport) OR the diff — never both. Uses the
    /// STORED creds only (<see cref="ResolveCredsAsync"/> with an empty request), so the stored key is never
    /// paired with a caller-supplied host (CR-01). Opens a fresh scope per run for the scoped
    /// <see cref="DbContext"/> and never mutates Cove or Whisparr.
    /// </summary>
    private async Task<(IResult? Error, ReconciliationDiff? Diff)> ComputeReconciliationAsync(
        WhisparrClient client, CancellationToken ct)
    {
        var (options, baseUrl, apiKey) = await ResolveCredsAsync(new TestConnectionRequest(null, null), ct);
        if (AdapterSelector.SelectForVersion(options.SelectedVersion, client) is not { } adapter)
        {
            return (Results.Json(new { code = "VERSION_UNSUPPORTED" }, statusCode: 400), null);
        }

        var movies = await adapter.ListMoviesAsync(baseUrl, apiKey, ct);
        if (!movies.IsOk)
        {
            return (Results.Json(new { result = FailureDiscriminator(movies.State) }, statusCode: 502), null);
        }

        // The 02-01 scope capture wired to the 02-02 service: a fresh CreateAsyncScope() per run so the scoped
        // DbContext has the correct lifetime (never a long-lived captured context), and the AsNoTracking port
        // is the only DbContext-touching surface — a plain reconcile writes nothing.
        await using var scope = ScopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DbContext>();
        var port = new CoveLibraryPort(db, options.StashDbEndpoint);
        var coveVideos = await port.LoadAllVideosAsync(ct);

        var persisted = await new MatchStateStore(Store).LoadAllAsync(ct);
        var diff = ReconciliationService.Reconcile(coveVideos, movies.Value!, persisted);
        return (null, diff);
    }

    /// <summary>Flattens the bucketed diff into a single ordered row list (matched → needs-review → unmatched) + counts.</summary>
    private static ReconResponse ToReconResponse(ReconciliationDiff diff)
    {
        var rows = new List<ReconRow>(diff.Counts.Total);
        rows.AddRange(diff.Matched.Select(r => ReconRow.From(r, "matched")));
        rows.AddRange(diff.NeedsReview.Select(r => ReconRow.From(r, "needsReview")));
        rows.AddRange(diff.Unmatched.Select(r => ReconRow.From(r, "unmatched")));
        return new ReconResponse(rows, diff.Counts);
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

    // The reconciliation responses are a fresh UI contract (no stored-blob spelling to preserve), so they use
    // the camelCase Web convention, and the JsonStringEnumConverter renders MatchedBy / MatchStatus as their
    // string names ("StashId" / "Fuzzy" / "Confirmed" …) rather than integers the UI would have to decode.
    private static readonly JsonSerializerOptions ReconciliationResponseJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    // The import-log is a fresh UI contract (no stored-blob spelling to preserve), so it uses the camelCase
    // Web convention; the JsonStringEnumConverter renders any enum-typed field as its string name for the UI.
    private static readonly JsonSerializerOptions ImportLogResponseJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>The Test-connection request body: the URL + key the user typed on the settings page.</summary>
    internal sealed record TestConnectionRequest(string? BaseUrl, string? ApiKey);

    /// <summary>
    /// The options-save request body. Case-insensitive minimal-API binding maps the UI's PascalCase JSON
    /// onto these. An empty/absent <see cref="ApiKey"/> preserves the stored key (write-only; CONN-06).
    /// </summary>
    internal sealed record OptionsSaveRequest(
        string? BaseUrl, string? ApiKey, string? SelectedVersion, int RootFolderId, int QualityProfileId);

    /// <summary>
    /// The confirm/reject request body: the Cove video id + Whisparr movie id of the needs-review suggestion the
    /// user is acting on. Validated against the freshly-computed diff before any write (a forged pair is refused).
    /// </summary>
    internal sealed record MatchDecisionRequest(int CoveId, int WhisparrMovieId);

    /// <summary>One reconciliation row for the UI table — a flat projection of a <see cref="MatchResult"/>.</summary>
    /// <remarks>
    /// <c>Status</c> is the bucket (<c>"matched"</c> / <c>"needsReview"</c> / <c>"unmatched"</c>); <c>MatchMethod</c>
    /// is the resolving leg (<c>"StashId"</c> / <c>"Path"</c> / <c>"Fuzzy"</c>) or null when unmatched; <c>CoveId</c>
    /// / <c>CoveTitle</c> are null when the movie matched nothing.
    /// </remarks>
    internal sealed record ReconRow(
        int WhisparrMovieId,
        string? SceneTitle,
        int? SceneYear,
        int? CoveId,
        string? CoveTitle,
        string? MatchMethod,
        string Status)
    {
        public static ReconRow From(MatchResult r, string status) => new(
            WhisparrMovieId: r.Movie.Id,
            SceneTitle: r.Movie.Title,
            SceneYear: r.Movie.Year,
            CoveId: r.MatchedVideo?.CoveId,
            CoveTitle: r.MatchedVideo?.Title,
            MatchMethod: r.Leg?.ToString(),
            Status: status);
    }

    /// <summary>The <c>/preview-sync</c> response: the flat rows + the bucket counts.</summary>
    internal sealed record ReconResponse(IReadOnlyList<ReconRow> Rows, ReconciliationCounts Counts);

    /// <summary>The <c>/reconciliation</c> status counts over the persisted match map (by user-decision status).</summary>
    internal sealed record PersistedCounts(int Confirmed, int NeedsReview, int Rejected, int Total);

    /// <summary>The <c>/import-log</c> counts over the audit journal (by ingest result).</summary>
    internal sealed record ImportLogCounts(int Imported, int Skipped, int Flagged, int Total);
}
