using Cove.Core.Auth;
using Cove.Core.Interfaces;
using Cove.Extensions.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
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
    /// <summary>
    /// Offers the connected instance every scene the library holds under one entity that its own
    /// catalogue does not, in the background.
    /// </summary>
    /// <remarks>
    /// Takes no body at all. Which entity is named by the route, and nothing outbound is a value a
    /// caller could supply: the entity's identifier and every scene's are read from the library's
    /// own stored rows, and the instance-side id the catalogue refresh names comes from the
    /// instance's own record of the entity.
    /// <para>
    /// The order is the monitor route's own, and every step of it is a stop taken before anything
    /// is created. Identity first, so an entity the connected generation cannot name is refused
    /// with no outbound request. Then the scene-registration role, whose ABSENCE is the whole of
    /// v2's refusal - no route there adds a catalogue item, so the role is not
    /// registered and nothing here compares a generation. Then the entity itself, because an
    /// instance that does not hold it has no catalogue to add to. Then the profile and the root,
    /// each empty answer a stop taken before the first scene is composed.
    /// </para>
    /// <para>
    /// The profile and the root are read HERE rather than at any earlier point, and read again when
    /// the run starts: they are the instance's own and are its to change in between, and they decide
    /// what every scene this verb creates is filed under.
    /// </para>
    /// <para>
    /// Enqueued rather than awaited, so a caller cannot hold a request thread for the length of an
    /// entity's catalogue.
    /// </para>
    /// </remarks>
    internal async Task<Results<Ok<AddAllMissingEnqueued>, Accepted<AddAllMissingEnqueued>, BadRequest, ForbiddenCode>>
        AddAllMissingEntityAsync(
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
                EnqueueAddAllMissing(jobs, scopes, entityKind, coveId), MonitorRefusalKind.None));
    }

    /// <summary>What one entity's registration run needs, or why it cannot be started.</summary>
    /// <param name="Aiming">What the run acts through, or null on a refusal.</param>
    /// <param name="Refusal">Why there is none, or <see cref="MonitorRefusalKind.None"/>.</param>
    private sealed record AddAllMissingResolution(
        AddAllMissingAiming? Aiming, MonitorRefusalKind Refusal);

    /// <summary>
    /// Resolves everything one registration run acts through, in the order that makes each refusal
    /// cost as little as it can.
    /// </summary>
    /// <remarks>
    /// Reached from the route AND again when the run starts, so the two cannot come to disagree
    /// about what a registration carries. The values it reads are the instance's own and are minutes
    /// apart on the two paths.
    /// </remarks>
    private async Task<AddAllMissingResolution> ResolveAddAllMissingAsync(
        WhisparrEntityKind kind,
        int coveId,
        MonitoringTarget target,
        IEntityIdentityPort identities,
        Func<AddDefaults, CancellationToken, Task<EntityAddDefaultsResolution>> composeAdd,
        CancellationToken ct)
    {
        var identity = await identities.ResolveAsync(kind, coveId, target.Generation, ct)
            .ConfigureAwait(false);
        var acting = target.Capabilities.Obtain<IWhisparrMissingSceneActing>()
            .Match<IWhisparrMissingSceneActing?>(held => held, _ => null);
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
            () => target.Reads.ReadQualityProfilesAsync(target.BaseAddress, target.ApiKey, ct),
            target,
            _log,
            ct).ConfigureAwait(false);
        var roots = profiles is null
            ? null
            : await ContainedAsync(
                () => target.Reads.ReadRootFoldersAsync(target.BaseAddress, target.ApiKey, ct),
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
                target.Generation,
                (foreignId, registerCt) => ContainedAsync(
                    () => acting.AddSceneAsync(
                        target.BaseAddress, target.ApiKey, foreignId, composeWith, registerCt),
                    target,
                    _log,
                    registerCt),
                async refreshCt =>
                {
                    await ContainedAsync(
                        () => acting.RefreshCatalogueAsync(
                            target.BaseAddress, target.ApiKey, kind, entityId, refreshCt),
                        target,
                        _log,
                        refreshCt).ConfigureAwait(false);
                }),
            MonitorRefusalKind.None);
    }

    /// <summary>Starts one entity's registration run in the background.</summary>
    /// <remarks>
    /// Enqueued EXCLUSIVE, for the reason the reflect-owned run is: two entities can name one scene
    /// - a video carries a studio and its performers at once - so overlapping runs would offer the
    /// same scene twice. What exclusivity costs when that does not happen is that the runs go one
    /// after the other, against a third party this product should not be issuing parallel work to.
    /// </remarks>
    private string EnqueueAddAllMissing(
        IJobService jobs, IServiceScopeFactory scopes, WhisparrEntityKind kind, int coveId)
    {
        var parameters = AddAllMissingJob.Encode(kind, coveId);

        return jobs.Enqueue(
            OwnJobTypePrefix + AddAllMissingJob.JobId,
            $"[{Name}] Add all missing, one {kind}",
            (progress, ct) => RunAddAllMissingAsync(parameters, scopes, progress, ct),
            exclusive: true);
    }

    /// <summary>Runs one enqueued registration pass.</summary>
    /// <remarks>
    /// Everything the run acts through is resolved when it STARTS. A cancellation is rethrown after
    /// the summary is written, so the host classifies the run as cancelled rather than completed
    /// while the reader is still told what it managed to register.
    /// </remarks>
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
                    services.GetRequiredService<OptionsStore>(),
                    services.GetRequiredService<ICredentialPort>(),
                    services.GetRequiredService<IWhisparrClient>(),
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
