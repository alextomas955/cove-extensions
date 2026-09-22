using Cove.Core.Auth;
using Cove.Core.Interfaces;
using Cove.Extensions.Shared;
using Cove.Sdk;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WhisparrSync.Connection;
using WhisparrSync.Contracts;
using WhisparrSync.Monitoring;
using WhisparrSync.Options;
using WhisparrSync.Whisparr;

namespace WhisparrSync;

public sealed partial class WhisparrSync
{
    private void MapMonitoringEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(MonitoringReadRoute,
            (string kind, int coveId, ICurrentPrincipalAccessor principal, OptionsStore options,
             ICredentialPort credentials, IWhisparrInstanceFactory instances, IEntityIdentityPort identities,
             CancellationToken ct)
                => ReadEntityMonitoringAsync(
                    kind, coveId, principal, options, credentials, instances, identities, _log, ct))
            .WithTags(WireTag)
            .RequireCovePermission(PermissionMode.Any, ReadPermissions);

        // No entity in the route and none reached: the selection bar's menu follows the connection,
        // so naming an entity here would cost a read of the instance for facts this answers from
        // stored settings.
        endpoints.MapGet(ConnectionOfferRoute,
            (ICurrentPrincipalAccessor principal, OptionsStore options, ICredentialPort credentials,
             IWhisparrInstanceFactory instances, CancellationToken ct)
                => ReadConnectionOfferAsync(principal, options, credentials, instances, ct))
            .WithTags(WireTag)
            .RequireCovePermission(PermissionMode.Any, ReadPermissions);

        endpoints.MapPost(MonitorRoute,
            (string kind, int coveId, MonitorEntityRequest request,
             ICurrentPrincipalAccessor principal, OptionsStore options, ICredentialPort credentials,
             IWhisparrInstanceFactory instances, IEntityIdentityPort identities, IJobService jobs,
             IServiceScopeFactory scopes, CancellationToken ct)
                => MonitorEntityAsync(
                    kind, coveId, request, principal, options, credentials, instances, identities, jobs,
                    scopes, _log, ct))
            .WithTags(WireTag)
            .RequireCovePermission(PermissionMode.Any, ConfigurePermissions);

        // The same tier as the monitor route, and for the same reason: each aims this extension's
        // stored credential at a third party.
        endpoints.MapPost(UnmonitorRoute,
            (string kind, int coveId, ICurrentPrincipalAccessor principal, OptionsStore options,
             ICredentialPort credentials, IWhisparrInstanceFactory instances, IEntityIdentityPort identities,
             CancellationToken ct)
                => UnmonitorEntityAsync(
                    kind, coveId, principal, options, credentials, instances, identities, _log, ct))
            .WithTags(WireTag)
            .RequireCovePermission(PermissionMode.Any, ConfigurePermissions);

        // The configure tier for two reasons: the route aims this extension's stored credential at a
        // third party, and it spends the reader's bandwidth and disk. Its reach is one Cove entity
        // named by the route segment, so it is neither a whole-library verb nor a body-named one.
        endpoints.MapPost(SearchAllMonitoredRoute,
            (string kind, int coveId, ICurrentPrincipalAccessor principal, OptionsStore options,
             ICredentialPort credentials, IWhisparrInstanceFactory instances, IEntityIdentityPort identities,
             CancellationToken ct)
                => SearchAllMonitoredEntityAsync(
                    kind, coveId, principal, options, credentials, instances, identities, _log, ct))
            .WithTags(WireTag)
            .RequireCovePermission(PermissionMode.Any, ConfigurePermissions);

