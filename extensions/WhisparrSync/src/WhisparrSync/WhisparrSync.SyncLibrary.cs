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
using WhisparrSync.Monitoring;
using WhisparrSync.Options;
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
    /// Everything the count compares against is resolved when it STARTS, because which instance is
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

    /// <summary>Whether one of this extension's own sync runs is pending or running.</summary>
    private bool SyncRunIsInFlight(IJobService jobs)
        => jobs.GetAllJobs().Any(job =>
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
