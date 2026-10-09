using System.Text.Json.Nodes;
using WhisparrSync.Contracts;
using V2Api = Whisparr2.Net.Api;

namespace WhisparrSync.Whisparr;

// What the instance holds, read back: the rows under a site, the sites themselves, and the
// cards a page draws from them.
internal sealed partial class WhisparrV2Instance
{
    // How many of a page's site lookups are in flight at once. One request per card is the cheap
    // shape here, and an unbounded fan-out over a page would still be a burst this product has no
    // reason to send.
    private const int LookupLanes = 6;

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
    // not help: this generation builds the whole set before filtering, so ?tvdbId= answers one row
    // no faster than the unfiltered list answers all. Bounded by the library read timeout, what it
    // waits on being the instance's work over its holdings.
    public async Task<SitesHeld> ReduceHeldSitesAsync(
        IReadOnlyCollection<int> siteNumbers, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(siteNumbers);

        if (siteNumbers.Count == 0)
        {
            return new SitesHeld(new HashSet<int>(), new HashSet<int>());
        }

        var listed = await GeneratedReadAsync(
            api => api.Api<V2Api.ISeriesApi>().ListSeriesAsync(cancellationToken: ct),
            WhisparrTransport.LibraryReadTimeout)
            .ConfigureAwait(false);

        // Raised rather than answered as an empty set, which would report every site asked about as
        // one the instance holds none of, and a caller acting on that registers the whole library a
        // second time.
        if (WhisparrTransport.Refused(listed))
        {
            throw new HttpRequestException(
                "The instance's own site list could not be read, so which of the sites it holds was "
                    + "not established.");
        }

        return V2ListProjector.HeldSitesIn(listed.Body, siteNumbers)
            ?? throw new HttpRequestException(
                "The answer to the instance's own site list is not a list of rows at all.");
    }

    // Adds the entity so the instance tracks its catalogue and wants none of it. A site is this
    // generation's only unit of presence, and registering one is exactly that request, so both
    // roles reach this member rather than composing one body twice.
    public Task<WhisparrResponse> TrackEntityAsync(
        WhisparrEntityKind kind, string foreignId, AddDefaults defaults, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(foreignId);
        ArgumentNullException.ThrowIfNull(defaults);

        return RegisterSiteAsync(foreignId, defaults, ct);
    }

    // The catalogue an entity's own scenes are read from, so the missing surface asks the metadata
    // source for no scene list. One request per entity, plus one to resolve the number this
    // generation addresses a site by: it lists a scene only under its site, and a site it names no
    // number for is one it holds no entry for.
    public async Task<WhisparrEntityCatalogue> ReadEntityCatalogueAsync(
        WhisparrEntityKind kind, string foreignId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(foreignId);

        // The lookup answers the site from the instance's own row where it holds it, so the id its
        // scenes are listed under arrives with it. Asking the site list instead would cost a pass
        // over every site the instance holds.
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

    // Answers one site by the identifier the library holds, from the instance's own row where it
    // holds that site: the search maps each result back through FindByTvdbId, so the id, the flag
    // and the statistics are the instance's. A site it does not hold maps the metadata result and
    // carries no id.
    //
    // One request per card on purpose: the list route recomputes statistics over every site before
    // answering, so a page of forty is faster as forty lookups. Bounded by the page and by
    // LookupLanes.
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
}
