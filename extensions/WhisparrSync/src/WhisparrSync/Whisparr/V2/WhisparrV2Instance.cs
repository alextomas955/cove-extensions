using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using WhisparrSync.Contracts;
using WhisparrSync.Scene;
using V2Api = Whisparr2.Net.Api;
using V2Client = Whisparr2.Net.Client;
using V2Model = Whisparr2.Net.Model;

namespace WhisparrSync.Whisparr;

// One v2 instance, bound to the address and key it answers on. It declares the roles v2 holds and
// no others, and the capabilities this generation offers are read from that list.
//
// Measured against a real v2: it answers a not-found on every performer and per-scene route, adds
// no catalogue item, and keeps no scene exclusions, so there is no performer member here, no
// per-scene search and no scene record. It does keep a row per scene, under a site and named by the
// provider's number, so the per-scene monitor and the site-row read are held. What it lacks is a
// route reaching a scene without its site. Site registration and the held-site read are v2's alone,
// a site being its unit of presence and its list the only route answering presence for many sites
// at once.
//
// No member takes an address, key or generation: all three arrive on the binding, so a read and the
// write after it cannot name different instances.
//
// Requests go through the Whisparr 2 generated client, except the two notification verbs, which are
// hand-composed. Both generations serve the v3 route family: the version in a path is not the
// generation.
internal sealed partial class WhisparrV2Instance(
    WhisparrBinding binding,
    WhisparrTransport transport,
    Whisparr2Gateway gateway,
    ISiteNumberPort siteNumbers,
    ILogger log)
    : IWhisparrClient,
        IWhisparrStudioActing,
        IWhisparrSiteRegistrationActing,
        IWhisparrEntityRelocationActing,
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
    // No request was sent, so there is no status to report. Zero is no status rather than a composed
    // one: every caller of an answer carrying it reads the refusal, which outranks the status.
    private const int NoInstanceStatus = 0;

    // Nothing was sent, so there is no status and the refusal is the whole of what a caller reads.
    // A source naming no site is the no-identity reading; a source that was not reached is not, and
    // reporting it as unidentified would send a reader to fix an identity that may be correct.
    private static WhisparrResponse NoSiteNumber(WhisparrSiteNumber numbered)
        => new(NoInstanceStatus, null, string.Empty)
        {
            Refusal = numbered.WasReached
                ? MonitorRefusalKind.NoIdentityInThisNamespace
                : MonitorRefusalKind.InstanceRefused,
        };

    public Task<WhisparrResponse> ReadRootFoldersAsync(CancellationToken ct)
        => GeneratedReadAsync(api => api.Api<V2Api.IRootFolderApi>().ListRootFolderAsync(ct));

    public Task<WhisparrResponse> ReadQualityProfilesAsync(CancellationToken ct)
        => GeneratedReadAsync(api => api.Api<V2Api.IQualityProfileApi>().ListQualityProfileAsync(ct));

    // This generation names its own metadata entity on this route, and that entity carries the
    // identifier the two ingest channels agree on. Embedded on the same request, so a page costs
    // one request whatever it holds.
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
