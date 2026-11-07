using System.Text.Json;
using Cove.Core.Auth;
using Cove.Core.Interfaces;
using Cove.Extensions.Shared;
using Cove.Plugins;
using Cove.Sdk;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using ScanAndIdentify.Contracts;
using ScanAndIdentify.Options;
using ScanAndIdentify.Run;
using static Cove.Extensions.Shared.MinimalApiPermissions;

namespace ScanAndIdentify;

public sealed partial class ScanAndIdentify
{
    // The route prefix mirrors how the host mounts an extension's IApiExtension endpoints:
    // /api/extensions/{id}/…
    //
    // Instance members because Id comes from extension.json: reading a route before the host has
    // applied the manifest throws instead of mounting the endpoints under the wrong id.
    private string RouteBase => "/api/extensions/" + Id;
    private string CapabilitiesRoute => RouteBase + "/capabilities";
    private string OptionsRoute => RouteBase + "/options";
    private string RunRoute => RouteBase + "/run";
    private string RunStagesRoute => RouteBase + "/run/stages";
    private string RunEndRoute => RouteBase + "/run/end";

    private RunOptionsStore StoredOptions => new(Store, _log);

    private RunRecordStore StoredRun => new(Store, _log);

    // One array, read by every run route's declaration and by every run handler, so an endpoint
    // cannot advertise one gate to the host while enforcing another.
    //
    // The first three are what the chain's own host calls demand, so pressing Run is refused once
    // before any stage starts rather than part-way through. extensions.configure is here because the
    // run view hands back the options the run was claimed with, which is the document the options
    // routes already gate on it; answering the same bytes under a weaker gate would be a way around
    // that gate. jobs.cancel is deliberately absent: a caller who may run a chain but not cancel one
    // is a real account, and demanding it here would refuse them the run itself.
    private static readonly string[] RunPermissions =
    [
        Permissions.LibraryScan,
        Permissions.JobsRun,
        Permissions.LibraryIdentify,
        Permissions.ExtensionsConfigure,
    ];

    // How long a stage may sit claimed with no job id before it is given up on. It bounds one window,
    // between a caller issuing the host POST and the job id coming back, and nothing else can close
    // it: a page that died inside that window would otherwise leave the run in flight for good, and
    // the second-run guard would then refuse every later run.
    private static readonly TimeSpan StageStartBudget = TimeSpan.FromMinutes(5);

    // The componentName has to equal the key in the bundle's defineExtension components map, or the
    // host mounts nothing and reports nothing.
    public override UIManifest GetUIManifest()
        => ManifestBuilder()
            .AddSettingsTab(
                key: "scan-and-identify",
                label: "Scan and Identify",
                layout: SettingsTabLayout.Panels,
                order: 100)
            .AddSettingsSection(
                targetTab: "scan-and-identify",
                label: "Scan and Identify",
                componentName: "ScanAndIdentifyPage")
            .WithJsBundle("index.mjs")
            .Build();

    // The response carries the host version and two resolution flags, no library data and no
    // configuration, so it needs no permission. An endpoint declaring none of the SDK's authorization
    // conventions is treated as anonymous and only warned about at host boot.
    public override void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet(CapabilitiesRoute, () => Capabilities())
            .AllowWithoutCovePermission();

        // Gated on extensions.configure, not on a media permission: this one document decides what a
        // later run does to the whole library, and it is the permission Cove's own extension-data
        // routes carry over the same bytes.
        endpoints.MapGet(OptionsRoute,
            (ICurrentPrincipalAccessor principal, CancellationToken ct) => GetOptionsAsync(principal, ct))
            .RequireCovePermission(Permissions.ExtensionsConfigure);

        // Binds the raw HttpContext so a malformed body is this route's own 400 rather than a
        // framework 400 with no schema. .Accepts<> is what puts the request shape in the document.
        endpoints.MapPut(OptionsRoute,
            (HttpContext http, ICurrentPrincipalAccessor principal, CancellationToken ct)
                => SaveOptionsAsync(http.Request, principal, ct))
            .Accepts<RunOptions>("application/json")
            .RequireCovePermission(Permissions.ExtensionsConfigure);

