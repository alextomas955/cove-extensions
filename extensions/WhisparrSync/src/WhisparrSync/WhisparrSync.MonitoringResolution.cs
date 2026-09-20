using Cove.Extensions.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WhisparrSync.Addressing;
using WhisparrSync.Connection;
using WhisparrSync.Contracts;
using WhisparrSync.Import;
using WhisparrSync.Jobs;
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
        var apiKey = await credentials.ReadAsync(generation, ct).ConfigureAwait(false);

        // Refused here rather than by handing an empty pair to the client, so an unconfigured
        // connection reaches nothing that could make a request.
        return ConnectionTester.TryReadConnection(
                stored.ConnectionFor(generation)?.Address, apiKey, out var baseAddress, out _)
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

    private static async Task<EntityMonitoringView> ReadResolvedAsync(
        WhisparrEntityKind kind,
        int coveId,
        MonitoringTarget target,
        IEntityIdentityPort identities,
        ILogger log,
        Func<string, CancellationToken, Task<WhisparrResponse>>? readEntity,
        CancellationToken ct)
    {
        var identity = await identities.ResolveAsync(kind, coveId, target.Generation, ct)
            .ConfigureAwait(false);

        if (readEntity is not { } reading || identity.ForeignId is not { } foreignId)
        {
            return Refused(kind, target, RefusalAmong(readEntity is null, identity.Refusal));
        }

        var read = await ContainedAsync(
            () => reading(foreignId, ct), target, log, ct).ConfigureAwait(false);

        if (read is null)
        {
            return Refused(kind, target, MonitorRefusalKind.InstanceRefused);
        }

        var answer = MonitoringProjector.Classify(read);
        return answer.Reading switch
        {
            // Not held is not a refusal: the instance holds no entry, so the entity is simply not
            // monitored yet.
            MonitoringProjector.EntityReading.NotHeld
                => State(kind, target, present: false, monitored: false, scope: null),
            MonitoringProjector.EntityReading.Held => Held(read.Body),
            _ => Refused(kind, target, RefusalIn(answer)),
        };

        EntityMonitoringView Held(string body)
        {
            var presence = MonitoringProjector.PresenceOf(
                MonitoringProjector.EntityReading.Held, body);
            var monitored = presence.Monitored ?? false;
            return State(
                kind, target, present: true, monitored, ScopeHeld(kind, target, monitored, body));
        }
    }

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

    // The scope reaches the instance through actingFor and is never answered from here: every
    // branch answers a read, for the reason ScopeHeld states.
    private static async Task<EntityMonitoringView> MonitorResolvedAsync(
        WhisparrEntityKind kind,
        int coveId,
        MonitoringTarget target,
        IEntityIdentityPort identities,
        ILogger log,
        Func<string, KindActing>? actingFor,
        Func<AddDefaults, CancellationToken, Task<EntityAddDefaultsResolution>> composeAdd,
        CancellationToken ct)
    {
        var identity = await identities.ResolveAsync(kind, coveId, target.Generation, ct)
            .ConfigureAwait(false);

        if (actingFor is not { } aiming || identity.ForeignId is not { } foreignId)
        {
            return Refused(kind, target, RefusalAmong(actingFor is null, identity.Refusal));
        }

        var acting = aiming(foreignId);
        var read = await ContainedAsync(() => acting.Held.ReadEntity(ct), target, log, ct)
            .ConfigureAwait(false);
        if (read is null)
        {
            return Refused(kind, target, MonitorRefusalKind.InstanceRefused);
        }

        var answer = MonitoringProjector.Classify(read);
        switch (answer.Reading)
        {
            case MonitoringProjector.EntityReading.Held:
                return await MonitorHeldEntityAsync(kind, read.Body, target, acting, log, ct)
                    .ConfigureAwait(false);
            case MonitoringProjector.EntityReading.NotHeld:
                break;
            default:
                return Refused(kind, target, RefusalIn(answer));
        }

        var profiles = await ContainedAsync(
            () => target.Reads.ReadQualityProfilesAsync(target.BaseAddress, target.ApiKey, ct),
            target,
            log,
            ct).ConfigureAwait(false);
        var roots = profiles is null
            ? null
            : await ContainedAsync(
                () => target.Reads.ReadRootFoldersAsync(target.BaseAddress, target.ApiKey, ct),
                target,
                log,
                ct).ConfigureAwait(false);
        if (profiles is null || roots is null)
        {
            return Refused(kind, target, MonitorRefusalKind.InstanceRefused);
        }

        var defaults = AddDefaultsProjector.From(profiles.Body, roots.Body);
        if (defaults.Defaults is not { } runWide)
        {
            return Refused(kind, target, defaults.Refusal);
        }

        var composed = await composeAdd(runWide, ct).ConfigureAwait(false);
        if (composed.Defaults is not { } composeWith)
        {
            return Refused(kind, target, composed.Refusal);
        }

        var added = await ContainedAsync(
            () => acting.AddMonitored(composeWith, ct), target, log, ct).ConfigureAwait(false);

        if (added is null)
        {
            return Refused(kind, target, MonitorRefusalKind.InstanceRefused);
        }

        var refused = MonitoringProjector.Accepted(added);
        return refused != MonitorRefusalKind.None
            ? Refused(kind, target, refused)
            : await ReadBackMonitoredAsync(kind, target, acting, log, ct)
                .ConfigureAwait(false);
    }

    // The evidence a write took effect is a later read, not the write's own status: this generation
    // answers an add it did not understand with a created status and an echo whose monitored field
    // is dropped. A read that cannot be classified is a refusal too, since what the instance holds
    // is then unknown.
    // The refusal answered is InstanceDidNotReportTheChange rather than the one a read's own
    // classification names, because a write was already accepted here and the other kinds say
    // nothing happened.
    private static async Task<EntityMonitoringView> ReadBackMonitoredAsync(
        WhisparrEntityKind kind,
        MonitoringTarget target,
        KindActing acting,
        ILogger log,
        CancellationToken ct)
    {
        var read = await ContainedAsync(() => acting.Held.ReadEntity(ct), target, log, ct)
            .ConfigureAwait(false);

        if (read is null)
        {
            return Refused(kind, target, MonitorRefusalKind.InstanceRefused);
        }

        var answer = MonitoringProjector.Classify(read);
        if (answer.Reading == MonitoringProjector.EntityReading.Held
            && MonitoringProjector.MonitoredIn(read.Body))
        {
            return State(
                kind,
                target,
                present: true,
                monitored: true,
                ScopeHeld(kind, target, monitored: true, read.Body));
        }

        var refusal = answer.Refusal is MonitorRefusalKind.None
            ? MonitorRefusalKind.InstanceDidNotReportTheChange
            : answer.Refusal;

        return Refused(kind, target, refusal);
    }

    // The field a narrower scope is carried in exists on the studio resource only.
    private static bool ExpressesAScope(WhisparrEntityKind kind)
        => kind switch
        {
            WhisparrEntityKind.Studio => true,
            WhisparrEntityKind.Performer => false,
            _ => throw new ArgumentOutOfRangeException(
                nameof(kind), kind, "This is not an entity kind this product expresses."),
        };

    // Identity is resolved first, so a refusal costs no outbound request.
    // The instance-side row id is read from the entity's own record rather than substituted: it
    // exists only for an entity the instance holds.
    private static async Task<EntityMonitoringView> ChangingHeldEntityAsync(
        WhisparrEntityKind kind,
        int coveId,
        MonitoringTarget target,
        IEntityIdentityPort identities,
        ILogger log,
        Func<HeldActing, int, bool, CancellationToken, Task<EntityMonitoringView>> change,
        CancellationToken ct)
    {
        var acting = HeldActingFor(kind, target);
        var identity = await identities.ResolveAsync(kind, coveId, target.Generation, ct)
            .ConfigureAwait(false);

        if (acting is not { } actingFor || identity.ForeignId is not { } named)
        {
            return Refused(kind, target, RefusalAmong(acting is null, identity.Refusal));
        }

        var held = actingFor(named);
        var read = await ContainedAsync(() => held.ReadEntity(ct), target, log, ct)
            .ConfigureAwait(false);
        if (read is null)
        {
            return Refused(kind, target, MonitorRefusalKind.InstanceRefused);
        }

        var answer = MonitoringProjector.Classify(read);
        switch (answer.Reading)
        {
            case MonitoringProjector.EntityReading.Held:
                break;
            case MonitoringProjector.EntityReading.NotHeld:
                return State(kind, target, present: false, monitored: false, scope: null);
            default:
                return Refused(kind, target, RefusalIn(answer));
        }

        return MonitoringProjector.EntityIdIn(read.Body) is { } entityId
            ? await change(held, entityId, MonitoringProjector.MonitoredIn(read.Body), ct)
                .ConfigureAwait(false)
            : Refused(kind, target, MonitorRefusalKind.InstanceRefused);
    }

    // Only the monitored flag is sent. Every other field of the editor resource is left unset,
    // because an unset field is not applied and the held entity keeps its own profile, root folder,
    // tags and date gate.
    private static async Task<EntityMonitoringView> MonitorHeldEntityAsync(
        WhisparrEntityKind kind,
        string body,
        MonitoringTarget target,
        KindActing acting,
        ILogger log,
        CancellationToken ct)
    {
        if (MonitoringProjector.MonitoredIn(body))
        {
            // Nothing is sent, so the read in hand is the state.
            return State(
                kind,
                target,
                present: true,
                monitored: true,
                ScopeHeld(kind, target, monitored: true, body));
        }

        if (MonitoringProjector.EntityIdIn(body) is not { } entityId)
        {
            return Refused(kind, target, MonitorRefusalKind.InstanceRefused);
        }

        var flipped = await ContainedAsync(
            () => acting.Held.SetMonitored(entityId, true, ct), target, log, ct).ConfigureAwait(false);

        // Classified from a read for the same reason the add branch is, stated at ReadBackMonitoredAsync.
        if (flipped is null)
        {
            return Refused(kind, target, MonitorRefusalKind.InstanceRefused);
        }

        var refused = MonitoringProjector.Accepted(flipped);
        return refused != MonitorRefusalKind.None
            ? Refused(kind, target, refused)
            : await ReadBackMonitoredAsync(kind, target, acting, log, ct)
                .ConfigureAwait(false);
    }

    // A refusal the answering seam read for itself outranks the status, for the reason
    // MonitoringProjector.Classify states.
    private static MonitorRefusalKind RefusalIn(MonitoringProjector.EntityAnswer answer)
        => (answer.Refusal, answer.Reading) switch
        {
            (not MonitorRefusalKind.None, _) => answer.Refusal,
            (_, MonitoringProjector.EntityReading.NotHeld)
                => MonitorRefusalKind.InstanceHoldsNoSuchEntity,
            (_, MonitoringProjector.EntityReading.Held or MonitoringProjector.EntityReading.Refused)
                => MonitorRefusalKind.InstanceRefused,
            _ => throw new ArgumentOutOfRangeException(
                nameof(answer), answer.Reading, "This reading has no refusal written down for it."),
        };

    // A connection has already been established wherever this is reached, so the first reason of
    // the precedence cannot hold. The precedence itself is read from MonitoringProjector.
    private static MonitorRefusalKind RefusalAmong(
        bool capabilityAbsent, MonitorRefusalKind identityRefusal)
        => MonitoringProjector.FirstRefusal(new MonitoringProjector.MonitorReasons(
            NoConnectionConfigured: false,
            CapabilityAbsentOnThisGeneration: capabilityAbsent,
            IdentityRefusal: identityRefusal));

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

    private static async Task<MonitoringBulkJob.MonitorBulkAim> AimSearchAsync(
        WhisparrEntityKind kind,
        int coveId,
        MonitoringTarget target,
        IEntityIdentityPort identities,
        bool searchHeld,
        ILogger log,
        CancellationToken ct)
    {
        var reading = HeldActingFor(kind, target);
        var identity = await identities.ResolveAsync(kind, coveId, target.Generation, ct)
            .ConfigureAwait(false);

        if (!searchHeld || reading is not { } actingFor || identity.ForeignId is not { } named)
        {
            return new MonitoringBulkJob.MonitorBulkAim(
                null, RefusalAmong(!searchHeld || reading is null, identity.Refusal));
        }

        var read = await ContainedAsync(() => actingFor(named).ReadEntity(ct), target, log, ct)
            .ConfigureAwait(false);
        if (read is null)
        {
            return new MonitoringBulkJob.MonitorBulkAim(null, MonitorRefusalKind.InstanceRefused);
        }

        var answer = MonitoringProjector.Classify(read);
        return answer.Reading != MonitoringProjector.EntityReading.Held
            || MonitoringProjector.EntityIdIn(read.Body) is not { } entityId
                ? new MonitoringBulkJob.MonitorBulkAim(null, RefusalIn(answer))
                : new MonitoringBulkJob.MonitorBulkAim(entityId, MonitorRefusalKind.None);
    }

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

    private static EntityMonitoringView Refused(
        WhisparrEntityKind kind, MonitoringTarget target, MonitorRefusalKind refusal)
        => EntityMonitoringView.Refused(kind, target.Generation, target.Capabilities.Held, refusal);

    private static EntityMonitoringView State(
        WhisparrEntityKind kind,
        MonitoringTarget target,
        bool present,
        bool monitored,
        MonitorScope? scope)
        => EntityMonitoringView.State(
            kind, target.Generation, target.Capabilities.Held, present, monitored, scope);

    // The instance's own answer is the only source of the scope. Neither acting path may substitute
    // the scope it asked for: this generation answers a body whose fields it dropped with a success.
    private static MonitorScope? ScopeHeld(
        WhisparrEntityKind kind, MonitoringTarget target, bool monitored, string? body)
        => MonitoringProjector.ScopeIn(kind, target.Generation, monitored, body);

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
