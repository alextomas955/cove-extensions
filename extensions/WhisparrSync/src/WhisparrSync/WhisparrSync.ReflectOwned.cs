using Cove.Core.Auth;
using Cove.Core.Interfaces;
using Cove.Extensions.Shared;
using Cove.Sdk;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using WhisparrSync.Addressing;
using WhisparrSync.Connection;
using WhisparrSync.Contracts;
using WhisparrSync.Jobs;
using WhisparrSync.Monitoring;
using WhisparrSync.Options;
using WhisparrSync.Whisparr;
using CoreJobProgress = Cove.Core.Interfaces.IJobProgress;

namespace WhisparrSync;

public sealed partial class WhisparrSync
{
    private void MapReflectOwnedEndpoints(IEndpointRouteBuilder endpoints)
    {
        // The same tier as the monitor route, and for the reason that route's own remark gives: it
        // aims this extension's stored credential at a third party. Its reach is the one Cove entity
        // the route segment names, so it is neither a whole-library read nor a body-named
        // no-content call, and neither lesser tier expresses it.
        endpoints.MapPost(ReflectOwnedRoute,
            (string kind, int coveId, ICurrentPrincipalAccessor principal, OptionsStore options,
             ICredentialPort credentials, IWhisparrClient client, IEntityIdentityPort identities,
             IJobService jobs, IServiceScopeFactory scopes, CancellationToken ct)
                => ReflectOwnedEntityAsync(
                    kind, coveId, principal, options, credentials, client, identities, jobs, scopes, ct))
            .WithTags(WireTag)
            .RequireCovePermission(PermissionMode.Any, ConfigurePermissions);
    }

    /// <summary>
    /// Asks the connected instance to link the files the library already holds for one entity into
    /// place, in the background.
    /// </summary>
    /// <remarks>
    /// Takes no body at all. Which entity is named by the route, and nothing about the outbound
    /// request is a value a caller could supply: the folders are read from the library and the
    /// identity that admits the entity at all is read from its own stored rows.
    /// <para>
    /// The order is the monitor route's own. Identity first, so an entity the connected generation
    /// cannot name is refused before anything is sent — even though the run itself names folders
    /// rather than the entity, acting for an entity this product could not identify would be acting
    /// on a link the library does not hold.
    /// </para>
    /// <para>
    /// The hard-link setting is read before anything else leaves, and a skip is ANSWERED rather than
    /// enqueued: with that setting off the instance has no mode that links, so every matched file
    /// would be copied in full. The reader is told at the control instead, and the run is read again
    /// when it starts, because the setting is the instance's to change in between.
    /// </para>
    /// <para>
    /// Enqueued rather than awaited, so a caller cannot hold a request thread for the length of an
    /// entity's folder set.
    /// </para>
    /// </remarks>
    internal async Task<Results<Ok<ReflectOwnedEnqueued>, Accepted<ReflectOwnedEnqueued>, BadRequest, ForbiddenCode>>
        ReflectOwnedEntityAsync(
            string kind,
            int coveId,
            ICurrentPrincipalAccessor principal,
            OptionsStore options,
            ICredentialPort credentials,
            IWhisparrClient client,
            IEntityIdentityPort identities,
            IJobService jobs,
            IServiceScopeFactory scopes,
            CancellationToken ct)
    {
        if (!HasConfigurePermission(principal))
        {
            return new ForbiddenCode();
        }

        ArgumentNullException.ThrowIfNull(identities);
        ArgumentNullException.ThrowIfNull(jobs);

        if (!Enum.TryParse<WhisparrEntityKind>(kind, ignoreCase: true, out var entityKind)
            || !Enum.IsDefined(entityKind))
        {
            return TypedResults.BadRequest();
        }

        if (await ResolveTargetAsync(options, credentials, client, ct).ConfigureAwait(false)
            is not { } target)
        {
            return TypedResults.Ok(Refusing(MonitorRefusalKind.NotConfigured));
        }

        var identity = await identities.ResolveAsync(entityKind, coveId, target.Generation, ct)
            .ConfigureAwait(false);
        var acting = ReflectOwnedActingOn(target);
        if (acting is null || identity.ForeignId is null)
        {
            return TypedResults.Ok(Refusing(RefusalAmong(acting is null, identity.Refusal)));
        }

        var decision = await ReflectOwnedDecisionAsync(target, acting, ct).ConfigureAwait(false);
        if (!decision.Act)
        {
            return TypedResults.Ok(
                new ReflectOwnedEnqueued(decision.Reason, null, MonitorRefusalKind.None));
        }

        return TypedResults.Accepted(
            (string?)null,
            new ReflectOwnedEnqueued(
                null, EnqueueReflectOwned(jobs, scopes, entityKind, coveId), MonitorRefusalKind.None));

        static ReflectOwnedEnqueued Refusing(MonitorRefusalKind refusal)
            => new(null, null, refusal);
    }

