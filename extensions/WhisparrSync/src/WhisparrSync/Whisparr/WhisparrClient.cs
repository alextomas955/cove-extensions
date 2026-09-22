using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using WhisparrSync.Contracts;
using WhisparrSync.Monitoring;
using WhisparrSync.Scene;
using V2Api = Whisparr2.Net.Api;
using V2Client = Whisparr2.Net.Client;
using V2Model = Whisparr2.Net.Model;
using V3Api = Whisparr3.Net.Api;
using V3Client = Whisparr3.Net.Client;
using V3Model = Whisparr3.Net.Model;

namespace WhisparrSync.Whisparr;

// The acting roles are implemented here rather than on a type of their own: they are one outbound
// surface and they share the routes declared below.
//
// Each generation's requests are composed by its own generated client, v3 through Whisparr3Gateway
// and v2 through Whisparr2Gateway. The routes declared here are hand-composed and sent through the
// transport, so the same bounds apply to them.
internal sealed class WhisparrClient(
    WhisparrTransport transport,
    Whisparr3Gateway v3Gateway,
    Whisparr2Gateway v2Gateway,
    ISiteNumberPort siteNumbers,
    ILogger log)
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
        IWhisparrEntityBatchReading,
        IWhisparrSceneBatchReading,
        IWhisparrEntityCatalogueReading,
        IWhisparrEntityTrackingActing,
        IWhisparrSceneMonitorActing,
        IWhisparrSceneExclusionActing,
        IWhisparrSiteSceneReading,
        IWhisparrHeldSiteReading,
        IWhisparrInstanceFilesystemReading
{
    // Relative, so they compose onto a base address carrying a URL base (a reverse-proxy subpath).
    // Both generations serve the v3 route family; the version in the path is not the generation.
    //
    // The self-composed routes are declared across the types the outbound seam is made of, and the
    // route invariant reads that named set rather than one type, so a constant declared on any of
    // them is covered.
    internal const string StudioPath = "api/v3/studio";
    internal const string PerformerPath = "api/v3/performer";
    internal const string ExclusionsPath = "api/v3/exclusions";

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

    private static readonly JsonSerializerOptions ExclusionRowShape = new(JsonSerializerDefaults.Web);

    // The two members of an exclusion row that are read. Declared with no others so a row costs one
    // small object that is dropped before the next is read. The id is the row's own, which the
    // removing route addresses; the foreign id is the scene's.
    private sealed record ExclusionRow(int Id, string? ForeignId);

    private static readonly JsonSerializerOptions HeldSceneRowShape = new(JsonSerializerDefaults.Web);

    // The two members of an answered entry that are read. Declared with no others so a chunk's
    // answer costs one small object per hit rather than the whole resource the instance sent.
    private sealed record HeldSceneRow(string? StashId, string? ForeignId);

    public async Task<WhisparrResponse> ReadStatusAsync(
        Uri baseAddress,
        string apiKey,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        if (!WhisparrTransport.IsAddressable(baseAddress))
        {
            throw new ArgumentException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"A Whisparr address must be an absolute http or https URL; the scheme given was '{baseAddress.Scheme}'."),
                nameof(baseAddress));
        }

        return await GeneratedReadAsync(
            baseAddress, apiKey, api => api.Api<V3Api.ISystemApi>().GetSystemStatusAsync(ct))
            .ConfigureAwait(false);
    }

    public Task<WhisparrResponse> ReadNotificationSchemaAsync(
        Uri baseAddress, string apiKey, CancellationToken ct)
        => GeneratedReadAsync(
            baseAddress, apiKey, api => api.Api<V3Api.INotificationApi>().ListNotificationSchemaAsync(ct));

    public Task<WhisparrResponse> ListNotificationsAsync(
        Uri baseAddress, string apiKey, CancellationToken ct)
        => GeneratedReadAsync(
            baseAddress, apiKey, api => api.Api<V3Api.INotificationApi>().ListNotificationAsync(ct));

    public Task<WhisparrResponse> ReadRootFoldersAsync(
        Uri baseAddress, string apiKey, CancellationToken ct)
        => GeneratedReadAsync(
            baseAddress, apiKey, api => api.Api<V3Api.IRootFolderApi>().ListRootFolderAsync(ct));

    public Task<WhisparrResponse> ReadQualityProfilesAsync(
        Uri baseAddress, string apiKey, CancellationToken ct)
        => GeneratedReadAsync(
            baseAddress, apiKey, api => api.Api<V3Api.IQualityProfileApi>().ListQualityProfileAsync(ct));

    public Task<WhisparrResponse> ReadHistoryAsync(
        Uri baseAddress,
        string apiKey,
        WhisparrGeneration generation,
        int page,
        int pageSize,
        CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);

        return generation switch
        {
            WhisparrGeneration.V3 => GeneratedReadAsync(
                baseAddress,
                apiKey,
                api => api.Api<V3Api.IHistoryApi>().GetHistoryAsync(
                    page: page,
                    pageSize: pageSize,
                    sortKey: WhisparrTransport.NewestFirstSortKey,
                    sortDirection: V3Model.SortDirection.Descending,
                    includeMovie: true,
                    cancellationToken: ct)),

            // v2 and v3 name their own metadata entity on this route, and that entity carries the
            // identifier the two ingest channels agree on. Embedded on the same request, so a page
            // costs one request whatever it holds.
            WhisparrGeneration.V2 => GeneratedV2ReadAsync(
                baseAddress,
                apiKey,
                api => api.Api<V2Api.IHistoryApi>().GetHistoryAsync(
                    page: page,
                    pageSize: pageSize,
                    sortKey: WhisparrTransport.NewestFirstSortKey,
                    sortDirection: V2Model.SortDirection.Descending,
                    includeEpisode: true,
                    cancellationToken: ct)),
            _ => throw new ArgumentOutOfRangeException(nameof(generation)),
        };
    }

    public Task<WhisparrResponse> ReadCommandAsync(
        Uri baseAddress, string apiKey, int commandId, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(commandId, 1);

        return GeneratedReadAsync(
            baseAddress,
            apiKey,
            api => api.Api<V3Api.ICommandApi>().GetCommandByIdAsync(commandId, ct));
    }

    public Task<WhisparrResponse> CreateNotificationAsync(
        Uri baseAddress, string apiKey, JsonNode body, CancellationToken ct)
        => transport.ConfigureAsync(
            baseAddress, apiKey, HttpMethod.Post, WhisparrTransport.NotificationPath, body, ct);

    public Task<WhisparrResponse> UpdateNotificationAsync(
        Uri baseAddress, string apiKey, int id, JsonNode body, CancellationToken ct)
        => transport.ConfigureAsync(
            baseAddress,
            apiKey,
            HttpMethod.Put,
            string.Create(CultureInfo.InvariantCulture, $"{WhisparrTransport.NotificationPath}/{id}"),
            body,
            ct);

    public Task<WhisparrResponse> ReadStudioAsync(
        Uri baseAddress,
        string apiKey,
        WhisparrGeneration generation,
        string foreignId,
        CancellationToken ct)
        => generation switch
        {
            WhisparrGeneration.V3 => GeneratedReadAsync(
                baseAddress,
                apiKey,
                api => api.Api<V3Api.IStudioApi>().GetStudioByStudioForeignIdAsync(Named(foreignId), ct)),
            WhisparrGeneration.V2 => ReadHeldSeriesAsync(baseAddress, apiKey, foreignId, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(generation)),
        };

    public Task<WhisparrResponse> AddMonitoredStudioAsync(
        Uri baseAddress,
        string apiKey,
        WhisparrGeneration generation,
        string foreignId,
        MonitorScope scope,
        AddDefaults defaults,
        CancellationToken ct)
        => generation switch
        {
            WhisparrGeneration.V3 => GeneratedActAsync(
                baseAddress,
                apiKey,
                api => api.Api<V3Api.IStudioApi>().CreateStudioAsync(
                    V3BodyProjector.AddStudio(foreignId, scope, defaults, DateTimeOffset.UtcNow), ct)),
            WhisparrGeneration.V2 => AddMonitoredSeriesAsync(
                baseAddress, apiKey, foreignId, scope, defaults, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(generation)),
        };

    public Task<WhisparrResponse> SetStudioMonitoredAsync(
        Uri baseAddress,
        string apiKey,
        WhisparrGeneration generation,
        int entityId,
        bool monitored,
        CancellationToken ct)
        => generation switch
        {
            WhisparrGeneration.V3 => GeneratedActAsync(
                baseAddress,
                apiKey,
                api => api.Api<V3Api.IStudioEditorApi>().PutStudioEditorAsync(
                    V3BodyProjector.SetStudioMonitored(entityId, monitored), ct)),
            WhisparrGeneration.V2 => GeneratedV2ActAsync(
                baseAddress,
                apiKey,
                api => api.Api<V2Api.ISeriesEditorApi>().PutSeriesEditorAsync(
                    V2BodyProjector.SetMonitored(entityId, monitored), ct)),
            _ => throw new ArgumentOutOfRangeException(nameof(generation)),
        };

    // On v3 a field-scoped patch carrying only what changes: a whole-resource replace would write
    // back a resource read a moment earlier, dropping whatever the read did not answer with. On v2
    // the flag travels on a list of exactly one row id, the only shape that route takes.
    public Task<WhisparrResponse> SetSceneMonitoredAsync(
        Uri baseAddress,
        string apiKey,
        WhisparrGeneration generation,
        int sceneId,
        bool monitored,
        CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sceneId, 1);

        return generation switch
        {
            WhisparrGeneration.V3 => GeneratedActAsync(
                baseAddress,
                apiKey,
                api => api.Api<V3Api.IMovieApi>().PatchMovieByIdAsync(
                    sceneId, V3BodyProjector.SceneMonitorPatch(monitored), ct)),
            WhisparrGeneration.V2 => GeneratedV2ActAsync(
                baseAddress,
                apiKey,
                api => api.Api<V2Api.IEpisodeApi>().PutEpisodeMonitorAsync(
                    episodesMonitoredResource: V2BodyProjector.MonitorScene(sceneId, monitored),
                    cancellationToken: ct)),
            _ => throw new ArgumentOutOfRangeException(nameof(generation)),
        };
    }

    // One request against the site's own row list. The members that would attach images, files or
    // the site resource are left off, so nothing arrives that this read drops.
    public async Task<IReadOnlyDictionary<int, int>> ReduceSiteSceneRowsAsync(
        Uri baseAddress,
        string apiKey,
        int siteId,
        IReadOnlyCollection<int> sceneNumbers,
        CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(siteId, 1);
        ArgumentNullException.ThrowIfNull(sceneNumbers);

        if (sceneNumbers.Count == 0)
        {
            return new Dictionary<int, int>();
        }

        var listed = await GeneratedV2ReadAsync(
            baseAddress,
            apiKey,
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
    // not help: v2 builds the whole set before filtering, so ?tvdbId= answers a single row no faster
    // than the unfiltered list answers all of them. The read is bounded by the transport's library
    // read timeout, because what it waits on is the instance's own work over its holdings.
    public async Task<IReadOnlySet<int>> ReduceHeldSitesAsync(
        Uri baseAddress,
        string apiKey,
        IReadOnlyCollection<int> siteNumbers,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(siteNumbers);

        if (siteNumbers.Count == 0)
        {
            return new HashSet<int>();
        }

        var listed = await GeneratedV2ReadAsync(
            baseAddress,
            apiKey,
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

    // Adds the entity so the instance tracks its catalogue and wants none of it. The two generations
    // express that differently: one carries a flag governing whether an arrival is wanted, the other
    // adds the site with its monitor rules set to none.
    public Task<WhisparrResponse> TrackEntityAsync(
        Uri baseAddress,
        string apiKey,
        WhisparrGeneration generation,
        WhisparrEntityKind kind,
        string foreignId,
        AddDefaults defaults,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(foreignId);
        ArgumentNullException.ThrowIfNull(defaults);

        if (generation != WhisparrGeneration.V3)
        {
            return TrackSiteAsync(baseAddress, apiKey, foreignId, defaults, ct);
        }

        var path = kind == WhisparrEntityKind.Studio ? StudioPath : PerformerPath;
        return transport.SendAsync(
            baseAddress,
            apiKey,
            HttpMethod.Post,
            path,
            V3BodyProjector.TrackEntity(foreignId, defaults),
            ct);
    }

    private async Task<WhisparrResponse> TrackSiteAsync(
        Uri baseAddress, string apiKey, string foreignId, AddDefaults defaults, CancellationToken ct)
    {
        var numbered = await siteNumbers.ResolveSiteNumberAsync(baseAddress, apiKey, foreignId, ct)
            .ConfigureAwait(false);
        if (numbered.Number is not { } siteNumber)
        {
            return NoSiteNumber(numbered);
        }

        return await GeneratedV2ActAsync(
                baseAddress,
                apiKey,
                api => api.Api<V2Api.ISeriesApi>().CreateSeriesAsync(
                    V2BodyProjector.RegisterSite(siteNumber, defaults), ct))
            .ConfigureAwait(false);
    }

    // The catalogue an entity's own scenes are read from, so the missing surface asks the metadata
    // source for no scene list at all. One request per entity, and on one generation one more to
    // resolve the number that generation addresses a site by.
    public async Task<WhisparrEntityCatalogue> ReadEntityCatalogueAsync(
        Uri baseAddress,
        string apiKey,
        WhisparrGeneration generation,
        WhisparrEntityKind kind,
        string foreignId,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(foreignId);

        return generation == WhisparrGeneration.V3
            ? await ReadV3WorksAsync(baseAddress, apiKey, kind, foreignId, ct).ConfigureAwait(false)
            : await ReadV2SiteScenesAsync(baseAddress, apiKey, foreignId, ct).ConfigureAwait(false);
    }

    // A works route answers only for an entity the instance holds, so a not-found there is the
    // entity's absence and not an empty catalogue.
    private async Task<WhisparrEntityCatalogue> ReadV3WorksAsync(
        Uri baseAddress,
        string apiKey,
        WhisparrEntityKind kind,
        string foreignId,
        CancellationToken ct)
    {
        var listed = kind == WhisparrEntityKind.Studio
            ? await GeneratedReadAsync(
                    baseAddress,
                    apiKey,
                    api => api.Api<V3Api.IStudioApi>().ListStudioWorksAsync(Named(foreignId), ct))
                .ConfigureAwait(false)
            : await GeneratedReadAsync(
                    baseAddress,
                    apiKey,
                    api => api.Api<V3Api.IPerformerApi>()
                        .ListPerformerWorksAsync(Named(foreignId), ct))
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

    // This generation lists a scene only under its site, so the site's own number is resolved first.
    // A site it names no number for is one it holds no entry for.
    private async Task<WhisparrEntityCatalogue> ReadV2SiteScenesAsync(
        Uri baseAddress, string apiKey, string foreignId, CancellationToken ct)
    {
        // The lookup answers the site from the instance's own row where it holds that site, so the
        // id its scenes are listed under arrives with it. Asking the site list for that id instead
        // would cost a pass over every site the instance holds.
        var answered = await GeneratedV2ReadAsync(
                baseAddress,
                apiKey,
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

        var listed = await GeneratedV2ReadAsync(
                baseAddress,
                apiKey,
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

    // One request for a page of entity cards, whatever the page holds. The alternative is a read per
    // card, which is what this replaces: a page of forty studios cost forty requests against a third
    // party.
    public async Task<WhisparrHeldCards> ReadHeldEntitiesAsync(
        Uri baseAddress,
        string apiKey,
        WhisparrGeneration generation,
        WhisparrEntityKind kind,
        IReadOnlyList<string> foreignIds,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(foreignIds);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        if (foreignIds.Count == 0)
        {
            return WhisparrHeldCards.Empty;
        }

        return generation == WhisparrGeneration.V3
            ? await ReadHeldV3EntitiesAsync(baseAddress, apiKey, kind, foreignIds, ct)
                .ConfigureAwait(false)
            : await ReadHeldV2SitesAsync(baseAddress, apiKey, foreignIds, ct).ConfigureAwait(false);
    }

    // Each kind has its own list route, and neither answers for the other. A kind this generation
    // does not address at all cannot reach here: the role is registered per generation.
    private async Task<WhisparrHeldCards> ReadHeldV3EntitiesAsync(
        Uri baseAddress,
        string apiKey,
        WhisparrEntityKind kind,
        IReadOnlyList<string> foreignIds,
        CancellationToken ct)
    {
        // One list route per kind, and neither answers for the other, so the two reads are issued
        // apart rather than through one call the generated client types differently per arm.
        List<string> wanted = [.. foreignIds];
        var answered = kind == WhisparrEntityKind.Studio
            ? await GeneratedReadAsync(
                    baseAddress,
                    apiKey,
                    api => api.Api<V3Api.IStudioApi>().CreateStudioListAsync(wanted, ct))
                .ConfigureAwait(false)
            : await GeneratedReadAsync(
                    baseAddress,
                    apiKey,
                    api => api.Api<V3Api.IPerformerApi>().CreatePerformerListAsync(wanted, ct))
                .ConfigureAwait(false);

        return new WhisparrHeldCards(HeldIn(answered, foreignIds), WhisparrTransport.NothingUnanswered);
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
    private async Task<WhisparrHeldCards> ReadHeldV2SitesAsync(
        Uri baseAddress,
        string apiKey,
        IReadOnlyList<string> foreignIds,
        CancellationToken ct)
    {
        List<string> asked = [.. foreignIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal)];
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
                var answered = await GeneratedV2ReadAsync(
                        baseAddress,
                        apiKey,
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

    // One request for a page of scene cards. Registered by the generation that addresses a scene
    // without its site; the other reads a scene only as a row under one.
    public async Task<WhisparrHeldCards> ReadHeldSceneCardsAsync(
        Uri baseAddress, string apiKey, IReadOnlyList<string> foreignIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(foreignIds);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        if (foreignIds.Count == 0)
        {
            return WhisparrHeldCards.Empty;
        }

        List<string> wanted = [.. foreignIds];
        var answered = await GeneratedReadAsync(
                baseAddress,
                apiKey,
                api => api.Api<V3Api.IMovieApi>().CreateMovieListAsync(wanted, ct))
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

    private static IReadOnlyDictionary<string, WhisparrHeldCard> NothingHeld { get; }
        = new Dictionary<string, WhisparrHeldCard>(StringComparer.Ordinal);

    public Task<WhisparrResponse> AddSceneExclusionAsync(
        Uri baseAddress, string apiKey, string foreignId, CancellationToken ct)
        => GeneratedActAsync(
            baseAddress,
            apiKey,
            api => api.Api<V3Api.IImportListExclusionApi>().CreateExclusionsAsync(
                V3BodyProjector.SceneExclusion(Named(foreignId)), ct));

    public Task<WhisparrResponse> RemoveSceneExclusionAsync(
        Uri baseAddress, string apiKey, int exclusionId, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(exclusionId, 1);

        return GeneratedActAsync(
            baseAddress,
            apiKey,
            api => api.Api<V3Api.IImportListExclusionApi>().DeleteExclusionsAsync(exclusionId, ct));
    }

    public Task<WhisparrResponse> SetStudioScopeAsync(
        Uri baseAddress,
        string apiKey,
        WhisparrGeneration generation,
        int entityId,
        MonitorScope scope,
        CancellationToken ct)
        => generation switch
        {
            WhisparrGeneration.V3 => SetStudioDateGateAsync(baseAddress, apiKey, entityId, scope, ct),

            // Re-applied over the existing catalogue in one request, so nothing is read first. The
            // route answers an empty body with a server failure, so the body is what makes it work.
            WhisparrGeneration.V2 => GeneratedV2ActAsync(
                baseAddress,
                apiKey,
                api => api.Api<V2Api.ISeasonPassApi>().CreateSeasonPassAsync(
                    V2BodyProjector.SetScope(entityId, scope), ct)),
            _ => throw new ArgumentOutOfRangeException(nameof(generation)),
        };

    private async Task<WhisparrResponse> SetStudioDateGateAsync(
        Uri baseAddress, string apiKey, int entityId, MonitorScope scope, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(entityId, 1);

        // Read then replaced, because the editor resource declares no add-time date gate: a scope
        // sent there is accepted and applies nothing. The read is idempotent and the replace is sent
        // once.
        //
        // Composed here rather than by the generated client, which carries a fixed member set: the
        // replacement is the answer itself with two members changed, and a member the generated
        // resource does not declare would be dropped on the way back out.
        var path = string.Create(CultureInfo.InvariantCulture, $"{StudioPath}/{entityId}");
        var held = await ReadAsync(baseAddress, apiKey, path, ct).ConfigureAwait(false);
        if (MonitoringProjector.AsObject(held.Body) is not { } studio)
        {
            return held;
        }

        return await ActAsync(
            baseAddress, apiKey, HttpMethod.Put, path,
            V3BodyProjector.WithScope(studio, scope, DateTimeOffset.UtcNow), ct)
            .ConfigureAwait(false);
    }

    // Whether v2's instance holds the entity an identifier names. One read of one site, so it is
    // bounded as the per-item call it is rather than as a read of everything held.
    //
    // The site list would answer this too, and did: v2 builds its whole set before filtering, so
    // asking it for one site costs the same pass over every site as asking for all of them. An
    // instance holding 512 sites answers that in about 30 seconds and this lookup in under one.
    private async Task<WhisparrResponse> ReadHeldSeriesAsync(
        Uri baseAddress, string apiKey, string foreignId, CancellationToken ct)
    {
        // The lookup answers the site under whichever spelling the library holds, and answers it
        // from the instance's own row where it holds that site.
        var answered = await GeneratedV2ReadAsync(
                baseAddress,
                apiKey,
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
        Uri baseAddress,
        string apiKey,
        string foreignId,
        MonitorScope scope,
        AddDefaults defaults,
        CancellationToken ct)
    {
        var numbered = await siteNumbers.ResolveSiteNumberAsync(baseAddress, apiKey, foreignId, ct)
            .ConfigureAwait(false);
        if (numbered.Number is not { } siteNumber)
        {
            return NoSiteNumber(numbered);
        }

        return await GeneratedV2ActAsync(
            baseAddress,
            apiKey,
            api => api.Api<V2Api.ISeriesApi>().CreateSeriesAsync(
                V2BodyProjector.AddStudio(siteNumber, scope, defaults),
                ct)).ConfigureAwait(false);
    }

    // The same request the monitoring add sends, with the presence-only body, so the catalogue the
    // instance then reads for the site is wanted by nothing.
    public async Task<WhisparrResponse> RegisterSiteAsync(
        Uri baseAddress,
        string apiKey,
        string foreignId,
        AddDefaults defaults,
        CancellationToken ct)
    {
        var numbered = await siteNumbers.ResolveSiteNumberAsync(baseAddress, apiKey, foreignId, ct)
            .ConfigureAwait(false);
        if (numbered.Number is not { } siteNumber)
        {
            return NoSiteNumber(numbered);
        }

        return await GeneratedV2ActAsync(
            baseAddress,
            apiKey,
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
        Uri baseAddress,
        string apiKey,
        int siteId,
        string rootFolderPath,
        CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(siteId, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootFolderPath);

        var (read, resource) = await ReadSeriesResourceAsync(baseAddress, apiKey, siteId, ct)
            .ConfigureAwait(false);
        if (resource is null)
        {
            // A success status carrying nothing the model could be read from arrives here too, and
            // its own status classifies as accepted. Returning it unchanged would report a move the
            // caller counts as done while no update was sent and the site still sits where it was.
            return WhisparrTransport.Refused(read)
                ? read
                : read with { Refusal = MonitorRefusalKind.InstanceRefused };
        }

        var moved = await GeneratedV2ActAsync(
            baseAddress,
            apiKey,
            api => api.Api<V2Api.ISeriesApi>().UpdateSeriesAsync(
                siteId.ToString(CultureInfo.InvariantCulture),
                seriesResource: V2BodyProjector.MovedSiteRoot(resource, rootFolderPath),
                cancellationToken: ct)).ConfigureAwait(false);
        if (WhisparrTransport.Refused(moved))
        {
            return moved;
        }

        var linked = await RefreshSiteCatalogueAsync(baseAddress, apiKey, siteId, ct)
            .ConfigureAwait(false);

        return WhisparrTransport.Refused(linked) ? linked : moved;
    }

    public Task<WhisparrResponse> RefreshSiteCatalogueAsync(
        Uri baseAddress, string apiKey, int siteId, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(siteId, 1);

        var (verb, payload) = WhisparrTransport.VerbAndPayload(V2BodyProjector.RefreshCatalogue(siteId));
        return GeneratedV2ActAsync(
            baseAddress,
            apiKey,
            api => api.Api<V2Api.CommandApi>().SendCommandAsync(verb, payload, ct));
    }

    // The typed resource beside the answer, because the update re-sends what the read answered.
    // Re-parsing the text answer into a member set named here would drop every member not named
    // here: the tags, the per-year flags, and whatever a later instance build adds.
    private async Task<(WhisparrResponse Answer, V2Model.SeriesResource? Held)>
        ReadSeriesResourceAsync(Uri baseAddress, string apiKey, int siteId, CancellationToken ct)
    {
        V2Model.SeriesResource? held = null;
        var answered = await GeneratedV2ReadAsync(
            baseAddress,
            apiKey,
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
    public Task<WhisparrResponse> ReadPerformerAsync(
        Uri baseAddress, string apiKey, string foreignId, CancellationToken ct)
        => GeneratedReadAsync(
            baseAddress,
            apiKey,
            api => api.Api<V3Api.IPerformerApi>()
                .GetPerformerByPerformerForeignIdAsync(Named(foreignId), ct));

    public Task<WhisparrResponse> AddMonitoredPerformerAsync(
        Uri baseAddress,
        string apiKey,
        string foreignId,
        AddDefaults defaults,
        CancellationToken ct)
        => GeneratedActAsync(
            baseAddress,
            apiKey,
            api => api.Api<V3Api.IPerformerApi>().CreatePerformerAsync(
                V3BodyProjector.AddPerformer(foreignId, defaults), ct));

    public Task<WhisparrResponse> SetPerformerMonitoredAsync(
        Uri baseAddress, string apiKey, int entityId, bool monitored, CancellationToken ct)
        => GeneratedActAsync(
            baseAddress,
            apiKey,
            api => api.Api<V3Api.IPerformerEditorApi>().PutPerformerEditorAsync(
                V3BodyProjector.SetPerformerMonitored(entityId, monitored), ct));

    public Task<WhisparrResponse> AddSceneAsync(
        Uri baseAddress,
        string apiKey,
        string foreignId,
        AddDefaults defaults,
        CancellationToken ct)
        => GeneratedActAsync(
            baseAddress,
            apiKey,
            api => api.Api<V3Api.IMovieApi>().CreateMovieAsync(
                V3BodyProjector.AddScene(foreignId, defaults), ct));

    public Task<WhisparrResponse> RefreshCatalogueAsync(
        Uri baseAddress,
        string apiKey,
        WhisparrEntityKind kind,
        int entityId,
        CancellationToken ct)
    {
        var command = V3BodyProjector.RefreshCatalogue(kind, entityId);
        return GeneratedCommandAsync(baseAddress, apiKey, command, ct);
    }

    // The operation is chosen here from the entity kind, so no caller can aim the stored credential
    // at a route of its own naming.
    public Task<WhisparrResponse> ReadEntityPresenceAsync(
        Uri baseAddress,
        string apiKey,
        WhisparrEntityKind kind,
        string foreignId,
        CancellationToken ct)
        => kind switch
        {
            WhisparrEntityKind.Studio => GeneratedReadAsync(
                baseAddress,
                apiKey,
                api => api.Api<V3Api.IStudioApi>()
                    .GetStudioByStudioForeignIdAsync(Named(foreignId), ct)),
            WhisparrEntityKind.Performer => GeneratedReadAsync(
                baseAddress,
                apiKey,
                api => api.Api<V3Api.IPerformerApi>()
                    .GetPerformerByPerformerForeignIdAsync(Named(foreignId), ct)),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

    // One key, single-valued. Repeating it answers only the first value's row, comma-joining
    // answers nothing, and the two plural spellings v3 accepts are ignored and answer with the whole
    // catalogue.
    public Task<WhisparrResponse> ReadSceneByRemoteIdAsync(
        Uri baseAddress, string apiKey, string remoteId, CancellationToken ct)
        => GeneratedReadAsync(
            baseAddress,
            apiKey,
            api => api.Api<V3Api.IMovieApi>().ListMovieAsync(
                stashId: Named(remoteId), cancellationToken: ct));

    // Each row is reduced to one question, so what this holds is the caller's own set and never the
    // instance's. There is no row cap: a cap would stop part way and report the rest as not
    // excluded, with nothing saying so.
    public async Task<IReadOnlySet<string>> ReduceExclusionsAsync(
        Uri baseAddress,
        string apiKey,
        IReadOnlyCollection<string> providerSceneIds,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(providerSceneIds);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

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
            baseAddress,
            apiKey,
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
        Uri baseAddress,
        string apiKey,
        IReadOnlyCollection<string> foreignIds,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(foreignIds);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

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
                baseAddress,
                apiKey,
                api => api.Api<V3Api.IMovieApi>().CreateMovieListAsync(wanted, ct))
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
        Uri baseAddress, string apiKey, string foreignId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(foreignId);

        int? named = null;
        var read = await OverExclusionRowsAsync(
            baseAddress,
            apiKey,
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
    private async Task<bool> OverExclusionRowsAsync(
        Uri baseAddress, string apiKey, Func<ExclusionRow, bool> visit, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get, WhisparrTransport.RequestUri(baseAddress, ExclusionsPath));
        request.Headers.Add(WhisparrTransport.ApiKeyHeader, apiKey);

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

    public Task<WhisparrResponse> ReadHardlinkSettingAsync(
        Uri baseAddress, string apiKey, CancellationToken ct)
        => GeneratedReadAsync(
            baseAddress,
            apiKey,
            api => api.Api<V3Api.IMediaManagementConfigApi>().GetMediaManagementConfigAsync(ct));

    // The instance is asked to include what it already holds, so a file the library holds and the
    // instance has not attached is still answered for.
    public Task<WhisparrResponse> ListImportableFilesAsync(
        Uri baseAddress, string apiKey, string folder, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        return GeneratedReadAsync(
            baseAddress,
            apiKey,
            api => api.Api<V3Api.IManualImportApi>().ListManualImportAsync(
                folder: folder, filterExistingFiles: false, cancellationToken: ct));
    }

    public Task<WhisparrResponse> AttachOwnedFilesAsync(
        Uri baseAddress, string apiKey, JsonNode files, CancellationToken ct)
        => GeneratedCommandAsync(baseAddress, apiKey, ReflectOwnedPlanner.Command(files), ct);

    public Task<WhisparrResponse> ReadInstanceFolderAsync(
        Uri baseAddress,
        string apiKey,
        WhisparrGeneration generation,
        string directory,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        var asDirectory = WhisparrTransport.WithTrailingSeparator(directory);

        return generation switch
        {
            WhisparrGeneration.V3 => GeneratedReadAsync(
                baseAddress,
                apiKey,
                api => api.Api<V3Api.IFileSystemApi>().GetFileSystemAsync(
                    path: asDirectory,
                    includeFiles: true,
                    allowFoldersWithoutTrailingSlashes: true,
                    cancellationToken: ct)),

            WhisparrGeneration.V2 => GeneratedV2ReadAsync(
                baseAddress,
                apiKey,
                api => api.Api<V2Api.IFileSystemApi>().GetFileSystemAsync(
                    path: asDirectory,
                    includeFiles: true,
                    allowFoldersWithoutTrailingSlashes: true,
                    cancellationToken: ct)),
            _ => throw new ArgumentOutOfRangeException(nameof(generation)),
        };
    }

    // The one member of this seam that can make an instance acquire anything, and the only one whose
    // invocation is recorded on its own. Its verb class has no retry entry: a second search is a
    // second download.
    //
    // v3's command names an id array and carries every id in one; v2's names a single scalar id, so
    // there it is one command per entity and the first answer that was not accepted is the one
    // reported. Either way each entity is searched once.
    public async Task<WhisparrResponse> SearchMonitoredAsync(
        Uri baseAddress,
        string apiKey,
        WhisparrGeneration generation,
        WhisparrEntityKind kind,
        IReadOnlyList<int> entityIds,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entityIds);
        ArgumentOutOfRangeException.ThrowIfZero(entityIds.Count);
        WhisparrSyncLog.SearchIssued(log, kind);

        switch (generation)
        {
            case WhisparrGeneration.V3:
                return await GeneratedCommandAsync(
                        baseAddress, apiKey, V3BodyProjector.SearchAllMonitored(kind, entityIds), ct)
                    .ConfigureAwait(false);
            case WhisparrGeneration.V2:
                WhisparrResponse? answered = null;
                foreach (var entityId in entityIds)
                {
                    answered = await GeneratedV2GrabCommandAsync(
                            baseAddress, apiKey, V2BodyProjector.SearchMonitored(entityId), ct)
                        .ConfigureAwait(false);
                    if (MonitoringProjector.Accepted(answered) != MonitorRefusalKind.None)
                    {
                        return answered;
                    }
                }

                return answered!;
            default:
                throw new ArgumentOutOfRangeException(nameof(generation));
        }
    }

    // Recorded as the entity search is, and given no arguments: the scene, the instance and the key
    // are caller-supplied or credentials, and a log sink is durable and readable. Sent once.
    public Task<WhisparrResponse> SearchSceneAsync(
        Uri baseAddress, string apiKey, int sceneId, CancellationToken ct)
    {
        WhisparrSyncLog.SceneSearchIssued(log);

        return GeneratedCommandAsync(baseAddress, apiKey, V3BodyProjector.SearchScene(sceneId), ct);
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
        Uri baseAddress,
        string apiKey,
        Func<Whisparr3Apis, Task<TResponse>> call)
        where TResponse : V3Client.IApiResponse
    {
        var target = TargetFor(baseAddress, apiKey);
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

    // Sent once, for the reason the hand-composed acting send is: a request whose answer did not
    // arrive is not the same as one that says nothing happened.
    private Task<WhisparrResponse> GeneratedActAsync<TResponse>(
        Uri baseAddress,
        string apiKey,
        Func<Whisparr3Apis, Task<TResponse>> call)
        where TResponse : V3Client.IApiResponse
        => GeneratedSendAsync(TargetFor(baseAddress, apiKey), call);

    // Every instance-side action this generation takes is issued through the one command route. Sent
    // once. The acting class and the grabbing class both reach the route through this send, so an
    // attempt count added here would cover the class that downloads.
    private Task<WhisparrResponse> GeneratedCommandAsync(
        Uri baseAddress, string apiKey, JsonObject command, CancellationToken ct)
    {
        var (name, payload) = WhisparrTransport.VerbAndPayload(command);

        return GeneratedSendAsync(
            TargetFor(baseAddress, apiKey),
            api => api.Api<V3Api.CommandApi>().SendCommandAsync(name, payload, ct));
    }

    private async Task<WhisparrResponse> GeneratedSendAsync<TResponse>(
        Whisparr3Target target,
        Func<Whisparr3Apis, Task<TResponse>> call)
        where TResponse : V3Client.IApiResponse
    {
        try
        {
            using var apis = v3Gateway.For(target);
            return Whisparr3Gateway.Answered(await call(apis).ConfigureAwait(false));
        }
        catch (AnswerTooLargeException beyond)
        {
            return transport.BeyondReadBound(target.BaseAddress, beyond);
        }
    }

    // The read class through v2's generated client, re-issued on the same failure
    // and for the same reason v3's is.
    private async Task<WhisparrResponse> GeneratedV2ReadAsync<TResponse>(
        Uri baseAddress,
        string apiKey,
        Func<Whisparr2Apis, Task<TResponse>> call,
        TimeSpan? budget = null)
        where TResponse : V2Client.IApiResponse
    {
        var target = V2TargetFor(baseAddress, apiKey, budget ?? WhisparrTransport.RequestTimeout);
        var attempts = WhisparrRetryPolicy.AttemptsFor(WhisparrVerbClass.Read);
        for (var attempt = 1; attempt < attempts; attempt++)
        {
            try
            {
                return await GeneratedV2SendAsync(target, call).ConfigureAwait(false);
            }
            catch (Exception failure) when (failure is HttpRequestException or IOException)
            {
                // No whole answer arrived, which is the one failure a read may be re-issued after.
            }
        }

        return await GeneratedV2SendAsync(target, call).ConfigureAwait(false);
    }

    // Sent once, for the reason v3's acting send is: a request whose answer did not
    // arrive is not the same as one that says nothing happened.
    private Task<WhisparrResponse> GeneratedV2ActAsync<TResponse>(
        Uri baseAddress,
        string apiKey,
        Func<Whisparr2Apis, Task<TResponse>> call)
        where TResponse : V2Client.IApiResponse
        => GeneratedV2SendAsync(V2TargetFor(baseAddress, apiKey), call);

    // Sent once. Held apart from the acting sends that name the same route, so an attempt count added
    // here covers the grabbing class alone.
    private Task<WhisparrResponse> GeneratedV2GrabCommandAsync(
        Uri baseAddress, string apiKey, JsonObject command, CancellationToken ct)
    {
        var (name, payload) = WhisparrTransport.VerbAndPayload(command);

        return GeneratedV2SendAsync(
            V2TargetFor(baseAddress, apiKey),
            api => api.Api<V2Api.CommandApi>().SendCommandAsync(name, payload, ct));
    }

    private async Task<WhisparrResponse> GeneratedV2SendAsync<TResponse>(
        Whisparr2Target target,
        Func<Whisparr2Apis, Task<TResponse>> call)
        where TResponse : V2Client.IApiResponse
    {
        try
        {
            using var apis = v2Gateway.For(target);
            return Whisparr2Gateway.Answered(await call(apis).ConfigureAwait(false));
        }
        catch (AnswerTooLargeException beyond)
        {
            return transport.BeyondReadBound(target.BaseAddress, beyond);
        }
    }

    private static Whisparr2Target V2TargetFor(
        Uri baseAddress, string apiKey, TimeSpan? budget = null)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        return new Whisparr2Target(baseAddress, apiKey, budget ?? WhisparrTransport.RequestTimeout);
    }

    private static Whisparr3Target TargetFor(Uri baseAddress, string apiKey)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        return new Whisparr3Target(baseAddress, apiKey);
    }

    // Re-issuing a read re-reads and can create nothing, so the read class is the only one that gets
    // more than one attempt. The last attempt is the plain send, so its failure propagates rather
    // than being counted again.
    private async Task<WhisparrResponse> ReadAsync(
        Uri baseAddress, string apiKey, string path, CancellationToken ct)
    {
        var attempts = WhisparrRetryPolicy.AttemptsFor(WhisparrVerbClass.Read);
        for (var attempt = 1; attempt < attempts; attempt++)
        {
            if (await transport.TrySendAsync(baseAddress, apiKey, HttpMethod.Get, path, null, ct)
                .ConfigureAwait(false) is { } answered)
            {
                return answered;
            }
        }

        return await transport.SendAsync(baseAddress, apiKey, HttpMethod.Get, path, null, ct).ConfigureAwait(false);
    }

    // Sent once for the same reason, and named apart from a configure because the retry policy is
    // keyed on the class of work: an attempt count added for one class must not cover the other.
    private Task<WhisparrResponse> ActAsync(
        Uri baseAddress, string apiKey, HttpMethod method, string path, JsonNode body, CancellationToken ct)
        => transport.SentOnceAsync(baseAddress, apiKey, method, path, body, ct);
}
