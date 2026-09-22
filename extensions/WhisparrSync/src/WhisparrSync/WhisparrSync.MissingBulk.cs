using Cove.Core.Auth;
using Cove.Core.Interfaces;
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
using WhisparrSync.Missing;
using WhisparrSync.Monitoring;
using WhisparrSync.Options;
using WhisparrSync.Scene;
using WhisparrSync.Whisparr;
using CoreJobProgress = Cove.Core.Interfaces.IJobProgress;

namespace WhisparrSync;

public sealed partial class WhisparrSync
{
    private void MapMissingBulkEndpoints(IEndpointRouteBuilder endpoints)
    {
        // Configure tier: one gesture aims the stored credential at a third party and creates items
        // in the reader's Whisparr, which a caller who cannot configure the extension may not do.
        endpoints.MapPost(MissingBulkMonitorRoute,
            (string kind, int coveId, MissingBulkRequest request,
             ICurrentPrincipalAccessor principal, IJobService jobs, IServiceScopeFactory scopes,
             OptionsStore options, ICredentialPort credentials, IWhisparrInstanceFactory instances,
             CancellationToken ct)
                => EnqueueMissingBulkMonitorAsync(
                    kind, coveId, request, principal, jobs, scopes, options, credentials, instances, ct))
            .WithTags(WireTag)
            .RequireCovePermission(PermissionMode.Any, ConfigurePermissions);
    }

    // Answers the run id immediately. A selection is a page of scenes and each one is a request to a
    // third party, so waiting for the run would hold the browser open for its whole length.
    internal async Task<Results<Ok<MissingBulkEnqueued>, BadRequest, ForbiddenCode>>
        EnqueueMissingBulkMonitorAsync(
            string kind,
            int coveId,
            MissingBulkRequest request,
            ICurrentPrincipalAccessor principal,
            IJobService jobs,
            IServiceScopeFactory scopes,
            OptionsStore options,
            ICredentialPort credentials,
            IWhisparrInstanceFactory instances,
            CancellationToken ct)
    {
        // Re-checked here because the route declaration enforces nothing on a minimal API.
        if (!HasConfigurePermission(principal))
        {
            return new ForbiddenCode();
        }

        ArgumentNullException.ThrowIfNull(jobs);

        // Bounded to one page of scenes: a longer body is one no page of this surface can produce,
        // and the run's cost grows with what it carries.
        if (!TryReadEntity(kind, coveId, out var entityKind)
            || request is not { ProviderSceneIds.Count: > 0 }
            || request.ProviderSceneIds.Count > MissingPerPage
            || !request.ProviderSceneIds.All(IsBoundedSceneId))
        {
            return TypedResults.BadRequest();
        }

        if (await ResolveTargetAsync(options, credentials, instances, ct).ConfigureAwait(false)
            is not { } target)
        {
            return TypedResults.Ok(
                new MissingBulkEnqueued(null, MissingRefusalKind.NoInstanceConnected));
        }

        // A generation that neither adds a catalogue item nor flips a row it already holds has no
        // implementation to hand over, so there is nothing to compose and no run to start. One that
        // can do either is enough: the run picks between them per generation and per verb.
        if (target.Reads is not (IWhisparrMissingSceneActing or IWhisparrSceneMonitorActing))
        {
            return TypedResults.Ok(
                new MissingBulkEnqueued(null, MissingRefusalKind.WhisparrKeepsNoSceneRecords));
        }

        return TypedResults.Ok(
            new MissingBulkEnqueued(
                EnqueueMissingBulk(
                    jobs, scopes, entityKind, coveId, request.ProviderSceneIds, request.Verb),
                MissingRefusalKind.None));
    }

    // One job for the whole selection: a job per scene would put a page of rows in the host's job
    // list for one gesture. Exclusive because one scene can be reached from two entities, a video
    // carrying a studio and its performers at once, so overlapping runs would offer it twice.
    private string EnqueueMissingBulk(
        IJobService jobs,
        IServiceScopeFactory scopes,
        WhisparrEntityKind kind,
        int coveId,
        IReadOnlyList<string> providerSceneIds,
        MissingBulkVerb verb)
    {
        var parameters = MissingBulkJob.Encode(kind, coveId, providerSceneIds, verb);

        return jobs.Enqueue(
            OwnJobTypePrefix + MissingBulkJob.JobId,
            $"[{Name}] {(verb == MissingBulkVerb.Monitor ? "Monitor" : "Unmonitor")} "
                + $"selected scenes, one {kind}",
            (progress, ct) => RunMissingBulkAsync(parameters, scopes, progress, ct),
            exclusive: true);
    }

    // Cancellation is rethrown after the summary is written, so the host classifies the run as
    // cancelled while the reader is still told what it managed to mark.
    private async Task RunMissingBulkAsync(
        IReadOnlyDictionary<string, string> parameters,
        IServiceScopeFactory scopes,
        CoreJobProgress progress,
        CancellationToken ct)
    {
        var batch = MissingBulkJob.Decode(parameters);
        var run = await MissingBulkJob
            .RunAsync(
                batch,
                scopes,
                (services, runCt) => ComposeSceneMarkAsync(batch, services, runCt),
                ct)
            .ConfigureAwait(false);

        // The run wrote to the instance, so what the catalogue held describes a state that has
        // moved. Left held, the page read after this run draws the flags from before it and the
        // reader sees their own gesture do nothing.
        using (var scope = scopes.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<InstanceCatalogueCache>().Forget();
        }

        // The host's progress carries no summary field, so the run's one line rides the final
        // report's sub-task.
        progress.Report(1d, MissingBulkJob.SummaryOf(run, batch.Verb));
        ct.ThrowIfCancellationRequested();
    }

