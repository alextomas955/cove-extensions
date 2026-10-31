using System.Text.Json;
using Cove.Core.Auth;
using Cove.Core.Interfaces;
using Cove.Extensions.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using ScanAndIdentify.Contracts;
using ScanAndIdentify.Options;
using ScanAndIdentify.Run;
using ScanAndIdentify.Tests.TestSupport;

namespace ScanAndIdentify.Tests.Api;

public sealed class RunEndpointTests
{
    [Fact]
    public async Task Claim_StartsTheScanStage_AndLeavesTheOthersWaiting()
    {
        var (_, _, run) = await ClaimedAsync(new RecordingJobService());

        Assert.NotEmpty(run.RunId);
        Assert.Null(run.EndedAt);
        Assert.Equal(
            [RunStage.Scan, RunStage.Fingerprints, RunStage.Identify, RunStage.Images],
            run.Stages.Select(stage => stage.Stage));
        Assert.Equal(
            [
                RunStageState.Starting,
                RunStageState.Pending,
                RunStageState.Pending,
                RunStageState.Pending,
            ],
            run.Stages.Select(stage => stage.State));
        Assert.All(run.Stages, stage => Assert.Null(stage.JobId));
    }

    [Theory]
    [InlineData(true, RunStageState.Pending)]
    [InlineData(false, RunStageState.Skipped)]
    public async Task Claim_WritesTheFingerprintStageFromTheSavedBackfillSwitch(
        bool backfill, RunStageState expected)
    {
        var (_, _, run) = await ClaimedAsync(
            new RecordingJobService(), new RunOptions { BackfillFingerprints = backfill });

        Assert.Equal(expected, StageOf(run, RunStage.Fingerprints).State);
        Assert.Equal(backfill, run.Options.BackfillFingerprints);
    }

    [Fact]
    public async Task Claim_Refuses_WhileTheFirstRunHasNotEnded()
    {
        var (extension, store, _) = await ClaimedAsync(new RecordingJobService());
        int writes = store.SetCallCount;

        var second = await extension.ClaimRunAsync(Runner, new RecordingJobService());

        var conflict = Assert.IsType<Conflict<ErrorCode>>(HttpResultUnwrap.Unwrap(second));
        Assert.Equal("RUN_IN_FLIGHT", conflict.Value!.Code);
        Assert.Equal(writes, store.SetCallCount);
    }

    [Fact]
    public async Task RecordStage_Refuses_ForARunIdThatIsNotTheCurrentRun()
    {
        var (extension, store, _) = await ClaimedAsync(new RecordingJobService());
        int writes = store.SetCallCount;

        var result = await extension.RecordStageAsync(
            new RunStageStarted("a-run-that-ended", RunStage.Scan, null), Runner, NoJobs);

        var conflict = Assert.IsType<Conflict<ErrorCode>>(HttpResultUnwrap.Unwrap(result));
        Assert.Equal("STALE_RUN", conflict.Value!.Code);
        Assert.Equal(writes, store.SetCallCount);
    }

    [Fact]
    public async Task RecordStage_Refuses_WhenAnEarlierStageHasNotFinished()
    {
        var (extension, store, run) = await ClaimedAsync(new RecordingJobService());
        int writes = store.SetCallCount;

        var result = await extension.RecordStageAsync(
            new RunStageStarted(run.RunId, RunStage.Identify, null), Runner, NoJobs);

        var bad = Assert.IsType<BadRequest<ErrorCode>>(HttpResultUnwrap.Unwrap(result));
        Assert.Equal("INVALID_STAGE", bad.Value!.Code);
        Assert.Equal(writes, store.SetCallCount);
    }

