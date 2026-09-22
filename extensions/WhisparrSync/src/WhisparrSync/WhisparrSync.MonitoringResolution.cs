using Cove.Extensions.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WhisparrSync.Addressing;
using WhisparrSync.Connection;
using WhisparrSync.Contracts;
using WhisparrSync.Import;
using WhisparrSync.Monitoring;
using WhisparrSync.Options;
using WhisparrSync.Whisparr;

namespace WhisparrSync;

public sealed partial class WhisparrSync
{
    private sealed record MonitoringTarget(
        WhisparrGeneration Generation,
        Uri BaseAddress,
        string ApiKey,
        WhisparrCapabilitySet Capabilities,
        IWhisparrClient Reads,
        MonitorScope DefaultMonitorScope);

    // Written outside the run's own cancellation. What a run established about a root holds whether
    // or not the run finished.
    private static async Task RecordRootReadingsAsync(
        IServiceScopeFactory scopes,
        IReadOnlyList<FolderAddressRefusal>? refused,
        IReadOnlyList<string>? addressed)
    {
        if (refused is not { Count: > 0 } && addressed is not { Count: > 0 })
        {
            return;
        }

        using var scope = scopes.CreateScope();
        var services = scope.ServiceProvider;

        await services.GetRequiredService<OptionsWriteGate>().MutateAsync(
            services.GetRequiredService<OptionsStore>(),
            stored => stored with
            {
                OutboundRefusals = OutboundRefusalProjector.Fold(
                    stored.OutboundRefusals, refused, addressed),
            },
            CancellationToken.None).ConfigureAwait(false);
    }

    private static async Task<MonitoringTarget?> ResolveTargetAsync(
        OptionsStore options, ICredentialPort credentials, IWhisparrClient client, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(credentials);

        var stored = await options.LoadAsync(ct).ConfigureAwait(false);
        var generation = stored.SelectedGeneration;
        var held = await credentials.ReadConnectionAsync(generation, ct).ConfigureAwait(false);
        var apiKey = held?.ApiKey;
        // The address comes from the row that holds the key, so the two cannot be observed from
        // either side of a save that changed both. A row written before the address was stored there
        // carries none, and the stored options answer for that installation until its next save.
        var address = string.IsNullOrWhiteSpace(held?.Address)
            ? stored.ConnectionFor(generation)?.Address
            : held.Address;

        // Refused here rather than by handing an empty pair to the client, so an unconfigured
        // connection reaches nothing that could make a request.
        return ConnectionTester.TryReadConnection(address, apiKey, out var baseAddress, out _)
            ? new MonitoringTarget(
                generation,
                baseAddress,
                apiKey,
                GenerationCapabilities.For(generation, WhisparrRoleSet.From(client)),
                client,
                stored.DefaultMonitorScope)
            : null;
    }

    private sealed record KindActing(
        HeldActing Held,
        Func<AddDefaults, CancellationToken, Task<WhisparrResponse>> AddMonitored);

    // SetScope is null for a kind that expresses no scope, so a scope cannot reach one at all.
    private sealed record HeldActing(
        Func<CancellationToken, Task<WhisparrResponse>> ReadEntity,
        Func<int, bool, CancellationToken, Task<WhisparrResponse>> SetMonitored,
        Func<int, MonitorScope, CancellationToken, Task<WhisparrResponse>>? SetScope);

    private static KindActing ActingOn(
        IWhisparrStudioActing acting, MonitoringTarget target, string foreignId, MonitorScope scope)
        => new(
            HeldOn(acting, target, foreignId),
            (defaults, addCt) => acting.AddMonitoredStudioAsync(
                target.BaseAddress, target.ApiKey, target.Generation, foreignId, scope, defaults, addCt));

    private static KindActing ActingOn(
        IWhisparrPerformerActing acting, MonitoringTarget target, string foreignId)
        => new(
            HeldOn(acting, target, foreignId),
            (defaults, addCt) => acting.AddMonitoredPerformerAsync(
                target.BaseAddress, target.ApiKey, foreignId, defaults, addCt));