        // Nothing in the host orders the four stages. Identify is enqueued non-exclusive, so it runs
        // the moment it is asked for; only scan and generate share Cove's exclusive queue. The order
        // comes entirely from a caller waiting for each stage to reach a terminal state before it
        // starts the next, which these four routes record and report but cannot impose.
        endpoints.MapPost(RunRoute,
            (ICurrentPrincipalAccessor principal, IJobService jobs, CancellationToken ct)
                => ClaimRunAsync(principal, jobs, ct))
            .RequireCovePermission(PermissionMode.All, RunPermissions);

        endpoints.MapGet(RunRoute,
            (ICurrentPrincipalAccessor principal, IJobService jobs, CancellationToken ct)
                => ReadRunAsync(principal, jobs, ct))
            .RequireCovePermission(PermissionMode.All, RunPermissions);

        endpoints.MapPost(RunStagesRoute,
            (RunStageStarted? body, ICurrentPrincipalAccessor principal, IJobService jobs,
                CancellationToken ct)
                => RecordStageAsync(body, principal, jobs, ct))
            .Accepts<RunStageStarted>("application/json")
            .RequireCovePermission(PermissionMode.All, RunPermissions);

        endpoints.MapPost(RunEndRoute,
            (RunEnded? body, ICurrentPrincipalAccessor principal, IJobService jobs,
                CancellationToken ct)
                => EndRunAsync(body, principal, jobs, ct))
            .Accepts<RunEnded>("application/json")
            .RequireCovePermission(PermissionMode.All, RunPermissions);
    }

    internal CapabilitiesView Capabilities()
        => new(_coveVersion, _jobServiceAvailable, _scanServiceAvailable);

    // The saved description of a run, read through the same store a run will read it through, so the
    // panel and a run can never disagree about what a stored blob means.
    internal async Task<Results<Ok<RunOptions>, ForbiddenCode>> GetOptionsAsync(
        ICurrentPrincipalAccessor principal, CancellationToken ct = default)
    {
        if (Forbidden(principal, Permissions.ExtensionsConfigure) is { } denied)
        {
            return denied;
        }

        return TypedResults.Ok(await StoredOptions.LoadAsync(ct));
    }

    // Persists the options in the spelling the store owns rather than the wire's.
    //
    // The stored document is replaced, not merged: what goes back is the members RunOptions declares.
    // A property only a newer version knows about is readable here, because a load ignores what it
    // does not recognize, but it does not survive this write.
    internal async Task<Results<NoContent, BadRequest<ErrorCode>, ForbiddenCode>> SaveOptionsAsync(
        HttpRequest request, ICurrentPrincipalAccessor principal, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (Forbidden(principal, Permissions.ExtensionsConfigure) is { } denied)
        {
            return denied;
        }

        RunOptions? options;
        try
        {
            options = await JsonSerializer.DeserializeAsync<RunOptions>(
                request.Body, RunOptions.JsonOptions, ct);
        }
        catch (JsonException)
        {
            return TypedResults.BadRequest(new ErrorCode("INVALID_OPTIONS"));
        }

        if (options is null)
        {
            return TypedResults.BadRequest(new ErrorCode("INVALID_OPTIONS"));
        }

        // Repaired before the write, so the stored blob holds what every later load would answer.
        var optionsStore = StoredOptions;
        await optionsStore.SaveAsync(optionsStore.Repair(options), ct);
        return TypedResults.NoContent();
    }

    // Starts a run and writes down what it will be doing. Taken under the lock together with the
    // check that no run is already in flight, because the store has no compare-and-set: two callers
    // reading "no run" before either writes would both start one.
    //
    // A stored run is advanced first, so a run whose stages have since settled - or whose job the
    // host no longer knows about - releases the guard rather than refusing every later run.
    internal async Task<Results<Ok<RunView>, Conflict<ErrorCode>, ForbiddenCode>> ClaimRunAsync(
        ICurrentPrincipalAccessor principal, IJobService jobs, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(jobs);

        if (ForbiddenForRun(principal) is { } denied)
        {
            return denied;
        }

        await _runLock.WaitAsync(ct);
        try
        {
            var store = StoredRun;
            var now = DateTime.UtcNow;
            if (await store.LoadAsync(ct) is { } existing)
            {
                var (advanced, changed) = Advance(existing, jobs, now);
                if (changed)
                {
                    await store.SaveAsync(advanced, ct);
                }

                if (advanced.EndedAt is null)
                {
                    return TypedResults.Conflict(new ErrorCode("RUN_IN_FLIGHT"));
                }
            }

            // Read once, here, and carried on the record: a switch flipped during the run must not
            // change what a later stage does, and a page that reopened mid-run has no other source.
            var options = await StoredOptions.LoadAsync(ct);
            var claimed = new RunRecord
            {
                RunId = Guid.NewGuid().ToString("N"),
                StartedAt = now,
                Options = options,
                Stages = [.. RunRecord.StageOrder.Select(stage => new RunStageEntry
                {
                    Stage = stage,
                    State = ClaimedStateFor(stage, options),
                    StartedAt = stage == RunStage.Scan ? now : null,
                })],
            };

            await store.SaveAsync(claimed, ct);
            return TypedResults.Ok(ViewOf(claimed, jobs));
        }
        finally
        {
            _runLock.Release();
        }
    }

    // The run as it stands, with every unsettled stage re-read from its host job and any newly
    // terminal state written down. A read that learned nothing writes nothing.
    internal async Task<Results<Ok<RunView>, NoContent, ForbiddenCode>> ReadRunAsync(
        ICurrentPrincipalAccessor principal, IJobService jobs, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(jobs);

        if (ForbiddenForRun(principal) is { } denied)
        {
            return denied;
        }

        await _runLock.WaitAsync(ct);
        try
        {
            var store = StoredRun;
            if (await store.LoadAsync(ct) is not { } record)
            {
                return TypedResults.NoContent();
            }

            var (advanced, changed) = Advance(record, jobs, DateTime.UtcNow);
            if (changed)
            {
                await store.SaveAsync(advanced, ct);
            }

            return TypedResults.Ok(ViewOf(advanced, jobs));
        }
        finally
        {
            _runLock.Release();
        }
    }

    // A caller reporting that it is starting a stage, or that the stage's job now exists.
    internal async Task<Results<Ok<RunView>, BadRequest<ErrorCode>, Conflict<ErrorCode>, NotFound, ForbiddenCode>>
        RecordStageAsync(
            RunStageStarted? body,
            ICurrentPrincipalAccessor principal,
            IJobService jobs,
            CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(jobs);

        if (ForbiddenForRun(principal) is { } denied)
        {
            return denied;
        }

        if (body is null)
        {
            return TypedResults.BadRequest(new ErrorCode("INVALID_STAGE"));
        }

        await _runLock.WaitAsync(ct);
        try
        {
            var store = StoredRun;
            if (await store.LoadAsync(ct) is not { } record)
            {
                return TypedResults.NotFound();
            }

            if (!string.Equals(record.RunId, body.RunId, StringComparison.Ordinal))
            {
                return TypedResults.Conflict(new ErrorCode("STALE_RUN"));
            }

            int index = record.Stages.FindIndex(entry => entry.Stage == body.Stage);
            if (index < 0 || record.Stages
                .Take(index)
                .Any(earlier => earlier.State is not (RunStageState.Succeeded or RunStageState.Skipped)))
            {
                return TypedResults.BadRequest(new ErrorCode("INVALID_STAGE"));
            }

            var stage = record.Stages[index];
            if (!MayTransition(stage, body.JobId))
            {
                return TypedResults.Conflict(new ErrorCode("STAGE_TAKEN"));
            }

            var now = DateTime.UtcNow;
            var written = body.JobId is null
                ? stage with { State = RunStageState.Starting, StartedAt = stage.StartedAt ?? now }
                : stage with { State = RunStageState.Running, JobId = body.JobId };

            var stages = new List<RunStageEntry>(record.Stages) { [index] = written };
            var updated = record with { Stages = stages };
            await store.SaveAsync(updated, ct);
            return TypedResults.Ok(ViewOf(updated, jobs));
        }
        finally
        {
            _runLock.Release();
        }
    }

    // Ends a run the stage states cannot end by themselves, such as a person stopping the chain while
    // the stage in flight keeps running.
    //
    // Only the three reasons a caller can know are accepted. Completed and interrupted are read off
    // the stages, so accepting either here would let a caller assert an outcome it cannot observe.
    internal async Task<Results<Ok<RunView>, BadRequest<ErrorCode>, Conflict<ErrorCode>, NotFound, ForbiddenCode>>
        EndRunAsync(
            RunEnded? body,
            ICurrentPrincipalAccessor principal,
            IJobService jobs,
            CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(jobs);

        if (ForbiddenForRun(principal) is { } denied)
        {
            return denied;
        }

        if (body is null || body.Reason is not
            (RunEndReason.Cancelled or RunEndReason.Failed or RunEndReason.StoppedByUser))
        {
            return TypedResults.BadRequest(new ErrorCode("INVALID_END_REASON"));
        }

        await _runLock.WaitAsync(ct);
        try
        {
            var store = StoredRun;
            if (await store.LoadAsync(ct) is not { } record)
            {
                return TypedResults.NotFound();
            }

            if (!string.Equals(record.RunId, body.RunId, StringComparison.Ordinal))
            {
                return TypedResults.Conflict(new ErrorCode("STALE_RUN"));
            }

            if (record.EndedAt is not null)
            {
                return TypedResults.Ok(ViewOf(record, jobs));
            }

            var ended = record with { EndedAt = DateTime.UtcNow, EndedReason = body.Reason };
            await store.SaveAsync(ended, ct);
            return TypedResults.Ok(ViewOf(ended, jobs));
        }
        finally
        {
            _runLock.Release();
        }
    }

    /// <summary>Whether the stage is in the state the caller's write starts from.</summary>
    /// <remarks>
    /// The stage write is a transition from a named current state rather than an assignment, which is
    /// what keeps two callers from starting the same stage twice. Every page watching an in-flight run
    /// advances it, so when a stage settles they all ask what to start next and all get the same
    /// answer; the run id matches in each and the earlier stages really have finished, so neither the
    /// stale-run check nor the ordering check fires. Without this one the losing caller writes the
    /// stage too, issues its own host POST, and a second whole-library job runs that the record does
    /// not name and nothing watches.
    /// <para>
    /// The claim writes the scan stage as <c>Starting</c>, so the first caller's no-job-id write for
    /// that stage starts from <c>Starting</c> rather than from <c>Pending</c>.
    /// </para>
    /// </remarks>
    private static bool MayTransition(RunStageEntry stage, string? jobId) => jobId is null
        ? stage.State == RunStageState.Pending
            || (stage.Stage == RunStage.Scan && stage.State == RunStageState.Starting && stage.JobId is null)
        : stage.State == RunStageState.Starting && stage.JobId is null;

    private static ForbiddenCode? ForbiddenForRun(ICurrentPrincipalAccessor principal)
        => principal.Current is { } current && Array.TrueForAll(RunPermissions, current.Has)
            ? null
            : new ForbiddenCode();

    private static RunStageState ClaimedStateFor(RunStage stage, RunOptions options) => stage switch
    {
        // The claim writes stage one as started, so the window before its job id arrives is visible
        // in the record rather than reading as a stage the run has not reached.
        RunStage.Scan => RunStageState.Starting,
        RunStage.Fingerprints when !options.BackfillFingerprints => RunStageState.Skipped,
        RunStage.Images when !options.GeneratesImages => RunStageState.Skipped,
        _ => RunStageState.Pending,
    };

    private static bool IsSettled(RunStageState state) => state
        is RunStageState.Succeeded
        or RunStageState.Failed
        or RunStageState.Cancelled
        or RunStageState.Skipped
        or RunStageState.Indeterminate;

    /// <summary>
    /// Re-reads every unsettled stage from its host job, writes a terminal state into the record the
    /// first time it is seen, and ends the run once the stages say it is over.
    /// </summary>
    /// <remarks>
    /// A settled stage is answered from the record and never re-read: the host forgets a finished job
    /// once a hundred others have finished, so a stage read later would otherwise report as unknown.
    /// Stage states keep resolving after the run has ended, so a cancel the host takes a batch to
    /// honor still settles here.
    /// </remarks>
    private static (RunRecord Record, bool Changed) Advance(RunRecord record, IJobService jobs, DateTime now)
    {
        bool changed = false;
        var stages = new List<RunStageEntry>(record.Stages.Count);
        foreach (var stage in record.Stages)
        {
            if (IsSettled(stage.State))
            {
                stages.Add(stage);
                continue;
            }

            // Read once, so the state and the detail describe the same observation of the same job.
            var job = stage.JobId is { } jobId ? jobs.GetJob(jobId) : null;
            var derived = DeriveState(stage, job, now);
            if (!IsSettled(derived))
            {
                // Reported, not stored. A stage still moving is re-derived on the next read anyway,
                // and storing it would write the store on every poll for as long as the run lasts.
                stages.Add(derived == stage.State ? stage : stage with { State = derived });
                continue;
            }

            // The detail the host reported at the moment the stage settled, kept because the host
            // forgets a finished job once a hundred others have finished. A stage that settled only
            // because its job stopped resolving keeps whatever it had, which is nothing when no read
            // ever saw the job.
            changed = true;
            stages.Add(stage with
            {
                State = derived,
                EndedAt = now,
                Detail = job is null ? stage.Detail : RunStageDetail.From(job),
            });
        }

        var advanced = record with { Stages = stages };
        if (advanced.EndedAt is null && EndReasonFor(stages) is { } reason)
        {
            advanced = advanced with { EndedAt = now, EndedReason = reason };
            changed = true;
        }

        return (advanced, changed);
    }

    // A null answer from GetJob is authoritative rather than a failed read: the call is in-process.
    // It covers a finished job evicted from the host's history, a job lost to a host restart and an
    // id that was never real, which the record cannot tell apart.
    private static RunStageState DeriveState(RunStageEntry stage, JobInfo? job, DateTime now)
    {
        if (stage.JobId is null)
        {
            return stage.State == RunStageState.Starting
                && stage.StartedAt is { } startedAt
                && now - startedAt > StageStartBudget
                    ? RunStageState.Indeterminate
                    : stage.State;
        }

        return job is null ? RunStageState.Indeterminate : RunStageView.StateFor(job.Status);
    }

    // The stages after a failed, cancelled or unknown one stay pending, which is what the run ending
    // on the first of them means.
    private static RunEndReason? EndReasonFor(IReadOnlyList<RunStageEntry> stages)
    {
        if (stages.Any(stage => stage.State == RunStageState.Failed))
        {
            return RunEndReason.Failed;
        }

        if (stages.Any(stage => stage.State == RunStageState.Cancelled))
        {
            return RunEndReason.Cancelled;
        }

        if (stages.Any(stage => stage.State == RunStageState.Indeterminate))
        {
            return RunEndReason.Interrupted;
        }

        return stages.All(stage => stage.State is RunStageState.Succeeded or RunStageState.Skipped)
            ? RunEndReason.Completed
            : null;
    }

    // One projection for all four routes, so they cannot answer different views of the same record.
    //
    // A settled stage's detail is answered from the record and never re-read, by the same rule that
    // already governs its state: the host forgets a finished job once a hundred others have finished,
    // so a finished run would otherwise report blank rows.
    private static RunView ViewOf(RunRecord record, IJobService jobs) => new(
        record.RunId,
        record.StartedAt,
        record.EndedAt,
        record.EndedReason,
        record.Options,
        [.. record.Stages.Select(stage => new RunStageView(
            stage.Stage, stage.State, stage.JobId, DetailOf(stage, jobs)))]);

    private static RunStageDetail? DetailOf(RunStageEntry stage, IJobService jobs)
    {
        if (IsSettled(stage.State))
        {
            return stage.Detail;
        }

        return stage.JobId is { } jobId && jobs.GetJob(jobId) is { } job
            ? RunStageDetail.From(job)
            : null;
    }
}
