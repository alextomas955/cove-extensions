using Cove.Core.Auth;
using Cove.Extensions.Shared;
using Cove.Sdk;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using WhisparrSync.Connection;
using WhisparrSync.Contracts;
using WhisparrSync.Jobs;
using WhisparrSync.Monitoring;
using WhisparrSync.Whisparr;
using CoreJobProgress = Cove.Core.Interfaces.IJobProgress;

namespace WhisparrSync;

public sealed partial class WhisparrSync
{
    private void MapAddAllMissingEndpoints(IEndpointRouteBuilder endpoints)
    {
        // Configure tier: the route reaches one entity's catalogue, named by the route segment, and
        // it aims the stored credential at a third party and creates items in the reader's Whisparr.
        endpoints.MapPost(AddAllMissingRoute,
            ([AsParameters] EntityRoute route, ICurrentPrincipalAccessor principal, WhisparrAccess whisparr, IEntityIdentityPort identities,
             BackgroundWork work, CancellationToken ct)
                => AddAllMissingEntityAsync(
                    route, principal, whisparr, identities, work, ct))
            .WithTags(WireTag)
            .RequireCovePermission(PermissionMode.Any, ConfigurePermissions);
    }

    // Takes no body: the entity comes from the route and every outbound value is read from the
    // library's own rows or the instance's record of the entity. A generation that registers no
    // scene-add role is refused by that absence, which is how v2 is refused; nothing compares a
    // generation. Enqueued rather than awaited, so no caller holds a request thread for the length
    // of an entity's catalogue.
    internal async Task<Results<Ok<AddAllMissingEnqueued>, Accepted<AddAllMissingEnqueued>, BadRequest, ForbiddenCode>>
        AddAllMissingEntityAsync(
            EntityRoute route,
            ICurrentPrincipalAccessor principal,
            WhisparrAccess whisparr,
            IEntityIdentityPort identities,
            BackgroundWork work,
            CancellationToken ct)
    {
        var (jobs, scopes) = work;

        var (kind, coveId) = route;

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

        if (await ResolveTargetAsync(whisparr, ct).ConfigureAwait(false)
            is not { } target)
        {
            return TypedResults.Ok(new AddAllMissingEnqueued(null, MonitorRefusalKind.NotConfigured));
        }

        var resolved = await ResolveAddAllMissingAsync(
            entityKind,
            coveId,
            target,
            identities,
            EntityRootThrough(scopes, target, FilesOfEntity(entityKind, coveId)),
            ct).ConfigureAwait(false);
        if (resolved.Aiming is null)
        {
            return TypedResults.Ok(new AddAllMissingEnqueued(null, resolved.Refusal));
        }

        return TypedResults.Accepted(
            (string?)null,
            new AddAllMissingEnqueued(
                EnqueueAddAllMissing(work, entityKind, coveId), MonitorRefusalKind.None));
    }

    // Aiming is null on a refusal, and Refusal then says why.
    private sealed record AddAllMissingResolution(
        AddAllMissingAiming? Aiming, MonitorRefusalKind Refusal);

