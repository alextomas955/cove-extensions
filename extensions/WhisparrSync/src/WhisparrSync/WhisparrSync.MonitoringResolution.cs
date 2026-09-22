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
    // The generation, the address and the key are read off the one binding rather than stored
    // beside each other, so a caller cannot take a generation from one instance and an address from
    // another. Reads is the instance bound to that same binding, and a caller reaching for a role
    // tests it for the interface that role names.
    private sealed record MonitoringTarget(
        WhisparrBinding Binding,
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
        OptionsStore options,
        ICredentialPort credentials,
        IWhisparrInstanceFactory instances,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(instances);

        var stored = await options.LoadAsync(ct).ConfigureAwait(false);
        var binding = await OutboundPair.ResolveAsync(stored, credentials, ct).ConfigureAwait(false);

        return binding is null
            ? null
            : new MonitoringTarget(
                binding, instances.Bound(binding), stored.DefaultMonitorScope);
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
        IWhisparrStudioActing acting, string foreignId, MonitorScope scope)
        => new(
            HeldOn(acting, foreignId),
            (defaults, addCt) => acting.AddMonitoredStudioAsync(foreignId, scope, defaults, addCt));

    private static KindActing ActingOn(IWhisparrPerformerActing acting, string foreignId)
        => new(
            HeldOn(acting, foreignId),
            (defaults, addCt) => acting.AddMonitoredPerformerAsync(foreignId, defaults, addCt));

    private static HeldActing HeldOn(IWhisparrStudioActing acting, string foreignId)
        => new(
            readCt => acting.ReadStudioAsync(foreignId, readCt),
            (entityId, monitored, flipCt) => acting.SetStudioMonitoredAsync(
                entityId, monitored, flipCt),
            (entityId, scope, scopeCt) => acting.SetStudioScopeAsync(entityId, scope, scopeCt));

    private static HeldActing HeldOn(IWhisparrPerformerActing acting, string foreignId)
        => new(
            readCt => acting.ReadPerformerAsync(foreignId, readCt),
            (entityId, monitored, flipCt) => acting.SetPerformerMonitoredAsync(
                entityId, monitored, flipCt),
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

    // A kind no arm names reads nothing, as a kind whose instance declares no acting role does.
    private static Func<string, CancellationToken, Task<WhisparrResponse>>? ReadingEntity(
        WhisparrEntityKind kind, MonitoringTarget target)
        => kind switch
        {
            WhisparrEntityKind.Studio when target.Reads is IWhisparrStudioActing acting
                => (foreignId, readCt) => acting.ReadStudioAsync(foreignId, readCt),
            WhisparrEntityKind.Performer when target.Reads is IWhisparrPerformerActing acting
                => (foreignId, readCt) => acting.ReadPerformerAsync(foreignId, readCt),
            _ => null,
        };

    private static Func<string, KindActing>? ActingFor(
        WhisparrEntityKind kind, MonitoringTarget target, MonitorScope scope)
        => kind switch
        {
            WhisparrEntityKind.Studio when target.Reads is IWhisparrStudioActing acting
                => foreignId => ActingOn(acting, foreignId, scope),
            WhisparrEntityKind.Performer when target.Reads is IWhisparrPerformerActing acting
                => foreignId => ActingOn(acting, foreignId),
            _ => null,
        };

    // The only place the search role is obtained. An instance declaring no search hands over no
    // implementation, so the refusal is at the caller rather than inside a member that declines.
    private static IWhisparrSearchGrabbing? SearchGrabbingOn(MonitoringTarget target)
        => target.Reads as IWhisparrSearchGrabbing;

    private static Func<string, HeldActing>? HeldActingFor(
        WhisparrEntityKind kind, MonitoringTarget target)
        => kind switch
        {
            WhisparrEntityKind.Studio when target.Reads is IWhisparrStudioActing acting
                => foreignId => HeldOn(acting, foreignId),
            WhisparrEntityKind.Performer when target.Reads is IWhisparrPerformerActing acting
                => foreignId => HeldOn(acting, foreignId),
            _ => null,
        };

    // A failure is contained rather than propagated, because the route's declared results hold no
    // failure. A shutdown rethrows: it is not a verdict about the instance.
    // The filter names IOException as well as HttpRequestException. A body is read out of the
    // response stream, so a connection dropped part way through an answer raises the former.
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
                log,
                target.Binding.Generation,
                WhisparrSyncLog.Classify(failure),
                target.Binding.BaseAddress.Host);
            return null;
        }
    }
}
