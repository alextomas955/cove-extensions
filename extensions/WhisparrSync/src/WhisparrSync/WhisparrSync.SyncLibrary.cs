using Cove.Core.Auth;
using Cove.Core.Interfaces;
using Cove.Extensions.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using WhisparrSync.Connection;
using WhisparrSync.Contracts;
using WhisparrSync.Jobs;
using WhisparrSync.Library;
using WhisparrSync.Missing;
using WhisparrSync.Monitoring;
using WhisparrSync.Options;
using WhisparrSync.Scene;
using WhisparrSync.Whisparr;
using CoreJobProgress = Cove.Core.Interfaces.IJobProgress;

namespace WhisparrSync;

public sealed partial class WhisparrSync
{
    /// <summary>The job id one library sync run is minted onto.</summary>
    /// <remarks>
    /// Declared beside the routes rather than on the run, because the count route derives whether a
    /// run is in flight from it and the run reads it for its own type. One literal, so the route's
    /// derivation and the type the host enqueues cannot drift apart.
    /// </remarks>
    internal const string SyncLibraryJobId = "sync-library";

    /// <summary>Starts one count of what a library sync would offer.</summary>
    /// <remarks>
    /// The count is a background run and its id is answered immediately: the comparison is one
    /// request per batch to a third party, and waiting would hold the browser open for its length.
    /// <para>
    /// Enqueued non-exclusive. The count creates nothing and changes nothing, so two of them cost
    /// only requests, and it must not queue behind an unrelated run this extension made exclusive.
    /// </para>
    /// </remarks>
    internal async Task<Results<Ok<SyncEnqueued>, ForbiddenCode>> EnqueueSyncPreviewAsync(
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

        var refused = await SyncRefusalFor(options, credentials, client, ct).ConfigureAwait(false);
        if (refused is not SyncRefusalKind.None)
        {
            return TypedResults.Ok(new SyncEnqueued(null, refused));
        }

        var started = jobs.Enqueue(
            OwnJobTypePrefix + SyncPreviewJob.JobId,
            $"[{Name}] Count what a library sync would offer",
            (progress, runCt) => RunSyncPreviewAsync(scopes, progress, runCt),
            exclusive: false);

        return TypedResults.Ok(new SyncEnqueued(started, SyncRefusalKind.None));
    }

    /// <summary>Answers the counts the last count left, and whether a run is in flight.</summary>
    /// <remarks>
    /// A local read of the held slot. It starts no run and issues no outbound request, so a reader
    /// opening the settings page has paid nothing.
    /// <para>
    /// Whether a run is in flight is derived from the host's own job list rather than from a stored
    /// flag, so it answers false the moment the run ends and no client-side timer is involved.
    /// </para>
    /// </remarks>
    internal async Task<Results<Ok<SyncPreviewRead>, ForbiddenCode>> ReadSyncPreviewAsync(
        ICurrentPrincipalAccessor principal,
        IJobService jobs,
        SyncPreviewCache counts,
        OptionsStore options,
        ICredentialPort credentials,
        IWhisparrClient client,
        CancellationToken ct)
    {
        if (!HasConfigurePermission(principal))
        {
            return new ForbiddenCode();
        }

        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(counts);
        ArgumentNullException.ThrowIfNull(options);

        var running = SyncRunIsInFlight(jobs);
        var refused = await SyncRefusalFor(options, credentials, client, ct).ConfigureAwait(false);
        if (refused is not SyncRefusalKind.None)
        {
            return TypedResults.Ok(new SyncPreviewRead(null, refused, running));
        }

        var stored = await options.LoadAsync(ct).ConfigureAwait(false);
        return TypedResults.Ok(
            new SyncPreviewRead(
                counts.Held(stored.SelectedGeneration), SyncRefusalKind.None, running));
    }

