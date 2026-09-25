using Cove.Core.Auth;
using Cove.Extensions.Shared;
using Cove.Sdk;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using WhisparrSync.Connection;
using WhisparrSync.Contracts;
using WhisparrSync.Jobs;
using WhisparrSync.Monitoring;
using WhisparrSync.Whisparr;

namespace WhisparrSync;

public sealed partial class WhisparrSync
{
    private void MapMonitoringEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(MonitoringReadRoute,
            ([AsParameters] EntityRoute route, ICurrentPrincipalAccessor principal, WhisparrAccess whisparr, IEntityIdentityPort identities,
             CancellationToken ct)
                => ReadEntityMonitoringAsync(
                    route, principal, whisparr, identities, ct))
            .WithTags(WireTag)
            .RequireCovePermission(PermissionMode.Any, ReadPermissions);

        // No entity in the route: the selection bar's menu follows the connection, and naming one
        // would cost a read of the instance for facts this answers from stored settings.
        endpoints.MapGet(ConnectionOfferRoute,
            (ICurrentPrincipalAccessor principal, WhisparrAccess whisparr, CancellationToken ct)
                => ReadConnectionOfferAsync(principal, whisparr, ct))
            .WithTags(WireTag)
            .RequireCovePermission(PermissionMode.Any, ReadPermissions);

        endpoints.MapPost(MonitorRoute,
            ([AsParameters] EntityRoute route, MonitorEntityRequest request,
             ICurrentPrincipalAccessor principal, WhisparrAccess whisparr, IEntityIdentityPort identities, BackgroundWork work, CancellationToken ct)
                => MonitorEntityAsync(
                    route, request, principal, whisparr, identities, work, ct))
            .WithTags(WireTag)
            .RequireCovePermission(PermissionMode.Any, ConfigurePermissions);

        endpoints.MapPost(UnmonitorRoute,
            ([AsParameters] EntityRoute route, ICurrentPrincipalAccessor principal, WhisparrAccess whisparr, IEntityIdentityPort identities,
             CancellationToken ct)
                => UnmonitorEntityAsync(
                    route, principal, whisparr, identities, ct))
            .WithTags(WireTag)
            .RequireCovePermission(PermissionMode.Any, ConfigurePermissions);

        endpoints.MapPost(SearchAllMonitoredRoute,
            ([AsParameters] EntityRoute route, ICurrentPrincipalAccessor principal, WhisparrAccess whisparr, IEntityIdentityPort identities,
             CancellationToken ct)
                => SearchAllMonitoredEntityAsync(
                    route, principal, whisparr, identities, ct))
            .WithTags(WireTag)
            .RequireCovePermission(PermissionMode.Any, ConfigurePermissions);

