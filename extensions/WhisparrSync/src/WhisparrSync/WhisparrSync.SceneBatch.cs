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
using WhisparrSync.Library;
using WhisparrSync.Options;
using WhisparrSync.Whisparr;
using CoreJobProgress = Cove.Core.Interfaces.IJobProgress;

namespace WhisparrSync;

public sealed partial class WhisparrSync
{
    private void MapSceneBatchEndpoints(IEndpointRouteBuilder endpoints)
    {
        // Configure tier: one gesture aims the stored credential at a third party for every scene
        // in the selection. The reach is what the body names.
        endpoints.MapPost(SceneBatchRoute,
            (SceneBatchRequest request, ICurrentPrincipalAccessor principal, IJobService jobs,
             IServiceScopeFactory scopes)
                => EnqueueSceneBatch(request, principal, jobs, scopes))
            .WithTags(WireTag)
            .RequireCovePermission(PermissionMode.Any, ConfigurePermissions);
    }

    // The gate is re-checked in the first statement, because the host's permission filter is inert
    // on a minimal-API endpoint and the manifest's declared permission only hides a button. The
    // bound is applied before anything is encoded, so nothing is partially applied, and the refusal
    // carries the bound so the browser can name the limit. An empty selection is refused rather
    // than enqueued, because a run that does nothing still reads as work in the host's job list.
    // Exclusive, so two runs over overlapping selections cannot issue overlapping adds.
    internal Results<Accepted<JobEnqueued>, BadRequest<ErrorCode>, ForbiddenCode> EnqueueSceneBatch(
        SceneBatchRequest request,
        ICurrentPrincipalAccessor principal,
        IJobService jobs,
        IServiceScopeFactory scopes)
    {
        if (!HasConfigurePermission(principal))
        {
            return new ForbiddenCode();
        }

        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(jobs);

        if (request.EntityType != VideosSelectionType)
        {
            return TypedResults.BadRequest(new ErrorCode("UNSUPPORTED_ENTITY_TYPE"));
        }

        if (request.Verb is not { } verb)
        {
            return TypedResults.BadRequest(new ErrorCode("MISSING_VERB"));
        }

        if (request.CoveIds is not { } coveIds)
        {
            return TypedResults.BadRequest(new ErrorCode("MISSING_ENTITY_IDS"));
        }

        var bound = BoundFor(verb);
        if (coveIds.Length > bound)
        {
            return TypedResults.BadRequest(
                new ErrorCode(
                    verb == SceneBatchVerb.Search ? "TOO_MANY_SEARCH_IDS" : "TOO_MANY_IDS",
                    bound));
        }

        if (coveIds.Length == 0)
        {
            return TypedResults.BadRequest(new ErrorCode("NOTHING_SELECTED"));
        }

        var parameters = SceneBatchJob.Encode(verb, coveIds);

        var jobId = jobs.Enqueue(
            OwnJobTypePrefix + SceneBatchJob.JobId,
            $"[{Name}] Scenes, {coveIds.Length} selected",
            (progress, ct) => RunSceneBatchAsync(parameters, scopes, progress, ct),
            exclusive: true);

        return TypedResults.Accepted((string?)null, new JobEnqueued(jobId));
    }

    // A search becomes one search per scene against every indexer the instance has, so its cost
    // multiplies outside Cove and it takes the lower bound.
    private static int BoundFor(SceneBatchVerb verb)
        => verb == SceneBatchVerb.Search ? MaxSceneSearchIdsPerRequest : MaxEntityIdsPerRequest;

    // The parameters are decoded tolerantly, so a run nobody can read does nothing rather than
    // faulting inside the host's job runner. The instance is resolved on the first scene's turn and
    // reused, so the stored read and the credential read do not repeat per scene. Cancellation is
    // rethrown after the summary is written, so the host classifies the run as cancelled while the
    // reader is still told what it managed to do.
    private async Task RunSceneBatchAsync(
        IReadOnlyDictionary<string, string> parameters,
        IServiceScopeFactory scopes,
        CoreJobProgress progress,
        CancellationToken ct)
    {
        var batch = SceneBatchJob.Decode(parameters);

        MonitoringTarget? target = null;
        var targetResolved = false;

        var run = batch.Verb is { } verb
            ? await SceneBatchJob.RunAsync(batch.CoveIds, scopes, ActOnOneAsync, progress, ct)
                .ConfigureAwait(false)
            : SceneBatchJob.Untaken;

        // The host's progress carries no summary field, so the run's one line rides the final
        // report's sub-task.
        progress.Report(1d, SceneBatchJob.SummaryOf(run));
        ct.ThrowIfCancellationRequested();

        async Task<SceneRefusalKind> ActOnOneAsync(
            IServiceProvider services, int coveId, CancellationToken sceneCt)
        {
            var options = services.GetRequiredService<OptionsStore>();
            var credentials = services.GetRequiredService<ICredentialPort>();
            var client = services.GetRequiredService<IWhisparrClient>();

            if (!targetResolved)
            {
                target = await ResolveTargetAsync(options, credentials, client, sceneCt)
                    .ConfigureAwait(false);
                targetResolved = true;
            }

            if (target is null)
            {
                return SceneRefusalKind.NoInstanceConnected;
            }

            var sceneCards = services.GetRequiredService<ILibraryCardIdentityPort>();

            // The same verbs the single-scene routes reach, so a selection cannot behave
            // differently from a click. A refusal is answered per scene and the run carries on.
            var acted = verb switch
            {
                SceneBatchVerb.Add => await AddSceneResolvedAsync(
                    coveId, target, options, credentials, client, sceneCards, scopes, _log, sceneCt)
                    .ConfigureAwait(false),
                SceneBatchVerb.Monitor => await SetSceneMonitoringResolvedAsync(
                    monitored: true,
                    coveId,
                    target,
                    options,
                    credentials,
                    client,
                    sceneCards,
                    _log,
                    sceneCt).ConfigureAwait(false),
                SceneBatchVerb.Unmonitor => await SetSceneMonitoringResolvedAsync(
                    monitored: false,
                    coveId,
                    target,
                    options,
                    credentials,
                    client,
                    sceneCards,
                    _log,
                    sceneCt).ConfigureAwait(false),
                SceneBatchVerb.Search => await SearchSceneResolvedAsync(
                    coveId, target, options, credentials, client, sceneCards, _log, sceneCt)
                    .ConfigureAwait(false),
                SceneBatchVerb.Exclude => await ExcludeSceneResolvedAsync(
                    coveId, target, options, credentials, client, sceneCards, _log, sceneCt)
                    .ConfigureAwait(false),
                _ => throw new InvalidOperationException(
                    $"{verb} is not a verb the scene selection carries."),
            };

            return acted.Refusal;
        }
    }
}