    private static HeldActing HeldOn(
        IWhisparrStudioActing acting, MonitoringTarget target, string foreignId)
        => new(
            readCt => acting.ReadStudioAsync(
                target.BaseAddress, target.ApiKey, target.Generation, foreignId, readCt),
            (entityId, monitored, flipCt) => acting.SetStudioMonitoredAsync(
                target.BaseAddress, target.ApiKey, target.Generation, entityId, monitored, flipCt),
            (entityId, scope, scopeCt) => acting.SetStudioScopeAsync(
                target.BaseAddress, target.ApiKey, target.Generation, entityId, scope, scopeCt));

    private static HeldActing HeldOn(
        IWhisparrPerformerActing acting, MonitoringTarget target, string foreignId)
        => new(
            readCt => acting.ReadPerformerAsync(target.BaseAddress, target.ApiKey, foreignId, readCt),
            (entityId, monitored, flipCt) => acting.SetPerformerMonitoredAsync(
                target.BaseAddress, target.ApiKey, entityId, monitored, flipCt),
            SetScope: null);

    private static Func<IEntityFolderPort, string, CancellationToken, Task<int>> FilesOfEntity(
        WhisparrEntityKind kind, int coveId)
        => (files, coveRoot, ct) => files.FilesUnderAsync(kind, coveId, coveRoot, ct);

    private static Func<IEntityFolderPort, string, CancellationToken, Task<int>> FilesOfVideo(
        int videoId)
        => (files, coveRoot, ct) => files.VideoFilesUnderAsync(videoId, coveRoot, ct);

    // The count is taken inside a System scope. Cove's per-principal query filters answer a reader
    // with the rows that reader can see, so a count taken as the caller would report an entity that
    // holds files as holding none, and the add would go to the wrong root with no error.
    // The readings are recorded because the settings page offers a root only once a reading exists
    // for it, and the refusal tells the reader to state that folder's path there.
    private static Func<AddDefaults, CancellationToken, Task<EntityAddDefaultsResolution>>
        EntityRootThrough(
            IServiceScopeFactory scopes,
            MonitoringTarget target,
            Func<IEntityFolderPort, string, CancellationToken, Task<int>> countUnder)
        => async (runWide, ct) =>
        {
            var readings = new List<AddressedFolder>();
            var composed = await RunAsSystem.RunInSystemScopeAsync(
                scopes,
                services => ComposeWithEntityRootAsync(
                    services, target, countUnder, runWide, readings.Add, ct))
                .ConfigureAwait(false);

            await RecordRootReadingsAsync(
                scopes,
                [.. readings
                    .Where(reading => reading.Refusal is not null)
                    .Select(reading => new FolderAddressRefusal(
                        reading.CoveRoot, reading.Refusal!.Value, reading.Tried))],
                [.. readings
                    .Where(reading => reading.Refusal is null)
                    .Select(reading => reading.CoveRoot)])
                .ConfigureAwait(false);

            return composed;
        };

    private static Func<AddDefaults, CancellationToken, Task<EntityAddDefaultsResolution>>
        EntityRootIn(
            IServiceProvider services,
            MonitoringTarget target,
            Func<IEntityFolderPort, string, CancellationToken, Task<int>> countUnder)
        => (runWide, ct) =>
            ComposeWithEntityRootAsync(services, target, countUnder, runWide, observe: null, ct);

    // observe is null where the caller records the root readings itself, as a run does from its own
    // loop over the roots.
    private static Task<EntityAddDefaultsResolution> ComposeWithEntityRootAsync(
        IServiceProvider services,
        MonitoringTarget target,
        Func<IEntityFolderPort, string, CancellationToken, Task<int>> countUnder,
        AddDefaults runWide,
        Action<AddressedFolder>? observe,
        CancellationToken ct)
    {
        var files = services.GetRequiredService<IEntityFolderPort>();
        var agreedRoot = AgreedRootThrough(
            target, services.GetRequiredService<IFolderAddressPort>());

        return EntityAddDefaults.ComposeAsync(
            runWide,
            services.GetRequiredService<ICoveLibraryPort>().LibraryRoots,
            (coveRoot, countCt) => countUnder(files, coveRoot, countCt),
            observe is null
                ? agreedRoot
                : async (coveRoot, addressCt) =>
                {
                    var addressed = await agreedRoot(coveRoot, addressCt).ConfigureAwait(false);
                    observe(addressed);
                    return addressed;
                },
            ct);
    }

