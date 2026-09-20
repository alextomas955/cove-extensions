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
    /// <summary>What the connected instance is, and what its generation can honour.</summary>
    /// <remarks>
    /// The stored default scope rides here because the same load that resolved the connection read
    /// it, so an acting path takes it without issuing a second read.
    /// </remarks>
    private sealed record MonitoringTarget(
        WhisparrGeneration Generation,
        Uri BaseAddress,
        string ApiKey,
        WhisparrCapabilitySet Capabilities,
        IWhisparrClient Reads,
        MonitorScope DefaultMonitorScope);

    /// <summary>The instance to act against, or null when none is configured.</summary>
    /// <summary>Records what one run established about the Cove library roots it reached.</summary>
    /// <remarks>
    /// A run that reached no folder at all writes nothing: it established nothing about any root, and
    /// its zero counts are the run never having been aimed rather than the roots disagreeing.
    /// <para>
    /// Written outside the run's own cancellation. What a run established about a root holds whether
    /// or not the run went on to finish, and a stopped run that dropped its readings would leave the
    /// settings page asking about roots it had just agreed with.
    /// </para>
    /// </remarks>
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

    /// <summary>The acting verbs one entity kind is monitored through, already aimed.</summary>
    /// <remarks>
    /// One kind's members reduced to what the shared flow needs, so the flow is written once and the
    /// difference between the two kinds is confined to where each is built. The scope is bound where
    /// the studio's verbs are, so no shared step can carry a scope to a kind that expresses none.
    /// </remarks>
    private sealed record KindActing(
        HeldActing Held,
        Func<AddDefaults, CancellationToken, Task<WhisparrResponse>> AddMonitored);

    /// <summary>The verbs an entity the instance ALREADY holds is changed through.</summary>
    /// <remarks>
    /// Separate from the add, and reachable without naming a scope, because unmonitoring names none.
    /// A shared record carrying the add's bound scope would hand every verb a scope its caller never
    /// chose.
    /// <para>
    /// <see cref="SetScope"/> is null for a kind expressing no scope, so a scope cannot reach one
    /// through this seam at all rather than reaching a member that refuses once it is called.
    /// </para>
    /// </remarks>
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
            // Not held is not a refusal: the instance holds no entry, and the entity is simply not
            // monitored yet. The two are answered as separate members, so a reader is not left to
            // infer an absence from an unmonitored flag.
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

    /// <summary>How many of one entity's own files sit under one library root.</summary>
    private static Func<IEntityFolderPort, string, CancellationToken, Task<int>> FilesOfEntity(
        WhisparrEntityKind kind, int coveId)
        => (files, coveRoot, ct) => files.FilesUnderAsync(kind, coveId, coveRoot, ct);

    /// <summary>How many of one video's own files sit under one library root.</summary>
    private static Func<IEntityFolderPort, string, CancellationToken, Task<int>> FilesOfVideo(
        int videoId)
        => (files, coveRoot, ct) => files.VideoFilesUnderAsync(videoId, coveRoot, ct);

    /// <summary>
    /// Composes an add's root per entity from a path holding no elevated services of its own.
    /// </summary>
    /// <remarks>
    /// The count is taken inside a System scope. Cove's per-principal query filters answer a reader
    /// with the rows that reader can see, so a count taken as the caller would report an entity that
    /// holds files as holding none, and the add would then go to the wrong root with no error.
    /// <para>
    /// What the addressing established about each root is recorded, the same way a run records it.
    /// This is the doorway a single gesture comes through, and its refusal tells the reader to state
    /// the folder's path on the settings page. That page offers a root only once something has
    /// recorded a reading for it, so without this the sentence names a remedy the page cannot show.
    /// </para>
    /// </remarks>
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

    /// <summary>Composes an add's root per entity inside a run's own elevated services.</summary>
    private static Func<AddDefaults, CancellationToken, Task<EntityAddDefaultsResolution>>
        EntityRootIn(
            IServiceProvider services,
            MonitoringTarget target,
            Func<IEntityFolderPort, string, CancellationToken, Task<int>> countUnder)
        => (runWide, ct) =>
            ComposeWithEntityRootAsync(services, target, countUnder, runWide, observe: null, ct);

    /// <summary>The one place every add body's root is composed, whatever doorway reached it.</summary>
    /// <remarks>
    /// An observer is told what the addressing established about each root it was asked about, and
    /// is null where the caller records nothing. A run keeps its own loop over the roots and records
    /// from there; a single gesture has no such loop, so this is where it learns the same thing.
    /// </remarks>
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

    /// <summary>Monitors one entity, at the scope <paramref name="actingFor"/> was armed with.</summary>
    /// <remarks>
    /// The scope reaches the instance through the arm and is never answered from here: every branch
    /// answers a read, for the reason <see cref="ScopeHeld"/> states. So a caller composing a scope
    /// supplies it once, to <paramref name="actingFor"/>, and reads the result back off the
    /// instance's own answer.
    /// </remarks>
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

    /// <summary>Reads the entity again and answers the state that read reports.</summary>
    /// <remarks>
    /// The evidence a write took effect is a later read rather than the write's own status. This
    /// generation answers an add it did not understand with a created status and an echo showing the
    /// monitored field dropped, so an accepted write the instance then reports unmonitored is a
    /// refusal.
    /// <para>
    /// The scope answered is the read's own, for the reason <see cref="ScopeHeld"/> states: what an
    /// add composed and what a later read reports are different facts, and this generation answers a
    /// body whose fields it dropped with a success. So a composed scope cannot stand in for one.
    /// </para>
    /// <para>
    /// A read that cannot be classified is a refusal too: what the instance holds is then unknown,
    /// and unknown is not evidence.
    /// </para>
    /// <para>
    /// The refusal it answers is <see cref="MonitorRefusalKind.InstanceDidNotReportTheChange"/> and
    /// not the refusal a read's own classification names. This site is reached only after a write was
    /// accepted, so an absence here is not "there was nothing to act on" and a reading this product
    /// rejected is not "nothing was changed": the sentences those kinds carry would tell a reader
    /// that nothing happened when something may well have. A refusal the answering seam read for
    /// itself is kept, because that names this product's own limit rather than what the instance now
    /// holds.
    /// </para>
    /// <para>
    /// The cost is one more outbound read per entity, which a batch pays per selected entity. It
    /// already issues a read and a write for each, so this is what turns a reported outcome into an
    /// observed one for a third of an increase.
    /// </para>
    /// </remarks>
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

    /// <summary>Whether <paramref name="kind"/> expresses a monitor scope at all.</summary>
    /// <remarks>
    /// Transcribed rather than derived from the acting seam, so a kind added later is classified by
    /// whoever adds it. The field a narrower scope is carried in exists on one resource only.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="kind"/> is not a kind this product expresses.
    /// </exception>
    private static bool ExpressesAScope(WhisparrEntityKind kind)
        => kind switch
        {
            WhisparrEntityKind.Studio => true,
            WhisparrEntityKind.Performer => false,
            _ => throw new ArgumentOutOfRangeException(
                nameof(kind), kind, "This is not an entity kind this product expresses."),
        };

    /// <summary>
    /// Resolves identity, reads the entity, and applies <paramref name="change"/> to one the instance
    /// holds.
    /// </summary>
    /// <remarks>
    /// The order is the monitor route's own: identity first, so a refusal costs no outbound request,
    /// then the entity itself. Nothing the instance holds is nothing to change, and it is not
    /// monitored either, so that answers the current state and sends nothing.
    /// <para>
    /// The instance-side row id is read from the entity's own record rather than substituted. It
    /// exists only for an entity the instance holds, so an absent one is refused rather than guessed.
    /// </para>
    /// </remarks>
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

    /// <summary>Turns monitoring on for an entity the instance already holds.</summary>
    /// <remarks>
    /// A held entity keeps its own profile, root folder, tags and date gate: only the flag is sent,
    /// and every other field of the editor resource is left unset because an unset field is not
    /// applied. Reporting the click as done without sending the flip would be a success for something
    /// that did not happen.
    /// </remarks>
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
            // Nothing is sent, so the read in hand IS the state: both the flag and the date gate it
            // reports are what the entity is left at.
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

    /// <summary>Which refusal a classified answer states.</summary>
    /// <remarks>
    /// Total over both members of the answer, in this order. A refusal the answering seam read for
    /// itself outranks the status, for the reason <see cref="MonitoringProjector.Classify"/> states.
    /// A not-held reading is its own fact: the instance reported an absence rather than declining, and
    /// a reader acts on the two differently. Everything left is the instance refusing, which includes
    /// a held entity the flow rejected for a reason of its own, such as one carrying no
    /// instance-side id.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="answer"/> carries a reading this product does not express. Every reading is
    /// named, so one added later stops here rather than arriving under whichever arm a fallthrough
    /// chose.
    /// </exception>
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

    /// <summary>
    /// Which refusal to answer, given the two reasons a resolved flow can observe.
    /// </summary>
    /// <remarks>
    /// A connection has already been established wherever this is reached, so the first reason of the
    /// precedence cannot hold here. The order among the rest is not restated: it is read from the one
    /// place that states it, so a change there moves every route at once.
    /// </remarks>
    private static MonitorRefusalKind RefusalAmong(
        bool capabilityAbsent, MonitorRefusalKind identityRefusal)
        => MonitoringProjector.FirstRefusal(new MonitoringProjector.MonitorReasons(
            NoConnectionConfigured: false,
            CapabilityAbsentOnThisGeneration: capabilityAbsent,
            IdentityRefusal: identityRefusal));

    /// <summary>How one entity is read, or null where the generation cannot read that kind.</summary>
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

    /// <summary>How one entity is monitored, or null where the generation cannot monitor that kind.</summary>
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

    /// <summary>
    /// How one entity the instance holds is changed, or null where the generation cannot.
    /// </summary>
    /// <summary>
    /// How <paramref name="target"/> is asked to search, or null where its generation holds no search
    /// at all.
    /// </summary>
    /// <remarks>
    /// Obtained BY NAME, and this is the only place in the product that does so, which is what makes
    /// "a call site that never asks for the role cannot express the request" a property of the type
    /// set rather than a habit. A generation holding no search has no implementation to hand over,
    /// which is a refusal at the caller rather than a member that accepts the call and declines it.
    /// <para>
    /// Both gestures that can reach a search come through here: the one over a single entity and the
    /// one over a selection. A third would have to be written against this same member.
    /// </para>
    /// </remarks>
    private static IWhisparrSearchGrabbing? SearchGrabbingOn(MonitoringTarget target)
        => target.Capabilities.Obtain<IWhisparrSearchGrabbing>()
            .Match<IWhisparrSearchGrabbing?>(held => held, _ => null);

    /// <summary>
    /// The instance's own identifier for one selected entity, or why the instance cannot be told to
    /// search it.
    /// </summary>
    /// <remarks>
    /// The same order the single-entity route takes: identity first, so an entity carrying no link
    /// costs no outbound request, then the instance's own record, which is what establishes that it
    /// holds the entity at all. An entity it does not hold monitors nothing there.
    /// </remarks>
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

    /// <summary>Nothing to act through for a kind no route has an arm for.</summary>
    /// <remarks>
    /// The capability table is the authority, so a generation that HOLDS the capability while no
    /// route can act on it is a fault rather than a refusal: a capability is registered with the
    /// member that honours it, never ahead of it, and reporting a gap that does not exist would send
    /// the user to a sentence about their instance.
    /// </remarks>
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

    /// <summary>The scope the entity <paramref name="body"/> describes is held at.</summary>
    /// <remarks>
    /// The instance's own answer is the only source. Neither acting path may substitute the scope it
    /// asked for here: what an add composed and what a later read reports are different facts, and
    /// this generation answers a body whose fields it dropped with a success.
    /// </remarks>
    private static MonitorScope? ScopeHeld(
        WhisparrEntityKind kind, MonitoringTarget target, bool monitored, string? body)
        => MonitoringProjector.ScopeIn(kind, target.Generation, monitored, body);

    /// <summary>
    /// <paramref name="request"/>'s answer, or null when it produced none.
    /// </summary>
    /// <remarks>
    /// Contained rather than propagated: it is raised into a route whose declared results hold no
    /// failure. Exactly one line is emitted, from a filter naming every exception it contains, and a
    /// named outcome is returned. A shutdown rethrows, because it is not a verdict about the
    /// instance.
    /// <para>
    /// The filter names an I/O failure as well as a request one. The client reads a body out of the
    /// response stream, so a connection dropped part way through an answer raises
    /// <see cref="IOException"/> rather than <see cref="HttpRequestException"/>; a batch that let one
    /// escape would lose the record of every entity it had already acted on.
    /// </para>
    /// </remarks>
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
