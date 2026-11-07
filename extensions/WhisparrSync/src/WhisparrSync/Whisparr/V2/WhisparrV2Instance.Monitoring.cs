using WhisparrSync.Contracts;
using V2Api = Whisparr2.Net.Api;

namespace WhisparrSync.Whisparr;

// What this instance is asked to want, and what it answers about a studio it already holds.
internal sealed partial class WhisparrV2Instance
{
    // The one status composed rather than received: v2 answers "do you hold this site" only as a
    // row inside its own list, so an absent row is reported in the spelling a caller already
    // classifies.
    private const int AssembledNotHeld = 404;

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
}
