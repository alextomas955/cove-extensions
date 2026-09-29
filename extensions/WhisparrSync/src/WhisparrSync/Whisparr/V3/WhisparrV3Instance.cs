using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using WhisparrSync.Scene;
using V3Api = Whisparr3.Net.Api;
using V3Client = Whisparr3.Net.Client;

namespace WhisparrSync.Whisparr;

// One v3 instance, bound to the address and key it answers on. It declares the roles v3 holds and
// no others, and the capabilities this generation offers are read from that list. There is no site
// registration member: presence here is a scene add, and a site arrives as a side effect of one.
//
// No member takes an address, key or generation: all three arrive on the binding, so a
// read and the write after it cannot name different instances. Requests go through the Whisparr 3
// generated client, except the two notification verbs, hand-composed onto the route below and sent
// through the transport, so the same bounds apply.
internal sealed partial class WhisparrV3Instance(
    WhisparrBinding binding,
    WhisparrTransport transport,
    Whisparr3Gateway gateway,
    ILogger log)
    : IWhisparrClient,
        IWhisparrStudioActing,
        IWhisparrPerformerActing,
        IWhisparrMissingSceneActing,
        IWhisparrEntityRelocationActing,
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
    // Relative, so they compose onto a base address carrying a URL base. Both generations serve the
    // v3 route family; the version in the path is not the generation. They are declared across the
    // types the outbound seam is made of, and the route invariant reads that named set rather than
    // one type.
    internal const string StudioPath = "api/v3/studio";
    internal const string PerformerPath = "api/v3/performer";
    internal const string ExclusionsPath = "api/v3/exclusions";
    internal const string ScenePath = "api/v3/movie";

    // The identifier comes from a stored identity row rather than from a caller. The generated client
    // escapes it as one path segment, so a value carrying a separator names no other route.
    private static string Named(string foreignId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(foreignId);
        return foreignId;
    }

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

    // Every instance-side action this generation takes goes through the one command route, sent
    // once. The acting and grabbing classes both reach it through this send, so an attempt count
    // added here would cover the class that downloads.
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

    // Re-issuing a read creates nothing, so the read class is the only one granted more than one
    // attempt. The last attempt is the plain send, so its failure propagates rather than being
    // counted again.
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
