using System.Net;
using System.Net.Http.Headers;
using System.Text;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.TestSupport;

// The body is read here rather than off the request afterwards: the client disposes the request and
// its content once the send returns. A queue rather than one answer, because one generation reaches
// its entity through two reads and the two answers are the point; a dry queue keeps answering with
// its last entry, so a case only has to state the answers that differ. The query is recorded beside
// the path, because a recording that dropped it could not tell a term that matches from one that
// silently matches nothing.
internal sealed class BodyRecordingHandler : HttpMessageHandler
{
    private readonly Queue<(HttpStatusCode Status, string Answer)> _answers;
    private readonly BodyShape _shape;
    private readonly Func<string, string>? _byPath;
    private readonly Func<HttpMethod, string, (HttpStatusCode Status, string Answer)>? _byCall;

    private BodyRecordingHandler(params (HttpStatusCode Status, string Answer)[] answers)
        : this(BodyShape.Whole, answers)
    {
    }

    private BodyRecordingHandler(Func<string, string> byPath)
        : this(BodyShape.Whole, (HttpStatusCode.OK, string.Empty))
        => _byPath = byPath;

    private BodyRecordingHandler(
        Func<HttpMethod, string, (HttpStatusCode Status, string Answer)> byCall)
        : this(BodyShape.Whole, (HttpStatusCode.OK, string.Empty))
        => _byCall = byCall;

    private BodyRecordingHandler(
        BodyShape shape, params (HttpStatusCode Status, string Answer)[] answers)
    {
        _answers = new Queue<(HttpStatusCode, string)>(answers);
        _shape = shape;
    }

    public List<(HttpMethod Method, string Path, string Body)> Requests { get; } = [];

    public List<string> Targets { get; } = [];

    public static BodyRecordingHandler Answering(HttpStatusCode status, string answer)
        => new((status, answer));

    public static BodyRecordingHandler AnsweringInTurn(
        params (HttpStatusCode Status, string Answer)[] answers)
        => new(answers);

    // Keyed on the path, so a case driving many routes states only the answers a route needs to be
    // reachable. The turn-taking factory keys on order, and the order shifts whenever a call is added.
    public static BodyRecordingHandler AnsweringByPath(Func<string, string> answer)
        => new(answer);

    // The status is the case's to state as well as the body. A read and a write can share a path, and
    // whether an instance holds an entity is read from the status rather than from the body, so a
    // fixture that always answers a success describes an instance holding everything.
    public static BodyRecordingHandler AnsweringEach(
        Func<HttpMethod, string, (HttpStatusCode Status, string Answer)> answer)
        => new(answer);

    // Generated rather than committed: the size is what the answer is for, so a fixture would be
    // eight megabytes of repository holding one fact. Declared here rather than in a case, so the
    // bound and the answer that passes it stay in one place.
    public static BodyRecordingHandler AnsweringPastTheReadBound()
        => Answering(
            HttpStatusCode.OK,
            $"[\"{new string('a', (int)WhisparrTransport.MaxResponseBytes)}\"]");

    // The stream raises IOException after its first read. That is the base type the framework's own
    // HttpIOException derives from, so a filter this answer reaches is a filter a real connection
    // dropped mid-body reaches. A handler rather than a socket, because neither a route nor a batch
    // can be aimed at a listener a test opened.
    public static BodyRecordingHandler AnsweringWithABodyThatStopsPartWay()
        => new(BodyShape.StopsPartWay, (HttpStatusCode.OK, string.Empty));

    // A framework-level string read skips the preamble before decoding and Encoding.GetString does
    // not, so a read that decodes the bytes itself has to skip it itself. The bytes are composed here
    // rather than by putting the mark's character in the literal, so what the case answers is a byte
    // sequence a later edit cannot normalise away.
    public static BodyRecordingHandler AnsweringWithAByteOrderMarkAhead(string answer)
        => new(BodyShape.PrefixedWithAMark, (HttpStatusCode.OK, answer));

    // Raises the exception a connection that reached nothing raises, after recording the attempt. A
    // request the client re-issues therefore appears as a second recorded attempt.
    public static BodyRecordingHandler ReachingNothing()
        => new(BodyShape.ReachesNothing, (HttpStatusCode.OK, string.Empty));

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var body = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var path = request.RequestUri?.AbsolutePath ?? string.Empty;
        Requests.Add((request.Method, path, body));
        Targets.Add(request.RequestUri?.PathAndQuery ?? string.Empty);

        if (_shape == BodyShape.ReachesNothing)
        {
            throw new HttpRequestException("the handler reached nothing");
        }

        if (_byPath is not null)
        {
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = Content(_byPath(path)) };
        }

        if (_byCall is not null)
        {
            var (called, answered) = _byCall(request.Method, path);
            return new HttpResponseMessage(called) { Content = Content(answered) };
        }

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

    private enum BodyShape
    {
        Whole,

        StopsPartWay,

        PrefixedWithAMark,

        ReachesNothing,
    }

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
