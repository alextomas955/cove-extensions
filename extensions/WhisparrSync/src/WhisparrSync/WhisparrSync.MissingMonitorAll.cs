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
using WhisparrSync.Jobs;
using WhisparrSync.Missing;
using WhisparrSync.Options;
using WhisparrSync.Providers;
using WhisparrSync.Whisparr;
using CoreJobProgress = Cove.Core.Interfaces.IJobProgress;

namespace WhisparrSync;

public sealed partial class WhisparrSync
{
    private void MapMissingMonitorAllEndpoints(IEndpointRouteBuilder endpoints)
    {
        // Configure tier. The request names no scene: the narrowing rides the query string and the
        // run re-derives its own set, so the reach is one entity's catalogue.
        endpoints.MapPost(MissingMonitorAllRoute,
            (string kind, int coveId, string? q, string? filters,
             ICurrentPrincipalAccessor principal, IJobService jobs, IServiceScopeFactory scopes,
             OptionsStore options, ICredentialPort credentials, IWhisparrInstanceFactory instances,
             CancellationToken ct)
                => EnqueueMissingMonitorAllAsync(
                    kind, coveId, q, filters, principal, jobs, scopes, options, credentials, instances,
                    ct))
            .WithTags(WireTag)
            .RequireCovePermission(PermissionMode.Any, ConfigurePermissions);
    }

    // Studios and performers only. A tag's catalogue is the whole library's scene set rather than
    // one entity's, so the run would carry no bound. The run id is answered immediately, because a
    // catalogue is many requests to a third party.
    internal async Task<Results<Ok<MissingBulkEnqueued>, BadRequest, ForbiddenCode>>
        EnqueueMissingMonitorAllAsync(
            string kind,
            int coveId,
            string? q,
            string? filters,
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

        if (!TryReadEntity(kind, coveId, out var entityKind)
            || entityKind == WhisparrEntityKind.Tag)
        {
            return TypedResults.BadRequest();
        }

        if (await ResolveTargetAsync(options, credentials, instances, ct).ConfigureAwait(false)
            is not { } target)
        {
            return TypedResults.Ok(
                new MissingBulkEnqueued(null, MissingRefusalKind.NoInstanceConnected));
        }

        // A generation registering no scene add has no implementation to hand over, so there is
        // nothing to compose and no run to start.
        if (target.Reads is not IWhisparrMissingSceneActing)
        {
            return TypedResults.Ok(
                new MissingBulkEnqueued(null, MissingRefusalKind.WhisparrKeepsNoSceneRecords));
        }

        return TypedResults.Ok(
            new MissingBulkEnqueued(
                EnqueueMissingMonitorAll(jobs, scopes, entityKind, coveId, Blank(q), Blank(filters)),
                MissingRefusalKind.None));
    }

    // Exclusive because one scene can be reached from two entities, a video carrying a studio and
    // its performers at once, so overlapping runs would offer the same scene twice.
    private string EnqueueMissingMonitorAll(
        IJobService jobs,
        IServiceScopeFactory scopes,
        WhisparrEntityKind kind,
        int coveId,
        string? titleSearch,
        string? filters)
    {
        var parameters = MissingMonitorAllJob.Encode(kind, coveId, titleSearch, filters);

        return jobs.Enqueue(
            OwnJobTypePrefix + MissingMonitorAllJob.JobId,
            $"[{Name}] Mark every listed scene wanted, one {kind}",
            (progress, ct) => RunMissingMonitorAllAsync(parameters, scopes, progress, ct),
            exclusive: true);
    }

    // Cancellation is rethrown after the summary is written, so the host classifies the run as
    // cancelled while the reader is still told what it managed to mark.
    private async Task RunMissingMonitorAllAsync(
        IReadOnlyDictionary<string, string> parameters,
        IServiceScopeFactory scopes,
        CoreJobProgress progress,
        CancellationToken ct)
    {
        var batch = MissingMonitorAllJob.Decode(parameters);
        var run = await MissingMonitorAllJob
            .RunAsync(batch, scopes, AimAsync, ReadPageAsync, ct)
            .ConfigureAwait(false);

        // The run wrote to the instance, so what the catalogue held describes a state that has
        // moved. Left held, the page read after this run draws the flags from before it.
        using (var scope = scopes.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<InstanceCatalogueCache>().Forget();
        }

        // The host's progress carries no summary field, so the run's one line rides the final
        // report's sub-task.
        progress.Report(1d, MissingBulkJob.SummaryOf(run, MissingBulkVerb.Monitor));
        ct.ThrowIfCancellationRequested();

        Task<Func<string, CancellationToken, Task<WhisparrResponse?>>?> AimAsync(
            IServiceProvider services, CancellationToken runCt)
            => OfferingSceneAddAsync(batch.Kind, batch.CoveId, services, runCt);

        Task<MissingPageView?> ReadPageAsync(
            IServiceProvider services,
            MissingMonitorAllBatch reading,
            int page,
            CancellationToken runCt)
            => ReadMonitorAllPageAsync(services, reading, page, _log, runCt);
    }

    // Null where the page could not be derived. It uses the same request shape as the grid's read,
    // so the run marks what the reader was looking at. No ordering is asked for: an ordering decides
    // which page a scene lands on and not whether it is in the set, and the run covers every page.
    private static async Task<MissingPageView?> ReadMonitorAllPageAsync(
        IServiceProvider services,
        MissingMonitorAllBatch batch,
        int page,
        ILogger log,
        CancellationToken ct)
    {
        if (batch.Kind is not { } kind)
        {
            return null;
        }

        var context = await ResolveMissingContextAsync(
                services.GetRequiredService<OptionsStore>(),
                services.GetRequiredService<ICredentialPort>(),
                services.GetRequiredService<IWhisparrInstanceFactory>(),
                services.GetRequiredService<ProviderEndpointPort>(),
                ct)
            .ConfigureAwait(false);
        if (context is null)
        {
            return null;
        }

        var request = new MissingPageRequest(
            kind,
            batch.CoveId,
            page,
            MissingPerPage,
            Sort: null,
            batch.TitleSearch,
            MissingFilterForm.Read(batch.Filters),
            MenusAlreadyHeld: true);

        try
        {
            return await services
                .GetRequiredService<MissingPagePlanner>()
                .PlanAsync(request, context, log, ct)
                .ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is HttpRequestException or IOException)
        {
            WhisparrSyncLog.CatalogueReadContained(log, WhisparrSyncLog.Classify(failure));
            return null;
        }
    }
}
