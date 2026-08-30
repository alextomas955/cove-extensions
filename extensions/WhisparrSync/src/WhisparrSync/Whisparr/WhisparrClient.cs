using System.Globalization;

namespace WhisparrSync.Whisparr;

/// <summary>The class of work a request does, which is what its retry behaviour is keyed on.</summary>
/// <remarks>
/// Only reads exist so far. A class that must never be re-issued is added as a member with no entry
/// in <see cref="WhisparrRetryPolicy"/>'s table, which is a data change rather than a structural one.
/// </remarks>
public enum WhisparrVerbClass
{
    /// <summary>A request that only reads. Re-issuing one creates nothing and grabs nothing.</summary>
    Read,
}

/// <summary>How many attempts a verb class is allowed.</summary>
/// <remarks>
/// Per verb class rather than uniform, because a uniform retry is what would silently re-issue a
/// request that acts. An unlisted class gets <see cref="NoRetry"/>, so the safe answer is the
/// default and a retrying class has to be written down.
/// </remarks>
public static class WhisparrRetryPolicy
{
    /// <summary>One attempt: the request is issued once and a failure is reported.</summary>
    public const int NoRetry = 1;

    private static readonly Dictionary<WhisparrVerbClass, int> AttemptsByVerbClass = new()
    {
        [WhisparrVerbClass.Read] = 2,
    };

    /// <summary>How many attempts <paramref name="verbClass"/> is allowed.</summary>
    public static int AttemptsFor(WhisparrVerbClass verbClass)
        => AttemptsByVerbClass.GetValueOrDefault(verbClass, NoRetry);
}

/// <summary>What one Whisparr request answered with.</summary>
/// <param name="StatusCode">The HTTP status.</param>
/// <param name="ContentType">
/// The <c>Content-Type</c> header as received, unparsed. A rejected key answers with none on both
/// generations, so the empty case is a real observation rather than a missing one.
/// </param>
/// <param name="Body">The response body as text; empty when there was none.</param>
public sealed record WhisparrResponse(int StatusCode, string? ContentType, string Body);

/// <summary>
/// The one seam through which this extension talks to a Whisparr instance.
/// </summary>
/// <remarks>
/// Deliberately narrow: there is no method taking a caller-supplied path and none taking an HTTP
/// verb, so no call site can express a request that makes Whisparr search for or download anything.
/// Widening it is the decision that would have to be taken openly.
/// </remarks>
public interface IWhisparrClient
{
    /// <summary>Reads the status document from the instance at <paramref name="baseAddress"/>.</summary>
    /// <remarks>
    /// Returns whatever the instance answered, including a non-success status: classifying the answer
    /// belongs to the caller. Throws only when no answer arrived at all.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// <paramref name="baseAddress"/> is relative, or its scheme is neither http nor https.
    /// </exception>
    /// <exception cref="HttpRequestException">The request produced no response.</exception>
    /// <exception cref="TaskCanceledException">The request outlived the client's timeout.</exception>
    Task<WhisparrResponse> ReadStatusAsync(Uri baseAddress, string apiKey, CancellationToken ct);
}

/// <inheritdoc cref="IWhisparrClient"/>
internal sealed class WhisparrClient(HttpClient http) : IWhisparrClient
{
    /// <summary>The header both generations authenticate an API request with.</summary>
    internal const string ApiKeyHeader = "X-Api-Key";

    // Relative, so it composes onto a base address carrying a URL base (a reverse-proxy subpath).
    private const string StatusPath = "api/v3/system/status";

    /// <summary>How long one attempt may take before it is reported as unreachable.</summary>
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How many redirects the client follows. A login redirect is a real deployment, and following an
    /// unbounded chain of them is not.
    /// </summary>
    internal const int MaxRedirects = 3;

    public async Task<WhisparrResponse> ReadStatusAsync(
        Uri baseAddress,
        string apiKey,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        if (!IsAddressable(baseAddress))
        {
            throw new ArgumentException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"A Whisparr address must be an absolute http or https URL; the scheme given was '{baseAddress.Scheme}'."),
                nameof(baseAddress));
        }

        // Only the read class reaches here, so re-issuing re-reads and can create nothing. The last
        // attempt is the plain send, so its failure propagates rather than being counted again.
        var attempts = WhisparrRetryPolicy.AttemptsFor(WhisparrVerbClass.Read);
        for (var attempt = 1; attempt < attempts; attempt++)
        {
            if (await TrySendAsync(baseAddress, apiKey, ct).ConfigureAwait(false) is { } answered)
            {
                return answered;
            }
        }

        return await SendAsync(baseAddress, apiKey, ct).ConfigureAwait(false);
    }

    /// <summary>Whether <paramref name="address"/> is one a socket may be opened to.</summary>
    /// <remarks>
    /// Checked before any request so a <c>file:</c> or <c>ftp:</c> address is refused rather than
    /// handed to a handler that would act on it.
    /// </remarks>
    internal static bool IsAddressable(Uri address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return address.IsAbsoluteUri
            && (address.Scheme == Uri.UriSchemeHttp || address.Scheme == Uri.UriSchemeHttps);
    }

    /// <summary>Applies the settings every request through this client is made under.</summary>
    internal static void Configure(HttpClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        client.Timeout = RequestTimeout;
    }

    /// <summary>The handler every request through this client is made through.</summary>
    /// <remarks>
    /// Certificate validation stays at its default. A self-signed Whisparr therefore reports as
    /// unreachable, which is an answer the user can act on; a bypass would make every instance's
    /// identity unverifiable to buy it.
    /// </remarks>
    internal static HttpMessageHandler CreateHandler()
        => new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = MaxRedirects,
        };

    // Null when the connection never established, which is the one failure a read may be re-issued
    // after. A status, however unwelcome, is an answer and is returned.
    private async Task<WhisparrResponse?> TrySendAsync(Uri baseAddress, string apiKey, CancellationToken ct)
    {
        try
        {
            return await SendAsync(baseAddress, apiKey, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    private async Task<WhisparrResponse> SendAsync(Uri baseAddress, string apiKey, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, StatusUri(baseAddress));
        request.Headers.Add(ApiKeyHeader, apiKey);

        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return new WhisparrResponse(
            (int)response.StatusCode,
            response.Content.Headers.ContentType?.ToString(),
            body);
    }

    // Relative-Uri composition drops the last segment of a base that does not end in a separator,
    // which would turn a URL base of /whisparr into a request at the site root instead.
    private static Uri StatusUri(Uri baseAddress)
    {
        var builder = new UriBuilder(baseAddress);
        if (!builder.Path.EndsWith('/'))
        {
            builder.Path += '/';
        }

        return new Uri(builder.Uri, StatusPath);
    }
}