    [Fact]
    public async Task RecordStage_RefusesASecondCallerStartingTheSameStage_AndKeepsTheFirstJobId()
    {
        var (extension, store, run) = await ClaimedAsync(
            new RecordingJobService(), new RunOptions { BackfillFingerprints = false });
        await SucceedScanAsync(extension, run);

        var started = await extension.RecordStageAsync(
            new RunStageStarted(run.RunId, RunStage.Identify, null), Runner, NoJobs);
        int writes = store.SetCallCount;
        var lostTheStart = await extension.RecordStageAsync(
            new RunStageStarted(run.RunId, RunStage.Identify, null), Runner, NoJobs);
        int afterTheRefusedStart = store.SetCallCount;

        var running = await extension.RecordStageAsync(
            new RunStageStarted(run.RunId, RunStage.Identify, "identify-job"), Runner, NoJobs);
        int reported = store.SetCallCount;
        var lostTheReport = await extension.RecordStageAsync(
            new RunStageStarted(run.RunId, RunStage.Identify, "a-second-identify-job"), Runner, NoJobs);

        Assert.Equal(
            RunStageState.Starting,
            StageOf(Answered(started), RunStage.Identify).State);
        Assert.Equal("STAGE_TAKEN", Refused(lostTheStart).Code);
        Assert.Equal(writes, afterTheRefusedStart);

        Assert.Equal(RunStageState.Running, StageOf(Answered(running), RunStage.Identify).State);
        Assert.Equal("STAGE_TAKEN", Refused(lostTheReport).Code);
        Assert.Equal(reported, store.SetCallCount);
        Assert.Equal("identify-job", StageOf(await ReadAsync(extension, Jobs("identify-job", JobStatus.Running)), RunStage.Identify).JobId);
    }

    [Theory]
    [InlineData(JobStatus.Pending, RunStageState.Queued)]
    [InlineData(JobStatus.Running, RunStageState.Running)]
    [InlineData(JobStatus.Completed, RunStageState.Succeeded)]
    [InlineData(JobStatus.Failed, RunStageState.Failed)]
    [InlineData(JobStatus.Cancelled, RunStageState.Cancelled)]
    public async Task Read_DerivesTheScanStageFromItsHostJob(JobStatus status, RunStageState expected)
    {
        var (extension, _, run) = await ClaimedAsync(new RecordingJobService());
        await extension.RecordStageAsync(new RunStageStarted(run.RunId, RunStage.Scan, "scan-job"), Runner, NoJobs);

        var read = await ReadAsync(extension, Jobs("scan-job", status));

        Assert.Equal(expected, StageOf(read, RunStage.Scan).State);
    }

    [Fact]
    public async Task Read_ReportsWhatTheRunningStagesHostJobSaysAboutItself()
    {
        var (extension, _, run) = await ClaimedAsync(new RecordingJobService());
        await extension.RecordStageAsync(
            new RunStageStarted(run.RunId, RunStage.Scan, "scan-job"), Runner, NoJobs);

        var read = await ReadAsync(extension, new RecordingJobService().With(
            "scan-job",
            JobStatus.Running,
            progress: 0.42,
            subTask: "Scanning clip.mp4",
            unitsTotal: 5000,
            unitsCompleted: 1204));

        var detail = StageOf(read, RunStage.Scan).Detail;
        Assert.NotNull(detail);
        Assert.Equal(0.42, detail.Progress);
        Assert.Equal("Scanning clip.mp4", detail.SubTask);
        Assert.Equal(5000, detail.UnitsTotal);
        Assert.Equal(1204, detail.UnitsCompleted);
    }

    [Fact]
    public async Task Read_AnswersNoDetail_ForAStageThatHasNoHostJob()
    {
        var (extension, _, run) = await ClaimedAsync(new RecordingJobService());
        await extension.RecordStageAsync(
            new RunStageStarted(run.RunId, RunStage.Scan, "scan-job"), Runner, NoJobs);

        var read = await ReadAsync(extension, Jobs("scan-job", JobStatus.Running));

        Assert.NotNull(StageOf(read, RunStage.Scan).Detail);
        Assert.Null(StageOf(read, RunStage.Identify).JobId);
        Assert.Null(StageOf(read, RunStage.Identify).Detail);
    }

    [Fact]
    public async Task Read_TruncatesAHostTextLine_AtTheLengthTheContractCaps()
    {
        var (extension, _, run) = await ClaimedAsync(new RecordingJobService());
        await extension.RecordStageAsync(
            new RunStageStarted(run.RunId, RunStage.Scan, "scan-job"), Runner, NoJobs);
        string overlong = new('x', RunStageDetail.MaxTextLength + 1);

        var read = await ReadAsync(extension, new RecordingJobService().With(
            "scan-job", JobStatus.Running, subTask: overlong));

        Assert.Equal(RunStageDetail.MaxTextLength, StageOf(read, RunStage.Scan).Detail!.SubTask!.Length);
    }

