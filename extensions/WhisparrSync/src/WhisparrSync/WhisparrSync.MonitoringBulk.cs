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
// The SDK declares a job-progress interface of its own, and the one the host's job service hands a
// work delegate is the core's. An unqualified reference compiles and means the other one.
using CoreJobProgress = Cove.Core.Interfaces.IJobProgress;

namespace WhisparrSync;

public sealed partial class WhisparrSync
{
    private void MapMonitoringBulkEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(BulkMonitorRoute,
            (MonitorBulkRequest request, ICurrentPrincipalAccessor principal, BackgroundWork work)
                => BulkMonitorEnqueue(request, principal, work))
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
        BackgroundWork work)
    {
        var (jobs, scopes) = work;

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
        var acting = TryParseSelectionType(batch.EntityType, out var kind) && batch.Verb is { } verb
            ? new BulkVerbs(this, batch, kind, verb)
            : null;

        var run = acting is null
            ? MonitorBulkRun.NothingSelected
            : await acting.RunAsync(scopes, progress, ct).ConfigureAwait(false);

        // The instance the run reached, which is null when nothing resolved. With no instance there
        // is no generation to file readings under, and there are none to file.
        if (acting?.Target.Resolved?.Binding.Generation is { } generation)
        {
            await RecordRootReadingsAsync(
                scopes, generation, acting.Linking.AddressRefusals, acting.Linking.AddressedRoots)
                .ConfigureAwait(false);
        }

        // The host's progress carries no summary field, so the run's one line rides the final
        // report's sub-task. Cancellation is rethrown after that write, so the host classifies the
        // run as cancelled and the reader still sees what it managed to do.
        progress.Report(1d, MonitoringBulkJob.SummaryOf(run, acting?.Linking.Summary));
        ct.ThrowIfCancellationRequested();
    }

    // One selection under one verb. Held as a type rather than as closures over the run, so the
    // target read once for the batch and the linking accumulated across it have an owner.
    private sealed class BulkVerbs(
        WhisparrSync owner, MonitorBulkBatch batch, WhisparrEntityKind kind, MonitorBulkVerb verb)
    {
        internal BatchTarget Target { get; } = new();

        internal BulkLinking Linking { get; } = new(kind, owner);

        // The search command on the instance takes an id array, so the whole selection is one call.
        // Every other verb is one call per entity.
        internal Task<MonitorBulkRun> RunAsync(
            IServiceScopeFactory scopes, CoreJobProgress progress, CancellationToken ct)
            => verb == MonitorBulkVerb.SearchAllMonitored
                ? MonitoringBulkJob.RunOneCallAsync(
                    batch.EntityIds, scopes, AimOneAsync, SearchNamedAsync, progress, ct)
                : MonitoringBulkJob.RunAsync(batch.EntityIds, scopes, ActOnOneAsync, progress, ct);

        private async Task<MonitoringBulkJob.MonitorBulkAim> AimOneAsync(
            IServiceProvider services, int coveId, CancellationToken entityCt)
            => await Target.OfAsync(services, entityCt).ConfigureAwait(false) is not { } resolved
                ? new MonitoringBulkJob.MonitorBulkAim(null, MonitorRefusalKind.NotConfigured)
                : await AimSearchAsync(
                    kind,
                    coveId,
                    resolved,
                    services.GetRequiredService<IEntityIdentityPort>(),
                    SearchGrabbingOn(resolved) is not null,
                    owner._log,
                    entityCt).ConfigureAwait(false);

        // The instance answers the command, not the ids inside it, so its one answer is every named
        // entity's outcome.
        private async Task<MonitorRefusalKind> SearchNamedAsync(
            IServiceProvider services, IReadOnlyList<int> entityIds, CancellationToken runCt)
        {
            if (await Target.OfAsync(services, runCt).ConfigureAwait(false) is not { } resolved)
            {
                return MonitorRefusalKind.NotConfigured;
            }

            if (SearchGrabbingOn(resolved) is not { } grabbing)
            {
                return MonitorRefusalKind.CapabilityAbsentOnThisGeneration;
            }

            var searched = await ContainedAsync(
                () => grabbing.SearchMonitoredAsync(kind, entityIds, runCt),
                resolved,
                owner._log,
                runCt).ConfigureAwait(false);

            return searched is null
                ? MonitorRefusalKind.InstanceRefused
                : MonitoringProjector.Accepted(searched);
        }

        private async Task<MonitorRefusalKind> ActOnOneAsync(
            IServiceProvider services, int coveId, CancellationToken entityCt)
        {
            if (await Target.OfAsync(services, entityCt).ConfigureAwait(false) is not { } resolved)
            {
                return MonitorRefusalKind.NotConfigured;
            }

            var view = await ActOnResolvedAsync(services, coveId, resolved, entityCt)
                .ConfigureAwait(false);

            // Inline rather than enqueued: this is already inside a run, and enqueuing per entity
            // would turn one gesture into a run per entity.
            if (verb == MonitorBulkVerb.Monitor
                && view is { Refusal: MonitorRefusalKind.None, Monitored: true })
            {
                await Linking.LinkAsync(services, resolved, coveId, entityCt).ConfigureAwait(false);
            }

            return view.Refusal;
        }

        // The same path the single-entity route takes, so a selection cannot behave differently from
        // a click. A verb the connected generation cannot honour is refused per entity there rather
        // than failing the batch.
        private async Task<EntityMonitoringView> ActOnResolvedAsync(
            IServiceProvider services,
            int coveId,
            MonitoringTarget resolved,
            CancellationToken entityCt)
        {
            var identities = services.GetRequiredService<IEntityIdentityPort>();

            return verb switch
            {
                MonitorBulkVerb.Monitor => await MonitorResolvedAsync(
                    new MonitoredEntity(kind, coveId),
                    resolved,
                    identities,
                    owner._log,
                    ActingFor(kind, resolved, batch.Scope ?? resolved.DefaultMonitorScope),
                    EntityRootIn(services, resolved, FilesOfEntity(kind, coveId)),
                    entityCt).ConfigureAwait(false),
                MonitorBulkVerb.Unmonitor => await UnmonitorResolvedAsync(
                    kind, coveId, resolved, identities, owner._log, entityCt).ConfigureAwait(false),
                _ => throw new InvalidOperationException(
                    $"{verb} is not a verb the bulk surface carries."),
            };
        }
    }

    // Resolved once for the whole batch: it is one stored read and one credential read, and taking
    // them per entity would be one pair per entity.
    private sealed class BatchTarget
    {
        private bool _read;

        internal MonitoringTarget? Resolved { get; private set; }

        internal async Task<MonitoringTarget?> OfAsync(
            IServiceProvider services, CancellationToken ct)
        {
            if (_read)
            {
                return Resolved;
            }

            _read = true;
            Resolved = await ResolveTargetAsync(
                services.GetRequiredService<WhisparrAccess>(), ct).ConfigureAwait(false);
            return Resolved;
        }
    }

    // The linking half of a bulk run: aimed once for the batch, then accumulated per entity.
    //
    // The refusals are keyed by library root, not per entity: a per-entity list would grow with the
    // selection, and every entity under one root reaches the same reason.
    private sealed class BulkLinking(WhisparrEntityKind kind, WhisparrSync owner)
    {
        private readonly Dictionary<string, FolderAddressRefusal> _refusalByRoot =
            new(StringComparer.Ordinal);

        private readonly HashSet<string> _addressedRoots = new(StringComparer.Ordinal);

        private ReflectOwnedAiming? _through;
        private ReflectOwnedSkipReason? _skipped;
        private bool _aimed;
        private bool _reached;
        private int _foldersAttached;
        private int _foldersRefused;
        private int _entriesLeftUnderAnotherRoot;
        private bool _rootsCouldNotBeRead;

        internal IReadOnlyList<FolderAddressRefusal> AddressRefusals => [.. _refusalByRoot.Values];

        internal IReadOnlyList<string> AddressedRoots => [.. _addressedRoots];

        // Null where no generation offered the linking role at all, which the summary states as an
        // absence rather than as a run that linked nothing.
        internal MonitorBulkLinking? Summary => _reached
            ? new MonitorBulkLinking(
                _skipped,
                _foldersAttached,
                _foldersRefused,
                AddressRefusals,
                _entriesLeftUnderAnotherRoot,
                _rootsCouldNotBeRead)
            : null;

        internal async Task LinkAsync(
            IServiceProvider services,
            MonitoringTarget resolved,
            int coveId,
            CancellationToken ct)
        {
            await AimAsync(services, resolved, ct).ConfigureAwait(false);
            if (_through is not { } aimed)
            {
                return;
            }

            Add(await ReflectOwnedJob.RunOneAsync(services, aimed, kind, coveId, ct)
                .ConfigureAwait(false));
        }

        // The hard-link setting belongs to the instance, so it is read once for the batch rather
        // than once per entity.
        private async Task AimAsync(
            IServiceProvider services, MonitoringTarget resolved, CancellationToken ct)
        {
            if (_aimed)
            {
                return;
            }

            _aimed = true;
            if (ReflectOwnedActingOn(resolved) is not { } acting)
            {
                return;
            }

            _reached = true;
            var decision = await owner.ReflectOwnedDecisionAsync(resolved, acting, ct)
                .ConfigureAwait(false);
            _skipped = decision.Reason;
            _through = decision.Act
                ? owner.AimedAt(
                    resolved, acting, services.GetRequiredService<IFolderAddressPort>())
                : null;
        }

        private void Add(ReflectOwnedRun linked)
        {
            _foldersAttached += linked.FoldersAttached;
            _foldersRefused += linked.FoldersRefused;
            _entriesLeftUnderAnotherRoot += linked.EntriesLeftUnderAnotherRoot;
            _rootsCouldNotBeRead |= linked.RootsCouldNotBeRead;

            foreach (var root in linked.AddressedRoots ?? [])
            {
                _addressedRoots.Add(root);
            }

            foreach (var refusal in linked.AddressRefusals ?? [])
            {
                _refusalByRoot.TryAdd(refusal.CoveRoot, refusal);
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