    /// <summary>Starts one entity's reflect-owned run in the background.</summary>
    /// <remarks>
    /// Enqueued EXCLUSIVE. Two entities can hold files in one folder — a video carries a studio and
    /// its performers at once — so overlapping runs would issue overlapping attaches for the same
    /// directory. What exclusivity costs when that does not happen is that the runs go one after the
    /// other, against a third party this product should not be issuing parallel work to anyway.
    /// </remarks>
    private string EnqueueReflectOwned(
        IJobService jobs, IServiceScopeFactory scopes, WhisparrEntityKind kind, int coveId)
    {
        var parameters = ReflectOwnedJob.Encode(kind, coveId);

        return jobs.Enqueue(
            OwnJobTypePrefix + ReflectOwnedJob.JobId,
            $"[{Name}] Reflect owned, one {kind}",
            (progress, ct) => RunReflectOwnedAsync(parameters, scopes, progress, ct),
            exclusive: true);
    }

    /// <summary>Runs one enqueued reflect-owned pass.</summary>
    /// <remarks>
    /// Everything the run acts through is resolved when it STARTS. A cancellation is rethrown after
    /// the summary is written, so the host classifies the run as cancelled rather than completed
    /// while the reader is still told what it managed to link.
    /// </remarks>
    private async Task RunReflectOwnedAsync(
        IReadOnlyDictionary<string, string> parameters,
        IServiceScopeFactory scopes,
        CoreJobProgress progress,
        CancellationToken ct)
    {
        var run = await ReflectOwnedJob.RunAsync(
            ReflectOwnedJob.Decode(parameters), scopes, AimAsync, ct).ConfigureAwait(false);

        await RecordRootReadingsAsync(scopes, run.AddressRefusals, run.AddressedRoots)
            .ConfigureAwait(false);

        // The host's progress carries no summary field, so the run's one line rides the final
        // report's sub-task.
        progress.Report(1d, ReflectOwnedJob.SummaryOf(run));
        ct.ThrowIfCancellationRequested();

        // Nothing this product could not resolve names a skip reason. A reader sent to the instance's
        // hard-link setting because no connection was configured would be sent to a value nobody read.
        async Task<ReflectOwnedAim> AimAsync(IServiceProvider services, CancellationToken runCt)
        {
            if (await ResolveTargetAsync(
                    services.GetRequiredService<OptionsStore>(),
                    services.GetRequiredService<ICredentialPort>(),
                    services.GetRequiredService<IWhisparrClient>(),
                    runCt).ConfigureAwait(false) is not { } target)
            {
                return new ReflectOwnedAim(null, null);
            }

            if (ReflectOwnedActingOn(target) is not { } acting)
            {
                return new ReflectOwnedAim(null, null);
            }

            var decision = await ReflectOwnedDecisionAsync(target, acting, runCt).ConfigureAwait(false);

            return decision.Act
                ? new ReflectOwnedAim(
                    AimedAt(target, acting, services.GetRequiredService<IFolderAddressPort>()), null)
                : new ReflectOwnedAim(null, decision.Reason);
        }
    }

