namespace WhisparrSync.Whisparr;

/// <summary>Raised when an answer was longer than this extension will hold in memory.</summary>
/// <remarks>
/// Not an <see cref="HttpRequestException"/>: failure classification reduces that one to a type name
/// a refused connection produces too, so the local bound would read as an instance never reached.
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

// The generated client buffers a whole body into a string with no bound of its own, so the read
// bound is applied here, before it sees the content. A refused add answers with a full stack trace,
// so a body past the bound is discarded rather than returned.
internal sealed class BoundedResponseHandler(long maxBytes) : DelegatingHandler
{
    private const int ReadChunkBytes = 64 * 1024;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        // One byte past the bound tells an answer at the bound from one over it.
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

    // Content headers are carried across unchanged: the charset named there drives the decode.
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
