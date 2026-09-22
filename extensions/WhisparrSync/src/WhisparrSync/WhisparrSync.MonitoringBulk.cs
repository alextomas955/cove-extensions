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
// The SDK declares a job-progress interface of its own, and the one the host's job service hands a
// work delegate is the core's. An unqualified reference compiles and means the other one.
using CoreJobProgress = Cove.Core.Interfaces.IJobProgress;

namespace WhisparrSync;

public sealed partial class WhisparrSync
{
    private void MapMonitoringBulkEndpoints(IEndpointRouteBuilder endpoints)
    {
        // A selection aims the stored credential at a third party once per entity, so it takes the
        // same permission as a single entity.
        endpoints.MapPost(BulkMonitorRoute,
            (MonitorBulkRequest request, ICurrentPrincipalAccessor principal, IJobService jobs,
             IServiceScopeFactory scopes)
                => BulkMonitorEnqueue(request, principal, jobs, scopes))
            .WithTags(WireTag)
            .RequireCovePermission(PermissionMode.Any, ConfigurePermissions);

        endpoints.MapGet(JobStatusRoute,
            (string jobId, ICurrentPrincipalAccessor principal, IJobService jobs)
                => BulkJobStatusOf(jobId, principal, jobs))
            .WithTags(WireTag)
            .RequireCovePermission(PermissionMode.Any, ConfigurePermissions);
    }

    // Each id fans out into a per-entity request against a third party, so an uncapped array is an
    // unbounded fan-out.
    private const int MaxEntityIdsPerRequest = 1000;

    // The search verb becomes one search per scene against every indexer the instance has, so it
    // takes a lower bound than the other verbs.
    private const int MaxSceneSearchIdsPerRequest = 100;

    private string OwnJobTypePrefix => "ext:" + Id + ":";

    internal Results<Accepted<JobEnqueued>, BadRequest<ErrorCode>, ForbiddenCode> BulkMonitorEnqueue(
        MonitorBulkRequest request,
        ICurrentPrincipalAccessor principal,
        IJobService jobs,
        IServiceScopeFactory scopes)
    {
        // The host's permission filter is inert on a minimal-API endpoint, and the manifest's
        // required permission only hides a button, so the gate is re-checked here.
        if (!HasConfigurePermission(principal))
        {
            return new ForbiddenCode();
        }

        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(jobs);

        if (!TryParseSelectionType(request.EntityType, out _))
        {
            return TypedResults.BadRequest(new ErrorCode("UNSUPPORTED_ENTITY_TYPE"));
        }

        // Checked before the id guards: a caller told to split an over-cap selection would resend
        // two halves that still name no verb.
        if (request.Verb is not { } verb)
        {
            return TypedResults.BadRequest(new ErrorCode("MISSING_VERB"));
        }

        if (request.EntityIds is not { } entityIds)
        {
            return TypedResults.BadRequest(new ErrorCode("MISSING_ENTITY_IDS"));
        }

        if (entityIds.Length > MaxEntityIdsPerRequest)
        {
            return TypedResults.BadRequest(new ErrorCode("TOO_MANY_IDS", MaxEntityIdsPerRequest));
        }

        if (entityIds.Length == 0)
        {
            return TypedResults.BadRequest(new ErrorCode("NOTHING_SELECTED"));
        }

        var parameters = MonitoringBulkJob.Encode(
            request.EntityType!, verb, request.Scope, entityIds);

        // Exclusive so two batches over overlapping selections do not issue overlapping adds.
        var jobId = jobs.Enqueue(
            OwnJobTypePrefix + MonitoringBulkJob.JobId,
            $"[{Name}] Monitoring, {entityIds.Length} selected",
            (progress, ct) => RunBulkMonitorAsync(parameters, scopes, progress, ct),
            exclusive: true);

        return TypedResults.Accepted((string?)null, new JobEnqueued(jobId));
    }

    // This route exists because Cove gates its own job route on unrestricted read, so a scoped
    // account is refused there even for a run it started itself.
    internal Results<Ok<BulkJobStatus>, NotFound, ForbiddenCode> BulkJobStatusOf(
        string jobId, ICurrentPrincipalAccessor principal, IJobService jobs)
    {
        if (!HasConfigurePermission(principal))
        {
            return new ForbiddenCode();
        }

        ArgumentNullException.ThrowIfNull(jobs);

        // A job outside this extension's prefix is answered not found, not forbidden: forbidden
        // would confirm the id names a real job, which is what the host's gate withholds.
        var job = jobs.GetJob(jobId);
        return job is null || !job.Type.StartsWith(OwnJobTypePrefix, StringComparison.Ordinal)
            ? TypedResults.NotFound()
            : TypedResults.Ok(BulkJobStatus.From(job));
    }

