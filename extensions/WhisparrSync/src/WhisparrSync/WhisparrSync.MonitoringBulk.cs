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
        // The same tier again: one gesture aiming this extension's stored credential at a third party
        // for every entity in a selection is not a lesser act than doing it for one.
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

    /// <summary>
    /// How many Cove ids one bulk request may carry.
    /// </summary>
    /// <remarks>
    /// Each id is fanned out into per-entity requests against a third party, so a caller-supplied
    /// array is an unbounded fan-out. The bound is applied before anything is encoded or enqueued,
    /// and it sits far above any selection a page can make. A larger job is the caller's to split.
    /// </remarks>
    private const int MaxEntityIdsPerRequest = 1000;

    /// <summary>
    /// How many Cove ids one scene selection may carry for the search verb.
    /// </summary>
    /// <remarks>
    /// One press of that verb becomes one search per scene against every indexer the instance has,
    /// so its cost multiplies outside Cove in a way the other four verbs' does not, and it takes a
    /// lower bound of its own. The bound is applied before anything is encoded or enqueued, and it
    /// is answered under its own code so a caller can state the limit that applied.
    /// </remarks>
    private const int MaxSceneSearchIdsPerRequest = 100;

    /// <summary>The prefix the host mints onto every job type this extension enqueues.</summary>
    private string OwnJobTypePrefix => "ext:" + Id + ":";

    /// <summary>Enqueues one bulk monitoring gesture over a whole selection.</summary>
    /// <remarks>
    /// The gate is re-checked here, in the first statement, because the host's own permission filter
    /// is inert on a minimal-API endpoint - and the required permission the manifest declares beside
    /// the action is a UI affordance only, which hides a button and enforces nothing.
    /// <para>
    /// The id array is capped BEFORE anything is encoded or enqueued, and an oversized one is refused
    /// with the bound named so a caller can split rather than guess.
    /// </para>
    /// <para>
    /// An empty selection is refused rather than enqueued. A job that does nothing still appears in
    /// the host's Job Drawer, where it reads as work that happened.
    /// </para>
    /// <para>
    /// Enqueued EXCLUSIVE. A monitor batch mutates only Whisparr's own flags, so exclusivity is not
    /// required for correctness; what it prevents is two batches over overlapping selections issuing
    /// overlapping adds. This is reasoned rather than measured, and the cost if it is wrong is that
    /// two batches run one after the other.
    /// </para>
    /// </remarks>
    internal Results<Accepted<JobEnqueued>, BadRequest<ErrorCode>, ForbiddenCode> BulkMonitorEnqueue(
        MonitorBulkRequest request,
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

        if (!TryParseSelectionType(request.EntityType, out _))
        {
            return TypedResults.BadRequest(new ErrorCode("UNSUPPORTED_ENTITY_TYPE"));
        }

        // Before the id guards rather than beside them. The verb decides what the request IS, so a
        // body naming none is refused without the size of the selection mattering: a caller told to
        // split an over-cap selection would send two halves, each still naming no verb.
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

        var jobId = jobs.Enqueue(
            OwnJobTypePrefix + MonitoringBulkJob.JobId,
            $"[{Name}] Monitoring, {entityIds.Length} selected",
            (progress, ct) => RunBulkMonitorAsync(parameters, scopes, progress, ct),
            exclusive: true);

        return TypedResults.Accepted((string?)null, new JobEnqueued(jobId));
    }

    /// <summary>Where one of this extension's own runs has got to.</summary>
    /// <remarks>
    /// This extension serves it because Cove gates its own job route on unrestricted read, so a
    /// scoped account is refused there even for a run it started itself.
    /// <para>
    /// A job whose type does not carry this extension's own prefix is answered NOT FOUND rather than
    /// forbidden. Answering forbidden would confirm that the id names a real job, which is exactly the
    /// fact the host's own gate withholds, and would make this route a way around that gate rather
    /// than a replacement for the part of it this extension owns.
    /// </para>
    /// </remarks>
    internal Results<Ok<BulkJobStatus>, NotFound, ForbiddenCode> BulkJobStatusOf(
        string jobId, ICurrentPrincipalAccessor principal, IJobService jobs)
    {
        if (!HasConfigurePermission(principal))
        {
            return new ForbiddenCode();
        }

        ArgumentNullException.ThrowIfNull(jobs);

        var job = jobs.GetJob(jobId);
        return job is null || !job.Type.StartsWith(OwnJobTypePrefix, StringComparison.Ordinal)
            ? TypedResults.NotFound()
            : TypedResults.Ok(BulkJobStatus.From(job));
    }

    /// <summary>Runs one enqueued batch.</summary>
    /// <remarks>
    /// The parameters are decoded tolerantly, so a batch nobody can read does nothing rather than
    /// faulting inside the host's job runner. A verb or a selection type the map does not name is
    /// that same case.
    /// <para>
    /// The target is resolved once, on the first entity's turn, and reused for the rest: it is one
    /// stored read and one credential read, and taking them per entity would be a batch of them.
    /// </para>
    /// <para>
    /// A cancellation is rethrown after the summary is written, so the host classifies the run as
    /// cancelled rather than completed while the reader is still told what it managed to do.
    /// </para>
    /// </remarks>
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

        // One line per library root for the whole selection. Every entity under one root reaches the
        // same reason, and a line per entity would grow with the selection.
        var addressRefusals = new Dictionary<string, FolderAddressRefusal>(StringComparer.Ordinal);
        var addressedRoots = new HashSet<string>(StringComparer.Ordinal);

        var run = TryParseSelectionType(batch.EntityType, out var kind) && batch.Verb is { } verb
            ? await UnderTheVerbAsync().ConfigureAwait(false)
            : MonitorBulkRun.NothingSelected;

        await RecordRootReadingsAsync(scopes, [.. addressRefusals.Values], [.. addressedRoots])
            .ConfigureAwait(false);

        // The host's progress carries no summary field, so the run's one line rides the final
        // report's sub-task.
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

        // The search verb's instance-side command names an id array, so the whole selection is one
        // call. Every other verb here is one instance call per entity, and loops.
        Task<MonitorBulkRun> UnderTheVerbAsync()
            => verb == MonitorBulkVerb.SearchAllMonitored
                ? MonitoringBulkJob.RunOneCallAsync(
                    batch.EntityIds, scopes, AimOneAsync, SearchNamedAsync, progress, ct)
                : MonitoringBulkJob.RunAsync(batch.EntityIds, scopes, ActOnOneAsync, progress, ct);

        // Resolved once for the whole batch: it is one stored read and one credential read, and
        // taking them per entity would be a batch of them.
        async Task<MonitoringTarget?> ResolvedAsync(
            IServiceProvider services, CancellationToken runCt)
        {
            if (!targetResolved)
            {
                target = await ResolveTargetAsync(
                    services.GetRequiredService<OptionsStore>(),
                    services.GetRequiredService<ICredentialPort>(),
                    services.GetRequiredService<IWhisparrClient>(),
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

        // One command naming every entity the resolution step established the instance holds. Its
        // answer is each of those entities' outcome, because the instance answers the command and
        // not the ids inside it.
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
                    resolved.BaseAddress, resolved.ApiKey, resolved.Generation, kind, entityIds, runCt),
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

            // The same statement of each verb the single-entity route reaches, so a selection cannot
            // behave differently from a click. A verb the connected generation cannot honour is
            // answered per entity by that shared path rather than failing the batch.
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

            // Inline rather than enqueued, and only for a monitor a read confirmed. The click
            // enqueues so the request does not wait for an entity's folder set; a selection is
            // already inside a run, and enqueuing per entity would make one gesture a run per entity.
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

                // The hard-link setting is a property of the INSTANCE, resolved once for the batch
                // the way the target is. A selection of a thousand entities must not read one value
                // a thousand times.
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

    /// <summary>
    /// The entity kind <paramref name="entityType"/> names, in the spelling the selection bar passes.
    /// </summary>
    /// <remarks>
    /// Matched against the same constants the registration declares, so what the bar has to send to
    /// see the button and what the route accepts cannot drift apart.
    /// </remarks>
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
