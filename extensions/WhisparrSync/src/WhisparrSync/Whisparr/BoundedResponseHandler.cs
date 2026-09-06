namespace WhisparrSync.Whisparr;

/// <summary>Raised when an answer was longer than this product will hold in memory.</summary>
/// <remarks>
/// Its own type rather than an <see cref="HttpRequestException"/>, because this product's failure
/// classification reduces that one to a type name a refused connection produces too: a caller could
/// not then tell this product's own limit from an instance it never reached.
/// <para>
/// It carries the status the instance answered with, so a caller reports what arrived rather than a
/// status this product would otherwise have had to invent.
/// </para>
/// </remarks>
public sealed class AnswerTooLargeException : Exception
{
    private const string Reason = "The answer was longer than this product will read.";

    public AnswerTooLargeException()
        : base(Reason)
    {
    }

    public AnswerTooLargeException(int statusCode)
        : base(Reason)
        => StatusCode = statusCode;

    public AnswerTooLargeException(string message)
        : base(message)
    {
    }

    public AnswerTooLargeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The status the instance answered with before the bound was reached.</summary>
    public int StatusCode { get; }
}

/// <summary>Stops a response body being read past the bound this product sets.</summary>
/// <remarks>
/// The generated client buffers a whole body into a string with no bound of its own, so the bound is
/// applied here, before it sees the content.
/// <para>
/// The body of an answer past the bound is discarded unread beyond the ceiling and never returned. A
/// refused add on one generation answers with a full stack trace, so the value the bound was passed
/// reading is exactly the value that must not travel.
/// </para>
/// </remarks>
internal sealed class BoundedResponseHandler(long maxBytes) : DelegatingHandler
{
    private const int ReadChunkBytes = 64 * 1024;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        // One byte past the bound is what tells an answer at the bound from one over it, so the read
        // stops there rather than buffering whatever else arrived.
        var ceiling = maxBytes + 1;
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var buffered = new MemoryStream();
            var chunk = new byte[ReadChunkBytes];

            while (buffered.Length < ceiling)
            {
                var wanted = (int)Math.Min(chunk.Length, ceiling - buffered.Length);
                var read = await stream
                    .ReadAsync(chunk.AsMemory(0, wanted), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    return WithBufferedContent(response, buffered);
                }

                await buffered
                    .WriteAsync(chunk.AsMemory(0, read), cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        var answered = (int)response.StatusCode;
        response.Dispose();
        throw new AnswerTooLargeException(answered);
    }

    // The content headers are carried across unchanged, because the charset named there is what the
    // decode downstream honours.
    private static HttpResponseMessage WithBufferedContent(
        HttpResponseMessage response, MemoryStream buffered)
    {
        var replacement = new ByteArrayContent(buffered.ToArray());
        foreach (var header in response.Content.Headers)
        {
            replacement.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        response.Content.Dispose();
        response.Content = replacement;
        return response;
    }
}