    // Parameters are decoded tolerantly, so an unreadable batch does nothing rather than faulting
    // inside the host's job runner.
    private async Task RunBulkMonitorAsync(
        IReadOnlyDictionary<string, string> parameters,
        IServiceScopeFactory scopes,
        CoreJobProgress progress,
        CancellationToken ct)
    {
        var batch = MonitoringBulkJob.Decode(parameters);

        MonitoringTarget? target = null;
        var targetResolved = false;

        ReflectOwnedAiming? linkingThrough = null;
        ReflectOwnedSkipReason? linkingSkipped = null;
        var linkingResolved = false;
        var foldersAttached = 0;
        var foldersRefused = 0;
        var entriesLeftUnderAnotherRoot = 0;
        var linkingReached = false;
        var rootsCouldNotBeRead = false;

        // One line per library root, not per entity: a per-entity list would grow with the
        // selection, and every entity under one root reaches the same reason.
        var addressRefusals = new Dictionary<string, FolderAddressRefusal>(StringComparer.Ordinal);
        var addressedRoots = new HashSet<string>(StringComparer.Ordinal);

        var run = TryParseSelectionType(batch.EntityType, out var kind) && batch.Verb is { } verb
            ? await UnderTheVerbAsync().ConfigureAwait(false)
            : MonitorBulkRun.NothingSelected;

        await RecordRootReadingsAsync(scopes, [.. addressRefusals.Values], [.. addressedRoots])
            .ConfigureAwait(false);

        // The host's progress carries no summary field, so the run's one line rides the final
        // report's sub-task. Cancellation is rethrown after that write, so the host classifies the
        // run as cancelled and the reader still sees what it managed to do.
        progress.Report(
            1d,
            MonitoringBulkJob.SummaryOf(
                run,
                linkingReached
                    ? new MonitorBulkLinking(
                        linkingSkipped,
                        foldersAttached,
                        foldersRefused,
                        [.. addressRefusals.Values],
                        entriesLeftUnderAnotherRoot,
                        rootsCouldNotBeRead)
                    : null));
        ct.ThrowIfCancellationRequested();

        // The search command on the instance takes an id array, so the whole selection is one call.
        // Every other verb is one call per entity.
        Task<MonitorBulkRun> UnderTheVerbAsync()
            => verb == MonitorBulkVerb.SearchAllMonitored
                ? MonitoringBulkJob.RunOneCallAsync(
                    batch.EntityIds, scopes, AimOneAsync, SearchNamedAsync, progress, ct)
                : MonitoringBulkJob.RunAsync(batch.EntityIds, scopes, ActOnOneAsync, progress, ct);

        // Resolved once for the whole batch: it is one stored read and one credential read, and
        // taking them per entity would be one pair per entity.
        async Task<MonitoringTarget?> ResolvedAsync(
            IServiceProvider services, CancellationToken runCt)
        {
            if (!targetResolved)
            {
                target = await ResolveTargetAsync(
                    services.GetRequiredService<OptionsStore>(),
                    services.GetRequiredService<ICredentialPort>(),
                    services.GetRequiredService<IWhisparrInstanceFactory>(),
                    runCt).ConfigureAwait(false);
                targetResolved = true;
            }

            return target;
        }

        async Task<MonitoringBulkJob.MonitorBulkAim> AimOneAsync(
            IServiceProvider services, int coveId, CancellationToken entityCt)
            => await ResolvedAsync(services, entityCt).ConfigureAwait(false) is not { } resolved
                ? new MonitoringBulkJob.MonitorBulkAim(null, MonitorRefusalKind.NotConfigured)
                : await AimSearchAsync(
                    kind,
                    coveId,
                    resolved,
                    services.GetRequiredService<IEntityIdentityPort>(),
                    SearchGrabbingOn(resolved) is not null,
                    _log,
                    entityCt).ConfigureAwait(false);

        // The instance answers the command, not the ids inside it, so its one answer is every
        // named entity's outcome.
        async Task<MonitorRefusalKind> SearchNamedAsync(
            IServiceProvider services, IReadOnlyList<int> entityIds, CancellationToken runCt)
        {
            if (await ResolvedAsync(services, runCt).ConfigureAwait(false) is not { } resolved)
            {
                return MonitorRefusalKind.NotConfigured;
            }

            if (SearchGrabbingOn(resolved) is not { } grabbing)
            {
                return MonitorRefusalKind.CapabilityAbsentOnThisGeneration;
            }

            var searched = await ContainedAsync(
                () => grabbing.SearchMonitoredAsync(
                    kind, entityIds, runCt),
                resolved,
                _log,
                runCt).ConfigureAwait(false);

            return searched is null
                ? MonitorRefusalKind.InstanceRefused
                : MonitoringProjector.Accepted(searched);
        }

        async Task<MonitorRefusalKind> ActOnOneAsync(
            IServiceProvider services, int coveId, CancellationToken entityCt)
        {
            if (await ResolvedAsync(services, entityCt).ConfigureAwait(false) is not { } resolved)
            {
                return MonitorRefusalKind.NotConfigured;
            }

            var identities = services.GetRequiredService<IEntityIdentityPort>();

            // The same path the single-entity route takes, so a selection cannot behave differently
            // from a click. A verb the connected generation cannot honour is refused per entity
            // there rather than failing the batch.
            var view = verb switch
            {
                MonitorBulkVerb.Monitor => await MonitorResolvedAsync(
                    kind,
                    coveId,
                    resolved,
                    identities,
                    _log,
                    ActingFor(kind, resolved, batch.Scope ?? resolved.DefaultMonitorScope),
                    EntityRootIn(services, resolved, FilesOfEntity(kind, coveId)),
                    entityCt).ConfigureAwait(false),
                MonitorBulkVerb.Unmonitor => await UnmonitorResolvedAsync(
                    kind, coveId, resolved, identities, _log, entityCt).ConfigureAwait(false),
                _ => throw new InvalidOperationException(
                    $"{verb} is not a verb the bulk surface carries."),
            };

            // Inline rather than enqueued: this is already inside a run, and enqueuing per entity
            // would turn one gesture into a run per entity.
            if (verb == MonitorBulkVerb.Monitor
                && view is { Refusal: MonitorRefusalKind.None, Monitored: true })
            {
                await LinkOwnedAsync(services, resolved, coveId, entityCt).ConfigureAwait(false);
            }

            return view.Refusal;
        }

        async Task LinkOwnedAsync(
            IServiceProvider services, MonitoringTarget resolved, int coveId, CancellationToken entityCt)
        {
            if (!linkingResolved)
            {
                linkingResolved = true;

                // The hard-link setting belongs to the instance, so it is resolved once for the
                // batch rather than once per entity.
                if (ReflectOwnedActingOn(resolved) is { } acting)
                {
                    linkingReached = true;
                    var decision = await ReflectOwnedDecisionAsync(resolved, acting, entityCt)
                        .ConfigureAwait(false);
                    linkingSkipped = decision.Reason;
                    linkingThrough = decision.Act
                        ? AimedAt(
                            resolved, acting, services.GetRequiredService<IFolderAddressPort>())
                        : null;
                }
            }

            if (linkingThrough is not { } aimed)
            {
                return;
            }

            var linked = await ReflectOwnedJob
                .RunOneAsync(services, aimed, kind, coveId, entityCt).ConfigureAwait(false);
            foldersAttached += linked.FoldersAttached;
            foldersRefused += linked.FoldersRefused;
            entriesLeftUnderAnotherRoot += linked.EntriesLeftUnderAnotherRoot;
            rootsCouldNotBeRead |= linked.RootsCouldNotBeRead;
            foreach (var root in linked.AddressedRoots ?? [])
            {
                addressedRoots.Add(root);
            }

            foreach (var refusal in linked.AddressRefusals ?? [])
            {
                addressRefusals.TryAdd(refusal.CoveRoot, refusal);
            }
        }
    }

    // Matched against the same constants the registration declares, so the spelling the selection
    // bar sends and the one the route accepts cannot drift apart.
    private static bool TryParseSelectionType(string? entityType, out WhisparrEntityKind kind)
    {
        switch (entityType)
        {
            case StudiosSelectionType:
                kind = WhisparrEntityKind.Studio;
                return true;
            case PerformersSelectionType:
                kind = WhisparrEntityKind.Performer;
                return true;
            default:
                kind = default;
                return false;
        }
    }
}
