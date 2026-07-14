using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace WhisparrSync.Client;

/// <summary>
/// Transport-only typed client for a Whisparr v3 instance. Attaches the <c>X-Api-Key</c> header, applies
/// a per-call timeout, and — before deserializing — guards the status code and <c>Content-Type</c>, then
/// classifies the outcome into a typed <see cref="WhisparrResult{T}"/> instead of throwing. Idempotent
/// GETs retry a bounded number of times on a transient transport fault; the non-idempotent webhook POST is
/// single-shot. Holds no Whisparr-shape knowledge beyond the endpoint paths + DTOs; all version/domain
/// decisions live above it (the adapter).
/// </summary>
internal sealed class WhisparrClient(HttpClient http)
{
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(15);

    // Idempotent GETs may re-issue once on a transient transport fault; a non-idempotent POST is never
    // blind-retried (a retried notification POST could double-register a webhook).
    private const int GetMaxAttempts = 2;
    private const int PostMaxAttempts = 1;

    /// <summary>
    /// Reads <c>GET {baseUrl}/api/v3/system/status</c>. Never throws for a transport/HTTP fault: 401/403 →
    /// <see cref="WhisparrResultState.BadKey"/>, a non-JSON body (reverse-proxy HTML/502) →
    /// <see cref="WhisparrResultState.NotWhisparr"/>, a timeout/refused connection →
    /// <see cref="WhisparrResultState.Unreachable"/>, otherwise <see cref="WhisparrResultState.Ok"/>.
    /// </summary>
    internal Task<WhisparrResult<SystemStatus>> GetStatusAsync(string baseUrl, string apiKey, CancellationToken ct)
        => SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, $"{baseUrl.TrimEnd('/')}/api/v3/system/status"),
            apiKey, GetMaxAttempts,
            (resp, token) => DeserializeAsync(resp, WhisparrJsonContext.Default.SystemStatus, token),
            ct);

    /// <summary>Reads <c>GET {baseUrl}/api/v3/rootfolder</c> (idempotent; bounded retry). Transport-only.</summary>
    internal Task<WhisparrResult<RootFolder[]>> ListRootFoldersAsync(string baseUrl, string apiKey, CancellationToken ct)
        => SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, $"{baseUrl.TrimEnd('/')}/api/v3/rootfolder"),
            apiKey, GetMaxAttempts,
            (resp, token) => DeserializeAsync(resp, WhisparrJsonContext.Default.RootFolderArray, token),
            ct);

    /// <summary>Reads <c>GET {baseUrl}/api/v3/qualityprofile</c> (idempotent; bounded retry). Transport-only.</summary>
    internal Task<WhisparrResult<QualityProfile[]>> ListQualityProfilesAsync(string baseUrl, string apiKey, CancellationToken ct)
        => SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, $"{baseUrl.TrimEnd('/')}/api/v3/qualityprofile"),
            apiKey, GetMaxAttempts,
            (resp, token) => DeserializeAsync(resp, WhisparrJsonContext.Default.QualityProfileArray, token),
            ct);

    /// <summary>
    /// Reads <c>GET {baseUrl}/api/v3/movie</c> — the full movie set, unpaged (issue #218), for MATCH-01
    /// reconciliation (idempotent; bounded retry). Transport-only: the classify-not-throw guards are
    /// inherited from <see cref="SendAsync"/>.
    /// </summary>
    internal Task<WhisparrResult<WhisparrMovie[]>> ListMoviesAsync(string baseUrl, string apiKey, CancellationToken ct)
        => SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, $"{baseUrl.TrimEnd('/')}/api/v3/movie"),
            apiKey, GetMaxAttempts,
            (resp, token) => DeserializeAsync(resp, WhisparrJsonContext.Default.WhisparrMovieArray, token),
            ct);

    /// <summary>
    /// Reads one page of <c>GET {baseUrl}/api/v3/history</c> newest-first — the IMPT-02 reconcile backstop's
    /// data source (idempotent; bounded retry). The caller pages until it reaches the stored checkpoint, so a
    /// full history is never pulled at once. Transport-only: the classify-not-throw guards are inherited from
    /// <see cref="SendAsync"/>.
    /// </summary>
    internal Task<WhisparrResult<WhisparrHistoryPage>> ListHistoryAsync(
        string baseUrl, string apiKey, int page, int pageSize, CancellationToken ct)
        => SendAsync(
            () => new HttpRequestMessage(
                HttpMethod.Get,
                $"{baseUrl.TrimEnd('/')}/api/v3/history?page={page}&pageSize={pageSize}&sortKey=date&sortDirection=descending"),
            apiKey, GetMaxAttempts,
            (resp, token) => DeserializeAsync(resp, WhisparrJsonContext.Default.WhisparrHistoryPage, token),
            ct);

    /// <summary>
    /// Posts a pre-serialized notification payload to <c>POST {baseUrl}/api/v3/notification</c> to register
    /// the Cove webhook connection. The caller (the adapter) owns the payload shape; this method is
    /// transport-only and single-shot — a non-idempotent POST is never blind-retried. The full payload is
    /// wired in plan 01-03; on any 2xx JSON response this reports <see cref="WhisparrResultState.Ok"/>.
    /// </summary>
    internal Task<WhisparrResult<bool>> RegisterWebhookAsync(string baseUrl, string apiKey, string notificationJson, CancellationToken ct)
        => SendAsync<bool>(
            () => new HttpRequestMessage(HttpMethod.Post, $"{baseUrl.TrimEnd('/')}/api/v3/notification")
            {
                Content = new StringContent(notificationJson, Encoding.UTF8, "application/json"),
            },
            apiKey, PostMaxAttempts,
            (_, _) => Task.FromResult(WhisparrResult<bool>.Ok(true)),
            ct);

    /// <summary>
    /// The shared send loop: per-call timeout linked to the caller's token, the <c>X-Api-Key</c> header,
    /// the status (401/403 → BadKey) and <c>Content-Type</c> (non-JSON → NotWhisparr) guards BEFORE any
    /// deserialize, and a bounded retry on a transient transport fault (timeout / refused). A terminal
    /// classification (BadKey / NotWhisparr / a non-2xx JSON response) returns immediately without retry;
    /// only a thrown transport fault re-issues the request, and only while attempts remain.
    /// </summary>
    private async Task<WhisparrResult<T>> SendAsync<T>(
        Func<HttpRequestMessage> requestFactory,
        string apiKey,
        int maxAttempts,
        Func<HttpResponseMessage, CancellationToken, Task<WhisparrResult<T>>> onSuccess,
        CancellationToken ct)
    {
        var last = WhisparrResult<T>.Unreachable("no response");
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(CallTimeout);

            try
            {
                using var req = requestFactory();

                // Guard the target URI at the transport edge (WR-02): a malformed absolute URL throws
                // UriFormatException from the request factory, and an empty/relative base URL yields a
                // relative RequestUri that http.SendAsync rejects with InvalidOperationException (no
                // BaseAddress). Reject both — plus any non-http(s) scheme — as a classified Unreachable
                // instead of letting the exception escape the classify-not-throw boundary as a 500. This
                // is deterministic, so it returns immediately rather than consuming a retry.
                if (req.RequestUri is not { IsAbsoluteUri: true } uri ||
                    (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                {
                    return WhisparrResult<T>.Unreachable("invalid url");
                }

                req.Headers.TryAddWithoutValidation("X-Api-Key", apiKey);

                using var resp = await http.SendAsync(req, linked.Token);

                if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    return WhisparrResult<T>.BadKey();
                }

                // Guard Content-Type BEFORE deserializing so a reverse-proxy HTML landing page / 502 becomes
                // a clean "not the Whisparr API" result rather than a confusing JSON-parse crash.
                var contentType = resp.Content.Headers.ContentType?.MediaType;
                if (contentType is not "application/json")
                {
                    return WhisparrResult<T>.NotWhisparr();
                }

                if (!resp.IsSuccessStatusCode)
                {
                    return WhisparrResult<T>.Unreachable($"HTTP {(int)resp.StatusCode}");
                }

                return await onSuccess(resp, linked.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // The linked token fired (per-call timeout) rather than the caller cancelling.
                last = WhisparrResult<T>.Unreachable("timeout");
            }
            catch (HttpRequestException ex)
            {
                last = WhisparrResult<T>.Unreachable(ex.Message);
            }
            catch (Exception ex) when (ex is UriFormatException or InvalidOperationException)
            {
                // A malformed absolute URL (thrown while building the request) or a relative request URI
                // with no BaseAddress — deterministic, so classify and return without retrying.
                return WhisparrResult<T>.Unreachable("invalid url");
            }
        }

        return last;
    }

    private static async Task<WhisparrResult<T>> DeserializeAsync<T>(
        HttpResponseMessage resp, JsonTypeInfo<T> typeInfo, CancellationToken ct)
    {
        try
        {
            var value = await resp.Content.ReadFromJsonAsync(typeInfo, ct);
            return value is null ? WhisparrResult<T>.NotWhisparr() : WhisparrResult<T>.Ok(value);
        }
        catch (JsonException)
        {
            // The response claimed application/json but the body was not parseable — classify rather than
            // let the parse exception escape the transport boundary.
            return WhisparrResult<T>.NotWhisparr();
        }
    }
}
