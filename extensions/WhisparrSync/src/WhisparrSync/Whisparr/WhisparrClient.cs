using System.Globalization;
using System.Net.Mime;
using System.Text;
using System.Text.Json.Nodes;

namespace WhisparrSync.Whisparr;

/// <summary>The class of work a request does, which is what its retry behaviour is keyed on.</summary>
/// <remarks>
/// A class that must never be re-issued is a member with no entry in
/// <see cref="WhisparrRetryPolicy"/>'s table, which is a data change rather than a structural one.
/// </remarks>
public enum WhisparrVerbClass
{
    /// <summary>A request that only reads. Re-issuing one creates nothing and grabs nothing.</summary>
    Read,

    /// <summary>
    /// A request that changes the instance's own configuration. Never re-issued: a second attempt
    /// after an answer that did not arrive would act twice.
    /// </summary>
    Configure,
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
/// <para>
/// <see cref="ReadRootFoldersAsync"/> was added under that rule. It takes no caller-supplied path,
/// no caller-supplied identifier and no verb, so the constraint above still holds over the whole
/// interface.
/// </para>
/// <para>
/// <see cref="ReadHistoryAsync"/> was added under the same rule, and is a read because re-issuing it
/// reads again and grabs nothing. It names a page and a page size; the route and the order it asks
/// for belong to the seam, so no call site supplies either.
/// </para>
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

    /// <summary>Reads the notification schema, which declares what a connection can be told.</summary>
    Task<WhisparrResponse> ReadNotificationSchemaAsync(Uri baseAddress, string apiKey, CancellationToken ct);

    /// <summary>Reads every notification the instance holds.</summary>
    Task<WhisparrResponse> ListNotificationsAsync(Uri baseAddress, string apiKey, CancellationToken ct);

    /// <summary>Reads the library roots the instance reports for itself.</summary>
    /// <remarks>
    /// The instance's own root folders are not carried on the import event it sends, so a consumer
    /// resolving a reported file path against its root has no other source for them.
    /// </remarks>
    Task<WhisparrResponse> ReadRootFoldersAsync(Uri baseAddress, string apiKey, CancellationToken ct);

    /// <summary>Reads one page of the instance's import history.</summary>
    /// <remarks>
    /// The newest-first order is asked for and not relied on: whether the route honours the request is
    /// unmeasured, so a caller reads the page's own order and refuses one it cannot walk.
    /// </remarks>
    /// <param name="baseAddress">The instance to read from.</param>
    /// <param name="apiKey">The key that instance authenticates the read with.</param>
    /// <param name="page">Which page, counting from one.</param>
    /// <param name="pageSize">How many records that page holds at most.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="page"/> or <paramref name="pageSize"/> is below one.
    /// </exception>
    Task<WhisparrResponse> ReadHistoryAsync(
        Uri baseAddress, string apiKey, int page, int pageSize, CancellationToken ct);

    /// <summary>Creates one notification.</summary>
    /// <remarks>
    /// Never re-issued on a failure, whatever the failure is. The instance enforces name uniqueness,
    /// so a second attempt after an answer that did not arrive is refused rather than duplicated —
    /// but the answer to that refusal is indistinguishable from a real one, so the re-issue is not
    /// made at all.
    /// </remarks>
    Task<WhisparrResponse> CreateNotificationAsync(
        Uri baseAddress, string apiKey, JsonNode body, CancellationToken ct);

    /// <summary>Replaces the notification with <paramref name="id"/>.</summary>
    /// <inheritdoc cref="CreateNotificationAsync" path="/remarks"/>
    Task<WhisparrResponse> UpdateNotificationAsync(
        Uri baseAddress, string apiKey, int id, JsonNode body, CancellationToken ct);
}

/// <inheritdoc cref="IWhisparrClient"/>
internal sealed class WhisparrClient(HttpClient http) : IWhisparrClient
{
    /// <summary>The header both generations authenticate an API request with.</summary>
    internal const string ApiKeyHeader = "X-Api-Key";

    // Relative, so they compose onto a base address carrying a URL base (a reverse-proxy subpath).
    // Both generations serve the v3 route family; the version in the path is not the generation.
    private const string StatusPath = "api/v3/system/status";
    private const string NotificationPath = "api/v3/notification";
    private const string NotificationSchemaPath = "api/v3/notification/schema";
    private const string RootFolderPath = "api/v3/rootfolder";
    private const string HistoryPath = "api/v3/history";

    // The order belongs to the verb rather than to a call: newest-first is the only order a walk that
    // stops at a stored position can read, and a call site free to spell it could ask for another.
    private const string NewestFirstQuery = "sortKey=date&sortDirection=descending";

