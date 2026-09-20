using System.Text.Json.Nodes;
using WhisparrSync.Contracts;
using WhisparrSync.Scene;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.TestSupport;

public sealed record NotificationCall(string Verb, Uri BaseAddress, int? Id, JsonNode? Body);

public sealed record HistoryCall(
    Uri BaseAddress, string ApiKey, WhisparrGeneration Generation, int Page, int PageSize);

// Every argument a role member can carry has a place here, so nothing a call site supplied is
// dropped on the way into the log. An argument the member in question does not take reads as null,
// which is a fact about that member rather than a gap.
public sealed record ActingCall(string Verb, Uri BaseAddress, string ApiKey)
{
    public WhisparrEntityKind? Kind { get; init; }

    public WhisparrGeneration? Generation { get; init; }

    public string? ForeignId { get; init; }

    public int? EntityId { get; init; }

    public IReadOnlyList<int>? EntityIds { get; init; }

    public MonitorScope? Scope { get; init; }

    public bool? Monitored { get; init; }

    public AddDefaults? Defaults { get; init; }

    public string? Folder { get; init; }

    public JsonNode? Body { get; init; }
}

// Stands in for the one seam every outbound request leaves through, so a path that reaches no call
// here contacted the instance not at all. The arguments are recorded rather than a count: a count
// answers whether a request was made, and the question a refusal has to answer is what would have
// been sent. No network and no timing behaviour, so an empty log is a fact about the path under
// test.
// It implements the whole outbound surface rather than the read half, so an empty Verbs list is
// evidence about every verb the product can issue, not only about the ones on the read-and-configure
// interface. One class rather than a second recorder beside it: two logs with independent
// ordering would let an assertion that a path issued nothing be read off a list that could never
// have held the call in question.
internal sealed class RecordingWhisparrClient(WhisparrResponse answer)
    : IWhisparrClient,
        IWhisparrStudioActing,
        IWhisparrPerformerActing,
        IWhisparrMissingSceneActing,
        IWhisparrSiteRegistrationActing,
        IWhisparrReflectOwnedActing,
        IWhisparrSearchGrabbing,
        IWhisparrSceneSearchGrabbing,
        IWhisparrSceneStatusReading,
        IWhisparrSceneExclusionReading,
        IWhisparrSceneMonitorActing,
        IWhisparrSceneExclusionActing,
        IWhisparrSiteSceneReading,
        IWhisparrHeldSiteReading,
        IWhisparrInstanceFilesystemReading
{
    private const string JsonContentType = "application/json; charset=utf-8";

    public List<(Uri BaseAddress, string ApiKey)> Calls { get; } = [];

    public List<SceneStatusCall> SceneStatuses { get; } = [];

    public List<IReadOnlyCollection<string>> ExclusionReads { get; } = [];

    public HashSet<string> Excluded { get; } = new(StringComparer.Ordinal);

    public List<IReadOnlyCollection<string>> HeldSceneReads { get; } = [];

    public HashSet<string> HeldScenes { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<(int SiteId, IReadOnlyCollection<int> SceneNumbers)> SiteSceneReads { get; } = [];

    public Dictionary<int, int> SiteSceneRowIds { get; } = [];

    public List<IReadOnlyCollection<int>> HeldSiteReads { get; } = [];

    public HashSet<int> HeldSites { get; } = [];

    public List<string> ExclusionLookups { get; } = [];

    public Dictionary<string, int> ExclusionIdByScene { get; } = new(StringComparer.Ordinal);

    public bool ExclusionReadCompletes { get; set; } = true;

    // The call is recorded first, so a case can state both that the request was made and that
    // nothing came back. It is the one answer a queued response cannot express: a status is an
    // answer, and this is the absence of one.
    public HashSet<string> Unreachable { get; } = new(StringComparer.Ordinal);

    public List<NotificationCall> Notifications { get; } = [];

    public List<HistoryCall> Histories { get; } = [];

    public List<ActingCall> Acting { get; } = [];

    // An assertion over this states which verbs a path used rather than which it avoided, so a verb
    // added to the seam and then called is a failure rather than an omission from a list.
    public List<string> Verbs { get; } = [];

    // A queue per verb, because a registration reads the list twice, once to find and once to read
    // back, and the two answers are the point. A verb whose queue runs dry keeps answering with its
    // last entry, so a test only has to state the answers that differ, and a paged walk longer than
    // the queue keeps reading the last page it was given.
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

    public Task<WhisparrResponse> ReadCommandAsync(
        Uri baseAddress, string apiKey, int commandId, CancellationToken ct)
        => Record(nameof(ReadCommandAsync), baseAddress, commandId, null);

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

    public Task<WhisparrResponse> SetSceneMonitoredAsync(
        Uri baseAddress,
        string apiKey,
        WhisparrGeneration generation,
        int sceneId,
        bool monitored,
        CancellationToken ct)
        => RecordActing(
            new ActingCall(nameof(SetSceneMonitoredAsync), baseAddress, apiKey)
            {
                Generation = generation,
                EntityId = sceneId,
                Monitored = monitored,
            });

    public Task<WhisparrResponse> AddSceneExclusionAsync(
        Uri baseAddress, string apiKey, string foreignId, CancellationToken ct)
        => RecordActing(
            new ActingCall(nameof(AddSceneExclusionAsync), baseAddress, apiKey)
            {
                ForeignId = foreignId,
            });

    public Task<WhisparrResponse> RemoveSceneExclusionAsync(
        Uri baseAddress, string apiKey, int exclusionId, CancellationToken ct)
        => RecordActing(
            new ActingCall(nameof(RemoveSceneExclusionAsync), baseAddress, apiKey)
            {
                EntityId = exclusionId,
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

    public Task<WhisparrResponse> RegisterSiteAsync(
        Uri baseAddress,
        string apiKey,
        string foreignId,
        AddDefaults defaults,
        CancellationToken ct)
        => RecordActing(
            new ActingCall(nameof(RegisterSiteAsync), baseAddress, apiKey)
            {
                Kind = WhisparrEntityKind.Studio,
                Generation = WhisparrGeneration.V2,
                ForeignId = foreignId,
                Defaults = defaults,
                Monitored = false,
            });

    public Task<WhisparrResponse> MoveSiteRootAsync(
        Uri baseAddress,
        string apiKey,
        int siteId,
        string rootFolderPath,
        CancellationToken ct)
        => RecordActing(
            new ActingCall(nameof(MoveSiteRootAsync), baseAddress, apiKey)
            {
                Kind = WhisparrEntityKind.Studio,
                Generation = WhisparrGeneration.V2,
                EntityId = siteId,
                Folder = rootFolderPath,
            });

    public Task<WhisparrResponse> RefreshSiteCatalogueAsync(
        Uri baseAddress,
        string apiKey,
        int siteId,
        CancellationToken ct)
        => RecordActing(
            new ActingCall(nameof(RefreshSiteCatalogueAsync), baseAddress, apiKey)
            {
                Kind = WhisparrEntityKind.Studio,
                Generation = WhisparrGeneration.V2,
                EntityId = siteId,
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

    public Task<WhisparrResponse> ReadInstanceFolderAsync(
        Uri baseAddress,
        string apiKey,
        WhisparrGeneration generation,
        string directory,
        CancellationToken ct)
        => RecordActing(
            new ActingCall(nameof(ReadInstanceFolderAsync), baseAddress, apiKey)
            {
                Generation = generation,
                Folder = directory,
            });

    public Task<WhisparrResponse> SearchMonitoredAsync(
        Uri baseAddress,
        string apiKey,
        WhisparrGeneration generation,
        WhisparrEntityKind kind,
        IReadOnlyList<int> entityIds,
        CancellationToken ct)
        => RecordActing(
            new ActingCall(nameof(SearchMonitoredAsync), baseAddress, apiKey)
            {
                Generation = generation,
                Kind = kind,
                EntityIds = [.. entityIds],
            });

    public Task<WhisparrResponse> SearchSceneAsync(
        Uri baseAddress, string apiKey, int sceneId, CancellationToken ct)
        => RecordActing(
            new ActingCall(nameof(SearchSceneAsync), baseAddress, apiKey) { EntityId = sceneId });

    public RecordingWhisparrClient Answering(string verb, params WhisparrResponse[] answers)
    {
        NotificationAnswers[verb] = new Queue<WhisparrResponse>(answers);
        return this;
    }

    public static WhisparrResponse Json(int status, string body)
        => new(status, JsonContentType, body);

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

    public Task<WhisparrResponse> ReadEntityPresenceAsync(
        Uri baseAddress,
        string apiKey,
        WhisparrEntityKind kind,
        string foreignId,
        CancellationToken ct)
    {
        SceneStatuses.Add(new SceneStatusCall(kind, foreignId, null));
        return Task.FromResult(Answer(nameof(ReadEntityPresenceAsync)));
    }

    public Task<WhisparrResponse> ReadSceneByRemoteIdAsync(
        Uri baseAddress, string apiKey, string remoteId, CancellationToken ct)
    {
        SceneStatuses.Add(new SceneStatusCall(null, null, remoteId));
        return Task.FromResult(Answer(nameof(ReadSceneByRemoteIdAsync)));
    }

    public Task<IReadOnlySet<string>> ReduceExclusionsAsync(
        Uri baseAddress,
        string apiKey,
        IReadOnlyCollection<string> providerSceneIds,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(providerSceneIds);
        ExclusionReads.Add([.. providerSceneIds]);
        return Task.FromResult<IReadOnlySet<string>>(
            providerSceneIds.Where(Excluded.Contains).ToHashSet(StringComparer.Ordinal));
    }

    public Task<IReadOnlySet<string>> ReduceHeldScenesAsync(
        Uri baseAddress,
        string apiKey,
        IReadOnlyCollection<string> foreignIds,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(foreignIds);
        HeldSceneReads.Add([.. foreignIds]);
        Verbs.Add(nameof(ReduceHeldScenesAsync));

        if (Unreachable.Contains(nameof(ReduceHeldScenesAsync)))
        {
            throw new HttpRequestException("nothing answered");
        }

        return Task.FromResult<IReadOnlySet<string>>(
            foreignIds.Where(HeldScenes.Contains).ToHashSet(StringComparer.Ordinal));
    }

    public Task<IReadOnlyDictionary<int, int>> ReduceSiteSceneRowsAsync(
        Uri baseAddress,
        string apiKey,
        int siteId,
        IReadOnlyCollection<int> sceneNumbers,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sceneNumbers);

        if (sceneNumbers.Count == 0)
        {
            return Task.FromResult<IReadOnlyDictionary<int, int>>(new Dictionary<int, int>());
        }

        SiteSceneReads.Add((siteId, [.. sceneNumbers]));
        Verbs.Add(nameof(ReduceSiteSceneRowsAsync));

        if (Unreachable.Contains(nameof(ReduceSiteSceneRowsAsync)))
        {
            throw new HttpRequestException("nothing answered");
        }

        return Task.FromResult<IReadOnlyDictionary<int, int>>(
            sceneNumbers
                .Where(SiteSceneRowIds.ContainsKey)
                .ToDictionary(number => number, number => SiteSceneRowIds[number]));
    }

    public Task<IReadOnlySet<int>> ReduceHeldSitesAsync(
        Uri baseAddress,
        string apiKey,
        IReadOnlyCollection<int> siteNumbers,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(siteNumbers);

        if (siteNumbers.Count == 0)
        {
            return Task.FromResult<IReadOnlySet<int>>(new HashSet<int>());
        }

        HeldSiteReads.Add([.. siteNumbers]);
        Verbs.Add(nameof(ReduceHeldSitesAsync));

        if (Unreachable.Contains(nameof(ReduceHeldSitesAsync)))
        {
            throw new HttpRequestException("nothing answered");
        }

        return Task.FromResult<IReadOnlySet<int>>(siteNumbers.Where(HeldSites.Contains).ToHashSet());
    }

    public Task<SceneExclusionLookup> FindSceneExclusionAsync(
        Uri baseAddress, string apiKey, string foreignId, CancellationToken ct)
    {
        ExclusionLookups.Add(foreignId);
        Verbs.Add(nameof(FindSceneExclusionAsync));

        if (!ExclusionReadCompletes)
        {
            return Task.FromResult(SceneExclusionLookup.DidNotComplete);
        }

        return Task.FromResult(
            ExclusionIdByScene.TryGetValue(foreignId, out var exclusionId)
                ? SceneExclusionLookup.At(exclusionId)
                : SceneExclusionLookup.NamesNoExclusion);
    }

    public bool RequireConfiguredResponses { get; init; }

    public List<string> UnexpectedCalls { get; } = [];

    private WhisparrResponse Answer(string verb)
    {
        if (Unreachable.Contains(verb))
        {
            throw new HttpRequestException("nothing answered");
        }

        if (!NotificationAnswers.TryGetValue(verb, out var queued) || queued.Count == 0)
        {
            if (RequireConfiguredResponses)
            {
                UnexpectedCalls.Add(verb);
                throw new InvalidOperationException($"Unexpected Whisparr call: {verb}. Configure its response explicitly.");
            }
            return answer;
        }

        return queued.Count == 1 ? queued.Peek() : queued.Dequeue();
    }
}

// Kind and ForeignId are set for an entity probe, RemoteId for a per-scene read.
internal sealed record SceneStatusCall(
    WhisparrEntityKind? Kind, string? ForeignId, string? RemoteId);
