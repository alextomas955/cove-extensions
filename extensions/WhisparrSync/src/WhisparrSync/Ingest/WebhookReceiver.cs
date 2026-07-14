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
/// The anonymous, token-gated <c>/webhook</c> handler (SEC-01). Unlike every other extension route it
/// carries NO Cove principal (Whisparr has none), so it does NOT use the shared <c>Forbidden(principal,…)</c>
/// gate — the shared-secret token IS the auth. The token is validated FIRST, in constant time, BEFORE the
/// body is parsed; only then is the event routed. The token and the raw body are never logged.
/// </summary>
internal sealed class WebhookReceiver(IExtensionStore store, IngestCoordinator coordinator)
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
        var presented = http.Request.Headers[TokenHeader].ToString();
        if (string.IsNullOrEmpty(presented))
        {
            presented = http.Request.Query["token"].ToString();
        }

        var expected = (await new OptionsStore(store).LoadAsync(ct)).WebhookSecret;
        if (expected.Length == 0 || !FixedTimeEquals(presented, expected))
        {
            return Results.Json(new { code = "UNAUTHORIZED" }, ResponseJsonOptions, statusCode: 401);
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
        var path = payload.MovieFile?.Path;
        if (string.IsNullOrWhiteSpace(path) || !FileKindResolver.TryResolve(path, out var kind))
        {
            return;
        }

        // Check-before-ingest on the cross-channel key so a redelivery (at-least-once webhook + poll overlap)
        // is a duplicate no-op, not a second entity (IMPT-03).
        var ledger = new EventLedger(store);
        var key = EventLedger.ImportKey(payload.DownloadId, path);
        if (await ledger.SeenAsync(key, ct))
        {
            return;
        }

        var existingId = payload.IsUpgrade ? await ResolveExistingCoveIdAsync(payload.Movie?.Id, ct) : null;
        await coordinator.IngestAsync(kind, path, existingId, ct);
        await ledger.RecordAsync(key, ct);
    }

    // An upgrade re-imports an already-matched movie: pass its Cove id so ImportDownloaded* upgrades in place
    // rather than creating a duplicate. The Phase-2 match map is the only durable WhisparrMovieId → Cove id
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
