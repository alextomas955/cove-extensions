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
        // The same tier again: one gesture aiming this extension's stored credential at a third
        // party for every scene in a selection is not a lesser act than doing it for one. The reach
        // is what the body names, and the verb it names decides which bound applies.
        endpoints.MapPost(SceneBatchRoute,
            (SceneBatchRequest request, ICurrentPrincipalAccessor principal, IJobService jobs,
             IServiceScopeFactory scopes)
                => EnqueueSceneBatch(request, principal, jobs, scopes))
            .WithTags(WireTag)
            .RequireCovePermission(PermissionMode.Any, ConfigurePermissions);
    }

    /// <summary>Enqueues one verb over a whole selection of scenes.</summary>
    /// <remarks>
    /// The gate is re-checked here, in the first statement, because the host's own permission filter
    /// is inert on a minimal-API endpoint, and the required permission the manifest declares beside
    /// the action hides a button and enforces nothing.
    /// <para>
    /// The verb is read before the size of the selection matters, because the verb decides which
    /// bound applies. The bound is applied BEFORE anything is encoded or enqueued, and an oversized
    /// selection is refused with a code carrying the bound that applied, so nothing is partially
    /// applied and the browser can name the limit a reader met.
    /// </para>
    /// <para>
    /// An empty selection is refused rather than enqueued. A run that does nothing still appears in
    /// the host's Job Drawer, where it reads as work that happened.
    /// </para>
    /// <para>
    /// Enqueued EXCLUSIVE, following the selection this product already enqueues: what it prevents
    /// is two runs over overlapping selections issuing overlapping adds.
    /// </para>
    /// </remarks>
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

    /// <summary>How many Cove ids a selection may carry for <paramref name="verb"/>.</summary>
    /// <remarks>
    /// One search becomes one search per scene against every indexer the instance has, so the search
    /// verb's cost multiplies outside Cove in a way the other four verbs' does not, and it takes the
    /// lower of the two bounds.
    /// </remarks>
    private static int BoundFor(SceneBatchVerb verb)
        => verb == SceneBatchVerb.Search ? MaxSceneSearchIdsPerRequest : MaxEntityIdsPerRequest;

    /// <summary>Runs one enqueued selection of scenes.</summary>
    /// <remarks>
    /// The parameters are decoded tolerantly, so a run nobody can read does nothing rather than
    /// faulting inside the host's job runner. A verb the map does not name is that same case.
    /// <para>
    /// The instance is resolved once, on the first scene's turn, and reused for the rest: it is one
    /// stored read and one credential read, and taking them per scene would be a selection of them.
    /// </para>
    /// <para>
    /// A cancellation is rethrown after the summary is written, so the host classifies the run as
    /// cancelled while the reader is still told what it managed to do.
    /// </para>
    /// </remarks>
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

            // The same statement of each verb the single-scene route reaches, so a selection cannot
            // behave differently from a click: the pre-send refusals a read establishes are answered
            // per scene and the run carries on to the scenes after it.
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