    /// <summary>What a reflect-owned run needs from <paramref name="target"/>, already aimed at it.</summary>
    /// <remarks>
    /// The ONE statement of the work, reached by the entity's own enqueued run and by a selection's
    /// per-entity step alike. Two statements of one gesture is how a selection comes to behave
    /// differently from a click.
    /// </remarks>
    private ReflectOwnedAiming AimedAt(
        MonitoringTarget target, IWhisparrReflectOwnedActing acting, IFolderAddressPort addressing)
        => new(
            target.Generation,
            AddressingThrough(target, addressing),
            async (folder, readCt) => (await ContainedAsync(
                    () => acting.ListImportableFilesAsync(
                        target.BaseAddress, target.ApiKey, folder, readCt),
                    target,
                    _log,
                    readCt).ConfigureAwait(false))
                is { } parsed && MonitoringProjector.Accepted(parsed) == MonitorRefusalKind.None
                    ? ImportableListing.Listed(parsed.Body)
                    : ImportableListing.Refused,
            async (files, attachCt) => (await ContainedAsync(
                    () => acting.AttachOwnedFilesAsync(
                        target.BaseAddress, target.ApiKey, files, attachCt),
                    target,
                    _log,
                    attachCt).ConfigureAwait(false))
                is { } attached
                && MonitoringProjector.Accepted(attached) == MonitorRefusalKind.None);

    /// <summary>
    /// How a run turns a folder the library names into the path <paramref name="target"/> can open.
    /// </summary>
    /// <remarks>
    /// Where the connected generation holds no filesystem role, every folder answers that the instance
    /// cannot be asked. The run then states that rather than handing over a path nobody checked, which
    /// on a mismatched root reads back as a clean pass over an empty folder.
    /// </remarks>
    private static Func<string, CancellationToken, Task<AddressedFolder>> AddressingThrough(
        MonitoringTarget target, IFolderAddressPort addressing)
    {
        if (FilesystemReadingOn(target) is not { } role)
        {
            return (_, _) => Task.FromResult(
                new AddressedFolder(
                    null,
                    FolderAgreementRefusal.InstanceCannotBeAsked,
                    string.Empty,
                    Array.Empty<string>()));
        }

        var aimed = new FolderAddressTarget(
            target.Generation, target.BaseAddress, target.ApiKey, role);

        return (folder, addressCt) => addressing.AddressAsync(aimed, folder, addressCt);
    }

    /// <summary>
    /// The role that answers what <paramref name="target"/> holds at a path of its own, or null where
    /// the connected generation holds none.
    /// </summary>
    private static IWhisparrInstanceFilesystemReading? FilesystemReadingOn(MonitoringTarget target)
        => target.Capabilities.Obtain<IWhisparrInstanceFilesystemReading>()
            .Match<IWhisparrInstanceFilesystemReading?>(filesystem => filesystem, _ => null);

    /// <summary>
    /// The role that links owned files into place on <paramref name="target"/>, or null where the
    /// connected generation holds none.
    /// </summary>
    private static IWhisparrReflectOwnedActing? ReflectOwnedActingOn(MonitoringTarget target)
        => target.Capabilities.Obtain<IWhisparrReflectOwnedActing>()
            .Match<IWhisparrReflectOwnedActing?>(acting => acting, _ => null);

    /// <summary>Whether <paramref name="target"/> links a file into place rather than copying it.</summary>
    /// <remarks>
    /// Read on the route AND again when the run starts. The two are minutes apart, and the value
    /// decides whether every matched file is linked or duplicated in full.
    /// </remarks>
    private async Task<ReflectOwnedDecision> ReflectOwnedDecisionAsync(
        MonitoringTarget target, IWhisparrReflectOwnedActing acting, CancellationToken ct)
    {
        var setting = await ContainedAsync(
            () => acting.ReadHardlinkSettingAsync(target.BaseAddress, target.ApiKey, ct),
            target,
            _log,
            ct).ConfigureAwait(false);

        return ReflectOwnedPlanner.Decide(setting?.Body);
    }
}
