using System.Globalization;
using System.Net.Mime;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using WhisparrSync.Contracts;

namespace WhisparrSync.Whisparr;

// What every outbound request crosses, whichever generation answers and whichever role issued it.
// The bounds live here as one set, so an attempt's timeout, its redirect cap and the size of answer
// that will be read are stated once rather than per generation.
internal sealed class WhisparrTransport(HttpClient http, Whisparr3Gateway v3Gateway, ILogger log)
{
    // The header both v2 and v3 authenticate an API request with.
    internal const string ApiKeyHeader = "X-Api-Key";

    // Relative, so it composes onto a base address carrying a URL base (a reverse-proxy subpath).
    // Both generations serve the v3 route family; the version in the path is not the generation.
    internal const string NotificationPath = "api/v3/notification";

    // The member naming the verb on a composed command body.
    private const string CommandNameProperty = "name";

    // Newest-first is the only order a walk that stops at a stored position can read, so the order
    // belongs to the verb rather than to a call.
    internal const string NewestFirstSortKey = "date";

    // A login redirect is a real deployment; an unbounded chain of them is not.
    internal const int MaxRedirects = 3;

    // How much of one answer is held in memory before it is refused. Exceeding it answers
    // MonitorRefusalKind.AnswerTooLargeToRead with an empty body, never a short body that would
    // parse as a valid page.
    internal const long MaxResponseBytes = 8L * 1024 * 1024;

    // One byte past the bound tells an answer at the bound from one over it.
    private const long ReadCeilingBytes = MaxResponseBytes + 1;

    private const int ReadChunkBytes = 64 * 1024;

    // How long one attempt may take before it is reported as unreachable.
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    // A read of everything an instance holds is answered only once the instance has built all of
    // it, so its cost grows with the holdings rather than signalling it cannot be reached: a v2
    // instance holding 512 sites takes about 20 seconds to answer GET /api/v3/series, all before
    // the first byte. Set well above that, and below where a waiting reader reads it as hung.
    internal static readonly TimeSpan LibraryReadTimeout = TimeSpan.FromSeconds(120);

    internal static IReadOnlySet<string> NothingUnanswered { get; }
        = new HashSet<string>(StringComparer.Ordinal);

    // The bound on one attempt, as the send applies it. Read off the client rather than restated, so
    // one setting bounds one attempt.
    internal TimeSpan AttemptBudget => http.Timeout;

    // Checked before any request, so a file: or ftp: address is refused rather than handed to a
    // handler that would act on it.
    internal static bool IsAddressable(Uri address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return address.IsAbsoluteUri
            && (address.Scheme == Uri.UriSchemeHttp || address.Scheme == Uri.UriSchemeHttps);
    }

    // The settings every request through this transport is made under. The timeout set here bounds
    // a whole attempt: the framework's own ends at the headers once the body is asked for
    // separately, so the send reads this value and bounds both phases.
    internal static void Configure(HttpClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        client.Timeout = RequestTimeout;
    }