        endpoints.MapPost(MonitorScopeRoute,
            (string kind, int coveId, MonitorEntityRequest request,
             ICurrentPrincipalAccessor principal, OptionsStore options, ICredentialPort credentials,
             IWhisparrInstanceFactory instances, IEntityIdentityPort identities, CancellationToken ct)
                => SetMonitorScopeAsync(
                    kind, coveId, request, principal, options, credentials, instances, identities, _log, ct))
            .WithTags(WireTag)
            .RequireCovePermission(PermissionMode.Any, ConfigurePermissions);
    }

    // Live on every read, holding nothing: no cache and no stored per-entity row, which would be a
    // table growing with the library.
    //
    // The read tier, checked before the store, so a principal without it causes no read.
    internal static async Task<Results<Ok<EntityMonitoringView>, BadRequest, ForbiddenCode>>
        ReadEntityMonitoringAsync(
            string kind,
            int coveId,
            ICurrentPrincipalAccessor principal,
            OptionsStore options,
            ICredentialPort credentials,
            IWhisparrInstanceFactory instances,
            IEntityIdentityPort identities,
            ILogger log,
            CancellationToken ct)
    {
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

        if (await ResolveTargetAsync(options, credentials, instances, ct).ConfigureAwait(false)
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
            OptionsStore options,
            ICredentialPort credentials,
            IWhisparrInstanceFactory instances,
            CancellationToken ct)
    {
        if (!HasReadPermission(principal))
        {
            return new ForbiddenCode();
        }

        return await ResolveTargetAsync(options, credentials, instances, ct).ConfigureAwait(false)
            is not { } target
                ? TypedResults.Ok(WhisparrConnectionOffer.NotConfigured)
                : TypedResults.Ok(
                    new WhisparrConnectionOffer(target.Binding.Generation, target.Capabilities.Held, true));
    }

    // The request carries a scope and nothing else. Which entity the instance is asked about comes
    // from the stored identity row for the Cove entity the route names, so an identifier a caller put
    // in the body reaches nothing.
    //
    // The configure tier: the route aims this extension's stored credential at a third party. The
    // gate is checked before the body is read.
    //
    // The order matters. Identity first, so a refusal happens before any outbound request. Then the
    // entity itself, because one the instance already holds keeps its own add defaults and reading
    // them would invite sending them over values a user chose. Only then the defaults.
    //
    // An accepted monitor enqueues the reflect-owned run rather than awaiting it: the run reads one
    // folder of the entity at a time, and awaiting it would make the click as long as the entity.
    internal async Task<Results<Ok<EntityMonitoringView>, BadRequest, ForbiddenCode>>
        MonitorEntityAsync(
            string kind,
            int coveId,
            MonitorEntityRequest request,
            ICurrentPrincipalAccessor principal,
            OptionsStore options,
            ICredentialPort credentials,
            IWhisparrInstanceFactory instances,
            IEntityIdentityPort identities,
            IJobService jobs,
            IServiceScopeFactory scopes,
            ILogger log,
            CancellationToken ct)
    {
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

        if (await ResolveTargetAsync(options, credentials, instances, ct).ConfigureAwait(false)
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
            entityKind,
            coveId,
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
            EnqueueReflectOwned(jobs, scopes, entityKind, coveId);
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
            string kind,
            int coveId,
            ICurrentPrincipalAccessor principal,
            OptionsStore options,
            ICredentialPort credentials,
            IWhisparrInstanceFactory instances,
            IEntityIdentityPort identities,
            ILogger log,
            CancellationToken ct)
    {
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

        if (await ResolveTargetAsync(options, credentials, instances, ct).ConfigureAwait(false)
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

    // The one route of this extension whose effect spends the reader's bandwidth and disk, and the
    // one place in this product that obtains IWhisparrSearchGrabbing.
    //
    // No request body: there is no verb, scope or identifier member in its input, so a body omitting
    // a field and binding to a permissive default is not expressible here. Which entity is named by
    // the route segment, and the identifier the instance is given comes from the stored identity row
    // and then from the instance's own record.
    //
    // Given its own path from identity to call rather than routed through the shared delegate seam
    // the monitor, unmonitor and scope verbs go through. No delegate that seam takes can express a
    // grabbing verb.
    //
    // The entity is read before anything is asked for: an entity the instance does not hold monitors
    // nothing there, so the read turns that into a refusal instead of a request.
    internal static async Task<Results<Ok<EntityMonitoringView>, BadRequest, ForbiddenCode>>
        SearchAllMonitoredEntityAsync(
            string kind,
            int coveId,
            ICurrentPrincipalAccessor principal,
            OptionsStore options,
            ICredentialPort credentials,
            IWhisparrInstanceFactory instances,
            IEntityIdentityPort identities,
            ILogger log,
            CancellationToken ct)
    {
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

        if (await ResolveTargetAsync(options, credentials, instances, ct).ConfigureAwait(false)
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
            string kind,
            int coveId,
            MonitorEntityRequest request,
            ICurrentPrincipalAccessor principal,
            OptionsStore options,
            ICredentialPort credentials,
            IWhisparrInstanceFactory instances,
            IEntityIdentityPort identities,
            ILogger log,
            CancellationToken ct)
    {
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

        if (await ResolveTargetAsync(options, credentials, instances, ct).ConfigureAwait(false)
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
