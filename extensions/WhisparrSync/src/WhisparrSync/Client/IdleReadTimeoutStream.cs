namespace WhisparrSync.Client;

/// <summary>
/// A read-only delegating stream that bounds how long ONE read may go without returning, rather than how
/// long the whole transfer may take.
/// </summary>
/// <remarks>
/// <para>
/// There is no built-in for this. <c>SocketsHttpHandler</c> exposes connect, drain, pooled-idle and
/// keep-alive-ping settings and none of them bounds an individual read from a response content stream;
/// keep-alive pings are HTTP/2-and-3 only, and Whisparr is a Servarr over HTTP/1.1. What is available is a
/// caller-owned budget re-armed per read, which is what this is.
/// </para>
/// <para>
/// It is needed precisely because headers-only completion is what makes a whole-library read affordable: the
/// transport stops governing the body once the response stream exists, so without a budget here the body has
/// no wall at all. Raising the whole-call budget instead would be the wrong shape — a bigger wall on a read
/// that grows with the library converts a visible failure into a slow success that fails later, at a larger
/// library, with no signal.
/// </para>
/// <para>
/// An expiry surfaces as an <see cref="IOException"/> so the transport's send loop classifies it as
/// unreachable alongside every other mid-transfer failure. A cancellation that came from the caller's own
/// token propagates untouched.
/// </para>
/// </remarks>
internal sealed class IdleReadTimeoutStream(Stream inner, TimeSpan idleTimeout) : Stream
{
    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(idleTimeout);
        try
        {
            return await inner.ReadAsync(buffer, budget.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException(
                $"the response stalled for more than {idleTimeout.TotalSeconds:0} s without sending anything");
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    // Synchronous reads are not on the path this exists for: the deserializer reads asynchronously, and a
    // blocking read could not be interrupted by a budget anyway.
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    // The inner stream is deliberately NOT disposed here: it belongs to the HttpResponseMessage the send loop
    // owns, and closing it from the wrapper would take it out from under a guard that still holds the response.
    protected override void Dispose(bool disposing) => base.Dispose(disposing);
}