    /// <summary>Runs one enqueued count.</summary>
    /// <remarks>
    /// Everything the count compares against is resolved when it starts, because which instance is
    /// connected is a setting a person can change while a run is queued.
    /// <para>
    /// The summary is the last progress call. The host writes its own unit line over
    /// <c>JobInfo.Summary</c> for a run that declares units, and this one declares none, so the line
    /// written here is the line a reader sees.
    /// </para>
    /// </remarks>
    private async Task RunSyncPreviewAsync(
        IServiceScopeFactory scopes, CoreJobProgress progress, CancellationToken ct)
    {
        var counted = await SyncPreviewJob.RunAsync(scopes, AimAsync, _log, ct).ConfigureAwait(false);
        if (counted is null)
        {
            return;
        }

        progress.SetSummary(SyncPreviewJob.SummaryOf(counted));
        ct.ThrowIfCancellationRequested();

        async Task<SyncPreviewAiming?> AimAsync(IServiceProvider services, CancellationToken runCt)
        {
            var target = await ResolveTargetAsync(
                    services.GetRequiredService<OptionsStore>(),
                    services.GetRequiredService<ICredentialPort>(),
                    services.GetRequiredService<IWhisparrClient>(),
                    runCt)
                .ConfigureAwait(false);

            if (target?.Capabilities.Obtain<IWhisparrSceneStatusReading>()
                    .Match<IWhisparrSceneStatusReading?>(held => held, _ => null) is not { } reads)
            {
                return null;
            }

            return new SyncPreviewAiming(
                target.Generation,
                SyncRegisters.Scenes,
                (asked, batchCt) =>
                    reads.ReduceHeldScenesAsync(target.BaseAddress, target.ApiKey, asked, batchCt));
        }
    }

    /// <summary>Starts one library run, or refuses it by name.</summary>
    /// <remarks>
    /// Enqueued non-exclusive, which is the one departure from every other enqueue in this
    /// extension. A library-wide run takes as long as the library is large, and enqueued exclusive it
    /// would hold the reader's own Cove scans and refreshes behind it for that whole time. A
    /// non-exclusive run still appears in the host's job list and is still cancellable, so nothing a
    /// reader can see or do about it is given up.
    /// <para>
    /// A second run is refused while the first is pending or running, and the refusal carries the
    /// running job's own id so the page can point at it. Whether one is in flight is the host's own
    /// job list rather than a stored flag: it answers false the moment the run ends, and it is empty
    /// after a process restart, which is the correct answer.
    /// </para>
    /// </remarks>
    internal async Task<Results<Ok<SyncEnqueued>, ForbiddenCode>> EnqueueSyncRunAsync(
        SyncRunRequest? request,
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

        var refused = await SyncRefusalFor(options, credentials, client, ct).ConfigureAwait(false);
        if (refused is not SyncRefusalKind.None)
        {
            return TypedResults.Ok(new SyncEnqueued(null, refused));
        }

        if (SyncRunInFlight(jobs) is { } running)
        {
            return TypedResults.Ok(new SyncEnqueued(running.Id, SyncRefusalKind.AlreadyRunning));
        }

        var parameters = SyncLibraryJob.Encode(request?.AlsoMonitor ?? false);

        var started = jobs.Enqueue(
            OwnJobTypePrefix + SyncLibraryJob.JobId,
            $"[{Name}] Offer every identified scene to Whisparr",
            (progress, runCt) => RunSyncLibraryAsync(parameters, scopes, progress, runCt),
            exclusive: false);

        return TypedResults.Ok(new SyncEnqueued(started, SyncRefusalKind.None));
    }