    // Which marker one selection's run uses. Unmonitoring is a flip of rows the instance already
    // holds, on either generation, and so is monitoring on the generation whose catalogue is its own
    // rows. Only the generation that adds a catalogue item composes an add.
    private async Task<Func<string, CancellationToken, Task<WhisparrResponse?>>?>
        ComposeSceneMarkAsync(
            MissingBulkBatch batch,
            IServiceProvider services,
            CancellationToken runCt)
    {
        if (await ResolveTargetAsync(
                services.GetRequiredService<OptionsStore>(),
                services.GetRequiredService<ICredentialPort>(),
                services.GetRequiredService<IWhisparrInstanceFactory>(),
                runCt).ConfigureAwait(false) is not { } target)
        {
            return null;
        }

        var adds = target.Reads is IWhisparrMissingSceneActing;

        return batch.Verb == MissingBulkVerb.Monitor && adds
            ? await ComposeSceneAddAsync(batch.Kind, batch.CoveId, services, runCt)
                .ConfigureAwait(false)
            : await ComposeSceneFlipAsync(
                batch.Kind, batch.CoveId, batch.Verb, target, services, runCt)
                .ConfigureAwait(false);
    }

    // The marker for a run that flips flags on rows the instance already holds. The rows come from
    // the entity's own catalogue, read once for the whole run and held by provider id, so the run
    // costs one catalogue read plus one write per scene rather than a read per scene.
    private async Task<Func<string, CancellationToken, Task<WhisparrResponse?>>?>
        ComposeSceneFlipAsync(
            WhisparrEntityKind? owningKind,
            int owningId,
            MissingBulkVerb verb,
            MonitoringTarget target,
            IServiceProvider services,
            CancellationToken runCt)
    {
        // A run over no one entity has no catalogue to read the instance's own row ids from.
        if (owningKind is not { } owning
            || target.Reads is not IWhisparrSceneMonitorActing marking
            || target.Reads is not IWhisparrEntityCatalogueReading reading)
        {
            return null;
        }

        var identity = await services.GetRequiredService<IEntityIdentityPort>()
            .ResolveAsync(owning, owningId, target.Binding.Generation, runCt).ConfigureAwait(false);
        if (identity.ForeignId is not { Length: > 0 } foreignId)
        {
            return null;
        }

        var catalogue = await reading.ReadEntityCatalogueAsync(
            owning, foreignId, runCt)
            .ConfigureAwait(false);
        if (catalogue.Scenes is not { } scenes)
        {
            return null;
        }

        var rows = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var scene in scenes)
        {
            if (scene.InstanceSceneId > 0)
            {
                rows[scene.ProviderSceneId] = scene.InstanceSceneId;
            }
        }

        var monitored = verb == MissingBulkVerb.Monitor;
        return (providerSceneId, markCt) => rows.TryGetValue(providerSceneId, out var rowId)
            ? ContainedAsync(
                () => marking.SetSceneMonitoredAsync(
                    rowId, monitored, markCt),
                target,
                _log,
                markCt)

            // A scene the catalogue named no row for is one the instance holds nothing to flip, and
            // the rest of the selection is still worth marking.
            : Task.FromResult<WhisparrResponse?>(null);
    }

    // Null where the run must not act at all. Nothing composed here grabs: the add is the
    // non-grabbing one and the flip writes a flag, so no run built from this can make an instance
    // download by itself.
    private async Task<Func<string, CancellationToken, Task<WhisparrResponse?>>?>
        ComposeSceneAddAsync(
            WhisparrEntityKind? owningKind,
            int owningId,
            IServiceProvider services,
            CancellationToken runCt)
    {
        if (await ResolveTargetAsync(
                services.GetRequiredService<OptionsStore>(),
                services.GetRequiredService<ICredentialPort>(),
                services.GetRequiredService<IWhisparrInstanceFactory>(),
                runCt).ConfigureAwait(false) is not { } target
            || target.Reads is not IWhisparrMissingSceneActing acting)
        {
            return null;
        }

        var profiles = await ContainedAsync(
            () => target.Reads.ReadQualityProfilesAsync(runCt),
            target,
            _log,
            runCt).ConfigureAwait(false);
        var roots = profiles is null
            ? null
            : await ContainedAsync(
                () => target.Reads.ReadRootFoldersAsync(runCt),
                target,
                _log,
                runCt).ConfigureAwait(false);
        if (profiles is null || roots is null)
        {
            return null;
        }

        if (AddDefaultsProjector.From(profiles.Body, roots.Body).Defaults is not { } runWide)
        {
            return null;
        }

        // Composed once for the run rather than once per scene. The agreement is cached per library
        // root, but a per-scene composition would still repeat the counts for every scene in a page.
        //
        // A pass naming no owning entity keeps the run-wide root: the library-wide scene pass offers
        // every scene the library holds, and there is no one entity to derive a root from.
        var composeWith = runWide;
        if (owningKind is { } owning)
        {
            var composed = await EntityRootIn(services, target, FilesOfEntity(owning, owningId))(
                runWide, runCt).ConfigureAwait(false);
            if (composed.Defaults is not { } perEntity)
            {
                return null;
            }

            composeWith = perEntity;
        }

        return (providerSceneId, markCt) => ContainedAsync(
            () => acting.AddSceneAsync(
                providerSceneId, composeWith, markCt),
            target,
            _log,
            markCt);
    }
}