    private static Func<string, CancellationToken, Task<WhisparrResponse>>? ReadingEntity(
        WhisparrEntityKind kind, MonitoringTarget target)
        => kind switch
        {
            WhisparrEntityKind.Studio => target.Capabilities.Obtain<IWhisparrStudioActing>()
                .Match<Func<string, CancellationToken, Task<WhisparrResponse>>?>(
                    acting => (foreignId, readCt) => acting.ReadStudioAsync(
                        target.BaseAddress, target.ApiKey, target.Generation, foreignId, readCt),
                    _ => null),
            WhisparrEntityKind.Performer => target.Capabilities.Obtain<IWhisparrPerformerActing>()
                .Match<Func<string, CancellationToken, Task<WhisparrResponse>>?>(
                    acting => (foreignId, readCt) => acting.ReadPerformerAsync(
                        target.BaseAddress, target.ApiKey, foreignId, readCt),
                    _ => null),
            _ => NoArmFor<Func<string, CancellationToken, Task<WhisparrResponse>>>(kind, target),
        };

    private static Func<string, KindActing>? ActingFor(
        WhisparrEntityKind kind, MonitoringTarget target, MonitorScope scope)
        => kind switch
        {
            WhisparrEntityKind.Studio => target.Capabilities.Obtain<IWhisparrStudioActing>()
                .Match<Func<string, KindActing>?>(
                    acting => foreignId => ActingOn(acting, target, foreignId, scope), _ => null),
            WhisparrEntityKind.Performer => target.Capabilities.Obtain<IWhisparrPerformerActing>()
                .Match<Func<string, KindActing>?>(
                    acting => foreignId => ActingOn(acting, target, foreignId), _ => null),
            _ => NoArmFor<Func<string, KindActing>>(kind, target),
        };

    // The only place the search role is obtained. A generation holding no search hands over no
    // implementation, so the refusal is at the caller rather than inside a member that declines.
    private static IWhisparrSearchGrabbing? SearchGrabbingOn(MonitoringTarget target)
        => target.Capabilities.Obtain<IWhisparrSearchGrabbing>()
            .Match<IWhisparrSearchGrabbing?>(held => held, _ => null);

    private static Func<string, HeldActing>? HeldActingFor(
        WhisparrEntityKind kind, MonitoringTarget target)
        => kind switch
        {
            WhisparrEntityKind.Studio => target.Capabilities.Obtain<IWhisparrStudioActing>()
                .Match<Func<string, HeldActing>?>(
                    acting => foreignId => HeldOn(acting, target, foreignId), _ => null),
            WhisparrEntityKind.Performer => target.Capabilities.Obtain<IWhisparrPerformerActing>()
                .Match<Func<string, HeldActing>?>(
                    acting => foreignId => HeldOn(acting, target, foreignId), _ => null),
            _ => NoArmFor<Func<string, HeldActing>>(kind, target),
        };

    // A generation that holds the capability while no route can act on it is a fault, not a
    // refusal: a capability is registered with the member that honours it, never ahead of it.
    private static T? NoArmFor<T>(WhisparrEntityKind kind, MonitoringTarget target)
        where T : class
    {
        var capability = MonitoringProjector.CapabilityFor(kind);
        return target.Capabilities.Held.Contains(capability)
            ? throw new InvalidOperationException(
                $"{target.Generation} holds {capability}, but no route has an arm acting on a {kind}.")
            : null;
    }

    // A failure is contained rather than propagated, because the route's declared results hold no
    // failure. A shutdown rethrows: it is not a verdict about the instance.
    // The filter names IOException as well as HttpRequestException. The client reads the body out
    // of the response stream, so a connection dropped part way through an answer raises the former.
    private static async Task<WhisparrResponse?> ContainedAsync(
        Func<Task<WhisparrResponse>> request,
        MonitoringTarget target,
        ILogger log,
        CancellationToken ct)
    {
        try
        {
            return await request().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception failure)
            when (failure is HttpRequestException or IOException or TaskCanceledException)
        {
            WhisparrSyncLog.MonitoringRequestContained(
                log, target.Generation, WhisparrSyncLog.Classify(failure), target.BaseAddress.Host);
            return null;
        }
    }
}
