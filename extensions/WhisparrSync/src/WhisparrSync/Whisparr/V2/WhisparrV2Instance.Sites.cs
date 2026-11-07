using System.Globalization;
using WhisparrSync.Contracts;
using V2Api = Whisparr2.Net.Api;
using V2Model = Whisparr2.Net.Model;

namespace WhisparrSync.Whisparr;

// A site is this generation's unit of presence. Registering one, moving where the instance
// records it, and asking it to read its catalogue again are the three requests that decide
// where its files are linked.
internal sealed partial class WhisparrV2Instance
{
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

    // One read and one update, then the catalogue re-read that links the files. The update's body
    // is the resource the read answered, and names no transfer parameter, which is what leaves the
    // files where they are. The re-read is not optional: the update alone rewrites where the
    // instance records the site and links nothing, so the site reports no file until the catalogue
    // is re-read.
    public async Task<WhisparrResponse> MoveEntityFolderAsync(
        int entityId, string rootFolderPath, string? entityFolderPath, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(entityId, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootFolderPath);

        var (read, resource) = await ReadSeriesResourceAsync(entityId, ct).ConfigureAwait(false);
        if (resource is null)
        {
            // A success status carrying nothing the model could be read from arrives here too, and
            // classifies as accepted. Returning it unchanged would report a move the caller counts
            // as done while no update was sent and the site still sits where it was.
            return WhisparrTransport.Refused(read)
                ? read
                : read with { Refusal = MonitorRefusalKind.InstanceRefused };
        }

        var moved = await GeneratedActAsync(
            api => api.Api<V2Api.ISeriesApi>().UpdateSeriesAsync(
                entityId.ToString(CultureInfo.InvariantCulture),
                seriesResource: V2BodyProjector.MovedSiteRoot(
                    resource, rootFolderPath, entityFolderPath),
                cancellationToken: ct)).ConfigureAwait(false);
        if (WhisparrTransport.Refused(moved))
        {
            return moved;
        }

        var linked = await RefreshSiteCatalogueAsync(entityId, ct).ConfigureAwait(false);

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
    // Re-parsing into a member set named here would drop every member not named: the tags, the
    // per-year flags, and whatever a later instance build adds.
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
}