        endpoints.MapPost(MonitorScopeRoute,
            ([AsParameters] EntityRoute route, MonitorEntityRequest request,
             ICurrentPrincipalAccessor principal, WhisparrAccess whisparr, IEntityIdentityPort identities, CancellationToken ct)
                => SetMonitorScopeAsync(
                    route, request, principal, whisparr, identities, ct))
            .WithTags(WireTag)
            .RequireCovePermission(PermissionMode.Any, ConfigurePermissions);
    }

    // Live on every read, holding nothing: no cache and no stored per-entity row, which would be a
    // table growing with the library.
    //
    // The read tier, checked before the store, so a principal without it causes no read.
    internal static async Task<Results<Ok<EntityMonitoringView>, BadRequest, ForbiddenCode>>
        ReadEntityMonitoringAsync(
            EntityRoute route,
            ICurrentPrincipalAccessor principal,
            WhisparrAccess whisparr,
            IEntityIdentityPort identities,
            CancellationToken ct)
    {
        var (kind, coveId) = route;

        var (_, _, _, log) = whisparr;

        if (!HasReadPermission(principal))
        {
            return new ForbiddenCode();
        }

        // Both halves in one expression, at every entity route. The parse succeeds for an integer
        // naming no member, and every arm below throws for a kind it cannot express, so the parse
        // alone lets untrusted route input reach a throw inside a handler whose declared results hold
        // no failure.
        if (!Enum.TryParse<WhisparrEntityKind>(kind, ignoreCase: true, out var entityKind)
            || !Enum.IsDefined(entityKind))
        {
            return TypedResults.BadRequest();
        }

        ArgumentNullException.ThrowIfNull(identities);

        if (await ResolveTargetAsync(whisparr, ct).ConfigureAwait(false)
            is not { } target)
        {
            return TypedResults.Ok(EntityMonitoringView.NotConfigured(entityKind));
        }

        var reading = await ReadResolvedAsync(
            entityKind, coveId, target, identities, log, ReadingEntity(entityKind, target), ct)
            .ConfigureAwait(false);

        return TypedResults.Ok(reading);
    }

    // Answered from the stored connection alone. Nothing outbound is sent, so the menu this fills
    // opens at the same speed whatever the instance is doing.
    internal static async Task<Results<Ok<WhisparrConnectionOffer>, ForbiddenCode>>
        ReadConnectionOfferAsync(
            ICurrentPrincipalAccessor principal,
            WhisparrAccess whisparr,
            CancellationToken ct)
    {
        if (!HasReadPermission(principal))
        {
            return new ForbiddenCode();
        }

        return await ResolveTargetAsync(whisparr, ct).ConfigureAwait(false)
            is not { } target
                ? TypedResults.Ok(WhisparrConnectionOffer.NotConfigured)
                : TypedResults.Ok(
                    new WhisparrConnectionOffer(
                        target.Binding.Generation,
                        GenerationCapabilities.CapabilitiesOf(target.Binding.Generation),
                        true,
                        GenerationCapabilities.AScopeChangeIsRetroactiveOn(
                            target.Binding.Generation)));
    }

    // The body carries a scope and nothing else: the entity comes from the stored identity row, so
    // an identifier in the body reaches nothing. The gate is checked before the body is read.
    //
    // Order: identity, so a refusal costs no request; then the entity, because one the instance
    // holds keeps its own add defaults and reading them would invite sending them over a user's
    // values; then the defaults. An accepted monitor enqueues the reflect-owned run rather than
    // awaiting it, which would make the click as long as the entity.
    internal async Task<Results<Ok<EntityMonitoringView>, BadRequest, ForbiddenCode>>
        MonitorEntityAsync(
            EntityRoute route,
            MonitorEntityRequest request,
            ICurrentPrincipalAccessor principal,
            WhisparrAccess whisparr,
            IEntityIdentityPort identities,
            BackgroundWork work,
            CancellationToken ct)
    {
        var (_, scopes) = work;

        var (kind, coveId) = route;

        var (_, _, _, log) = whisparr;

        if (!HasConfigurePermission(principal))
        {
            return new ForbiddenCode();
        }

        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(identities);

        if (!Enum.TryParse<WhisparrEntityKind>(kind, ignoreCase: true, out var entityKind)
            || !Enum.IsDefined(entityKind))
        {
            return TypedResults.BadRequest();
        }

        if (await ResolveTargetAsync(whisparr, ct).ConfigureAwait(false)
            is not { } target)
        {
            return TypedResults.Ok(EntityMonitoringView.NotConfigured(entityKind));
        }

        // The stored default rather than the instance's own, read off the load that resolved the
        // connection rather than through a second one.
        var scope = request.Scope ?? target.DefaultMonitorScope;

        // No scope reaches the performer arm: the field a future-only scope is expressed through
        // exists on the studio resource and on no other.
        var monitoring = await MonitorResolvedAsync(
            new MonitoredEntity(entityKind, coveId),
            target,
            identities,
            log,
            ActingFor(entityKind, target, scope),
            EntityRootThrough(scopes, target, FilesOfEntity(entityKind, coveId)),
            ct).ConfigureAwait(false);

        // Enqueued here and not in the resolved member the bulk path also reaches: a selection of a
        // thousand entities must not become a thousand background runs.
        if (monitoring is { Refusal: MonitorRefusalKind.None, Monitored: true })
        {
            EnqueueReflectOwned(work, entityKind, coveId);
        }

        return TypedResults.Ok(monitoring);
    }

    // No body: which entity is named by the route, and the identifier the instance is given comes
    // from the stored identity row, so a refusal happens before any outbound request.
    //
    // Setting the flag false governs what a later catalogue addition does and retracts nothing
    // already wanted. An entity the instance does not hold is already not monitored, so that answers
    // the current state rather than a refusal and nothing is sent.
    internal static async Task<Results<Ok<EntityMonitoringView>, BadRequest, ForbiddenCode>>
        UnmonitorEntityAsync(
            EntityRoute route,
            ICurrentPrincipalAccessor principal,
            WhisparrAccess whisparr,
            IEntityIdentityPort identities,
            CancellationToken ct)
    {
        var (kind, coveId) = route;

        var (_, _, _, log) = whisparr;

        if (!HasConfigurePermission(principal))
        {
            return new ForbiddenCode();
        }

        ArgumentNullException.ThrowIfNull(identities);

        if (!Enum.TryParse<WhisparrEntityKind>(kind, ignoreCase: true, out var entityKind)
            || !Enum.IsDefined(entityKind))
        {
            return TypedResults.BadRequest();
        }

        if (await ResolveTargetAsync(whisparr, ct).ConfigureAwait(false)
            is not { } target)
        {
            return TypedResults.Ok(EntityMonitoringView.NotConfigured(entityKind));
        }

        return TypedResults.Ok(
            await UnmonitorResolvedAsync(entityKind, coveId, target, identities, log, ct)
                .ConfigureAwait(false));
    }

    // Separate from the route so the bulk path reaches the same statement of the verb. Two
    // statements of one gesture is how a selection comes to behave differently from a click.
    private static Task<EntityMonitoringView> UnmonitorResolvedAsync(
        WhisparrEntityKind kind,
        int coveId,
        MonitoringTarget target,
        IEntityIdentityPort identities,
        ILogger log,
        CancellationToken ct)
    {
        return ChangingHeldEntityAsync(kind, coveId, target, identities, log, Unmonitoring, ct);

        async Task<EntityMonitoringView> Unmonitoring(
            HeldActing acting, int entityId, bool monitored, CancellationToken changeCt)
        {
            // Present in both arms: this runs only where the read above classified the entity as
            // held.
            if (!monitored)
            {
                return State(kind, target, present: true, monitored: false, scope: null);
            }

            var flipped = await ContainedAsync(
                () => acting.SetMonitored(entityId, false, changeCt), target, log, changeCt)
                .ConfigureAwait(false);

            if (flipped is null)
            {
                return Refused(kind, target, MonitorRefusalKind.InstanceRefused);
            }

            var refused = MonitoringProjector.Accepted(flipped);
            return refused != MonitorRefusalKind.None
                ? Refused(kind, target, refused)
                : State(kind, target, present: true, monitored: false, scope: null);
        }
    }

    // The only route that spends the reader's bandwidth and disk, and the only place
    // IWhisparrSearchGrabbing is obtained. It takes no body, so no field can bind to a permissive
    // default; the entity comes from the route segment and the instance's id from the stored
    // identity row. It has its own path rather than the delegate seam the monitor verbs use, which
    // can express no grabbing verb. The entity is read first, so one the instance does not hold
    // refuses instead of sending.
    internal static async Task<Results<Ok<EntityMonitoringView>, BadRequest, ForbiddenCode>>
        SearchAllMonitoredEntityAsync(
            EntityRoute route,
            ICurrentPrincipalAccessor principal,
            WhisparrAccess whisparr,
            IEntityIdentityPort identities,
            CancellationToken ct)
    {
        var (kind, coveId) = route;

        var (_, _, _, log) = whisparr;

        if (!HasConfigurePermission(principal))
        {
            return new ForbiddenCode();
        }

        ArgumentNullException.ThrowIfNull(identities);

        if (!Enum.TryParse<WhisparrEntityKind>(kind, ignoreCase: true, out var entityKind)
            || !Enum.IsDefined(entityKind))
        {
            return TypedResults.BadRequest();
        }

        if (await ResolveTargetAsync(whisparr, ct).ConfigureAwait(false)
            is not { } target)
        {
            return TypedResults.Ok(EntityMonitoringView.NotConfigured(entityKind));
        }

        var grabbing = SearchGrabbingOn(target);
        var reading = HeldActingFor(entityKind, target);

        // Identity first, so a refusal costs no outbound request. The outbound identifier is resolved
        // server-side from the stored rows, and nothing a caller supplied reaches it.
        var identity = await identities.ResolveAsync(entityKind, coveId, target.Binding.Generation, ct)
            .ConfigureAwait(false);

        if (grabbing is null || reading is not { } actingFor || identity.ForeignId is not { } named)
        {
            return TypedResults.Ok(
                Refused(
                    entityKind,
                    target,
                    RefusalAmong(grabbing is null || reading is null, identity.Refusal)));
        }

        var read = await ContainedAsync(() => actingFor(named).ReadEntity(ct), target, log, ct)
            .ConfigureAwait(false);

        if (read is null)
        {
            return TypedResults.Ok(Refused(entityKind, target, MonitorRefusalKind.InstanceRefused));
        }

        var answer = MonitoringProjector.Classify(read);
        if (answer.Reading != MonitoringProjector.EntityReading.Held
            || MonitoringProjector.EntityIdIn(read.Body) is not { } entityId)
        {
            return TypedResults.Ok(Refused(entityKind, target, RefusalIn(answer)));
        }

        // Read before the search and answered after it: a search changes what the instance goes
        // looking for and never the flag, so reporting the flag the search itself set would report
        // something that did not happen.
        var monitored = MonitoringProjector.MonitoredIn(read.Body);

        var searched = await ContainedAsync(
            () => grabbing.SearchMonitoredAsync(
                entityKind, [entityId], ct),
            target,
            log,
            ct).ConfigureAwait(false);

        return TypedResults.Ok(
            searched is null
                || MonitoringProjector.Accepted(searched) != MonitorRefusalKind.None
                    ? Refused(entityKind, target, MonitorRefusalKind.InstanceRefused)
                    : State(
                        entityKind,
                        target,
                        present: true,
                        monitored,
                        ScopeHeld(entityKind, target, monitored, read.Body)));
    }

    // A kind expressing no scope answers a bad request rather than a refusal: the field a scope is
    // carried in exists on one resource only, so a scope named for any other kind is a request the
    // contract cannot express.
    //
    // The flag is left as the instance reports it. Widening a scope is not the same gesture as
    // monitoring, so reporting a monitored state the caller did not ask for would report something
    // that did not happen.
    internal static async Task<Results<Ok<EntityMonitoringView>, BadRequest, ForbiddenCode>>
        SetMonitorScopeAsync(
            EntityRoute route,
            MonitorEntityRequest request,
            ICurrentPrincipalAccessor principal,
            WhisparrAccess whisparr,
            IEntityIdentityPort identities,
            CancellationToken ct)
    {
        var (kind, coveId) = route;

        var (_, _, _, log) = whisparr;

        if (!HasConfigurePermission(principal))
        {
            return new ForbiddenCode();
        }

        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(identities);

        if (!Enum.TryParse<WhisparrEntityKind>(kind, ignoreCase: true, out var entityKind)
            || !Enum.IsDefined(entityKind)
            || !ExpressesAScope(entityKind)
            || request.Scope is not { } scope)
        {
            return TypedResults.BadRequest();
        }

        if (await ResolveTargetAsync(whisparr, ct).ConfigureAwait(false)
            is not { } target)
        {
            return TypedResults.Ok(EntityMonitoringView.NotConfigured(entityKind));
        }

        return TypedResults.Ok(
            await ChangingHeldEntityAsync(entityKind, coveId, target, identities, log, Scoping, ct)
                .ConfigureAwait(false));

        async Task<EntityMonitoringView> Scoping(
            HeldActing acting, int entityId, bool monitored, CancellationToken changeCt)
        {
            if (acting.SetScope is not { } setScope)
            {
                throw new InvalidOperationException(
                    $"A {entityKind} expresses no monitor scope, so this route must not reach it.");
            }

            var applied = await ContainedAsync(
                () => setScope(entityId, scope, changeCt), target, log, changeCt).ConfigureAwait(false);

            // The scope just applied, not one read back. An entity nothing monitors has no scope in
            // force whatever was written, so that answers none.
            MonitorScope? inForce = monitored ? scope : null;

            if (applied is null)
            {
                return Refused(entityKind, target, MonitorRefusalKind.InstanceRefused);
            }

            var refused = MonitoringProjector.Accepted(applied);
            return refused != MonitorRefusalKind.None
                ? Refused(entityKind, target, refused)
                : State(entityKind, target, present: true, monitored, inForce);
        }
    }
}