    // Reached from the route and again when the run starts, minutes apart. The values it reads are
    // the instance's own and may change in between, so both paths resolve through this one method.
    private async Task<AddAllMissingResolution> ResolveAddAllMissingAsync(
        WhisparrEntityKind kind,
        int coveId,
        MonitoringTarget target,
        IEntityIdentityPort identities,
        Func<AddDefaults, CancellationToken, Task<EntityAddDefaultsResolution>> composeAdd,
        CancellationToken ct)
    {
        var identity = await identities.ResolveAsync(kind, coveId, target.Binding.Generation, ct)
            .ConfigureAwait(false);
        var acting = target.Reads as IWhisparrMissingSceneActing;
        var reading = HeldActingFor(kind, target);

        if (acting is null || reading is not { } actingFor || identity.ForeignId is not { } named)
        {
            return new AddAllMissingResolution(
                null, RefusalAmong(acting is null || reading is null, identity.Refusal));
        }

        var read = await ContainedAsync(() => actingFor(named).ReadEntity(ct), target, _log, ct)
            .ConfigureAwait(false);
        if (read is null)
        {
            return new AddAllMissingResolution(null, MonitorRefusalKind.InstanceRefused);
        }

        var answer = MonitoringProjector.Classify(read);
        if (answer.Reading != MonitoringProjector.EntityReading.Held
            || MonitoringProjector.EntityIdIn(read.Body) is not { } entityId)
        {
            return new AddAllMissingResolution(null, RefusalIn(answer));
        }

        var profiles = await ContainedAsync(
            () => target.Reads.ReadQualityProfilesAsync(ct),
            target,
            _log,
            ct).ConfigureAwait(false);
        var roots = profiles is null
            ? null
            : await ContainedAsync(
                () => target.Reads.ReadRootFoldersAsync(ct),
                target,
                _log,
                ct).ConfigureAwait(false);
        if (profiles is null || roots is null)
        {
            return new AddAllMissingResolution(null, MonitorRefusalKind.InstanceRefused);
        }

        var defaults = AddDefaultsProjector.From(profiles.Body, roots.Body);
        if (defaults.Defaults is not { } runWide)
        {
            return new AddAllMissingResolution(null, defaults.Refusal);
        }

        // Composed once for the run rather than once per scene. The agreement is cached per library
        // root, but a per-scene composition would still repeat the counts for every scene in a page.
        var composed = await composeAdd(runWide, ct).ConfigureAwait(false);
        if (composed.Defaults is not { } composeWith)
        {
            return new AddAllMissingResolution(null, composed.Refusal);
        }

        return new AddAllMissingResolution(
            new AddAllMissingAiming(
                target.Binding.Generation,
                (foreignId, registerCt) => ContainedAsync(
                    () => acting.AddSceneAsync(
                        foreignId, composeWith, registerCt),
                    target,
                    _log,
                    registerCt),
                async refreshCt =>
                {
                    await ContainedAsync(
                        () => acting.RefreshCatalogueAsync(
                            kind, entityId, refreshCt),
                        target,
                        _log,
                        refreshCt).ConfigureAwait(false);
                }),
            MonitorRefusalKind.None);
    }

    // Exclusive because two entities can name one scene, a video carrying a studio and its
    // performers at once, so overlapping runs would offer the same scene twice.
    private string EnqueueAddAllMissing(
        BackgroundWork work, WhisparrEntityKind kind, int coveId)
    {
        var (jobs, scopes) = work;

        var parameters = AddAllMissingJob.Encode(kind, coveId);

        return jobs.Enqueue(
            OwnJobTypePrefix + AddAllMissingJob.JobId,
            $"[{Name}] Add all missing, one {kind}",
            (progress, ct) => RunAddAllMissingAsync(parameters, scopes, progress, ct),
            exclusive: true);
    }

    // Cancellation is rethrown after the summary is written, so the host classifies the run as
    // cancelled while the reader is still told what it managed to register.
    private async Task RunAddAllMissingAsync(
        IReadOnlyDictionary<string, string> parameters,
        IServiceScopeFactory scopes,
        CoreJobProgress progress,
        CancellationToken ct)
    {
        var batch = AddAllMissingJob.Decode(parameters);
        var run = await AddAllMissingJob.RunAsync(batch, scopes, AimAsync, ct).ConfigureAwait(false);

        // The host's progress carries no summary field, so the run's one line rides the final
        // report's sub-task.
        progress.Report(1d, AddAllMissingJob.SummaryOf(run));
        ct.ThrowIfCancellationRequested();

        async Task<AddAllMissingAiming?> AimAsync(IServiceProvider services, CancellationToken runCt)
        {
            if (batch.Kind is not { } kind
                || await ResolveTargetAsync(
                    services.GetRequiredService<WhisparrAccess>(),
                    runCt).ConfigureAwait(false) is not { } target)
            {
                return null;
            }

            return (await ResolveAddAllMissingAsync(
                kind,
                batch.CoveId,
                target,
                services.GetRequiredService<IEntityIdentityPort>(),
                EntityRootIn(services, target, FilesOfEntity(kind, batch.CoveId)),
                runCt).ConfigureAwait(false)).Aiming;
        }
    }
}
