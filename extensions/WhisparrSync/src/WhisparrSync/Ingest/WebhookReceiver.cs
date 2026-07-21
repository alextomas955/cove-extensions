using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cove.Plugins;
using Microsoft.AspNetCore.Http;
using WhisparrSync.Matching;
using WhisparrSync.Options;
using WhisparrSync.State;

namespace WhisparrSync.Ingest;

/// <summary>
/// The anonymous, token-gated <c>/webhook</c> handler. Unlike every other extension route it
/// carries NO Cove principal (Whisparr has none), so it does NOT use the shared <c>Forbidden(principal,…)</c>
/// gate — the shared-secret token IS the auth. The token is validated FIRST, in constant time, BEFORE the
/// body is parsed; only then is the event routed. The token and the raw body are never logged.
///
/// The <c>X-Cove-Token</c> HEADER is the PREFERRED token channel (auto-register configures it, and it is not
/// captured by request logging). The <c>?token=</c> query string is a documented fallback for hand-pasted
/// webhooks ONLY: a secret in a URL query is routinely recorded by proxy/access logs (WR-03), so when it is
/// used <paramref name="onQueryTokenFallback"/> fires (the host logs a one-time warning). The header is
/// always checked first, so a request that presents both authenticates on the header.
/// </summary>
internal sealed class WebhookReceiver(
    IExtensionStore store, IngestCoordinator coordinator, Action? onQueryTokenFallback = null)
{
    private const string TokenHeader = "X-Cove-Token";

    // Terse { code } responses on the extension's own web-convention options — never the host default.
    private static readonly JsonSerializerOptions ResponseJsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Validates the token, then routes on <c>eventType</c>: a valid <c>Test</c> ping is a 200 no-op, a
    /// valid <c>Download</c> ingests the imported file, any other event is a 200 ignore. A missing / empty /
    /// mismatched token — or an unconfigured secret — is a fail-closed 401.
    /// </summary>
    public async Task<IResult> HandleAsync(HttpContext http, CancellationToken ct)
    {
        // Prefer the X-Cove-Token header; the ?token= query is a fallback for hand-pasted webhooks only (WR-03).
        var presented = http.Request.Headers[TokenHeader].ToString();
        var fromQueryFallback = false;
        if (string.IsNullOrEmpty(presented))
        {
            presented = http.Request.Query["token"].ToString();
            fromQueryFallback = !string.IsNullOrEmpty(presented);
        }

        var expected = (await new OptionsStore(store).LoadAsync(ct)).WebhookSecret;
        if (expected.Length == 0 || !FixedTimeEquals(presented, expected))
        {
            return Results.Json(new { code = "UNAUTHORIZED" }, ResponseJsonOptions, statusCode: 401);
        }

        // Authenticated via the query string: signal the host so it warns (once) about the log-exposure risk.
        if (fromQueryFallback)
        {
            onQueryTokenFallback?.Invoke();
        }

        var payload = await ParseAsync(http.Request.Body, ct);
        if (payload is null)
        {
            return Results.Json(new { code = "OK" }, ResponseJsonOptions, statusCode: 200);
        }

        if (string.Equals(payload.EventType, "Download", StringComparison.OrdinalIgnoreCase))
        {
            await HandleDownloadAsync(payload, ct);
        }

        // Test → 200 no-op; unknown eventType → 200 ignore; Download → handled above.
        return Results.Json(new { code = "OK" }, ResponseJsonOptions, statusCode: 200);
    }

    private async Task HandleDownloadAsync(WebhookPayload payload, CancellationToken ct)
    {
        // Version-blind: v3 posts movieFile, v2 posts episodeFile — prefer movieFile, then fall back to episodeFile.
        var path = payload.MovieFile?.Path ?? payload.EpisodeFile?.Path;
        if (string.IsNullOrWhiteSpace(path))
        {
            return; // a Download with no imported path is malformed — nothing to ingest, nothing to audit
        }

        var ledger = new EventLedger(store);
        var log = new ImportLog(store);
        var key = EventLedger.ImportKey(payload.DownloadId, path);

        // Atomically CLAIM-before-ingest on the cross-channel key: the claim's check-and-insert runs
        // in one gated critical section, so a redelivery (at-least-once webhook + poll overlap) has exactly one
        // winner. A loser lost the race — the import is already claimed by the other channel — so it is a
        // duplicate no-op, audited as Skipped, never a second entity. Claiming BEFORE ingest (not recording
        // after) is what closes the old TOCTOU where two concurrent flows both saw "not seen" and double-imported.
        if (!await ledger.TryClaimAsync(key, ct))
        {
            await log.AppendAsync(Entry(payload, path, kind: null, coveId: null, "Skipped", "duplicate delivery", key), ct);
            return;
        }

        // The upgrade handle is version-blind too: v3 keys on movie.id, v2 on the first episode id (its WhisparrMovieId).
        var existingId = payload.IsUpgrade
            ? await ResolveExistingCoveIdAsync(payload.Movie?.Id ?? payload.Episodes?.FirstOrDefault()?.Id, ct)
            : null;
        var identity = ResolveIdentity(payload, await new OptionsStore(store).LoadAsync(ct));

        IngestOutcome outcome;
        try
        {
            outcome = await coordinator.IngestAsync(path, existingId, identity, ct);
        }
        catch
        {
            // An UNEXPECTED ingest fault (not the classified Imported/Flagged outcome) must not permanently
            // swallow the import: release the claim so a retry / the poll backstop re-processes it.
            await ledger.ReleaseAsync(key, ct);
            throw;
        }

        // The claim already recorded the key. The event is audited once (imported OR flagged), so a Whisparr
        // retry or the reconcile-poll overlap is a Skipped no-op rather than a re-flag; the reconcile backstop keys
        // the same ImportKey, so it will not re-process it.
        var result = outcome.Result == IngestResult.Imported ? "Imported" : "Flagged";
        await log.AppendAsync(
            Entry(payload, path, outcome.Kind, outcome.CoveEntityId, result, outcome.Reason, key), ct);
    }

    private static ImportLogEntry Entry(
        WebhookPayload payload, string path, string? kind, int? coveId, string result, string? reason, string ledgerKey)
        => new(DateTime.UtcNow.Ticks, "webhook", payload.EventType, path, kind, coveId, result, reason, ledgerKey);

    // The scene's source id, version-blind: v3 posts movie.foreignId (a StashDB id), v2 posts series.tvdbId
    // (the site's ThePornDB id); the endpoint follows the connected version. Null when neither is present, so
    // ingest still imports the file but skips enrichment.
    private static SceneIdentity? ResolveIdentity(WebhookPayload payload, WhisparrOptions options)
    {
        var remoteId = payload.Movie?.ForeignId
            ?? payload.Series?.TvdbId?.ToString(CultureInfo.InvariantCulture);
        return string.IsNullOrWhiteSpace(remoteId) ? null : new SceneIdentity(remoteId, options.IdentityEndpoint);
    }

    // An upgrade re-imports an already-matched movie: pass its Cove id so ImportDownloaded* upgrades in place
    // rather than creating a duplicate. The persisted match map is the only durable WhisparrMovieId → Cove id
    // handle; an unmatched upgrade falls back to a create (null), which the ledger still dedups on replay.
    private async Task<int?> ResolveExistingCoveIdAsync(int? whisparrMovieId, CancellationToken ct)
    {
        if (whisparrMovieId is not { } movieId)
        {
            return null;
        }

        foreach (var match in await new MatchStateStore(store).LoadAllAsync(ct))
        {
            if (match.WhisparrMovieId == movieId)
            {
                return match.CoveId;
            }
        }

        return null;
    }

    private static async Task<WebhookPayload?> ParseAsync(Stream body, CancellationToken ct)
    {
        try
        {
            return await JsonSerializer.DeserializeAsync(body, IngestJsonContext.Default.WebhookPayload, ct);
        }
        catch (JsonException)
        {
            return null; // a malformed body is ignored (200), never a 500 — the token already authenticated it
        }
    }

    // Constant-time compare over UTF-8 bytes (V6): never ==/Equals/SequenceEqual, which leak length/content
    // via timing. Length-mismatched inputs are rejected before FixedTimeEquals (which requires equal lengths).
    private static bool FixedTimeEquals(string presented, string expected)
    {
        var a = Encoding.UTF8.GetBytes(presented);
        var b = Encoding.UTF8.GetBytes(expected);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }
}
