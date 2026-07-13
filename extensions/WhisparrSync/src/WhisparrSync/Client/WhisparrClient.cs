using System.Net;
using System.Net.Http.Json;

namespace WhisparrSync.Client;

/// <summary>
/// Transport-only typed client for a Whisparr v3 instance. Attaches the <c>X-Api-Key</c> header, applies
/// a per-call timeout, and — before deserializing — guards the status code and <c>Content-Type</c>, then
/// classifies the outcome into a typed <see cref="WhisparrResult{T}"/> instead of throwing. Holds zero
/// Whisparr-shape knowledge beyond the status DTO; all version/domain decisions live above it (adapter).
/// </summary>
internal sealed class WhisparrClient(HttpClient http)
{
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Reads <c>GET {baseUrl}/api/v3/system/status</c>. Never throws for a transport/HTTP fault: a
    /// 401/403 → <see cref="WhisparrResultState.BadKey"/>, a non-JSON body (reverse-proxy HTML/502) →
    /// <see cref="WhisparrResultState.NotWhisparr"/>, a timeout/refused connection →
    /// <see cref="WhisparrResultState.Unreachable"/>, otherwise <see cref="WhisparrResultState.Ok"/>.
    /// </summary>
    internal async Task<WhisparrResult<SystemStatus>> GetStatusAsync(string baseUrl, string apiKey, CancellationToken ct)
    {
        // Per-call timeout, linked to the caller's token so either can cancel the outbound request.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(CallTimeout);

        using var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl.TrimEnd('/')}/api/v3/system/status");
        req.Headers.TryAddWithoutValidation("X-Api-Key", apiKey);

        try
        {
            using var resp = await http.SendAsync(req, linked.Token);

            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return WhisparrResult<SystemStatus>.BadKey();
            }

            // Guard Content-Type BEFORE deserializing so a reverse-proxy HTML landing page / 502 becomes a
            // clean "not the Whisparr API" result rather than a confusing JSON-parse crash.
            var contentType = resp.Content.Headers.ContentType?.MediaType;
            if (contentType is not "application/json")
            {
                return WhisparrResult<SystemStatus>.NotWhisparr();
            }

            if (!resp.IsSuccessStatusCode)
            {
                return WhisparrResult<SystemStatus>.Unreachable($"HTTP {(int)resp.StatusCode}");
            }

            var status = await resp.Content.ReadFromJsonAsync(WhisparrJsonContext.Default.SystemStatus, linked.Token);
            return status is null
                ? WhisparrResult<SystemStatus>.NotWhisparr()
                : WhisparrResult<SystemStatus>.Ok(status);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The linked token fired (per-call timeout) rather than the caller cancelling.
            return WhisparrResult<SystemStatus>.Unreachable("timeout");
        }
        catch (HttpRequestException ex)
        {
            return WhisparrResult<SystemStatus>.Unreachable(ex.Message);
        }
    }
}
