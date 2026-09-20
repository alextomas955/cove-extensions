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
using WhisparrSync.Monitoring;
using WhisparrSync.Options;
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
             OptionsStore options, ICredentialPort credentials, IWhisparrClient client,
             CancellationToken ct)
                => EnqueueMissingBulkMonitorAsync(
                    kind, coveId, request, principal, jobs, scopes, options, credentials, client, ct))
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
            IWhisparrClient client,
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

        if (await ResolveTargetAsync(options, credentials, client, ct).ConfigureAwait(false)
            is not { } target)
        {
            return TypedResults.Ok(
                new MissingBulkEnqueued(null, MissingRefusalKind.NoInstanceConnected));
        }

        // A generation registering no scene add has no implementation to hand over, so there is
        // nothing to compose and no run to start.
        if (target.Capabilities.Obtain<IWhisparrMissingSceneActing>()
                .Match<IWhisparrMissingSceneActing?>(held => held, _ => null) is null)
        {
            return TypedResults.Ok(
                new MissingBulkEnqueued(null, MissingRefusalKind.WhisparrKeepsNoSceneRecords));
        }

        return TypedResults.Ok(
            new MissingBulkEnqueued(
                EnqueueMissingBulk(jobs, scopes, entityKind, coveId, request.ProviderSceneIds),
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
        IReadOnlyList<string> providerSceneIds)
    {
        var parameters = MissingBulkJob.Encode(kind, coveId, providerSceneIds);

        return jobs.Enqueue(
            OwnJobTypePrefix + MissingBulkJob.JobId,
            $"[{Name}] Mark selected scenes wanted, one {kind}",
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
                (services, runCt) => ComposeSceneAddAsync(
                    batch.Kind, batch.CoveId, services, runCt),
                ct)
            .ConfigureAwait(false);

        // The host's progress carries no summary field, so the run's one line rides the final
        // report's sub-task.
        progress.Report(1d, MissingBulkJob.SummaryOf(run));
        ct.ThrowIfCancellationRequested();
    }

    // Null where the run must not act at all. The verb closed over is the non-grabbing scene add, so
    // no run built from this can make an instance download. The profile and the root are read here
    // rather than at enqueue, because the instance may change either after a run is queued.
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
                services.GetRequiredService<IWhisparrClient>(),
                runCt).ConfigureAwait(false) is not { } target
            || target.Capabilities.Obtain<IWhisparrMissingSceneActing>()
                .Match<IWhisparrMissingSceneActing?>(held => held, _ => null) is not { } acting)
        {
            return null;
        }

        var profiles = await ContainedAsync(
            () => target.Reads.ReadQualityProfilesAsync(target.BaseAddress, target.ApiKey, runCt),
            target,
            _log,
            runCt).ConfigureAwait(false);
        var roots = profiles is null
            ? null
            : await ContainedAsync(
                () => target.Reads.ReadRootFoldersAsync(target.BaseAddress, target.ApiKey, runCt),
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
                target.BaseAddress, target.ApiKey, providerSceneId, composeWith, markCt),
            target,
            _log,
            markCt);
    }
}
