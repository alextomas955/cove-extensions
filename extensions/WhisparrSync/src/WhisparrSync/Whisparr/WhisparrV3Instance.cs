using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using WhisparrSync.Contracts;
using WhisparrSync.Monitoring;
using WhisparrSync.Scene;
using V3Api = Whisparr3.Net.Api;
using V3Client = Whisparr3.Net.Client;

namespace WhisparrSync.Whisparr;

// One v3 instance, bound to the address and key it answers on. It declares the roles v3 holds and
// no others. No member takes an address, key or generation: all three arrive on the binding, so a
// read and the write after it cannot name different instances. Requests go through the Whisparr 3
// generated client, except the two notification verbs, hand-composed onto the route below and sent
// through the transport, so the same bounds apply.
internal sealed class WhisparrV3Instance(
    WhisparrBinding binding,
    WhisparrTransport transport,
    Whisparr3Gateway gateway,
    ILogger log)
    : IWhisparrClient,
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
    // This generation carries the secret in a headers field on the Webhook connection. The shape is
    // its own type, which has its own tests.
    public OutOfBandSecretField Carry(string secret) => new V3HeaderSecretRegistration().Carry(secret);

    // Relative, so they compose onto a base address carrying a URL base (a reverse-proxy subpath).
    // Both generations serve the v3 route family; the version in the path is not the generation.
    //
    // The self-composed routes are declared across the types the outbound seam is made of, and the
    // route invariant reads that named set rather than one type, so a constant declared on any of
    // them is covered.
    internal const string StudioPath = "api/v3/studio";
    internal const string PerformerPath = "api/v3/performer";
    internal const string ExclusionsPath = "api/v3/exclusions";

    private static readonly JsonSerializerOptions ExclusionRowShape = new(JsonSerializerDefaults.Web);

    // The two members of an exclusion row that are read. Declared with no others so a row costs one
    // small object that is dropped before the next is read. The id is the row's own, which the
    // removing route addresses; the foreign id is the scene's.
    private sealed record ExclusionRow(int Id, string? ForeignId);

    private static readonly JsonSerializerOptions HeldSceneRowShape = new(JsonSerializerDefaults.Web);

    // The two members of an answered entry that are read. Declared with no others so a chunk's
    // answer costs one small object per hit rather than the whole resource the instance sent.
    private sealed record HeldSceneRow(string? StashId, string? ForeignId);

    public Task<WhisparrResponse> ReadNotificationSchemaAsync(CancellationToken ct)
        => GeneratedReadAsync(api => api.Api<V3Api.INotificationApi>().GetNotificationSchemaAsync(ct));

    public Task<WhisparrResponse> ListNotificationsAsync(CancellationToken ct)
        => GeneratedReadAsync(api => api.Api<V3Api.INotificationApi>().GetNotificationAsync(ct));

    public Task<WhisparrResponse> ReadRootFoldersAsync(CancellationToken ct)
        => GeneratedReadAsync(api => api.Api<V3Api.IRootFolderApi>().GetRootfolderAsync(ct));

    public Task<WhisparrResponse> ReadQualityProfilesAsync(CancellationToken ct)
        => GeneratedReadAsync(api => api.Api<V3Api.IQualityProfileApi>().GetQualityprofileAsync(ct));

    public Task<WhisparrResponse> ReadHistoryAsync(int page, int pageSize, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);

        return GeneratedReadAsync(
            api => api.Api<V3Api.IHistoryApi>().GetHistoryAsync(
                page: page,
                pageSize: pageSize,
                sortKey: WhisparrTransport.NewestFirstSortKey,
                sortDirection: Whisparr3.Net.Model.SortDirection.Descending,
                includeMovie: true,
                cancellationToken: ct));
    }

    public Task<WhisparrResponse> ReadCommandAsync(int commandId, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(commandId, 1);

        return GeneratedReadAsync(
            api => api.Api<V3Api.ICommandApi>().GetCommandByIdAsync(commandId, ct));
    }

    public Task<WhisparrResponse> CreateNotificationAsync(JsonNode body, CancellationToken ct)
        => transport.ConfigureAsync(
            binding.BaseAddress,
            binding.ApiKey,
            HttpMethod.Post,
            WhisparrTransport.NotificationPath,
            body,
            ct);

    public Task<WhisparrResponse> UpdateNotificationAsync(int id, JsonNode body, CancellationToken ct)
        => transport.ConfigureAsync(
            binding.BaseAddress,
            binding.ApiKey,
            HttpMethod.Put,
            string.Create(CultureInfo.InvariantCulture, $"{WhisparrTransport.NotificationPath}/{id}"),
            body,
            ct);

    public Task<WhisparrResponse> ReadStudioAsync(string foreignId, CancellationToken ct)
        => GeneratedReadAsync(
            api => api.Api<V3Api.IStudioApi>().GetStudioByIdAsync(Named(foreignId), ct));

    public Task<WhisparrResponse> AddMonitoredStudioAsync(
        string foreignId, MonitorScope scope, AddDefaults defaults, CancellationToken ct)
        => GeneratedActAsync(
            api => api.Api<V3Api.IStudioApi>().PostStudioAsync(
                V3BodyProjector.AddStudio(foreignId, scope, defaults, DateTimeOffset.UtcNow), ct));

    public Task<WhisparrResponse> SetStudioMonitoredAsync(
        int entityId, bool monitored, CancellationToken ct)
        => GeneratedActAsync(
            api => api.Api<V3Api.IStudioEditorApi>().PutStudioEditorAsync(
                V3BodyProjector.SetStudioMonitored(entityId, monitored), ct));

    // A field-scoped patch carrying only what changes: a whole-resource replace would write back a
    // resource read a moment earlier, dropping whatever the read did not answer with.
    public Task<WhisparrResponse> SetSceneMonitoredAsync(
        int sceneId, bool monitored, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sceneId, 1);

        return GeneratedActAsync(
            api => api.Api<V3Api.IMovieApi>().PatchMovieByIdAsync(
                sceneId, V3BodyProjector.SceneMonitorPatch(monitored), ct));
    }

    // Adds the entity so the instance tracks its catalogue and wants none of it: the add carries the
    // flag governing whether an arrival is wanted, set so that nothing is.
    public Task<WhisparrResponse> TrackEntityAsync(
        WhisparrEntityKind kind, string foreignId, AddDefaults defaults, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(foreignId);
        ArgumentNullException.ThrowIfNull(defaults);

        var path = kind == WhisparrEntityKind.Studio ? StudioPath : PerformerPath;
        return transport.SendAsync(
            binding.BaseAddress,
            binding.ApiKey,
            HttpMethod.Post,
            path,
            V3BodyProjector.TrackEntity(foreignId, defaults),
            ct);
    }

    // The catalogue an entity's own scenes are read from, so the missing surface asks the metadata
    // source for no scene list at all. One request per entity.
    //
    // A works route answers only for an entity the instance holds, so a not-found there is the
    // entity's absence and not an empty catalogue.
    public async Task<WhisparrEntityCatalogue> ReadEntityCatalogueAsync(
        WhisparrEntityKind kind, string foreignId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(foreignId);

        var listed = kind == WhisparrEntityKind.Studio
            ? await GeneratedReadAsync(
                    api => api.Api<V3Api.IStudioApi>()
                        .GetStudioByStudioForeignIdWorksAsync(Named(foreignId), ct))
                .ConfigureAwait(false)
            : await GeneratedReadAsync(
                    api => api.Api<V3Api.IPerformerApi>()
                        .GetPerformerByPerformerForeignIdWorksAsync(Named(foreignId), ct))
                .ConfigureAwait(false);

        if (listed.StatusCode == 404)
        {
            return WhisparrEntityCatalogue.Refused(WhisparrCatalogueRefusal.EntityNotHeld);
        }

        if (WhisparrTransport.Refused(listed))
        {
            return WhisparrEntityCatalogue.Refused(WhisparrCatalogueRefusal.NotReached);
        }

        return CatalogueSceneProjector.V3Works(listed.Body) is { } scenes
            ? WhisparrEntityCatalogue.Listing(scenes)
            : WhisparrEntityCatalogue.Refused(WhisparrCatalogueRefusal.NotReached);
    }

    // One request for a page of entity cards, whatever the page holds. The alternative is a read per
    // card, which is what this replaces: a page of forty studios cost forty requests against a third
    // party.
    //
    // One list route per kind, and neither answers for the other, so the two reads are issued apart
    // rather than through one call the generated client types differently per arm.
    public async Task<WhisparrHeldCards> ReadHeldEntitiesAsync(
        WhisparrEntityKind kind, IReadOnlyList<string> foreignIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(foreignIds);

        if (foreignIds.Count == 0)
        {
            return WhisparrHeldCards.Empty;
        }

        List<string> wanted = [.. foreignIds];
        var answered = kind == WhisparrEntityKind.Studio
            ? await GeneratedReadAsync(
                    api => api.Api<V3Api.IStudioApi>().PostStudioListAsync(wanted, ct))
                .ConfigureAwait(false)
            : await GeneratedReadAsync(
                    api => api.Api<V3Api.IPerformerApi>().PostPerformerListAsync(wanted, ct))
                .ConfigureAwait(false);

        return new WhisparrHeldCards(HeldIn(answered, foreignIds), WhisparrTransport.NothingUnanswered);
    }

    // One request for a page of scene cards. Declared by the generation that addresses a scene
    // without its site; the other reads a scene only as a row under one.
    public async Task<WhisparrHeldCards> ReadHeldSceneCardsAsync(
        IReadOnlyList<string> foreignIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(foreignIds);

        if (foreignIds.Count == 0)
        {
            return WhisparrHeldCards.Empty;
        }

        List<string> wanted = [.. foreignIds];
        var answered = await GeneratedReadAsync(
                api => api.Api<V3Api.IMovieApi>().PostMovieListAsync(wanted, ct))
            .ConfigureAwait(false);

        return new WhisparrHeldCards(HeldIn(answered, foreignIds), WhisparrTransport.NothingUnanswered);
    }

    // Raised rather than reduced to an empty set, for the reason ReduceHeldScenesAsync gives.
    private static IReadOnlyDictionary<string, WhisparrHeldCard> HeldIn(
        WhisparrResponse answered, IReadOnlyCollection<string> asked)
    {
        if (WhisparrTransport.Refused(answered))
        {
            throw new HttpRequestException(
                "The instance did not answer what it holds for the cards asked about.");
        }

        return HeldCardProjector.ByForeignId(answered.Body, asked)
            ?? throw new HttpRequestException(
                "The instance's answer could not be read as the entries it holds.");
    }

    public Task<WhisparrResponse> AddSceneExclusionAsync(string foreignId, CancellationToken ct)
        => GeneratedActAsync(
            api => api.Api<V3Api.IImportListExclusionApi>().PostExclusionsAsync(
                V3BodyProjector.SceneExclusion(Named(foreignId)), ct));

    public Task<WhisparrResponse> RemoveSceneExclusionAsync(int exclusionId, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(exclusionId, 1);

        return GeneratedActAsync(
            api => api.Api<V3Api.IImportListExclusionApi>().DeleteExclusionsByIdAsync(exclusionId, ct));
    }

    // Gates only what a later catalogue read adds, which is what this generation's date field
    // expresses.
    //
    // Read then replaced, because the editor resource declares no add-time date gate: a scope sent
    // there is accepted and applies nothing. The read is idempotent and the replace is sent once.
    //
    // Composed here rather than by the generated client, which carries a fixed member set: the
    // replacement is the answer itself with two members changed, and a member the generated
    // resource does not declare would be dropped on the way back out.
    public async Task<WhisparrResponse> SetStudioScopeAsync(
        int entityId, MonitorScope scope, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(entityId, 1);

        var path = string.Create(CultureInfo.InvariantCulture, $"{StudioPath}/{entityId}");
        var held = await ReadAsync(path, ct).ConfigureAwait(false);
        if (MonitoringProjector.AsObject(held.Body) is not { } studio)
        {
            return held;
        }

        return await ActAsync(
            HttpMethod.Put, path, V3BodyProjector.WithScope(studio, scope, DateTimeOffset.UtcNow), ct)
            .ConfigureAwait(false);
    }

    public Task<WhisparrResponse> ReadPerformerAsync(string foreignId, CancellationToken ct)
        => GeneratedReadAsync(
            api => api.Api<V3Api.IPerformerApi>().GetPerformerByIdAsync(Named(foreignId), ct));

    public Task<WhisparrResponse> AddMonitoredPerformerAsync(
        string foreignId, AddDefaults defaults, CancellationToken ct)
        => GeneratedActAsync(
            api => api.Api<V3Api.IPerformerApi>().PostPerformerAsync(
                V3BodyProjector.AddPerformer(foreignId, defaults), ct));

    public Task<WhisparrResponse> SetPerformerMonitoredAsync(
        int entityId, bool monitored, CancellationToken ct)
        => GeneratedActAsync(
            api => api.Api<V3Api.IPerformerEditorApi>().PutPerformerEditorAsync(
                V3BodyProjector.SetPerformerMonitored(entityId, monitored), ct));

    public Task<WhisparrResponse> AddSceneAsync(
        string foreignId, AddDefaults defaults, CancellationToken ct)
        => GeneratedActAsync(
            api => api.Api<V3Api.IMovieApi>().PostMovieAsync(
                V3BodyProjector.AddScene(foreignId, defaults), ct));

    public Task<WhisparrResponse> RefreshCatalogueAsync(
        WhisparrEntityKind kind, int entityId, CancellationToken ct)
        => GeneratedCommandAsync(V3BodyProjector.RefreshCatalogue(kind, entityId), ct);

    // The operation is chosen here from the entity kind, so no caller can aim the stored credential
    // at a route of its own naming.
    public Task<WhisparrResponse> ReadEntityPresenceAsync(
        WhisparrEntityKind kind, string foreignId, CancellationToken ct)
        => kind switch
        {
            WhisparrEntityKind.Studio => GeneratedReadAsync(
                api => api.Api<V3Api.IStudioApi>().GetStudioByIdAsync(Named(foreignId), ct)),
            WhisparrEntityKind.Performer => GeneratedReadAsync(
                api => api.Api<V3Api.IPerformerApi>().GetPerformerByIdAsync(Named(foreignId), ct)),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

    // One key, single-valued. Repeating it answers only the first value's row, comma-joining
    // answers nothing, and the two plural spellings v3 accepts are ignored and answer with the whole
    // catalogue.
    public Task<WhisparrResponse> ReadSceneByRemoteIdAsync(string remoteId, CancellationToken ct)
        => GeneratedReadAsync(
            api => api.Api<V3Api.IMovieApi>().GetMovieAsync(
                stashId: Named(remoteId), cancellationToken: ct));

    // Each row is reduced to one question, so what this holds is the caller's own set and never the
    // instance's. There is no row cap: a cap would stop part way and report the rest as not
    // excluded, with nothing saying so.
    public async Task<IReadOnlySet<string>> ReduceExclusionsAsync(
        IReadOnlyCollection<string> providerSceneIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(providerSceneIds);

        // Keyed without regard to case: an identifier is a hexadecimal uuid and each side stored its
        // own spelling. The caller's spelling is what is answered back.
        var asked = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in providerSceneIds)
        {
            if (!string.IsNullOrWhiteSpace(id))
            {
                asked[id] = id;
            }
        }

        var excluded = new HashSet<string>(StringComparer.Ordinal);
        if (asked.Count == 0)
        {
            return excluded;
        }

        // An answer that did not arrive, or one this could not read, excludes nothing: reporting a
        // scene as excluded on a failed read would remove it from the surface with nothing saying
        // why. The walk's own outcome is left unread here for that reason.
        await OverExclusionRowsAsync(
            row =>
            {
                if (row.ForeignId is { Length: > 0 } named
                    && asked.TryGetValue(named, out var asAsked))
                {
                    excluded.Add(asAsked);
                }

                return true;
            },
            ct).ConfigureAwait(false);

        return excluded;
    }

    // Each answered row is reduced to one question, so what this holds is the caller's own set and
    // never the instance's. There is no row cap, for the reason the exclusion reduce has none.
    //
    // The body is a bare JSON array of identifier strings. An object naming the ids as a member is
    // answered 400, measured against whisparr:v3-3.3.8-release.1097.
    public async Task<IReadOnlySet<string>> ReduceHeldScenesAsync(
        IReadOnlyCollection<string> foreignIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(foreignIds);

        // Keyed without regard to case: an identifier is a hexadecimal uuid and each side stored its
        // own spelling. The caller's spelling is what is answered back.
        var asked = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in foreignIds)
        {
            if (!string.IsNullOrWhiteSpace(id))
            {
                asked[id] = id;
            }
        }

        var held = new HashSet<string>(StringComparer.Ordinal);
        if (asked.Count == 0)
        {
            return held;
        }

        List<string> wanted = [.. asked.Values];
        var answered = await GeneratedReadAsync(
                api => api.Api<V3Api.IMovieApi>().PostMovieListAsync(wanted, ct))
            .ConfigureAwait(false);

        // Raised rather than reduced to an empty set: a caller comparing its library against an
        // empty set would report every scene it asked about as one the instance does not hold.
        if (answered.StatusCode is < 200 or > 299
            || answered.Refusal is not MonitorRefusalKind.None)
        {
            throw new HttpRequestException(
                "The instance did not answer which of the asked-about scenes it holds.");
        }

        List<HeldSceneRow?>? rows;
        try
        {
            rows = JsonSerializer.Deserialize<List<HeldSceneRow?>>(answered.Body, HeldSceneRowShape);
        }
        catch (JsonException failure)
        {
            throw new HttpRequestException(
                "The instance's answer could not be read as the entries it holds.", failure);
        }

        foreach (var row in rows ?? [])
        {
            // The identifier is read off the row's stash id, falling back to its foreign id: both
            // carry the same uuid and which one an instance fills in varies.
            var named = row?.StashId is { Length: > 0 } stashed ? stashed : row?.ForeignId;
            if (named is { Length: > 0 } spelled && asked.TryGetValue(spelled, out var asAsked))
            {
                held.Add(asAsked);
            }
        }

        return held;
    }

    public async Task<SceneExclusionLookup> FindSceneExclusionAsync(
        string foreignId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(foreignId);

        int? named = null;
        var read = await OverExclusionRowsAsync(
            row =>
            {
                // Compared without regard to case, as the reduce above keys. A non-positive
                // identifier is no address the removing route could take, so such a row is skipped.
                if (row.Id >= 1
                    && string.Equals(row.ForeignId, foreignId, StringComparison.OrdinalIgnoreCase))
                {
                    named = row.Id;
                    return false;
                }

                return true;
            },
            ct).ConfigureAwait(false);

        if (!read)
        {
            return SceneExclusionLookup.DidNotComplete;
        }

        return named is { } exclusionId
            ? SceneExclusionLookup.At(exclusionId)
            : SceneExclusionLookup.NamesNoExclusion;
    }

    // Reads the exclusion list row by row until visit answers false, and answers whether a whole
    // answer arrived and could be read.
    //
    // No parameter narrows this route: a filter key and a bare foreign id are both ignored and
    // answer the whole list under a success, and a foreign id as a further segment is a not-found.
    // The answer is read as it arrives and each row is dropped before the next, so nothing here
    // grows with what the instance holds. Sent once, since nothing is retained between rows.
    private async Task<bool> OverExclusionRowsAsync(Func<ExclusionRow, bool> visit, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get, WhisparrTransport.RequestUri(binding.BaseAddress, ExclusionsPath));
        request.Headers.Add(WhisparrTransport.ApiKeyHeader, binding.ApiKey);

        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
        attempt.CancelAfter(transport.AttemptBudget);

        try
        {
            using var response = await transport
                .OpenAsync(request, attempt.Token)
                .ConfigureAwait(false);

            if (!WhisparrTransport.IsSuccess((int)response.StatusCode))
            {
                return false;
            }

            var stream = await response.Content.ReadAsStreamAsync(attempt.Token).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                var rows = JsonSerializer.DeserializeAsyncEnumerable<ExclusionRow>(
                    stream, ExclusionRowShape, attempt.Token);

                await foreach (var row in rows.ConfigureAwait(false))
                {
                    if (row is not null && !visit(row))
                    {
                        break;
                    }
                }
            }

            return true;
        }
        catch (Exception failure)
            when (failure is HttpRequestException or IOException or JsonException)
        {
            return false;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
    }

    public Task<WhisparrResponse> ReadHardlinkSettingAsync(CancellationToken ct)
        => GeneratedReadAsync(
            api => api.Api<V3Api.IMediaManagementConfigApi>().GetConfigMediamanagementAsync(ct));

    // The instance is asked to include what it already holds, so a file the library holds and the
    // instance has not attached is still answered for.
    //
    // Sent under the library budget rather than the ordinary one: the instance walks and parses
    // every entry in the folder before it answers, so a folder holding a few hundred files takes
    // longer than the ordinary timeout allows. Refused for time, the folder is counted as refused
    // and its files are silently left unlinked.
    public Task<WhisparrResponse> ListImportableFilesAsync(string folder, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        return GeneratedReadAsync(
            api => api.Api<V3Api.IManualImportApi>().GetManualimportAsync(
                folder: folder, filterExistingFiles: false, cancellationToken: ct),
            WhisparrTransport.LibraryReadTimeout);
    }

    // The instance's own reading of the file itself. Sent under the library budget: it opens the
    // file and reads it, which on a large one runs past the ordinary budget.
    public Task<WhisparrResponse> ReadFileAsync(OwnedFilePlacement file, CancellationToken ct)
        => GeneratedReadAsync(
            api => api.Api<V3Api.IManualImportApi>().PostManualimportAsync(
                V3BodyProjector.ReadOwnedFile(file), ct),
            WhisparrTransport.LibraryReadTimeout);

    public Task<WhisparrResponse> AttachOwnedFilesAsync(JsonNode files, CancellationToken ct)
        => GeneratedCommandAsync(ReflectOwnedPlanner.Command(files), ct);

    public Task<WhisparrResponse> ReadInstanceFolderAsync(string directory, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        var asDirectory = WhisparrTransport.WithTrailingSeparator(directory);

        return GeneratedReadAsync(
            api => api.Api<V3Api.IFileSystemApi>().GetFilesystemAsync(
                path: asDirectory,
                includeFiles: true,
                allowFoldersWithoutTrailingSlashes: true,
                cancellationToken: ct));
    }

    // The one member of this instance that can make it acquire anything, apart from the per-scene
    // search below, and the only ones whose invocation is recorded on its own. Their verb class has
    // no retry entry: a second search is a second download.
    //
    // This generation's command names an id array and carries every id in one, so each entity is
    // searched once.
    public Task<WhisparrResponse> SearchMonitoredAsync(
        WhisparrEntityKind kind, IReadOnlyList<int> entityIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entityIds);
        ArgumentOutOfRangeException.ThrowIfZero(entityIds.Count);
        WhisparrSyncLog.SearchIssued(log, kind);

        return GeneratedCommandAsync(V3BodyProjector.SearchAllMonitored(kind, entityIds), ct);
    }

    // Recorded as the entity search is, and given no arguments: the scene, the instance and the key
    // are caller-supplied or credentials, and a log sink is durable and readable. Sent once.
    public Task<WhisparrResponse> SearchSceneAsync(int sceneId, CancellationToken ct)
    {
        WhisparrSyncLog.SceneSearchIssued(log);

        return GeneratedCommandAsync(V3BodyProjector.SearchScene(sceneId), ct);
    }

    // The identifier comes from a stored identity row rather than from a caller. The generated client
    // escapes it as one path segment, so a value carrying a separator names no other route.
    private static string Named(string foreignId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(foreignId);
        return foreignId;
    }

    // The read class through the generated client. Re-issued on the same failure and for the same
    // reason the hand-composed read is: a re-read creates nothing.
    private async Task<WhisparrResponse> GeneratedReadAsync<TResponse>(
        Func<Whisparr3Apis, Task<TResponse>> call, TimeSpan? budget = null)
        where TResponse : V3Client.IApiResponse
    {
        var attempts = WhisparrRetryPolicy.AttemptsFor(WhisparrVerbClass.Read);
        for (var attempt = 1; attempt < attempts; attempt++)
        {
            try
            {
                return await GeneratedSendAsync(call, budget).ConfigureAwait(false);
            }
            catch (Exception failure) when (failure is HttpRequestException or IOException)
            {
                // No whole answer arrived, which is the one failure a read may be re-issued after.
            }
        }

        return await GeneratedSendAsync(call, budget).ConfigureAwait(false);
    }

    // Sent once, for the reason the hand-composed acting send is: a request whose answer did not
    // arrive is not the same as one that says nothing happened.
    private Task<WhisparrResponse> GeneratedActAsync<TResponse>(
        Func<Whisparr3Apis, Task<TResponse>> call)
        where TResponse : V3Client.IApiResponse
        => GeneratedSendAsync(call);

    // Every instance-side action this generation takes is issued through the one command route. Sent
    // once. The acting class and the grabbing class both reach the route through this send, so an
    // attempt count added here would cover the class that downloads.
    private Task<WhisparrResponse> GeneratedCommandAsync(JsonObject command, CancellationToken ct)
    {
        var (name, payload) = WhisparrTransport.VerbAndPayload(command);

        return GeneratedSendAsync(
            api => api.Api<V3Api.CommandApi>().SendCommandAsync(name, payload, ct));
    }

    private async Task<WhisparrResponse> GeneratedSendAsync<TResponse>(
        Func<Whisparr3Apis, Task<TResponse>> call, TimeSpan? budget = null)
        where TResponse : V3Client.IApiResponse
    {
        var target = new Whisparr3Target(
            binding.BaseAddress, binding.ApiKey, budget ?? WhisparrTransport.RequestTimeout);
        try
        {
            using var apis = gateway.For(target);
            return Whisparr3Gateway.Answered(await call(apis).ConfigureAwait(false));
        }
        catch (AnswerTooLargeException beyond)
        {
            return transport.BeyondReadBound(binding.BaseAddress, beyond);
        }
    }

    // Re-issuing a read re-reads and can create nothing, so the read class is the only one that gets
    // more than one attempt. The last attempt is the plain send, so its failure propagates rather
    // than being counted again.
    private async Task<WhisparrResponse> ReadAsync(string path, CancellationToken ct)
    {
        var attempts = WhisparrRetryPolicy.AttemptsFor(WhisparrVerbClass.Read);
        for (var attempt = 1; attempt < attempts; attempt++)
        {
            if (await transport
                .TrySendAsync(binding.BaseAddress, binding.ApiKey, HttpMethod.Get, path, null, ct)
                .ConfigureAwait(false) is { } answered)
            {
                return answered;
            }
        }

        return await transport
            .SendAsync(binding.BaseAddress, binding.ApiKey, HttpMethod.Get, path, null, ct)
            .ConfigureAwait(false);
    }

    // Sent once for the same reason, and named apart from a configure because the retry policy is
    // keyed on the class of work: an attempt count added for one class must not cover the other.
    private Task<WhisparrResponse> ActAsync(
        HttpMethod method, string path, JsonNode body, CancellationToken ct)
        => transport.SentOnceAsync(binding.BaseAddress, binding.ApiKey, method, path, body, ct);
}
