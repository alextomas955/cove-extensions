using System.Text.Json.Nodes;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.TestSupport;

/// <summary>One notification request this client was asked to make, with its arguments.</summary>
/// <param name="Verb">Which of the notification calls it was.</param>
/// <param name="BaseAddress">The instance it was aimed at.</param>
/// <param name="Id">The notification's id on an update, or null on any other call.</param>
/// <param name="Body">The body sent, or null on a read.</param>
public sealed record NotificationCall(string Verb, Uri BaseAddress, int? Id, JsonNode? Body);

/// <summary>
/// An <see cref="IWhisparrClient"/> that records the ARGUMENTS of every request asked of it and
/// answers with a response the caller chose.
/// </summary>
/// <remarks>
/// It stands in for the one seam every outbound request leaves through, so a path that reaches no
/// call here contacted the instance not at all. The arguments are what is recorded rather than a
/// count: a count answers whether a request was made, and the question a refusal has to answer is
/// what would have been sent.
/// <para>
/// No network and no timing behaviour, so an empty log is a fact about the path under test.
/// </para>
/// </remarks>
internal sealed class RecordingWhisparrClient(WhisparrResponse answer) : IWhisparrClient
{
    private const string JsonContentType = "application/json; charset=utf-8";

    /// <summary>Every status read this client was asked for, in order.</summary>
    public List<(Uri BaseAddress, string ApiKey)> Calls { get; } = [];

    /// <summary>Every notification request this client was asked for, in order.</summary>
    public List<NotificationCall> Notifications { get; } = [];

    /// <summary>
    /// What each notification verb answers with, keyed by verb name.
    /// </summary>
    /// <remarks>
    /// A queue per verb, because a registration reads the list TWICE — once to find, once to read
    /// back — and the two answers are the point. A verb whose queue runs dry keeps answering with its
    /// last entry, so a test only has to state the answers that differ.
    /// </remarks>
    public Dictionary<string, Queue<WhisparrResponse>> NotificationAnswers { get; } = [];

    public Task<WhisparrResponse> ReadStatusAsync(Uri baseAddress, string apiKey, CancellationToken ct)
    {
        Calls.Add((baseAddress, apiKey));
        return Task.FromResult(answer);
    }

    public Task<WhisparrResponse> ReadNotificationSchemaAsync(
        Uri baseAddress, string apiKey, CancellationToken ct)
        => Record(nameof(ReadNotificationSchemaAsync), baseAddress, null, null);

    public Task<WhisparrResponse> ListNotificationsAsync(
        Uri baseAddress, string apiKey, CancellationToken ct)
        => Record(nameof(ListNotificationsAsync), baseAddress, null, null);

    public Task<WhisparrResponse> ReadRootFoldersAsync(
        Uri baseAddress, string apiKey, CancellationToken ct)
        => Record(nameof(ReadRootFoldersAsync), baseAddress, null, null);

    public Task<WhisparrResponse> CreateNotificationAsync(
        Uri baseAddress, string apiKey, JsonNode body, CancellationToken ct)
        => Record(nameof(CreateNotificationAsync), baseAddress, null, body);

    public Task<WhisparrResponse> UpdateNotificationAsync(
        Uri baseAddress, string apiKey, int id, JsonNode body, CancellationToken ct)
        => Record(nameof(UpdateNotificationAsync), baseAddress, id, body);

    /// <summary>Queues <paramref name="answers"/> as what <paramref name="verb"/> answers with.</summary>
    public RecordingWhisparrClient Answering(string verb, params WhisparrResponse[] answers)
    {
        NotificationAnswers[verb] = new Queue<WhisparrResponse>(answers);
        return this;
    }

    /// <summary>A JSON answer with <paramref name="status"/> and <paramref name="body"/>.</summary>
    public static WhisparrResponse Json(int status, string body)
        => new(status, JsonContentType, body);

    /// <summary>A client answering with the status document <paramref name="fixtureFileName"/> holds.</summary>
    public static RecordingWhisparrClient Reporting(string fixtureFileName)
        => new(new WhisparrResponse(200, JsonContentType, ProbeFixtures.Read(fixtureFileName)));

    private Task<WhisparrResponse> Record(string verb, Uri baseAddress, int? id, JsonNode? body)
    {
        Notifications.Add(new NotificationCall(verb, baseAddress, id, body?.DeepClone()));
        if (!NotificationAnswers.TryGetValue(verb, out var queued) || queued.Count == 0)
        {
            return Task.FromResult(answer);
        }

        return Task.FromResult(queued.Count == 1 ? queued.Peek() : queued.Dequeue());
    }
}