    /// <summary>Runs one enqueued library run.</summary>
    /// <remarks>
    /// Everything the run acts through is resolved when it starts, because the profile, the root and
    /// which instance is connected are each the reader's to change while a run is queued.
    /// <para>
    /// A cancellation is rethrown after the run has written its own summary, so the host classifies
    /// the run as cancelled rather than completed while the reader is still told what it offered.
    /// </para>
    /// </remarks>
    private async Task RunSyncLibraryAsync(
        IReadOnlyDictionary<string, string> parameters,
        IServiceScopeFactory scopes,
        CoreJobProgress progress,
        CancellationToken ct)
    {
        await SyncLibraryJob.RunAsync(
            SyncLibraryJob.Decode(parameters), scopes, AimAsync, progress, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        async Task<SyncLibraryAiming?> AimAsync(
            IServiceProvider services, SyncLibraryBatch batch, CancellationToken runCt)
        {
            var target = await ResolveTargetAsync(
                    services.GetRequiredService<OptionsStore>(),
                    services.GetRequiredService<ICredentialPort>(),
                    services.GetRequiredService<IWhisparrClient>(),
                    runCt)
                .ConfigureAwait(false);

            // The one composition every registering run in this product aims through, so the add
            // this run offers is the non-grabbing one and the values it composes with are read here
            // rather than at enqueue.
            if (target is null
                || await ComposeSceneAddAsync(services, runCt).ConfigureAwait(false)
                    is not { } register)
            {
                return null;
            }

            // The newer generation offers scenes and nothing else: it creates a scene's studio and
            // its performers itself as presence, so there is no studio pass and no performer pass.
            // Nor is there a catalogue refresh after the loop - that is a per-entity act, and there
            // is no single entity here.
            return new SyncLibraryAiming(target.Generation, register, MonitorFor(batch, target));
        }
    }

    /// <summary>How one offered scene is marked wanted, or null where nothing marks one.</summary>
    /// <remarks>
    /// Null unless the reader asked and the generation registers a per-scene monitor, so the older
    /// generation obtains none and monitors nothing rather than being refused once it is called.
    /// <para>
    /// One request at a time throughout. The instance's own command queue is the shared resource, so
    /// there is no parallel loop here and no second request in flight.
    /// </para>
    /// </remarks>
    private Func<string, WhisparrResponse?, CancellationToken, Task<WhisparrResponse?>>? MonitorFor(
        SyncLibraryBatch batch, MonitoringTarget target)
    {
        if (!batch.AlsoMonitor
            || target.Capabilities.Obtain<IWhisparrSceneMonitorActing>()
                .Match<IWhisparrSceneMonitorActing?>(held => held, _ => null) is not { } monitoring)
        {
            return null;
        }

        var reading = target.Capabilities.Obtain<IWhisparrSceneStatusReading>()
            .Match<IWhisparrSceneStatusReading?>(held => held, _ => null);

        return (identity, offered, ct) =>
            MonitorOfferedSceneAsync(target, monitoring, reading, identity, offered, ct);
    }

    /// <summary>Marks one scene the instance now holds wanted.</summary>
    /// <remarks>
    /// The flag is set by the instance's own numeric scene id, which is not an identifier this
    /// product holds. It is taken off the accepted add's own answer where the add was accepted, and
    /// otherwise off one read of the scene the instance already held - one extra request for a scene
    /// that was already there, and none for a scene that was just registered.
    /// </remarks>
    private async Task<WhisparrResponse?> MonitorOfferedSceneAsync(
        MonitoringTarget target,
        IWhisparrSceneMonitorActing monitoring,
        IWhisparrSceneStatusReading? reading,
        string identity,
        WhisparrResponse? offered,
        CancellationToken ct)
    {
        var sceneId = MonitoringProjector.EntityIdIn(offered?.Body);

        if (sceneId is null && reading is not null)
        {
            var held = await ContainedAsync(
                () => reading.ReadSceneByRemoteIdAsync(target.BaseAddress, target.ApiKey, identity, ct),
                target,
                _log,
                ct).ConfigureAwait(false);
            sceneId = held is null ? null : SceneStatusPort.ReadRow(held).InstanceId;
        }

        // No id is no flag to set. Answering nothing counts it as not monitored, which is the
        // reading that claims less rather than more.
        return sceneId is { } named
            ? await ContainedAsync(
                () => monitoring.SetSceneMonitoredAsync(
                    target.BaseAddress, target.ApiKey, named, monitored: true, ct),
                target,
                _log,
                ct).ConfigureAwait(false)
            : null;
    }

    /// <summary>Whether one of this extension's own sync runs is pending or running.</summary>
    private bool SyncRunIsInFlight(IJobService jobs) => SyncRunInFlight(jobs) is not null;

    /// <summary>The one of this extension's own sync runs that is pending or running, or none.</summary>
    /// <remarks>
    /// One derivation for both readers: the count route answers whether a run is in flight, and the
    /// run route names the job it refuses a second run for, so neither can disagree with the other
    /// about what is running.
    /// </remarks>
    private JobInfo? SyncRunInFlight(IJobService jobs)
        => jobs.GetAllJobs().FirstOrDefault(job =>
            string.Equals(
                job.Type, OwnJobTypePrefix + SyncLibraryJobId, StringComparison.Ordinal)
            && job.Status is JobStatus.Pending or JobStatus.Running);

    /// <summary>Why the sync surface cannot act at all, or that it can.</summary>
    /// <remarks>
    /// Both routes refuse for the same two reasons, and a reader is shown one sentence for each, so
    /// the two are derived once rather than in each handler.
    /// </remarks>
    private static async Task<SyncRefusalKind> SyncRefusalFor(
        OptionsStore options,
        ICredentialPort credentials,
        IWhisparrClient client,
        CancellationToken ct)
    {
        if (await ResolveTargetAsync(options, credentials, client, ct).ConfigureAwait(false)
            is not { } target)
        {
            return SyncRefusalKind.NoInstanceConnected;
        }

        // A generation keeping no per-scene records registers no scene-status read, so there is
        // nothing to ask which scenes it holds. What it can be told about instead is the site pass.
        return target.Capabilities.Obtain<IWhisparrSceneStatusReading>()
                .Match<IWhisparrSceneStatusReading?>(held => held, _ => null) is null
            ? SyncRefusalKind.WhisparrKeepsNoSceneRecords
            : SyncRefusalKind.None;
    }
}
