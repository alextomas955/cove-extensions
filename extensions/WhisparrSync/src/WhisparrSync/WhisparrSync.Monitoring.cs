using Cove.Core.Auth;
using Cove.Core.Interfaces;
using Cove.Extensions.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
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
    /// <summary>Reads how the connected instance monitors one Cove entity, right now.</summary>
    /// <remarks>
    /// Live on every read, holding nothing: one request per entity page view, no cache and no stored
    /// per-entity row. A stored answer would be a table growing with the library, and a stale one
    /// would paint a state the instance no longer reports.
    /// <para>
    /// The read tier, which is the tier a caller already needs to see the entity page this answers
    /// for. The gate is checked before the store, so a principal without it causes no read.
    /// </para>
    /// <para>
    /// The answer names the capabilities the connected generation holds, so the browser reads its
    /// menu from the server rather than carrying a generation table of its own.
    /// </para>
    /// </remarks>
    internal static async Task<Results<Ok<EntityMonitoringView>, BadRequest, ForbiddenCode>>
        ReadEntityMonitoringAsync(
            string kind,
            int coveId,
            ICurrentPrincipalAccessor principal,
            OptionsStore options,
            ICredentialPort credentials,
            IWhisparrClient client,
            IEntityIdentityPort identities,
            ILogger log,
            CancellationToken ct)
    {
        if (!HasReadPermission(principal))
        {
            return new ForbiddenCode();
        }

        // Both halves, in ONE expression, at every entity route. The parse succeeds for an integer
        // naming no member, and every arm below classifies a kind by switching on it and throwing for
        // one it cannot express - by design, because a kind resolving to a default arm would act on
        // the wrong table. So the parse alone lets untrusted route input reach a throw inside a
        // handler whose declared results hold no failure. Splitting the two into separate statements
        // is what lets a later edit take one away.
        if (!Enum.TryParse<WhisparrEntityKind>(kind, ignoreCase: true, out var entityKind)
            || !Enum.IsDefined(entityKind))
        {
            return TypedResults.BadRequest();
        }

        ArgumentNullException.ThrowIfNull(identities);

        if (await ResolveTargetAsync(options, credentials, client, ct).ConfigureAwait(false)
            is not { } target)
        {
            return TypedResults.Ok(EntityMonitoringView.NotConfigured(entityKind));
        }

        var reading = await ReadResolvedAsync(
            entityKind, coveId, target, identities, log, ReadingEntity(entityKind, target), ct)
            .ConfigureAwait(false);

        return TypedResults.Ok(reading);
    }

    /// <summary>Monitors one Cove entity on the connected instance, in one gesture.</summary>
    /// <remarks>
    /// The request carries a scope and nothing else. Which entity the instance is asked about is read
    /// from the stored identity row for the Cove entity the route names, so an identifier a caller put
    /// in the body reaches nothing and there is no value to validate.
    /// <para>
    /// The configure tier, the same tier the connection test takes: this route aims this extension's
    /// stored credential at a third party, so it is deliberately out of reach of a caller who cannot
    /// configure the extension. The gate is checked before the body is read.
    /// </para>
    /// <para>
    /// The order is load-bearing. Identity first, so a refusal happens before any outbound request.
    /// Then the entity itself, because one the instance already holds keeps its own add defaults and
    /// reading them would only invite sending them over values a user chose. Only then the defaults,
    /// which are the instance's own, and each empty answer is a stop taken before anything is sent.
    /// </para>
    /// <para>
    /// An accepted monitor starts the reflect-owned run by itself, so a user who asked for one thing
    /// is not left a second gesture to discover. It is ENQUEUED rather than awaited: the run reads
    /// one folder of the entity at a time, and awaiting it would make the length of the click the
    /// length of the entity. Nothing is asked of the caller for it, and no dialog appears.
    /// </para>
    /// </remarks>
    internal async Task<Results<Ok<EntityMonitoringView>, BadRequest, ForbiddenCode>>
        MonitorEntityAsync(
            string kind,
            int coveId,
            MonitorEntityRequest request,
            ICurrentPrincipalAccessor principal,
            OptionsStore options,
            ICredentialPort credentials,
            IWhisparrClient client,
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

        if (await ResolveTargetAsync(options, credentials, client, ct).ConfigureAwait(false)
            is not { } target)
        {
            return TypedResults.Ok(EntityMonitoringView.NotConfigured(entityKind));
        }

        // The stored default rather than the instance's own, and read off the load that resolved the
        // connection rather than through a second one. There is no literal beside it: with a
        // non-nullable stored member there is nothing to fall back from, and a second fallback would
        // be a second answer to one question.
        var scope = request.Scope ?? target.DefaultMonitorScope;

        // No scope reaches the performer arm. The field a future-only scope is expressed through
        // exists on the studio resource and on no other, so a scope a caller named for a performer
        // names nothing the request could carry.
        var monitoring = await MonitorResolvedAsync(
            entityKind,
            coveId,
            target,
            identities,
            log,
            ActingFor(entityKind, target, scope),
            EntityRootThrough(scopes, target, FilesOfEntity(entityKind, coveId)),
            ct).ConfigureAwait(false);

        // From HERE and not from the resolved member the bulk path also reaches: a selection of a
        // thousand entities must not become a thousand background runs. One reflect step per entity
        // inside the batch is the bulk gesture's own shape.
        if (monitoring is { Refusal: MonitorRefusalKind.None, Monitored: true })
        {
            EnqueueReflectOwned(jobs, scopes, entityKind, coveId);
        }

        return TypedResults.Ok(monitoring);
    }

    /// <summary>Stops the connected instance monitoring one Cove entity.</summary>
    /// <remarks>
    /// Takes no body at all. There is nothing for a caller to say: which entity is named by the
    /// route, and the identifier the instance is given is read from the stored identity row, so the
    /// same order holds as for the monitor route and a refusal happens before any outbound request.
    /// <para>
    /// Setting the flag false governs what a later catalogue addition does and retracts nothing
    /// already wanted. An entity the instance does not hold is already not monitored, so that answers
    /// the current state rather than a refusal, and nothing is sent.
    /// </para>
    /// </remarks>
    internal static async Task<Results<Ok<EntityMonitoringView>, BadRequest, ForbiddenCode>>
        UnmonitorEntityAsync(
            string kind,
            int coveId,
            ICurrentPrincipalAccessor principal,
            OptionsStore options,
            ICredentialPort credentials,
            IWhisparrClient client,
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

        if (await ResolveTargetAsync(options, credentials, client, ct).ConfigureAwait(false)
            is not { } target)
        {
            return TypedResults.Ok(EntityMonitoringView.NotConfigured(entityKind));
        }

        return TypedResults.Ok(
            await UnmonitorResolvedAsync(entityKind, coveId, target, identities, log, ct)
                .ConfigureAwait(false));
    }

    /// <summary>Stops <paramref name="target"/> monitoring one entity it is known to be able to.</summary>
    /// <remarks>
    /// Separate from the route so the bulk path reaches the SAME statement of the verb. Two
    /// statements of one gesture is how a selection comes to behave differently from a click.
    /// </remarks>
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
            // Present, in both arms. This runs only where the read above classified the entity as
            // held, so the instance holding it is established here rather than assumed.
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

    /// <summary>
    /// Asks the connected instance to search for what it monitors for one Cove entity.
    /// </summary>
    /// <remarks>
    /// The ONE route of this extension whose effect spends the reader's bandwidth and disk, and the
    /// one place in this product that obtains <see cref="IWhisparrSearchGrabbing"/>. Everything else
    /// here sets flags and tells the instance where files already are.
    /// <para>
    /// Takes NO request body at all, like the unmonitor route. There is no verb member, no scope
    /// member and no identifier member anywhere in its input, so a body omitting a field and binding
    /// to a permissive default is not expressible on this route by construction rather than by a
    /// check. Which entity is named by the route segment, and the identifier the instance is given
    /// comes from the stored identity row and then from the instance's own record.
    /// </para>
    /// <para>
    /// Given its own path from identity to call rather than routed through the shared delegate seam
    /// the monitor, unmonitor and scope verbs go through. A shared flow that can carry a grabbing verb
    /// is exactly the shape "one gesture grows into acquisition" describes, and the seam's value is
    /// that no delegate it takes can express this one.
    /// </para>
    /// <para>
    /// The entity is read before anything is asked for. An entity the instance does not hold monitors
    /// nothing there, so the command would name a row that does not exist and the read is what turns
    /// that into a refusal instead of a request.
    /// </para>
    /// </remarks>
    internal static async Task<Results<Ok<EntityMonitoringView>, BadRequest, ForbiddenCode>>
        SearchAllMonitoredEntityAsync(
            string kind,
            int coveId,
            ICurrentPrincipalAccessor principal,
            OptionsStore options,
            ICredentialPort credentials,
            IWhisparrClient client,
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

        if (await ResolveTargetAsync(options, credentials, client, ct).ConfigureAwait(false)
            is not { } target)
        {
            return TypedResults.Ok(EntityMonitoringView.NotConfigured(entityKind));
        }

        var grabbing = SearchGrabbingOn(target);
        var reading = HeldActingFor(entityKind, target);

        // Identity first, so a refusal costs no outbound request. SEC-4: the outbound identifier is
        // resolved server-side from the stored rows, and nothing a caller supplied reaches it.
        var identity = await identities.ResolveAsync(entityKind, coveId, target.Generation, ct)
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
                target.BaseAddress, target.ApiKey, target.Generation, entityKind, [entityId], ct),
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

    /// <summary>Changes the monitor scope the connected instance holds for one Cove entity.</summary>
    /// <remarks>
    /// A kind expressing no scope answers a bad request rather than a refusal. The field a scope is
    /// carried in exists on one resource only, so a scope named for any other kind is a request the
    /// contract cannot express at all, which is what an unparsable kind answers too.
    /// <para>
    /// The flag is left exactly as the instance reports it. Widening a scope is not the same gesture
    /// as monitoring, and answering this with a monitored state the caller did not ask for would
    /// report something that did not happen.
    /// </para>
    /// </remarks>
    internal static async Task<Results<Ok<EntityMonitoringView>, BadRequest, ForbiddenCode>>
        SetMonitorScopeAsync(
            string kind,
            int coveId,
            MonitorEntityRequest request,
            ICurrentPrincipalAccessor principal,
            OptionsStore options,
            ICredentialPort credentials,
            IWhisparrClient client,
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

        if (await ResolveTargetAsync(options, credentials, client, ct).ConfigureAwait(false)
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

            // The scope the instance just took, not one read back: this is the one path where the
            // product knows what was applied because it applied it. An entity nothing monitors has
            // no scope in force whatever was written, so that answers none.
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
