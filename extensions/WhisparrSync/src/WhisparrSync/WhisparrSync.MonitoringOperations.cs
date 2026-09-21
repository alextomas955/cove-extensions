using Microsoft.Extensions.Logging;
using WhisparrSync.Contracts;
using WhisparrSync.Jobs;
using WhisparrSync.Monitoring;
using WhisparrSync.Whisparr;

namespace WhisparrSync;

public sealed partial class WhisparrSync
{
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
}
