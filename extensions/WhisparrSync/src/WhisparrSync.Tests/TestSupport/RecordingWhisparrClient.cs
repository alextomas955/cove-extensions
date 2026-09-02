using System.Text.Json.Nodes;
using WhisparrSync.Contracts;
using WhisparrSync.Monitoring;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.TestSupport;

/// <summary>One notification request this client was asked to make, with its arguments.</summary>
/// <param name="Verb">Which of the notification calls it was.</param>
/// <param name="BaseAddress">The instance it was aimed at.</param>
/// <param name="Id">The notification's id on an update, or null on any other call.</param>
/// <param name="Body">The body sent, or null on a read.</param>
public sealed record NotificationCall(string Verb, Uri BaseAddress, int? Id, JsonNode? Body);

/// <summary>One history read this client was asked to make, with its arguments.</summary>
/// <param name="BaseAddress">The instance it was aimed at.</param>
/// <param name="ApiKey">The key it presented.</param>
/// <param name="Generation">The lineage whose entity spelling it asked the page for.</param>
/// <param name="Page">Which page it asked for.</param>
/// <param name="PageSize">How many records it asked that page to hold.</param>
public sealed record HistoryCall(
    Uri BaseAddress, string ApiKey, WhisparrGeneration Generation, int Page, int PageSize);

/// <summary>One acting or grabbing request this client was asked to make, with its arguments.</summary>
/// <remarks>
/// Every argument a role member can carry has a place here, so nothing a call site supplied is
/// dropped on the way into the log. An argument the member in question does not take reads as null,
/// which is a fact about that member rather than a gap.
/// </remarks>
/// <param name="Verb">Which member it was.</param>
/// <param name="BaseAddress">The instance it was aimed at.</param>
/// <param name="ApiKey">The key it presented.</param>
public sealed record ActingCall(string Verb, Uri BaseAddress, string ApiKey)
{
    /// <summary>The entity kind named, or null where the member names one kind by itself.</summary>
    public WhisparrEntityKind? Kind { get; init; }

    /// <summary>The generation named, or null where the member names none.</summary>
    public WhisparrGeneration? Generation { get; init; }

    /// <summary>The already-resolved identifier supplied, or null where the member takes none.</summary>
    public string? ForeignId { get; init; }

    /// <summary>The instance-side identifier supplied, or null where the member takes none.</summary>
    public int? EntityId { get; init; }

    /// <summary>The scope asked for, or null where the member expresses no scope.</summary>
    public MonitorScope? Scope { get; init; }

    /// <summary>The flag asked for, or null on a member that does not set it.</summary>
    public bool? Monitored { get; init; }

    /// <summary>The add defaults supplied, or null on a member that adds nothing.</summary>
    public AddDefaults? Defaults { get; init; }

    /// <summary>The directory named, or null on a member that names none.</summary>
    public string? Folder { get; init; }

    /// <summary>The body sent, or null where there was none.</summary>
    public JsonNode? Body { get; init; }
}

