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
        // The same tier again, and the same act over a wider reach. It names no scene at all: the
        // narrowing rides the query string and the run re-derives its own set, so what a caller can
        // reach is one entity's catalogue and never a set it composed itself.
        endpoints.MapPost(MissingMonitorAllRoute,
            (string kind, int coveId, string? q, string? filters,
             ICurrentPrincipalAccessor principal, IJobService jobs, IServiceScopeFactory scopes,
             OptionsStore options, ICredentialPort credentials, IWhisparrClient client,
             CancellationToken ct)
                => EnqueueMissingMonitorAllAsync(
                    kind, coveId, q, filters, principal, jobs, scopes, options, credentials, client,
                    ct))
            .WithTags(WireTag)
            .RequireCovePermission(PermissionMode.Any, ConfigurePermissions);
    }

    /// <summary>Marks every scene the narrowed catalogue lists as wanted, as one background run.</summary>
    /// <remarks>
    /// The route carries the narrowing the grid is showing and no scene identifiers at all: the run
    /// re-derives its own set through the derivation the grid reads through, so the set acted on is
    /// the set on screen without the browser enumerating pages to name it.
    /// <para>
    /// Studios and performers only. A tag's catalogue is the whole library's scene set rather than
    /// one entity's, so this route can put no bound on the run it would start and expresses no tag.
    /// </para>
    /// <para>
    /// The run is started and its id answered immediately, for the reason the selection route's is: a
    /// catalogue is many requests to a third party and waiting would hold the browser open for the
    /// length of the run.
    /// </para>
    /// </remarks>
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
            IWhisparrClient client,
            CancellationToken ct)
    {
        // Checked in the handler, because the route's own declaration enforces nothing on a minimal
        // API.
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
                EnqueueMissingMonitorAll(jobs, scopes, entityKind, coveId, Blank(q), Blank(filters)),
                MissingRefusalKind.None));
    }

    /// <summary>Starts one whole-catalogue marking run in the background.</summary>
    /// <remarks>
    /// Enqueued EXCLUSIVE, for the reason the selection run is: one scene can be reached from two
    /// entities, because a video carries a studio and its performers at once, so overlapping runs
    /// would offer the same scene twice.
    /// </remarks>
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

    /// <summary>Runs one enqueued whole-catalogue pass.</summary>
    /// <remarks>
    /// Everything the run acts through is resolved when it STARTS. The profile and the root each add
    /// carries are the instance's to change at any time, and a run enqueued minutes ago must not
    /// create catalogue items under values read before that.
    /// <para>
    /// A cancellation is rethrown after the summary is written, so the host classifies the run as
    /// cancelled rather than completed while the reader is still told what it managed to mark.
    /// </para>
    /// </remarks>
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

        // The host's progress carries no summary field, so the run's one line rides the final
        // report's sub-task. The wording is the selection run's, both being one marking pass.
        progress.Report(1d, MissingBulkJob.SummaryOf(run));
        ct.ThrowIfCancellationRequested();

        Task<Func<string, CancellationToken, Task<WhisparrResponse?>>?> AimAsync(
            IServiceProvider services, CancellationToken runCt)
            => ComposeSceneAddAsync(batch.Kind, batch.CoveId, services, runCt);

        Task<MissingPageView?> ReadPageAsync(
            IServiceProvider services,
            MissingMonitorAllBatch reading,
            int page,
            CancellationToken runCt)
            => ReadMonitorAllPageAsync(services, reading, page, _log, runCt);
    }

    /// <summary>One page of the run's own narrowed catalogue, or null where it could not be derived.</summary>
    /// <remarks>
    /// Through <see cref="MissingPagePlanner"/> and the same request shape the grid's read builds, so
    /// what the run marks is what the reader was looking at rather than a second derivation that can
    /// disagree with it.
    /// <para>
    /// No ordering is asked for. An ordering decides which page a scene lands on and never whether it
    /// is in the set, and the run covers every page.
    /// </para>
    /// </remarks>
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
                services.GetRequiredService<IWhisparrClient>(),
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
