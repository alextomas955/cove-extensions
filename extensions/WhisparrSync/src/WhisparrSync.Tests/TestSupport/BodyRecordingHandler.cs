using System.Net;
using System.Text;

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

    private BodyRecordingHandler(params (HttpStatusCode Status, string Answer)[] answers)
        => _answers = new Queue<(HttpStatusCode, string)>(answers);

    public List<(HttpMethod Method, string Path, string Body)> Requests { get; } = [];

    /// <summary>The full path and query of each request, in order.</summary>
    public List<string> Targets { get; } = [];

    public static BodyRecordingHandler Answering(HttpStatusCode status, string answer)
        => new((status, answer));

    /// <summary>Answers each of <paramref name="answers"/> in turn.</summary>
    public static BodyRecordingHandler AnsweringInTurn(
        params (HttpStatusCode Status, string Answer)[] answers)
        => new(answers);

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
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(answer, Encoding.UTF8, "application/json"),
        };
    }
}