/// <summary>
/// A double for every seam interface this product can make a request through, recording the
/// ARGUMENTS of each one and answering with a response the caller chose.
/// </summary>
/// <remarks>
/// It stands in for the one seam every outbound request leaves through, so a path that reaches no
/// call here contacted the instance not at all. The arguments are what is recorded rather than a
/// count: a count answers whether a request was made, and the question a refusal has to answer is
/// what would have been sent.
/// <para>
/// No network and no timing behaviour, so an empty log is a fact about the path under test.
/// </para>
/// <para>
/// It implements the whole outbound surface rather than the read half, so an empty
/// <see cref="Verbs"/> is evidence about every verb the product can issue and not only about the
/// ones on the read-and-configure interface. One class rather than a second recorder beside it: two
/// logs with independent ordering would let an assertion that a path issued nothing be read off a
/// list that could never have held the call in question.
/// </para>
/// </remarks>
internal sealed class RecordingWhisparrClient(WhisparrResponse answer)
    : IWhisparrClient,
        IWhisparrStudioActing,
        IWhisparrPerformerActing,
        IWhisparrMissingSceneActing,
        IWhisparrReflectOwnedActing,
        IWhisparrSearchGrabbing
{
    private const string JsonContentType = "application/json; charset=utf-8";

    /// <summary>Every status read this client was asked for, in order.</summary>
    public List<(Uri BaseAddress, string ApiKey)> Calls { get; } = [];

    /// <summary>Every notification request this client was asked for, in order.</summary>
    public List<NotificationCall> Notifications { get; } = [];

    /// <summary>Every history read this client was asked for, in order.</summary>
    public List<HistoryCall> Histories { get; } = [];

    /// <summary>Every acting and grabbing request this client was asked for, in order.</summary>
    public List<ActingCall> Acting { get; } = [];

    /// <summary>
    /// The name of every request this client was asked to make, in order, whichever verb it was.
    /// </summary>
    /// <remarks>
    /// An assertion over this states which verbs a path used rather than which it avoided, so a verb
    /// added to the seam and then called is a failure rather than an omission from a list.
    /// </remarks>
    public List<string> Verbs { get; } = [];

    /// <summary>
    /// What each verb answers with, keyed by verb name.
    /// </summary>
    /// <remarks>
    /// A queue per verb, because a registration reads the list TWICE — once to find, once to read
    /// back — and the two answers are the point. A verb whose queue runs dry keeps answering with its
    /// last entry, so a test only has to state the answers that differ, and a paged walk longer than
    /// the queue keeps reading the last page it was given.
    /// </remarks>
    public Dictionary<string, Queue<WhisparrResponse>> NotificationAnswers { get; } = [];

    public Task<WhisparrResponse> ReadStatusAsync(Uri baseAddress, string apiKey, CancellationToken ct)
    {
        Calls.Add((baseAddress, apiKey));
        Verbs.Add(nameof(ReadStatusAsync));
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

    public Task<WhisparrResponse> ReadQualityProfilesAsync(
        Uri baseAddress, string apiKey, CancellationToken ct)
        => Record(nameof(ReadQualityProfilesAsync), baseAddress, null, null);

    public Task<WhisparrResponse> ReadHistoryAsync(
        Uri baseAddress,
        string apiKey,
        WhisparrGeneration generation,
        int page,
        int pageSize,
        CancellationToken ct)
    {
        Histories.Add(new HistoryCall(baseAddress, apiKey, generation, page, pageSize));
        Verbs.Add(nameof(ReadHistoryAsync));
        return Task.FromResult(Answer(nameof(ReadHistoryAsync)));
    }

    public Task<WhisparrResponse> CreateNotificationAsync(
        Uri baseAddress, string apiKey, JsonNode body, CancellationToken ct)
        => Record(nameof(CreateNotificationAsync), baseAddress, null, body);

    public Task<WhisparrResponse> UpdateNotificationAsync(
        Uri baseAddress, string apiKey, int id, JsonNode body, CancellationToken ct)
        => Record(nameof(UpdateNotificationAsync), baseAddress, id, body);

    public Task<WhisparrResponse> ReadStudioAsync(
        Uri baseAddress,
        string apiKey,
        WhisparrGeneration generation,
        string foreignId,
        CancellationToken ct)
        => RecordActing(
            new ActingCall(nameof(ReadStudioAsync), baseAddress, apiKey)
            {
                Kind = WhisparrEntityKind.Studio,
                Generation = generation,
                ForeignId = foreignId,
            });

    public Task<WhisparrResponse> AddMonitoredStudioAsync(
        Uri baseAddress,
        string apiKey,
        WhisparrGeneration generation,
        string foreignId,
        MonitorScope scope,
        AddDefaults defaults,
        CancellationToken ct)
        => RecordActing(
            new ActingCall(nameof(AddMonitoredStudioAsync), baseAddress, apiKey)
            {
                Kind = WhisparrEntityKind.Studio,
                Generation = generation,
                ForeignId = foreignId,
                Scope = scope,
                Defaults = defaults,
                Monitored = true,
            });

    public Task<WhisparrResponse> SetStudioMonitoredAsync(
        Uri baseAddress,
        string apiKey,
        WhisparrGeneration generation,
        int entityId,
        bool monitored,
        CancellationToken ct)
        => RecordActing(
            new ActingCall(nameof(SetStudioMonitoredAsync), baseAddress, apiKey)
            {
                Kind = WhisparrEntityKind.Studio,
                Generation = generation,
                EntityId = entityId,
                Monitored = monitored,
            });

    public Task<WhisparrResponse> SetStudioScopeAsync(
        Uri baseAddress,
        string apiKey,
        WhisparrGeneration generation,
        int entityId,
        MonitorScope scope,
        CancellationToken ct)
        => RecordActing(
            new ActingCall(nameof(SetStudioScopeAsync), baseAddress, apiKey)
            {
                Kind = WhisparrEntityKind.Studio,
                Generation = generation,
                EntityId = entityId,
                Scope = scope,
            });

    public Task<WhisparrResponse> ReadPerformerAsync(
        Uri baseAddress, string apiKey, string foreignId, CancellationToken ct)
        => RecordActing(
            new ActingCall(nameof(ReadPerformerAsync), baseAddress, apiKey)
            {
                Kind = WhisparrEntityKind.Performer,
                ForeignId = foreignId,
            });

    public Task<WhisparrResponse> AddMonitoredPerformerAsync(
        Uri baseAddress,
        string apiKey,
        string foreignId,
        AddDefaults defaults,
        CancellationToken ct)
        => RecordActing(
            new ActingCall(nameof(AddMonitoredPerformerAsync), baseAddress, apiKey)
            {
                Kind = WhisparrEntityKind.Performer,
                ForeignId = foreignId,
                Defaults = defaults,
                Monitored = true,
            });

    public Task<WhisparrResponse> SetPerformerMonitoredAsync(
        Uri baseAddress, string apiKey, int entityId, bool monitored, CancellationToken ct)
        => RecordActing(
            new ActingCall(nameof(SetPerformerMonitoredAsync), baseAddress, apiKey)
            {
                Kind = WhisparrEntityKind.Performer,
                EntityId = entityId,
                Monitored = monitored,
            });

    public Task<WhisparrResponse> AddSceneAsync(
        Uri baseAddress,
        string apiKey,
        string foreignId,
        AddDefaults defaults,
        CancellationToken ct)
        => RecordActing(
            new ActingCall(nameof(AddSceneAsync), baseAddress, apiKey)
            {
                ForeignId = foreignId,
                Defaults = defaults,
            });

    public Task<WhisparrResponse> RefreshCatalogueAsync(
        Uri baseAddress,
        string apiKey,
        WhisparrEntityKind kind,
        int entityId,
        CancellationToken ct)
        => RecordActing(
            new ActingCall(nameof(RefreshCatalogueAsync), baseAddress, apiKey)
            {
                Kind = kind,
                EntityId = entityId,
            });

    public Task<WhisparrResponse> ReadHardlinkSettingAsync(
        Uri baseAddress, string apiKey, CancellationToken ct)
        => RecordActing(new ActingCall(nameof(ReadHardlinkSettingAsync), baseAddress, apiKey));

    public Task<WhisparrResponse> ListImportableFilesAsync(
        Uri baseAddress, string apiKey, string folder, CancellationToken ct)
        => RecordActing(
            new ActingCall(nameof(ListImportableFilesAsync), baseAddress, apiKey)
            {
                Folder = folder,
            });

    public Task<WhisparrResponse> AttachOwnedFilesAsync(
        Uri baseAddress, string apiKey, JsonNode files, CancellationToken ct)
        => RecordActing(
            new ActingCall(nameof(AttachOwnedFilesAsync), baseAddress, apiKey) { Body = files });

    public Task<WhisparrResponse> SearchMonitoredAsync(
        Uri baseAddress,
        string apiKey,
        WhisparrEntityKind kind,
        int entityId,
        CancellationToken ct)
        => RecordActing(
            new ActingCall(nameof(SearchMonitoredAsync), baseAddress, apiKey)
            {
                Kind = kind,
                EntityId = entityId,
            });

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
        Verbs.Add(verb);
        return Task.FromResult(Answer(verb));
    }

    // Both lists are appended here and nowhere else, so the ordered verb log cannot disagree with the
    // arguments log about what happened. The body is cloned for the same reason the notification log
    // clones one: a recorded body that aliased a mutable node would answer for its own later state.
    private Task<WhisparrResponse> RecordActing(ActingCall call)
    {
        Acting.Add(call with { Body = call.Body?.DeepClone() });
        Verbs.Add(call.Verb);
        return Task.FromResult(Answer(call.Verb));
    }

    private WhisparrResponse Answer(string verb)
    {
        if (!NotificationAnswers.TryGetValue(verb, out var queued) || queued.Count == 0)
        {
            return answer;
        }

        return queued.Count == 1 ? queued.Peek() : queued.Dequeue();
    }
}
