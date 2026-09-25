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

// Every role member takes the cancellation token its signature declares and records nothing from
// it, and the members sit here rather than on the two types that declare the roles.
#pragma warning disable IDE0060, S1172 // The member this stands in for takes it.

// Stands in for the one seam every outbound request leaves through, so a path that reaches no call
// here contacted the instance not at all. The arguments are recorded rather than a count: the
// question a refusal has to answer is what would have been sent. Every member of every role lives
// here and every call is appended to one ordered log, so an assertion that a path issued nothing
// reads a list that could have held the call. Which roles a case can reach is the interface list
// its generation's type declares below.
internal abstract class RecordingWhisparrCore(WhisparrResponse answer, WhisparrBinding? binding = null)
    : IWhisparrClient
{
    private const string JsonContentType = "application/json; charset=utf-8";

    // What a case that asserts on the recorded instance gets back. A recorded call names the binding
    // rather than arguments, because a bound member takes none.
    internal static WhisparrBinding AnyInstance { get; }
        = new(WhisparrGeneration.V3, new Uri("http://whisparr.test:6969/"), "recorded-key");

    public WhisparrBinding Binding { get; } = binding ?? AnyInstance;

    public List<(Uri BaseAddress, string ApiKey)> Calls { get; } = [];

    public List<SceneStatusCall> SceneStatuses { get; } = [];

    public List<IReadOnlyCollection<string>> ExclusionReads { get; } = [];

    public HashSet<string> Excluded { get; } = new(StringComparer.Ordinal);

    public List<IReadOnlyCollection<string>> HeldSceneReads { get; } = [];

    public HashSet<string> HeldScenes { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<(int SiteId, IReadOnlyCollection<int> SceneNumbers)> SiteSceneReads { get; } = [];

    public Dictionary<int, int> SiteSceneRowIds { get; } = [];

    public List<IReadOnlyCollection<int>> HeldSiteReads { get; } = [];

    // What each batch read was asked about, so a page's cost is read off the list's length rather
    // than off a counter that cannot say which identifiers reached the instance.
    public List<IReadOnlyCollection<string>> EntityBatchReads { get; } = [];

    public List<IReadOnlyCollection<string>> SceneBatchReads { get; } = [];

    // What the instance lists for each entity, which is what the missing surface is composed from.
    // An entity absent from this map is one the instance holds no entry for.
    public Dictionary<string, IReadOnlyList<WhisparrCatalogueScene>> EntityCatalogues { get; }
        = new(StringComparer.OrdinalIgnoreCase);

    public List<string> EntityCatalogueReads { get; } = [];

    // The entities and scenes this instance holds, keyed as the answering row would spell them. An
    // identifier absent from these is one the batch answers no row for.
    public Dictionary<string, WhisparrHeldCard> HeldEntityCards { get; }
        = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, WhisparrHeldCard> HeldSceneCards { get; }
        = new(StringComparer.OrdinalIgnoreCase);

    // Identifiers the batch cannot speak for at all, which the caller then asks about one at a time.
    public HashSet<string> BatchCannotSpeakFor { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<int> HeldSites { get; } = [];

    public List<string> ExclusionLookups { get; } = [];

    public Dictionary<string, int> ExclusionIdByScene { get; } = new(StringComparer.Ordinal);

    public bool ExclusionReadCompletes { get; set; } = true;

    // The call is recorded first, so a case can state both that the request was made and that
    // nothing came back. It is the one answer a queued response cannot express: a status is an
    // answer, and this is the absence of one.
    public HashSet<string> Unreachable { get; } = new(StringComparer.Ordinal);

    // What the instance reads for every file a case hands it. Zero is the value it answers a path
    // it could read nothing off, which is the case an import must not be composed from.
    public int QualityRead { get; set; } = 7;

    public int LanguageRead { get; set; } = 1;

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

    public Task<WhisparrResponse> ReadNotificationSchemaAsync(CancellationToken ct)
        => Record(nameof(ReadNotificationSchemaAsync), null, null);

    public Task<WhisparrResponse> ListNotificationsAsync(CancellationToken ct)
        => Record(nameof(ListNotificationsAsync), null, null);

    public Task<WhisparrResponse> ReadRootFoldersAsync(CancellationToken ct)
        => Record(nameof(ReadRootFoldersAsync), null, null);

    public Task<WhisparrResponse> ReadQualityProfilesAsync(CancellationToken ct)
        => Record(nameof(ReadQualityProfilesAsync), null, null);

    public Task<WhisparrResponse> ReadHistoryAsync(int page, int pageSize, CancellationToken ct)
    {
        Histories.Add(new HistoryCall(
            Binding.BaseAddress, Binding.ApiKey, Binding.Generation, page, pageSize));
        Verbs.Add(nameof(ReadHistoryAsync));
        return Task.FromResult(Answer(nameof(ReadHistoryAsync)));
    }

    public Task<WhisparrResponse> ReadCommandAsync(int commandId, CancellationToken ct)
        => Record(nameof(ReadCommandAsync), commandId, null);

    public Task<WhisparrResponse> CreateNotificationAsync(JsonNode body, CancellationToken ct)
        => Record(nameof(CreateNotificationAsync), null, body);

    public Task<WhisparrResponse> UpdateNotificationAsync(
        int id, JsonNode body, CancellationToken ct)

        => Record(nameof(UpdateNotificationAsync), id, body);

    public Task<WhisparrResponse> ReadStudioAsync(
        string foreignId,
        CancellationToken ct)
        => RecordActing(
            new ActingCall(nameof(ReadStudioAsync), Binding.BaseAddress, Binding.ApiKey)
            {
                Kind = WhisparrEntityKind.Studio,
                Generation = Binding.Generation,
                ForeignId = foreignId,
            });

    public Task<WhisparrResponse> AddMonitoredStudioAsync(
        string foreignId,
        MonitorScope scope,
        AddDefaults defaults,
        CancellationToken ct)
        => RecordActing(
            new ActingCall(nameof(AddMonitoredStudioAsync), Binding.BaseAddress, Binding.ApiKey)
            {
                Kind = WhisparrEntityKind.Studio,
                Generation = Binding.Generation,
                ForeignId = foreignId,
                Scope = scope,
                Defaults = defaults,
                Monitored = true,
            });

    public Task<WhisparrResponse> SetStudioMonitoredAsync(
        int entityId,
        bool monitored,
        CancellationToken ct)
        => RecordActing(
            new ActingCall(nameof(SetStudioMonitoredAsync), Binding.BaseAddress, Binding.ApiKey)
            {
                Kind = WhisparrEntityKind.Studio,
                Generation = Binding.Generation,
                EntityId = entityId,
                Monitored = monitored,
            });

    public Task<WhisparrResponse> SetStudioScopeAsync(
        int entityId,
        MonitorScope scope,
        CancellationToken ct)
        => RecordActing(
            new ActingCall(nameof(SetStudioScopeAsync), Binding.BaseAddress, Binding.ApiKey)
            {
                Kind = WhisparrEntityKind.Studio,
                Generation = Binding.Generation,
                EntityId = entityId,
                Scope = scope,
            });

    public Task<WhisparrResponse> ReadPerformerAsync(
        string foreignId, CancellationToken ct)
        => RecordActing(
            new ActingCall(nameof(ReadPerformerAsync), Binding.BaseAddress, Binding.ApiKey)
            {
                Kind = WhisparrEntityKind.Performer,
                ForeignId = foreignId,
            });

    public Task<WhisparrResponse> AddMonitoredPerformerAsync(
        string foreignId,
        AddDefaults defaults,
        CancellationToken ct)
        => RecordActing(
            new ActingCall(nameof(AddMonitoredPerformerAsync), Binding.BaseAddress, Binding.ApiKey)
            {
                Kind = WhisparrEntityKind.Performer,
                ForeignId = foreignId,
                Defaults = defaults,
                Monitored = true,
            });

    public Task<WhisparrResponse> SetPerformerMonitoredAsync(
        int entityId, bool monitored, CancellationToken ct)
        => RecordActing(
            new ActingCall(nameof(SetPerformerMonitoredAsync), Binding.BaseAddress, Binding.ApiKey)
            {
                Kind = WhisparrEntityKind.Performer,
                EntityId = entityId,
                Monitored = monitored,
            });

    public Task<WhisparrResponse> SetSceneMonitoredAsync(
        int sceneId,
        bool monitored,
        CancellationToken ct)
        => RecordActing(
            new ActingCall(nameof(SetSceneMonitoredAsync), Binding.BaseAddress, Binding.ApiKey)
            {
                Generation = Binding.Generation,
                EntityId = sceneId,
                Monitored = monitored,
            });

    public Task<WhisparrResponse> AddSceneExclusionAsync(
        string foreignId, CancellationToken ct)
        => RecordActing(
            new ActingCall(nameof(AddSceneExclusionAsync), Binding.BaseAddress, Binding.ApiKey)
            {
                ForeignId = foreignId,
            });

    public Task<WhisparrResponse> RemoveSceneExclusionAsync(
        int exclusionId, CancellationToken ct)
        => RecordActing(
            new ActingCall(nameof(RemoveSceneExclusionAsync), Binding.BaseAddress, Binding.ApiKey)
            {
                EntityId = exclusionId,
            });

    public Task<WhisparrResponse> AddSceneAsync(
        string foreignId,
        AddDefaults defaults,
        CancellationToken ct)
        => RecordActing(
            new ActingCall(nameof(AddSceneAsync), Binding.BaseAddress, Binding.ApiKey)
            {
                ForeignId = foreignId,
                Defaults = defaults,
            });

    public Task<WhisparrResponse> RegisterSiteAsync(
        string foreignId,
        AddDefaults defaults,
        CancellationToken ct)
        => RecordActing(
            new ActingCall(nameof(RegisterSiteAsync), Binding.BaseAddress, Binding.ApiKey)
            {
                Kind = WhisparrEntityKind.Studio,
                Generation = WhisparrGeneration.V2,
                ForeignId = foreignId,
                Defaults = defaults,
                Monitored = false,
            });

    public Task<WhisparrResponse> MoveSiteRootAsync(
        int siteId,
        string rootFolderPath,
        CancellationToken ct)
        => RecordActing(
            new ActingCall(nameof(MoveSiteRootAsync), Binding.BaseAddress, Binding.ApiKey)
            {
                Kind = WhisparrEntityKind.Studio,
                Generation = WhisparrGeneration.V2,
                EntityId = siteId,
                Folder = rootFolderPath,
            });

    public Task<WhisparrResponse> RefreshSiteCatalogueAsync(
        int siteId,
        CancellationToken ct)
        => RecordActing(
            new ActingCall(nameof(RefreshSiteCatalogueAsync), Binding.BaseAddress, Binding.ApiKey)
            {
                Kind = WhisparrEntityKind.Studio,
                Generation = WhisparrGeneration.V2,
                EntityId = siteId,
            });

    public Task<WhisparrResponse> RefreshCatalogueAsync(
        WhisparrEntityKind kind,
        int entityId,
        CancellationToken ct)
        => RecordActing(
            new ActingCall(nameof(RefreshCatalogueAsync), Binding.BaseAddress, Binding.ApiKey)
            {
                Kind = kind,
                EntityId = entityId,
            });

    public Task<WhisparrResponse> ReadHardlinkSettingAsync(CancellationToken ct)
        => RecordActing(new ActingCall(nameof(ReadHardlinkSettingAsync), Binding.BaseAddress, Binding.ApiKey));

    public Task<WhisparrResponse> ListImportableFilesAsync(
        string folder, CancellationToken ct)
        => RecordActing(
            new ActingCall(nameof(ListImportableFilesAsync), Binding.BaseAddress, Binding.ApiKey)
            {
                Folder = folder,
            });

    // The instance answers a reading with the row it was asked about, carrying the path it was
    // asked with. A canned body cannot carry a path a case never spells, so the row is composed
    // from the request and the quality alone is the case's to state.
    public Task<WhisparrResponse> ReadFileAsync(OwnedFilePlacement file, CancellationToken ct)
    {
        Acting.Add(
            new ActingCall(nameof(ReadFileAsync), Binding.BaseAddress, Binding.ApiKey)
            {
                Folder = file.Path,
                EntityId = file.EntityId,
            });
        Verbs.Add(nameof(ReadFileAsync));

        if (Unreachable.Contains(nameof(ReadFileAsync)))
        {
            throw new HttpRequestException("nothing answered");
        }

        var rows = new JsonArray(new JsonObject
        {
            ["path"] = file.Path,
            ["movieId"] = file.EntityId,
            ["quality"] = new JsonObject { ["quality"] = new JsonObject { ["id"] = QualityRead } },
            ["languages"] = new JsonArray(new JsonObject { ["id"] = LanguageRead }),
        });

        return Task.FromResult(Json(200, rows.ToJsonString()));
    }

    public Task<WhisparrResponse> AttachOwnedFilesAsync(
        JsonNode files, CancellationToken ct)
        => RecordActing(
            new ActingCall(nameof(AttachOwnedFilesAsync), Binding.BaseAddress, Binding.ApiKey) { Body = files });

    public Task<WhisparrResponse> ReadInstanceFolderAsync(
        string directory,
        CancellationToken ct)
        => RecordActing(
            new ActingCall(nameof(ReadInstanceFolderAsync), Binding.BaseAddress, Binding.ApiKey)
            {
                Generation = Binding.Generation,
                Folder = directory,
            });

    public Task<WhisparrResponse> SearchMonitoredAsync(
        WhisparrEntityKind kind,
        IReadOnlyList<int> entityIds,
        CancellationToken ct)
        => RecordActing(
            new ActingCall(nameof(SearchMonitoredAsync), Binding.BaseAddress, Binding.ApiKey)
            {
                Generation = Binding.Generation,
                Kind = kind,
                EntityIds = [.. entityIds],
            });

    public Task<WhisparrResponse> SearchSceneAsync(
        int sceneId, CancellationToken ct)
        => RecordActing(
            new ActingCall(nameof(SearchSceneAsync), Binding.BaseAddress, Binding.ApiKey) { EntityId = sceneId });

    // A library run reads the hard-link setting before it links anything, so a case whose subject
    // is the registration pass answers it once and leaves the linking skipped.
    public RecordingWhisparrCore AnsweringThatLinkingWouldCopy()
        => Answering(
            nameof(IWhisparrReflectOwnedActing.ReadHardlinkSettingAsync),
            MonitorHost.Json(200, """{"copyUsingHardlinks":false}"""));

    public RecordingWhisparrCore Answering(string verb, params WhisparrResponse[] answers)
    {
        NotificationAnswers[verb] = new Queue<WhisparrResponse>(answers);
        return this;
    }

    public static WhisparrResponse Json(int status, string body)
        => new(status, JsonContentType, body);

    public static RecordingWhisparrV3Client Reporting(string fixtureFileName)
        => new(new WhisparrResponse(200, JsonContentType, ProbeFixtures.Read(fixtureFileName)));

    public static RecordingWhisparrV2Client ReportingV2(string fixtureFileName)
        => new(new WhisparrResponse(200, JsonContentType, ProbeFixtures.Read(fixtureFileName)));

    private Task<WhisparrResponse> Record(string verb, int? id, JsonNode? body)
    {
        Notifications.Add(
            new NotificationCall(verb, Binding.BaseAddress, id, body?.DeepClone()));
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
        WhisparrEntityKind kind,
        string foreignId,
        CancellationToken ct)
    {
        SceneStatuses.Add(new SceneStatusCall(kind, foreignId, null));
        return Task.FromResult(Answer(nameof(ReadEntityPresenceAsync)));
    }

    public Task<WhisparrResponse> ReadSceneByRemoteIdAsync(
        string remoteId, CancellationToken ct)
    {
        SceneStatuses.Add(new SceneStatusCall(null, null, remoteId));
        return Task.FromResult(Answer(nameof(ReadSceneByRemoteIdAsync)));
    }

    public Task<IReadOnlySet<string>> ReduceExclusionsAsync(
        IReadOnlyCollection<string> providerSceneIds,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(providerSceneIds);
        ExclusionReads.Add([.. providerSceneIds]);
        return Task.FromResult<IReadOnlySet<string>>(
            providerSceneIds.Where(Excluded.Contains).ToHashSet(StringComparer.Ordinal));
    }

    public Task<IReadOnlySet<string>> ReduceHeldScenesAsync(
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

    public Task<WhisparrHeldCards> ReadHeldEntitiesAsync(
        WhisparrEntityKind kind,
        IReadOnlyList<string> foreignIds,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(foreignIds);
        EntityBatchReads.Add([.. foreignIds]);
        Verbs.Add(nameof(ReadHeldEntitiesAsync));

        if (Unreachable.Contains(nameof(ReadHeldEntitiesAsync)))
        {
            throw new HttpRequestException("nothing answered");
        }

        return Task.FromResult(HeldAmong(foreignIds, HeldEntityCards));
    }

    public Task<WhisparrHeldCards> ReadHeldSceneCardsAsync(
        IReadOnlyList<string> foreignIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(foreignIds);
        SceneBatchReads.Add([.. foreignIds]);
        Verbs.Add(nameof(ReadHeldSceneCardsAsync));

        if (Unreachable.Contains(nameof(ReadHeldSceneCardsAsync)))
        {
            throw new HttpRequestException("nothing answered");
        }

        return Task.FromResult(HeldAmong(foreignIds, HeldSceneCards));
    }

    private WhisparrHeldCards HeldAmong(
        IReadOnlyList<string> asked, Dictionary<string, WhisparrHeldCard> held)
    {
        var found = new Dictionary<string, WhisparrHeldCard>(StringComparer.Ordinal);
        foreach (var id in asked)
        {
            if (!BatchCannotSpeakFor.Contains(id) && held.TryGetValue(id, out var card))
            {
                found[id] = card;
            }
        }

        return new WhisparrHeldCards(
            found,
            new HashSet<string>(asked.Where(BatchCannotSpeakFor.Contains), StringComparer.Ordinal));
    }

    public Task<WhisparrResponse> TrackEntityAsync(
        WhisparrEntityKind kind,
        string foreignId,
        AddDefaults defaults,
        CancellationToken ct)
        => RecordActing(
            new ActingCall(nameof(TrackEntityAsync), Binding.BaseAddress, Binding.ApiKey)
            {
                Kind = kind,
                Generation = Binding.Generation,
                ForeignId = foreignId,
                Defaults = defaults,
            });

    public Task<WhisparrEntityCatalogue> ReadEntityCatalogueAsync(
        WhisparrEntityKind kind,
        string foreignId,
        CancellationToken ct)
    {
        EntityCatalogueReads.Add(foreignId);
        Verbs.Add(nameof(ReadEntityCatalogueAsync));

        if (Unreachable.Contains(nameof(ReadEntityCatalogueAsync)))
        {
            throw new HttpRequestException("nothing answered");
        }

        return Task.FromResult(
            EntityCatalogues.TryGetValue(foreignId, out var scenes)
                ? WhisparrEntityCatalogue.Listing(scenes)
                : WhisparrEntityCatalogue.Refused(WhisparrCatalogueRefusal.EntityNotHeld));
    }

    public Task<IReadOnlyDictionary<int, int>> ReduceSiteSceneRowsAsync(
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
        string foreignId, CancellationToken ct)
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


#pragma warning restore IDE0060, S1172

// The role list of WhisparrV3Instance, and nothing beyond it.
internal sealed class RecordingWhisparrV3Client(
    WhisparrResponse answer, WhisparrBinding? binding = null)
    : RecordingWhisparrCore(answer, binding),
        IWhisparrStudioActing,
        IWhisparrPerformerActing,
        IWhisparrMissingSceneActing,
        IWhisparrReflectOwnedActing,
        IWhisparrOwnedFileReading,
        IWhisparrSearchGrabbing,
        IWhisparrSceneSearchGrabbing,
        IWhisparrSceneStatusReading,
        IWhisparrSceneExclusionReading,
        IWhisparrEntityBatchReading,
        IWhisparrSceneBatchReading,
        IWhisparrEntityCatalogueReading,
        IWhisparrEntityTrackingActing,
        IWhisparrSceneMonitorActing,
        IWhisparrSceneExclusionActing,
        IWhisparrInstanceFilesystemReading,
        IOutOfBandSecretRegistration
{
    public OutOfBandSecretField Carry(string secret) => new V3HeaderSecretRegistration().Carry(secret);
}

// The role list of WhisparrV2Instance, and nothing beyond it.
internal sealed class RecordingWhisparrV2Client(
    WhisparrResponse answer, WhisparrBinding? binding = null)
    : RecordingWhisparrCore(answer, binding),
        IWhisparrStudioActing,
        IWhisparrSiteRegistrationActing,
        IWhisparrReflectOwnedActing,
        IWhisparrSearchGrabbing,
        IWhisparrEntityBatchReading,
        IWhisparrEntityCatalogueReading,
        IWhisparrEntityTrackingActing,
        IWhisparrSceneMonitorActing,
        IWhisparrSiteSceneReading,
        IWhisparrHeldSiteReading,
        IWhisparrInstanceFilesystemReading,
        IOutOfBandSecretRegistration
{
    public OutOfBandSecretField Carry(string secret)
        => new V2BasicAuthSecretRegistration().Carry(secret);
}

// Kind and ForeignId are set for an entity probe, RemoteId for a per-scene read.
internal sealed record SceneStatusCall(
    WhisparrEntityKind? Kind, string? ForeignId, string? RemoteId);
