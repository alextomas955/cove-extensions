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
        // The monitor route's tier: it aims the stored credential at a third party, and its reach
        // is the one entity the route segment names, which no lesser tier expresses.
        endpoints.MapPost(ReflectOwnedRoute,
            (string kind, int coveId, ICurrentPrincipalAccessor principal, OptionsStore options,
             ICredentialPort credentials, IWhisparrInstanceFactory instances, IEntityIdentityPort identities,
             IJobService jobs, IServiceScopeFactory scopes, CancellationToken ct)
                => ReflectOwnedEntityAsync(
                    kind, coveId, principal, options, credentials, instances, identities, jobs, scopes, ct))
            .WithTags(WireTag)
            .RequireCovePermission(PermissionMode.Any, ConfigurePermissions);
    }

    // The route takes no body: the entity comes from the route, the folders from the library, and
    // the identity from stored rows, so a caller supplies nothing the outbound request carries.
    // The hard-link setting is read before anything leaves, and a skip is answered rather than
    // enqueued, because with that setting off every matched file would be copied in full.
    // Enqueued rather than awaited, so a caller does not hold a request thread for the length of
    // an entity's folder set.
    internal async Task<Results<Ok<ReflectOwnedEnqueued>, Accepted<ReflectOwnedEnqueued>, BadRequest, ForbiddenCode>>
        ReflectOwnedEntityAsync(
            string kind,
            int coveId,
            ICurrentPrincipalAccessor principal,
            OptionsStore options,
            ICredentialPort credentials,
            IWhisparrInstanceFactory instances,
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

        if (await ResolveTargetAsync(options, credentials, instances, ct).ConfigureAwait(false)
            is not { } target)
        {
            return TypedResults.Ok(Refusing(MonitorRefusalKind.NotConfigured));
        }

        var identity = await identities.ResolveAsync(entityKind, coveId, target.Binding.Generation, ct)
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

    // Exclusive: two entities can hold files in one folder, since a video carries a studio and its
    // performers at once, so overlapping runs would issue overlapping attaches for one directory.
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

    // Everything the run acts through is resolved when the run starts, because the hard-link
    // setting is the instance's to change between the route's read and the run.
    private async Task RunReflectOwnedAsync(
        IReadOnlyDictionary<string, string> parameters,
        IServiceScopeFactory scopes,
        CoreJobProgress progress,
        CancellationToken ct)
    {
        // The generation the run aimed at, not the one selected when it finishes: a selection change
        // during a run would otherwise file what this instance established under the other one.
        WhisparrGeneration? aimedAt = null;

        var run = await ReflectOwnedJob.RunAsync(
            ReflectOwnedJob.Decode(parameters), scopes, AimAsync, ct).ConfigureAwait(false);

        if (aimedAt is { } generation)
        {
            await RecordRootReadingsAsync(
                scopes, generation, run.AddressRefusals, run.AddressedRoots).ConfigureAwait(false);
        }

        // The host's progress carries no summary field, so the run's one line rides the final
        // report's sub-task. Cancellation is rethrown after that write, so the host classifies the
        // run as cancelled and the reader still sees what it managed to link.
        progress.Report(1d, ReflectOwnedJob.SummaryOf(run));
        ct.ThrowIfCancellationRequested();

        // A target or role that could not be resolved names no skip reason: pointing the reader at
        // the instance's hard-link setting would name a value nobody read.
        async Task<ReflectOwnedAim> AimAsync(IServiceProvider services, CancellationToken runCt)
        {
            if (await ResolveTargetAsync(
                    services.GetRequiredService<OptionsStore>(),
                    services.GetRequiredService<ICredentialPort>(),
                    services.GetRequiredService<IWhisparrInstanceFactory>(),
                    runCt).ConfigureAwait(false) is not { } target)
            {
                return new ReflectOwnedAim(null, null);
            }

            aimedAt = target.Binding.Generation;

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

    // The one statement of the work, reached by the entity's own run and by a selection's
    // per-entity step alike, so a selection cannot behave differently from a click.
    private ReflectOwnedAiming AimedAt(
        MonitoringTarget target, IWhisparrReflectOwnedActing acting, IFolderAddressPort addressing)
        => new(
            target.Binding.Generation,
            AddressingThrough(target, addressing),
            async (folder, readCt) => (await ContainedAsync(
                    () => acting.ListImportableFilesAsync(
                        folder, readCt),
                    target,
                    _log,
                    readCt).ConfigureAwait(false))
                is { } parsed && MonitoringProjector.Accepted(parsed) == MonitorRefusalKind.None
                    ? ImportableListing.Listed(parsed.Body)
                    : ImportableListing.Refused,
            async (files, attachCt) => (await ContainedAsync(
                    () => acting.AttachOwnedFilesAsync(
                        files, attachCt),
                    target,
                    _log,
                    attachCt).ConfigureAwait(false))
                is { } attached
                && MonitoringProjector.Accepted(attached) == MonitorRefusalKind.None);

    // Where the connected generation holds no filesystem role, every folder answers that the
    // instance cannot be asked. Handing over an unchecked path instead would read back, on a
    // mismatched root, as a clean pass over an empty folder.
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

        var aimed = new FolderAddressTarget(target.Binding, role);

        return (folder, addressCt) => addressing.AddressAsync(aimed, folder, addressCt);
    }

    private static IWhisparrInstanceFilesystemReading? FilesystemReadingOn(MonitoringTarget target)
        => target.Reads as IWhisparrInstanceFilesystemReading;

    private static IWhisparrReflectOwnedActing? ReflectOwnedActingOn(MonitoringTarget target)
        => target.Reads as IWhisparrReflectOwnedActing;

    // Read on the route and again when the run starts. The two are minutes apart, and the value
    // decides whether every matched file is linked or duplicated in full.
    private async Task<ReflectOwnedDecision> ReflectOwnedDecisionAsync(
        MonitoringTarget target, IWhisparrReflectOwnedActing acting, CancellationToken ct)
    {
        var setting = await ContainedAsync(
            () => acting.ReadHardlinkSettingAsync(ct),
            target,
            _log,
            ct).ConfigureAwait(false);

        return ReflectOwnedPlanner.Decide(setting?.Body);
    }
}
