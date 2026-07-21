using Cove.Core.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WhisparrSync.Adapters;
using WhisparrSync.Client;
using WhisparrSync.Library;
using WhisparrSync.Matching;
using WhisparrSync.Options;
using WhisparrSync.Scene;
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

        var options = await new OptionsStore(Store).LoadAsync(ct);
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

        var options = await new OptionsStore(Store).LoadAsync(ct);
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

        var store = new OptionsStore(Store);
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

    /// <summary>Maps a non-Ok transport state to the UI's error discriminator (never leaks the key/reason).</summary>
    private static string FailureDiscriminator(WhisparrResultState state) => state switch
    {
        WhisparrResultState.BadKey => "badKey",
        WhisparrResultState.NotWhisparr => "notWhisparr",
        WhisparrResultState.Rejected => "rejected",
        _ => "unreachable",
    };

    /// <summary>
    /// Returns the ready-to-use webhook URL with the embedded secret. The secret is minted via
    /// <c>RandomNumberGenerator</c> and persisted once so the URL is stable across calls. 403-first on
    /// <c>extensions.configure</c>: minting/persisting the webhook secret is part of the configure
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
    /// Best-effort auto-register of the Cove webhook in Whisparr. Mints/persists the secret,
    /// builds the URL, and posts the v3 Notification via the adapter. A non-2xx (or a refused version)
    /// returns <c>registered:false</c> — the UI falls back to copy-paste, and the connect flow never fails.
    /// 403-first on <c>extensions.configure</c>. The secret is never logged.
    /// </summary>
    /// <remarks>
    /// The webhook URL defaults to the request's own scheme+host (<paramref name="coveBaseUrl"/>), but a
    /// containerized Whisparr cannot reach a <c>localhost</c> the admin happens to browse Cove at. So when the UI
    /// forwards the (possibly hand-edited) URL in <paramref name="overrideUrl"/>, ONLY its origin (scheme+host+port)
    /// is honored — the token is always re-minted from the stored secret via <see cref="WebhookUrlBuilder.BuildUrl"/>,
    /// so an edited host can never register a URL carrying a wrong or absent secret.
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
    /// Computes the read-only reconciliation diff: reads Cove via <see cref="CoveLibraryPort"/>,
    /// fetches the Whisparr movie set via the adapter, loads the persisted match map, and composes them with
    /// <see cref="ReconciliationService"/>. Returns the diff as a flat list of rows (matched / needs-review /
    /// unmatched) plus counts. Configure-gated: it reaches the stored credentials to call Whisparr, so
    /// a read-only principal must not reach it. ZERO mutation of Cove or Whisparr.
    /// </summary>
    internal async Task<IResult> PreviewSyncAsync(
        WhisparrClient client, ICurrentPrincipalAccessor principal, CancellationToken ct)
    {
        if (Forbidden(principal, Permissions.ExtensionsConfigure) is { } denied)
        {
            return denied;
        }

        var (error, diff, excluded) = await ComputeReconciliationAsync(client, ct);
        return error ?? Results.Json(ToReconResponse(diff!, excluded!), ReconciliationResponseJsonOptions);
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
    /// Promotes a needs-review suggestion to a confirmed link: validates the submitted pair against
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

        var (error, diff, _) = await ComputeReconciliationAsync(client, ct);
        return error ?? await ApplyMatchDecisionAsync(req, diff!, MatchStatus.Confirmed, ct);
    }

    /// <summary>
    /// Marks a needs-review suggestion rejected so a re-run suppresses it. Same validate-then-write
    /// shape as <see cref="MatchConfirmAsync"/>; the decision is <see cref="MatchStatus.Rejected"/>. The only
    /// write is to the extension's own match map (never Cove or Whisparr). NOTE: there is no un-reject /
    /// un-confirm path — once written, a decision moves the pair out of needs-review (rejected → suppressed to
    /// unmatched, confirmed → matched), so it cannot be reversed from the UI until a clear/reset endpoint
    /// (IN-05) is added.
    /// </summary>
    internal async Task<IResult> MatchRejectAsync(
        MatchDecisionRequest req, WhisparrClient client, ICurrentPrincipalAccessor principal, CancellationToken ct)
    {
        if (Forbidden(principal, Permissions.ExtensionsConfigure) is { } denied)
        {
            return denied;
        }

        var (error, diff, _) = await ComputeReconciliationAsync(client, ct);
        return error ?? await ApplyMatchDecisionAsync(req, diff!, MatchStatus.Rejected, ct);
    }

    /// <summary>
    /// Validates <paramref name="req"/> against <paramref name="diff"/> and, only on a match, upserts the
    /// decision into the match store. Extracted from the routed handlers so the validation-before-write rule is
    /// unit-testable host-free (no scope / no Whisparr).
    /// </summary>
    /// <remarks>
    /// The submitted <c>(coveId, whisparrMovieId)</c> pair MUST be a live
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
            MatchedBy: row.Leg ?? MatchedBy.StashId,
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
    /// Loads Cove + Whisparr behind the read seams and composes the diff, plus the exclusion set
    /// that enriches each row's <see cref="ReconRow.WhisparrState"/>. Returns a classified
    /// error <see cref="IResult"/> (400 unsupported version / 502 transport) OR the diff+excluded set — never
    /// both. Uses the STORED creds only (<see cref="ResolveCredsAsync"/> with an empty request), so the stored
    /// key is never paired with a caller-supplied host. Opens a fresh scope per run for the scoped
    /// <see cref="DbContext"/> and never mutates Cove or Whisparr. Delegates the Whisparr reads + composition to
    /// <see cref="ComputeReconciliationCoreAsync"/> so that half is unit-testable with a fake adapter + port.
    /// </summary>
    private async Task<(IResult? Error, ReconciliationDiff? Diff, IReadOnlySet<string>? Excluded)> ComputeReconciliationAsync(
        WhisparrClient client, CancellationToken ct)
    {
        var (options, baseUrl, apiKey) = await ResolveCredsAsync(new TestConnectionRequest(null, null), ct);
        if (AdapterSelector.SelectForVersion(options.SelectedVersion, client) is not { } adapter)
        {
            return (Results.Json(new { code = "VERSION_UNSUPPORTED" }, statusCode: 400), null, null);
        }

        // A fresh CreateAsyncScope() per run so the scoped
        // DbContext has the correct lifetime (never a long-lived captured context), and the AsNoTracking port
        // is the only DbContext-touching surface — a plain reconcile writes nothing.
        await using var scope = ScopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DbContext>();
        var port = new CoveLibraryPort(db, options.StashDbEndpoint, options.TpdbEndpoint);
        return await ComputeReconciliationCoreAsync(adapter, port, baseUrl, apiKey, ct);
    }

    /// <summary>
    /// The Whisparr-read + compose half of reconciliation, extracted so it is unit-testable host-free with a
    /// fake adapter (a fake-HTTP <see cref="WhisparrClient"/>) + a fake <see cref="ICoveLibraryPort"/> — no
    /// scope, no live DB. Fetches the movie set (a non-Ok movie read is a 502; movies are the reconciliation
    /// spine), composes the diff via <see cref="ReconciliationService"/>, then reads the exclusion set for the
    /// row-status enrichment. The exclusion read degrades to an EMPTY set on any non-Ok result — a v2 instance
    /// defers <c>ListExclusionsAsync</c> (VersionMismatch, no wire call), so v2 simply yields no "excluded"
    /// rows rather than a 502 (graceful v2 deferral). Mutates nothing.
    /// </summary>
    internal async Task<(IResult? Error, ReconciliationDiff? Diff, IReadOnlySet<string>? Excluded)> ComputeReconciliationCoreAsync(
        IWhisparrAdapter adapter, ICoveLibraryPort port, string baseUrl, string apiKey, CancellationToken ct)
    {
        var movies = await adapter.ListMoviesAsync(baseUrl, apiKey, ct);
        if (!movies.IsOk)
        {
            return (Results.Json(new { result = FailureDiscriminator(movies.State) }, statusCode: 502), null, null);
        }

        var coveVideos = await port.LoadAllVideosAsync(ct);
        var persisted = await new MatchStateStore(Store).LoadAllAsync(ct);
        var diff = ReconciliationService.Reconcile(coveVideos, movies.Value!, persisted);

        var exclusions = await adapter.ListExclusionsAsync(baseUrl, apiKey, ct);
        var excluded = SceneStatusProjector.BuildExcludedSet(exclusions.IsOk ? exclusions.Value! : []);
        return (null, diff, excluded);
    }

    /// <summary>
    /// Flattens the bucketed diff into a single ordered row list (matched → needs-review → unmatched) + counts,
    /// enriching each row with its Whisparr status derived from the movie + the <paramref name="excludedSet"/>.
    /// </summary>
    internal static ReconResponse ToReconResponse(ReconciliationDiff diff, IReadOnlySet<string> excludedSet)
    {
        var rows = new List<ReconRow>(diff.Counts.Total);
        rows.AddRange(diff.Matched.Select(r => ReconRow.From(r, "matched", excludedSet)));
        rows.AddRange(diff.NeedsReview.Select(r => ReconRow.From(r, "needsReview", excludedSet)));
        rows.AddRange(diff.Unmatched.Select(r => ReconRow.From(r, "unmatched", excludedSet)));
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