    [Fact]
    public async Task Read_AnswersASettledStageFromTheRecord_WhenTheHostHasForgottenItsJob()
    {
        var (extension, _, run) = await ClaimedAsync(new RecordingJobService());
        await extension.RecordStageAsync(new RunStageStarted(run.RunId, RunStage.Scan, "scan-job"), Runner, NoJobs);
        await ReadAsync(extension, Jobs("scan-job", JobStatus.Completed));

        var later = await ReadAsync(extension, new RecordingJobService());

        Assert.Equal(RunStageState.Succeeded, StageOf(later, RunStage.Scan).State);
    }

    [Fact]
    public async Task Read_KeepsWhatAStageReported_AfterTheHostHasForgottenItsJob()
    {
        var (extension, _, run) = await ClaimedAsync(new RecordingJobService());
        await extension.RecordStageAsync(
            new RunStageStarted(run.RunId, RunStage.Scan, "scan-job"), Runner, NoJobs);
        await ReadAsync(extension, new RecordingJobService().With(
            "scan-job",
            JobStatus.Completed,
            progress: 1,
            summary: "4,920 of 5,000 units succeeded",
            unitsTotal: 5000,
            unitsCompleted: 5000,
            unitsSucceeded: 4920,
            unitsFailed: 80));

        var later = await ReadAsync(extension, new RecordingJobService());

        var stage = StageOf(later, RunStage.Scan);
        Assert.Equal(RunStageState.Succeeded, stage.State);
        Assert.NotNull(stage.Detail);
        Assert.Equal("4,920 of 5,000 units succeeded", stage.Detail.Summary);
        Assert.Equal(5000, stage.Detail.UnitsTotal);
        Assert.Equal(4920, stage.Detail.UnitsSucceeded);
        Assert.Equal(80, stage.Detail.UnitsFailed);
    }

    [Fact]
    public async Task Read_WritesNothing_WhenAllThatMovedWasARunningStagesProgress()
    {
        var (extension, store, run) = await ClaimedAsync(new RecordingJobService());
        await extension.RecordStageAsync(
            new RunStageStarted(run.RunId, RunStage.Scan, "scan-job"), Runner, NoJobs);
        var first = await ReadAsync(extension, new RecordingJobService().With(
            "scan-job", JobStatus.Running, progress: 0.1));
        int writes = store.SetCallCount;

        var second = await ReadAsync(extension, new RecordingJobService().With(
            "scan-job", JobStatus.Running, progress: 0.5));

        Assert.Equal(0.1, StageOf(first, RunStage.Scan).Detail!.Progress);
        Assert.Equal(0.5, StageOf(second, RunStage.Scan).Detail!.Progress);
        Assert.Equal(writes, store.SetCallCount);
    }

    [Fact]
    public async Task Read_EndsTheRunAsInterrupted_WhenAStagesJobNoLongerResolves()
    {
        var (extension, _, run) = await ClaimedAsync(new RecordingJobService());
        await extension.RecordStageAsync(new RunStageStarted(run.RunId, RunStage.Scan, "scan-job"), Runner, NoJobs);

        var read = await ReadAsync(extension, new RecordingJobService());
        var next = await extension.ClaimRunAsync(Runner, new RecordingJobService());

        Assert.Equal(RunStageState.Indeterminate, StageOf(read, RunStage.Scan).State);
        Assert.Null(StageOf(read, RunStage.Scan).Detail);
        Assert.Equal(RunEndReason.Interrupted, read.EndedReason);
        Assert.NotNull(read.EndedAt);
        Assert.NotEqual(run.RunId, Assert.IsType<Ok<RunView>>(HttpResultUnwrap.Unwrap(next)).Value!.RunId);
    }

    [Fact]
    public async Task Read_GivesUpOnAStageLeftStartingWithNoJobId_PastTheStartBudget()
    {
        var store = new FakeStore();
        var extension = ScanAndIdentifyFixture.Create(store);
        await Seed(store, WithStages(
            Stage(RunStage.Scan, RunStageState.Starting, startedAt: DateTime.UtcNow.AddHours(-1)),
            Stage(RunStage.Fingerprints, RunStageState.Pending),
            Stage(RunStage.Identify, RunStageState.Pending),
            Stage(RunStage.Images, RunStageState.Pending)));

        var read = await ReadAsync(extension, new RecordingJobService());

        Assert.Equal(RunStageState.Indeterminate, StageOf(read, RunStage.Scan).State);
        Assert.Equal(RunEndReason.Interrupted, read.EndedReason);
    }

