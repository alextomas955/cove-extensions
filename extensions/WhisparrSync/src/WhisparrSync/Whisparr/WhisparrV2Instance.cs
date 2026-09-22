using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using WhisparrSync.Contracts;
using WhisparrSync.Monitoring;
using WhisparrSync.Scene;
using V2Api = Whisparr2.Net.Api;
using V2Client = Whisparr2.Net.Client;
using V2Model = Whisparr2.Net.Model;

namespace WhisparrSync.Whisparr;

// One Whisparr v2 instance, bound to the address and key it answers on. It declares the roles v2
// holds and no others, so a caller asking it for a role v2 does not hold has nothing to call: there
// is no performer member here, no per-scene search and no scene record of any kind.
//
// No member takes an address, a key or a generation: all three arrive on the binding, so a read and
// the write after it cannot name different instances.
//
// Every request is composed by the Whisparr 2 generated client through Whisparr2Gateway, except the
// two notification verbs, which are hand-composed and sent through the transport. Both generations
// serve the v3 route family, so the routes this client names are the ones the other names; the
// version in a path is not the generation.
internal sealed class WhisparrV2Instance(
    WhisparrBinding binding,
    WhisparrTransport transport,
    Whisparr2Gateway gateway,
    ISiteNumberPort siteNumbers,
    ILogger log)
    : IWhisparrClient,
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
        IWhisparrInstanceFilesystemReading
{
    // The one status composed rather than received. Whisparr v2 answers "do you hold this site" only
    // as a row inside its own list, so an absent row is reported in the spelling a caller already
    // classifies.
    private const int AssembledNotHeld = 404;

    // No request was sent, so there is no status to report. Zero is no status rather than a composed
    // one: every caller of an answer carrying it reads the refusal, which outranks the status.
    private const int NoInstanceStatus = 0;

    // How many of a page's site lookups are in flight at once. One request per card is the
    // cheap shape here, and an unbounded fan-out over a page would still be a burst this
    // product has no reason to send.
    private const int LookupLanes = 6;

    public Task<WhisparrResponse> ReadNotificationSchemaAsync(CancellationToken ct)
        => GeneratedReadAsync(api => api.Api<V2Api.INotificationApi>().ListNotificationSchemaAsync(ct));

    public Task<WhisparrResponse> ListNotificationsAsync(CancellationToken ct)
        => GeneratedReadAsync(api => api.Api<V2Api.INotificationApi>().ListNotificationAsync(ct));

    public Task<WhisparrResponse> ReadRootFoldersAsync(CancellationToken ct)
        => GeneratedReadAsync(api => api.Api<V2Api.IRootFolderApi>().ListRootFolderAsync(ct));

    public Task<WhisparrResponse> ReadQualityProfilesAsync(CancellationToken ct)
        => GeneratedReadAsync(api => api.Api<V2Api.IQualityProfileApi>().ListQualityProfileAsync(ct));

    // This generation names its own metadata entity on this route, and that entity carries the
    // identifier the two ingest channels agree on. Embedded on the same request, so a page costs one
    // request whatever it holds.
    public Task<WhisparrResponse> ReadHistoryAsync(int page, int pageSize, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);

        return GeneratedReadAsync(
            api => api.Api<V2Api.IHistoryApi>().GetHistoryAsync(
                page: page,
                pageSize: pageSize,
                sortKey: WhisparrTransport.NewestFirstSortKey,
                sortDirection: V2Model.SortDirection.Descending,
                includeEpisode: true,
                cancellationToken: ct));
    }

    public Task<WhisparrResponse> ReadCommandAsync(int commandId, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(commandId, 1);

        return GeneratedReadAsync(
            api => api.Api<V2Api.ICommandApi>().GetCommandByIdAsync(commandId, ct));
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
        => ReadHeldSeriesAsync(foreignId, ct);

    public Task<WhisparrResponse> AddMonitoredStudioAsync(
        string foreignId, MonitorScope scope, AddDefaults defaults, CancellationToken ct)
        => AddMonitoredSeriesAsync(foreignId, scope, defaults, ct);

    public Task<WhisparrResponse> SetStudioMonitoredAsync(
        int entityId, bool monitored, CancellationToken ct)
        => GeneratedActAsync(
            api => api.Api<V2Api.ISeriesEditorApi>().PutSeriesEditorAsync(
                V2BodyProjector.SetMonitored(entityId, monitored), ct));

    // Re-applied over the existing catalogue in one request, so nothing is read first. The route
    // answers an empty body with a server failure, so the body is what makes it work.
    public Task<WhisparrResponse> SetStudioScopeAsync(
        int entityId, MonitorScope scope, CancellationToken ct)
        => GeneratedActAsync(
            api => api.Api<V2Api.ISeasonPassApi>().CreateSeasonPassAsync(
                V2BodyProjector.SetScope(entityId, scope), ct));

    // The flag travels on a list of exactly one row id, the only shape this route takes.
    public Task<WhisparrResponse> SetSceneMonitoredAsync(
        int sceneId, bool monitored, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sceneId, 1);

        return GeneratedActAsync(
            api => api.Api<V2Api.IEpisodeApi>().PutEpisodeMonitorAsync(
                episodesMonitoredResource: V2BodyProjector.MonitorScene(sceneId, monitored),
                cancellationToken: ct));
    }

    // One request against the site's own row list. The members that would attach images, files or
    // the site resource are left off, so nothing arrives that this read drops.
    public async Task<IReadOnlyDictionary<int, int>> ReduceSiteSceneRowsAsync(
        int siteId, IReadOnlyCollection<int> sceneNumbers, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(siteId, 1);
        ArgumentNullException.ThrowIfNull(sceneNumbers);

        if (sceneNumbers.Count == 0)
        {
            return new Dictionary<int, int>();
        }

        var listed = await GeneratedReadAsync(
            api => api.Api<V2Api.IEpisodeApi>().ListEpisodeAsync(
                seriesId: siteId, cancellationToken: ct)).ConfigureAwait(false);

        // Raised rather than answered as an empty map. An empty map would report every scene it
        // asked about as one this site holds no row for, which is the opposite of the truth.
        if (WhisparrTransport.Refused(listed))
        {
            throw new HttpRequestException(
                "The site's own scene rows could not be read, so which of them the instance holds "
                    + "was not established.");
        }

        return V2ListProjector.RowsByNumber(listed.Body, sceneNumbers)
            ?? throw new HttpRequestException(
                "The answer to the site's own scene rows is not a list of rows at all.");
    }

    // One request against the instance's own site list, whatever the batch holds. Narrowing would
    // not help: this generation builds the whole set before filtering, so ?tvdbId= answers a single
    // row no faster than the unfiltered list answers all of them. The read is bounded by the
    // transport's library read timeout, because what it waits on is the instance's own work over its
    // holdings.
    public async Task<IReadOnlySet<int>> ReduceHeldSitesAsync(
        IReadOnlyCollection<int> siteNumbers, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(siteNumbers);

        if (siteNumbers.Count == 0)
        {
            return new HashSet<int>();
        }

        var listed = await GeneratedReadAsync(
            api => api.Api<V2Api.ISeriesApi>().ListSeriesAsync(cancellationToken: ct),
            WhisparrTransport.LibraryReadTimeout)
            .ConfigureAwait(false);

        // Raised rather than answered as an empty set. An empty set would report every site it asked
        // about as one the instance holds none of, and a caller acting on that registers the whole
        // library a second time.
        if (WhisparrTransport.Refused(listed))
        {
            throw new HttpRequestException(
                "The instance's own site list could not be read, so which of the sites it holds was "
                    + "not established.");
        }

        var rows = V2ListProjector.RowsByNumber(listed.Body, siteNumbers)
            ?? throw new HttpRequestException(
                "The answer to the instance's own site list is not a list of rows at all.");

        return rows.Keys.ToHashSet();
    }

    // Adds the entity so the instance tracks its catalogue and wants none of it. A site is this
    // generation's only unit of presence, and registering one is exactly that request, so the two
    // roles reach the same member rather than composing the same body twice.
    public Task<WhisparrResponse> TrackEntityAsync(
        WhisparrEntityKind kind, string foreignId, AddDefaults defaults, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(foreignId);
        ArgumentNullException.ThrowIfNull(defaults);

        return RegisterSiteAsync(foreignId, defaults, ct);
    }

    // The catalogue an entity's own scenes are read from, so the missing surface asks the metadata
    // source for no scene list at all. One request per entity, plus one more to resolve the number
    // this generation addresses a site by.
    //
    // This generation lists a scene only under its site, so the site's own number is resolved first.
    // A site it names no number for is one it holds no entry for.
    public async Task<WhisparrEntityCatalogue> ReadEntityCatalogueAsync(
        WhisparrEntityKind kind, string foreignId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(foreignId);

        // The lookup answers the site from the instance's own row where it holds that site, so the
        // id its scenes are listed under arrives with it. Asking the site list for that id instead
        // would cost a pass over every site the instance holds.
        var answered = await GeneratedReadAsync(
                api => api.Api<V2Api.ISeriesLookupApi>()
                    .ListSeriesLookupAsync(HeldCardProjector.SiteLookupTerm(foreignId), ct))
            .ConfigureAwait(false);

        if (WhisparrTransport.Refused(answered))
        {
            return WhisparrEntityCatalogue.Refused(WhisparrCatalogueRefusal.NotReached);
        }

        // A lookup naming no site, and a row carrying no id of the instance's own, are both the
        // instance holding no site under the identifier the library holds.
        if (V2ListProjector.LookupEntry(answered.Body) is not { } site
            || V2ListProjector.InstanceRowIdIn(site) is not (> 0 and var seriesId))
        {
            return WhisparrEntityCatalogue.Refused(WhisparrCatalogueRefusal.EntityNotHeld);
        }

        var listed = await GeneratedReadAsync(
                // Images are asked for: a card draws a cover, and this generation leaves the image
                // list off an episode row unless the read says otherwise.
                api => api.Api<V2Api.IEpisodeApi>().ListEpisodeAsync(
                    seriesId: seriesId, includeImages: true, cancellationToken: ct),
                WhisparrTransport.LibraryReadTimeout)
            .ConfigureAwait(false);

        if (WhisparrTransport.Refused(listed))
        {
            return WhisparrEntityCatalogue.Refused(WhisparrCatalogueRefusal.NotReached);
        }

        var siteName = site["title"] is JsonValue titled && titled.TryGetValue<string>(out var title)
            ? title
            : null;

        return CatalogueSceneProjector.V2Episodes(listed.Body, siteName) is { } scenes
            ? WhisparrEntityCatalogue.Listing(scenes)
            : WhisparrEntityCatalogue.Refused(WhisparrCatalogueRefusal.NotReached);
    }

    // The lookup answers one site by the identifier the library holds, and answers it from the
    // instance's own row where it holds that site: its search maps each result back through
    // FindByTvdbId, so the id, the monitored flag and the statistics are the instance's own rather
    // than the metadata source's. A site it does not hold maps the metadata result instead and
    // carries no id.
    //
    // One request per card, deliberately, against this generation's own site list which costs the
    // whole library: the list route recomputes statistics over every site before it answers, so a
    // page of forty cards is faster as forty lookups than as one list read. Bounded by the page and
    // by LookupLanes, so nothing here grows with the library.
    public async Task<WhisparrHeldCards> ReadHeldEntitiesAsync(
        WhisparrEntityKind kind, IReadOnlyList<string> foreignIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(foreignIds);

        if (foreignIds.Count == 0)
        {
            return WhisparrHeldCards.Empty;
        }

        List<string> asked =
            [.. foreignIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal)];
        if (asked.Count == 0)
        {
            return new WhisparrHeldCards(NothingHeld, WhisparrTransport.NothingUnanswered);
        }

        var held = new Dictionary<string, WhisparrHeldCard>(StringComparer.Ordinal);
        var unanswered = new HashSet<string>(StringComparer.Ordinal);
        var gate = new SemaphoreSlim(LookupLanes);

        var reads = asked.Select(async id =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var answered = await GeneratedReadAsync(
                        api => api.Api<V2Api.ISeriesLookupApi>()
                            .ListSeriesLookupAsync(HeldCardProjector.SiteLookupTerm(id), ct))
                    .ConfigureAwait(false);

                return (Id: id, Answered: answered);
            }
            finally
            {
                gate.Release();
            }
        });

        foreach (var (id, answered) in await Task.WhenAll(reads).ConfigureAwait(false))
        {
            if (WhisparrTransport.Refused(answered))
            {
                // This one card was not established. Reported apart from an absence: an absence
                // says the instance holds no such site, which is not what a dropped read said.
                unanswered.Add(id);
                continue;
            }

            var reading = HeldCardProjector.FromSiteLookup(answered.Body);
            if (!reading.Readable)
            {
                unanswered.Add(id);
            }
            else if (reading.Held is { } card)
            {
                held[id] = card;
            }
        }

        return new WhisparrHeldCards(held, unanswered);
    }

    private static IReadOnlyDictionary<string, WhisparrHeldCard> NothingHeld { get; }
        = new Dictionary<string, WhisparrHeldCard>(StringComparer.Ordinal);

    // Whether this instance holds the entity an identifier names. One read of one site, so it is
    // bounded as the per-item call it is rather than as a read of everything held.
    //
    // The site list would answer this too, and did: this generation builds its whole set before
    // filtering, so asking it for one site costs the same pass over every site as asking for all of
    // them. An instance holding 512 sites answers that in about 30 seconds and this lookup in under
    // one.
    private async Task<WhisparrResponse> ReadHeldSeriesAsync(string foreignId, CancellationToken ct)
    {
        // The lookup answers the site under whichever spelling the library holds, and answers it
        // from the instance's own row where it holds that site.
        var answered = await GeneratedReadAsync(
                api => api.Api<V2Api.ISeriesLookupApi>()
                    .ListSeriesLookupAsync(HeldCardProjector.SiteLookupTerm(foreignId), ct))
            .ConfigureAwait(false);

        if (WhisparrTransport.Refused(answered))
        {
            return answered;
        }

        if (V2ListProjector.LookupEntry(answered.Body) is not { } site)
        {
            // The lookup named no site at all, so nothing in this generation's namespace answers to
            // the identifier the library holds.
            return new WhisparrResponse(NoInstanceStatus, null, string.Empty)
            {
                Refusal = MonitorRefusalKind.NoIdentityInThisNamespace,
            };
        }

        // A row carrying no id of the instance's own was mapped from the metadata source, which is
        // the instance answering that it holds no site under that identifier.
        return V2ListProjector.InstanceRowIdIn(site) > 0
            ? new WhisparrResponse(answered.StatusCode, answered.ContentType, site.ToJsonString())
            : new WhisparrResponse(AssembledNotHeld, answered.ContentType, string.Empty);
    }

    // The add carries the number and the scope and nothing the metadata source said: the instance
    // resolves the site's own title and slug from that number.
    private async Task<WhisparrResponse> AddMonitoredSeriesAsync(
        string foreignId, MonitorScope scope, AddDefaults defaults, CancellationToken ct)
    {
        var numbered = await siteNumbers.ResolveSiteNumberAsync(binding, foreignId, ct)
            .ConfigureAwait(false);
        if (numbered.Number is not { } siteNumber)
        {
            return NoSiteNumber(numbered);
        }

        return await GeneratedActAsync(
            api => api.Api<V2Api.ISeriesApi>().CreateSeriesAsync(
                V2BodyProjector.AddStudio(siteNumber, scope, defaults),
                ct)).ConfigureAwait(false);
    }

    // The same request the monitoring add sends, with the presence-only body, so the catalogue the
    // instance then reads for the site is wanted by nothing.
    public async Task<WhisparrResponse> RegisterSiteAsync(
        string foreignId, AddDefaults defaults, CancellationToken ct)
    {
        var numbered = await siteNumbers.ResolveSiteNumberAsync(binding, foreignId, ct)
            .ConfigureAwait(false);
        if (numbered.Number is not { } siteNumber)
        {
            return NoSiteNumber(numbered);
        }

        return await GeneratedActAsync(
            api => api.Api<V2Api.ISeriesApi>().CreateSeriesAsync(
                V2BodyProjector.RegisterSite(siteNumber, defaults),
                ct)).ConfigureAwait(false);
    }

    // One read and one update, then the catalogue re-read that links the files. The update's body is
    // the resource the read answered rather than one composed here. No transfer parameter is named,
    // which is what leaves the files where they are.
    //
    // The re-read is not optional: the update alone rewrites where the instance records the site and
    // links nothing, so the site reports no file until the catalogue is re-read.
    public async Task<WhisparrResponse> MoveSiteRootAsync(
        int siteId, string rootFolderPath, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(siteId, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootFolderPath);

        var (read, resource) = await ReadSeriesResourceAsync(siteId, ct).ConfigureAwait(false);
        if (resource is null)
        {
            // A success status carrying nothing the model could be read from arrives here too, and
            // its own status classifies as accepted. Returning it unchanged would report a move the
            // caller counts as done while no update was sent and the site still sits where it was.
            return WhisparrTransport.Refused(read)
                ? read
                : read with { Refusal = MonitorRefusalKind.InstanceRefused };
        }

        var moved = await GeneratedActAsync(
            api => api.Api<V2Api.ISeriesApi>().UpdateSeriesAsync(
                siteId.ToString(CultureInfo.InvariantCulture),
                seriesResource: V2BodyProjector.MovedSiteRoot(resource, rootFolderPath),
                cancellationToken: ct)).ConfigureAwait(false);
        if (WhisparrTransport.Refused(moved))
        {
            return moved;
        }

        var linked = await RefreshSiteCatalogueAsync(siteId, ct).ConfigureAwait(false);

        return WhisparrTransport.Refused(linked) ? linked : moved;
    }

    public Task<WhisparrResponse> RefreshSiteCatalogueAsync(int siteId, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(siteId, 1);

        var (verb, payload) = WhisparrTransport.VerbAndPayload(V2BodyProjector.RefreshCatalogue(siteId));
        return GeneratedActAsync(
            api => api.Api<V2Api.CommandApi>().SendCommandAsync(verb, payload, ct));
    }

    // The typed resource beside the answer, because the update re-sends what the read answered.
    // Re-parsing the text answer into a member set named here would drop every member not named
    // here: the tags, the per-year flags, and whatever a later instance build adds.
    private async Task<(WhisparrResponse Answer, V2Model.SeriesResource? Held)>
        ReadSeriesResourceAsync(int siteId, CancellationToken ct)
    {
        V2Model.SeriesResource? held = null;
        var answered = await GeneratedReadAsync(
            async api =>
            {
                var read = await api.Api<V2Api.ISeriesApi>()
                    .GetSeriesByIdAsync(siteId, cancellationToken: ct).ConfigureAwait(false);
                read.TryOk(out held);
                return read;
            }).ConfigureAwait(false);

        return WhisparrTransport.Refused(answered) ? (answered, null) : (answered, held);
    }

    // Nothing was sent, so there is no status and the refusal is the whole of what a caller reads. A
    // source naming no site is the no-identity reading; a source that was not reached is not, and
    // reporting it as unidentified would send a reader to fix an identity that may be correct.
    private static WhisparrResponse NoSiteNumber(WhisparrSiteNumber numbered)
        => new(NoInstanceStatus, null, string.Empty)
        {
            Refusal = numbered.WasReached
                ? MonitorRefusalKind.NoIdentityInThisNamespace
                : MonitorRefusalKind.InstanceRefused,
        };

    // Declared here because this generation serves all three of these routes: measured against a
    // running 2.2.0.231 instance on 2026-09-22, each answering as v3's does, and its hard-link
    // document carried the member this product reads.
    public Task<WhisparrResponse> ReadHardlinkSettingAsync(CancellationToken ct)
        => GeneratedReadAsync(
            api => api.Api<V2Api.IMediaManagementConfigApi>().GetMediaManagementConfigAsync(ct));

    // The instance is asked to include what it already holds, so a file the library holds and the
    // instance has not attached is still answered for.
    public Task<WhisparrResponse> ListImportableFilesAsync(string folder, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        return GeneratedReadAsync(
            api => api.Api<V2Api.IManualImportApi>().ListManualImportAsync(
                folder: folder, filterExistingFiles: false, cancellationToken: ct));
    }

    public Task<WhisparrResponse> AttachOwnedFilesAsync(JsonNode files, CancellationToken ct)
    {
        var (verb, payload) = WhisparrTransport.VerbAndPayload(ReflectOwnedPlanner.Command(files));
        return GeneratedActAsync(
            api => api.Api<V2Api.CommandApi>().SendCommandAsync(verb, payload, ct));
    }

    public Task<WhisparrResponse> ReadInstanceFolderAsync(string directory, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        var asDirectory = WhisparrTransport.WithTrailingSeparator(directory);

        return GeneratedReadAsync(
            api => api.Api<V2Api.IFileSystemApi>().GetFileSystemAsync(
                path: asDirectory,
                includeFiles: true,
                allowFoldersWithoutTrailingSlashes: true,
                cancellationToken: ct));
    }

    // The one member of this instance that can make it acquire anything, and the only one whose
    // invocation is recorded on its own. Its verb class has no retry entry: a second search is a
    // second download.
    //
    // This generation's command names a single scalar id, so it is one command per entity and the
    // first answer that was not accepted is the one reported. Either way each entity is searched
    // once.
    public async Task<WhisparrResponse> SearchMonitoredAsync(
        WhisparrEntityKind kind, IReadOnlyList<int> entityIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entityIds);
        ArgumentOutOfRangeException.ThrowIfZero(entityIds.Count);
        WhisparrSyncLog.SearchIssued(log, kind);

        WhisparrResponse? answered = null;
        foreach (var entityId in entityIds)
        {
            answered = await GeneratedGrabCommandAsync(V2BodyProjector.SearchMonitored(entityId), ct)
                .ConfigureAwait(false);
            if (MonitoringProjector.Accepted(answered) != MonitorRefusalKind.None)
            {
                return answered;
            }
        }

        return answered!;
    }

    // The read class through the generated client, re-issued on the same failure and for the same
    // reason the other generation's is: a re-read creates nothing.
    private async Task<WhisparrResponse> GeneratedReadAsync<TResponse>(
        Func<Whisparr2Apis, Task<TResponse>> call, TimeSpan? budget = null)
        where TResponse : V2Client.IApiResponse
    {
        var target = TargetFor(budget ?? WhisparrTransport.RequestTimeout);
        var attempts = WhisparrRetryPolicy.AttemptsFor(WhisparrVerbClass.Read);
        for (var attempt = 1; attempt < attempts; attempt++)
        {
            try
            {
                return await GeneratedSendAsync(target, call).ConfigureAwait(false);
            }
            catch (Exception failure) when (failure is HttpRequestException or IOException)
            {
                // No whole answer arrived, which is the one failure a read may be re-issued after.
            }
        }

        return await GeneratedSendAsync(target, call).ConfigureAwait(false);
    }

    // Sent once: a request whose answer did not arrive is not the same as one that says nothing
    // happened.
    private Task<WhisparrResponse> GeneratedActAsync<TResponse>(
        Func<Whisparr2Apis, Task<TResponse>> call)
        where TResponse : V2Client.IApiResponse
        => GeneratedSendAsync(TargetFor(WhisparrTransport.RequestTimeout), call);

    // Sent once. Held apart from the acting sends that name the same route, so an attempt count added
    // here covers the grabbing class alone.
    private Task<WhisparrResponse> GeneratedGrabCommandAsync(JsonObject command, CancellationToken ct)
    {
        var (name, payload) = WhisparrTransport.VerbAndPayload(command);

        return GeneratedSendAsync(
            TargetFor(WhisparrTransport.RequestTimeout),
            api => api.Api<V2Api.CommandApi>().SendCommandAsync(name, payload, ct));
    }

    private async Task<WhisparrResponse> GeneratedSendAsync<TResponse>(
        Whisparr2Target target, Func<Whisparr2Apis, Task<TResponse>> call)
        where TResponse : V2Client.IApiResponse
    {
        try
        {
            using var apis = gateway.For(target);
            return Whisparr2Gateway.Answered(await call(apis).ConfigureAwait(false));
        }
        catch (AnswerTooLargeException beyond)
        {
            return transport.BeyondReadBound(target.BaseAddress, beyond);
        }
    }

    private Whisparr2Target TargetFor(TimeSpan budget)
        => new(binding.BaseAddress, binding.ApiKey, budget);
}