    /// <summary>How long one attempt may take before it is reported as unreachable.</summary>
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How many redirects the client follows. A login redirect is a real deployment, and following an
    /// unbounded chain of them is not.
    /// </summary>
    internal const int MaxRedirects = 3;

    /// <summary>How much of one answer the client will hold in memory before refusing it.</summary>
    internal const long MaxResponseBytes = 8L * 1024 * 1024;

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

        return await ReadAsync(baseAddress, apiKey, StatusPath, ct).ConfigureAwait(false);
    }

    public Task<WhisparrResponse> ReadNotificationSchemaAsync(
        Uri baseAddress, string apiKey, CancellationToken ct)
        => ReadAsync(baseAddress, apiKey, NotificationSchemaPath, ct);

    public Task<WhisparrResponse> ListNotificationsAsync(
        Uri baseAddress, string apiKey, CancellationToken ct)
        => ReadAsync(baseAddress, apiKey, NotificationPath, ct);

    public Task<WhisparrResponse> ReadRootFoldersAsync(
        Uri baseAddress, string apiKey, CancellationToken ct)
        => ReadAsync(baseAddress, apiKey, RootFolderPath, ct);

    public Task<WhisparrResponse> ReadHistoryAsync(
        Uri baseAddress, string apiKey, int page, int pageSize, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);

        return ReadAsync(
            baseAddress,
            apiKey,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{HistoryPath}?page={page}&pageSize={pageSize}&{NewestFirstQuery}"),
            ct);
    }

    public Task<WhisparrResponse> CreateNotificationAsync(
        Uri baseAddress, string apiKey, JsonNode body, CancellationToken ct)
        => ConfigureAsync(baseAddress, apiKey, HttpMethod.Post, NotificationPath, body, ct);

    public Task<WhisparrResponse> UpdateNotificationAsync(
        Uri baseAddress, string apiKey, int id, JsonNode body, CancellationToken ct)
        => ConfigureAsync(
            baseAddress,
            apiKey,
            HttpMethod.Put,
            string.Create(CultureInfo.InvariantCulture, $"{NotificationPath}/{id}"),
            body,
            ct);

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

    // Re-issuing a read re-reads and can create nothing, so the read class is the only one that gets
    // more than one attempt. The last attempt is the plain send, so its failure propagates rather
    // than being counted again.
    private async Task<WhisparrResponse> ReadAsync(
        Uri baseAddress, string apiKey, string path, CancellationToken ct)
    {
        var attempts = WhisparrRetryPolicy.AttemptsFor(WhisparrVerbClass.Read);
        for (var attempt = 1; attempt < attempts; attempt++)
        {
            if (await TrySendAsync(baseAddress, apiKey, HttpMethod.Get, path, null, ct)
                .ConfigureAwait(false) is { } answered)
            {
                return answered;
            }
        }

        return await SendAsync(baseAddress, apiKey, HttpMethod.Get, path, null, ct).ConfigureAwait(false);
    }

    // Sent once. This class acts on the instance, and a request whose answer did not arrive is not
    // the same as one that says nothing happened.
    private Task<WhisparrResponse> ConfigureAsync(
        Uri baseAddress, string apiKey, HttpMethod method, string path, JsonNode body, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        return SendAsync(baseAddress, apiKey, method, path, body, ct);
    }

    // Null when the connection never established, which is the one failure a read may be re-issued
    // after. A status, however unwelcome, is an answer and is returned.
    private async Task<WhisparrResponse?> TrySendAsync(
        Uri baseAddress, string apiKey, HttpMethod method, string path, JsonNode? body, CancellationToken ct)
    {
        try
        {
            return await SendAsync(baseAddress, apiKey, method, path, body, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    private async Task<WhisparrResponse> SendAsync(
        Uri baseAddress, string apiKey, HttpMethod method, string path, JsonNode? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, RequestUri(baseAddress, path));
        request.Headers.Add(ApiKeyHeader, apiKey);
        if (body is not null)
        {
            request.Content = new StringContent(
                body.ToJsonString(), Encoding.UTF8, MediaTypeNames.Application.Json);
        }

        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        var answered = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return new WhisparrResponse(
            (int)response.StatusCode,
            response.Content.Headers.ContentType?.ToString(),
            answered);
    }

    // Relative-Uri composition drops the last segment of a base that does not end in a separator,
    // which would turn a URL base of /whisparr into a request at the site root instead.
    private static Uri RequestUri(Uri baseAddress, string path)
    {
        var builder = new UriBuilder(baseAddress);
        if (!builder.Path.EndsWith('/'))
        {
            builder.Path += '/';
        }

        return new Uri(builder.Uri, path);
    }
}
