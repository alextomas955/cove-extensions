using System.Net;
using System.Net.Http.Headers;
using System.Text;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.TestSupport;

/// <summary>
/// A stub answering a prepared sequence, below the client and above the socket, recording what each
/// request was.
/// </summary>
/// <remarks>
/// The body is read here rather than off the request afterwards: the client disposes the request and
/// its content once the send returns, so a body read later is a read of a disposed stream.
/// <para>
/// A queue rather than one answer, because one generation reaches its entity through two reads and
/// the two answers are the point. A dry queue keeps answering with its last entry, so a case only has
/// to state the answers that differ.
/// </para>
/// <para>
/// The query is recorded beside the path. What one generation is asked under is a query value, and a
/// recording that dropped it could not tell a term that matches from one that silently matches
/// nothing.
/// </para>
/// </remarks>
internal sealed class BodyRecordingHandler : HttpMessageHandler
{
    private readonly Queue<(HttpStatusCode Status, string Answer)> _answers;
    private readonly BodyShape _shape;

    private BodyRecordingHandler(params (HttpStatusCode Status, string Answer)[] answers)
        : this(BodyShape.Whole, answers)
    {
    }

    private BodyRecordingHandler(
        BodyShape shape, params (HttpStatusCode Status, string Answer)[] answers)
    {
        _answers = new Queue<(HttpStatusCode, string)>(answers);
        _shape = shape;
    }

    public List<(HttpMethod Method, string Path, string Body)> Requests { get; } = [];

    /// <summary>The full path and query of each request, in order.</summary>
    public List<string> Targets { get; } = [];

    public static BodyRecordingHandler Answering(HttpStatusCode status, string answer)
        => new((status, answer));

    /// <summary>Answers each of <paramref name="answers"/> in turn.</summary>
    public static BodyRecordingHandler AnsweringInTurn(
        params (HttpStatusCode Status, string Answer)[] answers)
        => new(answers);

    /// <summary>Answers with more of one answer than the client reads at once.</summary>
    /// <remarks>
    /// Generated rather than committed: the size is what the answer is for, so a fixture would be
    /// eight megabytes of repository holding one fact. Declared here rather than in a case, so the
    /// bound and the answer that passes it stay in one place.
    /// </remarks>
    public static BodyRecordingHandler AnsweringPastTheReadBound()
        => Answering(
            HttpStatusCode.OK,
            $"[\"{new string('a', (int)WhisparrClient.MaxResponseBytes)}\"]");

    /// <summary>Answers a success status and then a body that stops part way through.</summary>
    /// <remarks>
    /// The stream raises <see cref="IOException"/> after its first read. That is the base type the
    /// framework's own <c>HttpIOException</c> derives from, so a filter this answer reaches is a
    /// filter a real connection dropped mid-body reaches. A handler rather than a socket, so a case
    /// can drive a route or a batch: neither can be aimed at a listener a test opened.
    /// </remarks>
    public static BodyRecordingHandler AnsweringWithABodyThatStopsPartWay()
        => new(BodyShape.StopsPartWay, (HttpStatusCode.OK, string.Empty));

    /// <summary>Answers <paramref name="answer"/> behind the declared charset's preamble.</summary>
    /// <remarks>
    /// A framework-level string read skips the preamble before decoding and
    /// <c>Encoding.GetString</c> does not, so a read that decodes the bytes itself has to skip it
    /// itself. The bytes are composed here rather than by putting the mark's character in the
    /// literal, so what the case answers is the byte sequence and not a string a later edit could
    /// normalise away.
    /// </remarks>
    public static BodyRecordingHandler AnsweringWithAByteOrderMarkAhead(string answer)
        => new(BodyShape.PrefixedWithAMark, (HttpStatusCode.OK, answer));

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var body = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        Requests.Add((request.Method, request.RequestUri?.AbsolutePath ?? string.Empty, body));
        Targets.Add(request.RequestUri?.PathAndQuery ?? string.Empty);

        var (status, answer) = _answers.Count > 1 ? _answers.Dequeue() : _answers.Peek();
        return new HttpResponseMessage(status) { Content = Content(answer) };
    }

    private HttpContent Content(string answer)
    {
        if (_shape == BodyShape.StopsPartWay)
        {
            return new StreamContent(new StoppingPartWayStream());
        }

        if (_shape == BodyShape.Whole)
        {
            return new StringContent(answer, Encoding.UTF8, "application/json");
        }

        var marked = new ByteArrayContent(
            [.. Encoding.UTF8.Preamble, .. Encoding.UTF8.GetBytes(answer)]);
        marked.Headers.ContentType = MediaTypeHeaderValue.Parse("application/json; charset=utf-8");
        return marked;
    }

    /// <summary>How the answer's bytes reach the client.</summary>
    private enum BodyShape
    {
        /// <summary>Every byte of the answer, decoded from the declared charset.</summary>
        Whole,

        /// <summary>Part of the answer, then an I/O failure.</summary>
        StopsPartWay,

        /// <summary>The declared charset's preamble, then every byte of the answer.</summary>
        PrefixedWithAMark,
    }

    /// <summary>A response body that yields one byte and then reports an I/O failure.</summary>
    private sealed class StoppingPartWayStream : Stream
    {
        private int _served;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            if (_served++ == 0)
            {
                buffer[offset] = (byte)'[';
                return 1;
            }

            throw new IOException("The response ended prematurely.");
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_served++ == 0)
            {
                buffer.Span[0] = (byte)'[';
                return ValueTask.FromResult(1);
            }

            throw new IOException("The response ended prematurely.");
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();
    }
}
