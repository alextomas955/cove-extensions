using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using WhisparrSync.Client;

namespace WhisparrSync.Discovery;

/// <summary>
/// The shared transport-classify spine for the direct-metadata clients (StashDB + ThePornDB). Both issue read-only
/// requests to an external box and must classify every outcome into a <see cref="WhisparrResult{T}"/> rather than
/// throwing — mirroring <see cref="WhisparrClient"/>'s spine: a linked-CTS per-call timeout, an absolute-http(s)
/// URI guard, <c>401/403 → BadKey</c>, a bounded 429 back-off, a non-JSON <c>Content-Type → NotWhisparr</c>, a
/// non-2xx <c>→ Unreachable</c>, and the three catch arms (timeout / transport / parse).
/// </summary>
/// <remarks>
/// The auth header and the JSON source-gen context are the only per-source variation, so each client supplies its
/// own request factory, its <c>(header, value)</c> auth pair (StashDB's <c>ApiKey</c> vs ThePornDB's
/// <c>Authorization: Bearer</c>), and its <see cref="JsonTypeInfo{T}"/>. The factory is re-invoked per attempt so a
/// retry sends a fresh request (an <see cref="HttpContent"/> cannot be re-sent).
/// </remarks>
internal static class MetadataHttpSend
{
    internal static async Task<WhisparrResult<T>> SendAsync<T>(
        HttpClient http,
        Func<HttpRequestMessage> requestFactory,
        (string Name, string Value) auth,
        TimeSpan callTimeout,
        int max429Retries,
        JsonTypeInfo<T> typeInfo,
        CancellationToken ct)
        where T : class
    {
        for (var attempt = 0; ; attempt++)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(callTimeout);

            try
            {
                using var req = requestFactory();

                // An empty/relative or non-http(s) endpoint is a classified Unreachable, never an exception escaping
                // the classify-not-throw boundary.
                if (req.RequestUri is not { IsAbsoluteUri: true } uri ||
                    (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                {
                    return WhisparrResult<T>.Unreachable("invalid url");
                }

                req.Headers.TryAddWithoutValidation(auth.Name, auth.Value);

                using var resp = await http.SendAsync(req, linked.Token);

                if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    return WhisparrResult<T>.BadKey();
                }

                if ((int)resp.StatusCode == 429)
                {
                    if (attempt < max429Retries)
                    {
                        await BackOffAsync(resp, attempt, linked.Token);
                        continue;
                    }

                    return WhisparrResult<T>.Unreachable("rate limited");
                }

                // A reverse-proxy HTML page / 5xx classifies as NotWhisparr before it reaches the parser.
                var contentType = resp.Content.Headers.ContentType?.MediaType;
                if (contentType is null || !contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
                {
                    return WhisparrResult<T>.NotWhisparr();
                }

                if (!resp.IsSuccessStatusCode)
                {
                    return WhisparrResult<T>.Unreachable($"HTTP {(int)resp.StatusCode}");
                }

                var value = await resp.Content.ReadFromJsonAsync(typeInfo, linked.Token);
                return value is null ? WhisparrResult<T>.NotWhisparr() : WhisparrResult<T>.Ok(value);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return WhisparrResult<T>.Unreachable("timeout");
            }
            catch (HttpRequestException ex)
            {
                return WhisparrResult<T>.Unreachable(ex.Message);
            }
            catch (JsonException)
            {
                return WhisparrResult<T>.NotWhisparr();
            }
        }
    }

    // Honor a Retry-After (seconds) when the box sends one, else a conservative exponential back-off, capped at 8s.
    private static Task BackOffAsync(HttpResponseMessage resp, int attempt, CancellationToken ct)
    {
        var seconds = resp.Headers.RetryAfter?.Delta?.TotalSeconds ?? Math.Min(8, Math.Pow(2, attempt + 1));
        return Task.Delay(TimeSpan.FromSeconds(seconds), ct);
    }
}