    [Fact]
    public async Task Read_EndsTheRunAsCompleted_WhenEveryStageHasSucceededOrBeenSkipped()
    {
        var store = new FakeStore();
        var extension = ScanAndIdentifyFixture.Create(store);
        await Seed(store, WithStages(
            Stage(RunStage.Scan, RunStageState.Succeeded),
            Stage(RunStage.Fingerprints, RunStageState.Skipped),
            Stage(RunStage.Identify, RunStageState.Succeeded),
            Stage(RunStage.Images, RunStageState.Running, jobId: "images-job")));

        var read = await ReadAsync(extension, Jobs("images-job", JobStatus.Completed));

        Assert.Equal(RunStageState.Succeeded, StageOf(read, RunStage.Images).State);
        Assert.Equal(RunEndReason.Completed, read.EndedReason);
        Assert.NotNull(read.EndedAt);
    }

    [Fact]
    public async Task Read_EndsTheRunAsFailed_AndLeavesTheStagesAfterTheFailureWaiting()
    {
        var store = new FakeStore();
        var extension = ScanAndIdentifyFixture.Create(store);
        await Seed(store, WithStages(
            Stage(RunStage.Scan, RunStageState.Succeeded),
            Stage(RunStage.Fingerprints, RunStageState.Running, jobId: "fingerprints-job"),
            Stage(RunStage.Identify, RunStageState.Pending),
            Stage(RunStage.Images, RunStageState.Pending)));

        var read = await ReadAsync(extension, Jobs("fingerprints-job", JobStatus.Failed));

        Assert.Equal(RunEndReason.Failed, read.EndedReason);
        Assert.Equal(RunStageState.Failed, StageOf(read, RunStage.Fingerprints).State);
        Assert.Equal(RunStageState.Pending, StageOf(read, RunStage.Identify).State);
        Assert.Equal(RunStageState.Pending, StageOf(read, RunStage.Images).State);
    }

    [Fact]
    public async Task Read_AnswersNoContent_WhenNoRunHasEverBeenClaimed()
    {
        var extension = ScanAndIdentifyFixture.Create(new FakeStore());

        var read = await extension.ReadRunAsync(Runner, new RecordingJobService());

        Assert.IsType<NoContent>(HttpResultUnwrap.Unwrap(read));
    }

    [Fact]
    public async Task EndRun_StopsTheCurrentRunWithTheReasonTheCallerNamed()
    {
        var (extension, _, run) = await ClaimedAsync(new RecordingJobService());

        var ended = await extension.EndRunAsync(new RunEnded(run.RunId, RunEndReason.Cancelled), Runner, NoJobs);

        var view = Answered(ended);
        Assert.Equal(RunEndReason.Cancelled, view.EndedReason);
        Assert.NotNull(view.EndedAt);
    }

    [Fact]
    public async Task EndRun_Refuses_ForARunIdThatIsNotTheCurrentRun()
    {
        var (extension, store, _) = await ClaimedAsync(new RecordingJobService());
        int writes = store.SetCallCount;

        var result = await extension.EndRunAsync(
            new RunEnded("a-run-that-ended", RunEndReason.Cancelled), Runner, NoJobs);

        var conflict = Assert.IsType<Conflict<ErrorCode>>(HttpResultUnwrap.Unwrap(result));
        Assert.Equal("STALE_RUN", conflict.Value!.Code);
        Assert.Equal(writes, store.SetCallCount);
    }

    [Theory]
    // The two reasons the stage states decide. A caller asserting either would be claiming an
    // outcome only the derivation can observe.
    [InlineData(RunEndReason.Completed)]
    [InlineData(RunEndReason.Interrupted)]
    public async Task EndRun_RefusesAReasonThatIsDerived(RunEndReason reason)
    {
        var (extension, store, run) = await ClaimedAsync(new RecordingJobService());
        int writes = store.SetCallCount;

        var result = await extension.EndRunAsync(new RunEnded(run.RunId, reason), Runner, NoJobs);

        var bad = Assert.IsType<BadRequest<ErrorCode>>(HttpResultUnwrap.Unwrap(result));
        Assert.Equal("INVALID_END_REASON", bad.Value!.Code);
        Assert.Equal(writes, store.SetCallCount);
    }