    // Certificate validation stays at its default, so a self-signed Whisparr reports as unreachable
    // rather than every instance's identity becoming unverifiable.
    internal static HttpMessageHandler CreateHandler()
        => new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = MaxRedirects,
        };

    internal static bool IsSuccess(int statusCode) => statusCode is >= 200 and < 300;

    // A refusal the send read for itself outranks the status, the rule MonitoringProjector.Classify
    // applies too. An answer past the read bound arrives with whatever status the instance gave, so
    // a success one, and an empty body: parsing it would report the entity as absent and lose the
    // reason the send established.
    internal static bool Refused(WhisparrResponse answered)
        => answered.Refusal is not MonitorRefusalKind.None || !IsSuccess(answered.StatusCode);

    // The verb travels as the call's own argument, so the composed body carries it and the payload
    // does not.
    internal static (string Name, JsonObject Payload) VerbAndPayload(JsonObject command)
    {
        var name = (string?)command[CommandNameProperty]
            ?? throw new ArgumentException("A command names no verb.", nameof(command));

        var payload = (JsonObject)command.DeepClone();
        payload.Remove(CommandNameProperty);
        return (name, payload);
    }

    // Without a trailing separator the instance reads the spelling as a partial name and answers
    // the names its parent holds that start with it. The separator already in the spelling is the
    // one appended, so a path rooted on a drive letter keeps its own.
    internal static string WithTrailingSeparator(string directory)
    {
        if (directory.EndsWith('/') || directory.EndsWith('\\'))
        {
            return directory;
        }

        return directory + (directory.Contains('\\') ? '\\' : '/');
    }

    // Relative-Uri composition drops the last segment of a base that does not end in a separator,
    // which would turn a URL base of /whisparr into a request at the site root instead.
    internal static Uri RequestUri(Uri baseAddress, string path)
    {
        var builder = new UriBuilder(baseAddress);
        if (!builder.Path.EndsWith('/'))
        {
            builder.Path += '/';
        }

        return new Uri(builder.Uri, path);
    }

    // The read that establishes which generation answered, so it runs before one is known and cannot
    // sit on an instance bound to one. Both generations serve this route; the version in the path is
    // not the generation, and the Whisparr 3 generated client is what composes it for either.
    //
    // Returns whatever the instance answered, a non-success status included: classifying the answer
    // belongs to the caller. Re-issued on the same failure and for the same reason every other read
    // is, because a re-read creates nothing.
    internal async Task<WhisparrResponse> ReadStatusAsync(
        Uri baseAddress, string apiKey, CancellationToken ct)
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

        var target = new Whisparr3Target(baseAddress, apiKey);
        var attempts = WhisparrRetryPolicy.AttemptsFor(WhisparrVerbClass.Read);
        for (var attempt = 1; attempt < attempts; attempt++)
        {
            try
            {
                return await SendStatusAsync(target, ct).ConfigureAwait(false);
            }
            catch (Exception failure) when (failure is HttpRequestException or IOException)
            {
                // No whole answer arrived, which is the one failure a read may be re-issued after.
            }
        }

        return await SendStatusAsync(target, ct).ConfigureAwait(false);
    }

    private async Task<WhisparrResponse> SendStatusAsync(
        Whisparr3Target target, CancellationToken ct)
    {
        try
        {
            using var apis = v3Gateway.For(target);
            return Whisparr3Gateway.Answered(
                await apis.Api<Whisparr3.Net.Api.ISystemApi>().GetSystemStatusAsync(ct)
                    .ConfigureAwait(false));
        }
        catch (AnswerTooLargeException beyond)
        {
            return BeyondReadBound(target.BaseAddress, beyond);
        }
    }

    // For a read whose rows are consumed as they arrive. The bounded send buffers a whole answer
    // within the read bound, which a list whose length grows with the library cannot be held in.
    internal Task<HttpResponseMessage> OpenAsync(HttpRequestMessage request, CancellationToken ct)
        => http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

    // The status the instance answered with, carried on the failure, and an empty body: the body a
    // short read produced would parse as a valid answer.
    internal WhisparrResponse BeyondReadBound(Uri baseAddress, AnswerTooLargeException beyond)
    {
        WhisparrSyncLog.ResponseBeyondReadBound(log, baseAddress.Host, MaxResponseBytes);
        return new WhisparrResponse(beyond.StatusCode, null, string.Empty)
        {
            Refusal = MonitorRefusalKind.AnswerTooLargeToRead,
        };
    }

    // Sent once. This class changes the instance's own configuration, and a request whose answer did
    // not arrive is not the same as one that says nothing happened.
    internal Task<WhisparrResponse> ConfigureAsync(
        Uri baseAddress, string apiKey, HttpMethod method, string path, JsonNode body, CancellationToken ct)
        => SentOnceAsync(baseAddress, apiKey, method, path, body, ct);

    internal Task<WhisparrResponse> SentOnceAsync(
        Uri baseAddress, string apiKey, HttpMethod method, string path, JsonNode body, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        return SendAsync(baseAddress, apiKey, method, path, body, ct);
    }

    // Null when no whole answer arrived, the one failure a read may be re-issued after; a status,
    // however unwelcome, is an answer. Two failures reach that reading: a connection that never
    // established raises HttpRequestException, and a body ending before its declared length raises
    // IOException, the body being read out of the stream here rather than buffered inside the send.
    // An answer past the read bound carries its own refusal instead of throwing, so it returns on
    // the first attempt and is not downloaded twice.
    internal async Task<WhisparrResponse?> TrySendAsync(
        Uri baseAddress, string apiKey, HttpMethod method, string path, JsonNode? body, CancellationToken ct)
    {
        try
        {
            return await SendAsync(baseAddress, apiKey, method, path, body, ct).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is HttpRequestException or IOException)
        {
            return null;
        }
    }

    internal async Task<WhisparrResponse> SendAsync(
        Uri baseAddress, string apiKey, HttpMethod method, string path, JsonNode? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, RequestUri(baseAddress, path));
        request.Headers.Add(ApiKeyHeader, apiKey);
        if (body is not null)
        {
            request.Content = new StringContent(
                body.ToJsonString(), Encoding.UTF8, MediaTypeNames.Application.Json);
        }

        // The whole attempt is bounded here, headers and body alike: the client's own timeout stops
        // at the headers once the body is asked for separately, so a body phase left to it runs
        // until the instance gives up. The number is read off the client, so one setting bounds one
        // attempt.
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
        attempt.CancelAfter(http.Timeout);

        try
        {
            using var response = await http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, attempt.Token)
                .ConfigureAwait(false);
            var contentType = response.Content.Headers.ContentType?.ToString();
            var answered = await ReadWithinBoundAsync(response.Content, attempt.Token)
                .ConfigureAwait(false);

            if (answered is null)
            {
                WhisparrSyncLog.ResponseBeyondReadBound(
                    log, request.RequestUri?.Host ?? string.Empty, MaxResponseBytes);
                return new WhisparrResponse((int)response.StatusCode, contentType, string.Empty)
                {
                    Refusal = MonitorRefusalKind.AnswerTooLargeToRead,
                };
            }

            return new WhisparrResponse((int)response.StatusCode, contentType, answered);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Reported as the framework reports its own timeout, so a caller classifying the
            // failure need not know where the bound lives. A shutdown fails the filter and
            // propagates as itself, which keeps it classified as cancelled rather than as a verdict
            // about the instance.
            throw new TaskCanceledException(
                "The request outlived the bound on one attempt.", new TimeoutException(), attempt.Token);
        }
    }

    // Null when the answer is past the bound. Read here rather than bounded by the handler, whose
    // own bound raises an exception whose type a refused connection shares. A body past the bound
    // is discarded unread and never returned.
    private static async Task<string?> ReadWithinBoundAsync(HttpContent content, CancellationToken ct)
    {
        var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var buffered = new MemoryStream();
            var chunk = new byte[ReadChunkBytes];

            while (buffered.Length < ReadCeilingBytes)
            {
                var wanted = (int)Math.Min(chunk.Length, ReadCeilingBytes - buffered.Length);
                var read = await stream.ReadAsync(chunk.AsMemory(0, wanted), ct).ConfigureAwait(false);
                if (read == 0)
                {
                    return Decode(EncodingFor(content), buffered);
                }

                await buffered.WriteAsync(chunk.AsMemory(0, read), ct).ConfigureAwait(false);
            }

            return null;
        }
    }

    // Decoded without the encoding's preamble, which a framework-level string read also skips. A
    // preamble left in place puts U+FEFF at the front, and every reader of a body here parses it as
    // JSON: the parse fails and each answers null, so a BOM-prefixed instance would read as holding
    // nothing anywhere, with nothing saying why.
    private static string Decode(Encoding encoding, MemoryStream buffered)
    {
        var preamble = encoding.Preamble;
        var buffer = buffered.GetBuffer();
        var length = (int)buffered.Length;
        var offset = length >= preamble.Length
            && buffer.AsSpan(0, preamble.Length).SequenceEqual(preamble)
                ? preamble.Length
                : 0;

        return encoding.GetString(buffer, offset, length - offset);
    }

    // The charset the answer names, which is what a framework-level string read honours. Decoding as
    // UTF-8 unconditionally would change what a non-UTF-8 instance's answer says.
    private static Encoding EncodingFor(HttpContent content)
    {
        var charset = content.Headers.ContentType?.CharSet?.Trim('"');
        if (string.IsNullOrWhiteSpace(charset))
        {
            return Encoding.UTF8;
        }

        try
        {
            return Encoding.GetEncoding(charset);
        }
        catch (ArgumentException)
        {
            return Encoding.UTF8;
        }
    }
}