    [Theory]
    [InlineData(Permissions.LibraryScan)]
    [InlineData(Permissions.JobsRun)]
    [InlineData(Permissions.LibraryIdentify)]
    [InlineData(Permissions.ExtensionsConfigure)]
    public async Task EveryRunRoute_Refuses_ForAPrincipalMissingOneOfTheFourPermissions(string missing)
    {
        var (extension, store, run) = await ClaimedAsync(new RecordingJobService());
        var partial = FakePrincipalAccessor.WithPermissions(
            [.. RunPermissions.Where(held => held != missing)]);
        int writes = store.SetCallCount;

        Assert.IsType<ForbiddenCode>(HttpResultUnwrap.Unwrap(
            await extension.ClaimRunAsync(partial, new RecordingJobService())));
        Assert.IsType<ForbiddenCode>(HttpResultUnwrap.Unwrap(
            await extension.ReadRunAsync(partial, new RecordingJobService())));
        Assert.IsType<ForbiddenCode>(HttpResultUnwrap.Unwrap(
            await extension.RecordStageAsync(new RunStageStarted(run.RunId, RunStage.Scan, null), partial, NoJobs)));
        Assert.IsType<ForbiddenCode>(HttpResultUnwrap.Unwrap(
            await extension.EndRunAsync(new RunEnded(run.RunId, RunEndReason.Cancelled), partial, NoJobs)));
        Assert.Equal(writes, store.SetCallCount);
    }

    private static readonly string[] RunPermissions =
    [
        Permissions.LibraryScan,
        Permissions.JobsRun,
        Permissions.LibraryIdentify,
        Permissions.ExtensionsConfigure,
    ];

    private static FakePrincipalAccessor Runner => FakePrincipalAccessor.WithPermissions(RunPermissions);

    // The routes that record a stage or end a run project the same view as the two that read one,
    // so they take the job service too. These tests assert the record, not the projection.
    private static RecordingJobService NoJobs => new();

    private static RecordingJobService Jobs(string jobId, JobStatus status)
        => new RecordingJobService().With(jobId, status);

    private static async Task<(global::ScanAndIdentify.ScanAndIdentify Extension, FakeStore Store, RunView Run)>
        ClaimedAsync(IJobService jobs, RunOptions? options = null)
    {
        var store = new FakeStore();
        if (options is not null)
        {
            await store.SetAsync(
                RunOptionsStore.Key, JsonSerializer.Serialize(options, RunOptions.JsonOptions));
        }

        var extension = ScanAndIdentifyFixture.Create(store);
        var claimed = await extension.ClaimRunAsync(Runner, jobs);
        return (extension, store, Assert.IsType<Ok<RunView>>(HttpResultUnwrap.Unwrap(claimed)).Value!);
    }

    // Takes the scan stage through to succeeded, which is what lets a later stage be started at all.
    private static async Task SucceedScanAsync(
        global::ScanAndIdentify.ScanAndIdentify extension, RunView run)
    {
        await extension.RecordStageAsync(new RunStageStarted(run.RunId, RunStage.Scan, "scan-job"), Runner, NoJobs);
        await ReadAsync(extension, Jobs("scan-job", JobStatus.Completed));
    }

    private static async Task<RunView> ReadAsync(
        global::ScanAndIdentify.ScanAndIdentify extension, IJobService jobs)
        => Assert.IsType<Ok<RunView>>(HttpResultUnwrap.Unwrap(await extension.ReadRunAsync(Runner, jobs))).Value!;

    private static RunView Answered(IResult result)
        => Assert.IsType<Ok<RunView>>(HttpResultUnwrap.Unwrap(result)).Value!;

    private static ErrorCode Refused(IResult result)
        => Assert.IsType<Conflict<ErrorCode>>(HttpResultUnwrap.Unwrap(result)).Value!;

    private static RunStageView StageOf(RunView run, RunStage stage)
        => run.Stages.Single(entry => entry.Stage == stage);

    private static RunStageEntry Stage(
        RunStage stage, RunStageState state, string? jobId = null, DateTime? startedAt = null)
        => new() { Stage = stage, State = state, JobId = jobId, StartedAt = startedAt };

    private static RunRecord WithStages(params RunStageEntry[] stages) => new()
    {
        RunId = "7f1c",
        StartedAt = DateTime.UtcNow.AddHours(-1),
        Stages = [.. stages],
    };

    private static async Task Seed(FakeStore store, RunRecord record)
        => await store.SetAsync(
            RunRecordStore.Key, JsonSerializer.Serialize(record, RunRecord.JsonOptions));
}
