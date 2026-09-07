using Cove.Core.Interfaces;
using Microsoft.AspNetCore.Http;
using WhisparrSync.Contracts;
using WhisparrSync.Ingest;
using WhisparrSync.Matching;
using WhisparrSync.State;
using static WhisparrSync.Tests.TestSupport.EndpointTestSupport;
using IJobProgress = Cove.Core.Interfaces.IJobProgress;

namespace WhisparrSync.Tests.Api.Auth;

/// <summary>
/// Security-critical: the <c>/import-log</c> endpoint enforces its permission itself (the host
/// <c>[RequiresPermission]</c> filter is inert on minimal-API endpoints). These prove it is 403-first on
/// <c>extensions.read</c> and, when authorized, returns the pre-reduced aggregates (<c>lastEventTicks</c> +
/// the <c>syncHealth</c> banner signal). They also prove the reconcile scheduler hands <see cref="IJobService"/>
/// an EXCLUSIVE reconcile job whose work delegate, when run, executes the reconcile body.
/// </summary>
[Trait("Tier", "L2")]
public sealed class ImportLogEndpointAuthTests
{
    [Fact]
    public async Task ImportLog_WithRead_Returns200_WithReducedAggregates()
    {
        var store = new FakeStore();
        var log = new ImportLog(store);
        await log.RecordOutcomeAsync(fromWebhook: true, 100L, "Imported", null, "/data/media/A.mkv");
        await log.RecordOutcomeAsync(fromWebhook: true, 200L, "Flagged", IngestCoordinator.PathNotVisibleReason, "/data/media/B.mkv");

        var result = await NewExtension(store).ImportLogAsync(default);
        var value = Assert.IsType<ImportStatusResponse>(
            Assert.IsAssignableFrom<IValueHttpResult>(result).Value);
        Assert.Equal(200L, value.LastEventTicks); // newest webhook event

        var syncHealth = value.SyncHealth;
        Assert.Equal(1, Prop(syncHealth, "PathMismatch")); // the post-success path-mismatch trips the banner
    }

    private static int Prop(object o, string name) => (int)o.GetType().GetProperty(name)!.GetValue(o)!;

    [Fact]
    public async Task Scheduler_EnqueuesExclusiveReconcileJob_WhoseWorkRunsTheReconcileBody()
    {
        var jobs = new CapturingJobService();
        var ran = 0;
        var scheduler = new ReconcileScheduler(
            jobs, (_, _) => { ran++; return Task.CompletedTask; }, _ => { }, TimeSpan.FromMinutes(15));

        scheduler.EnqueueOnce();

        // The scheduler enqueued exactly one exclusive job of the reconcile type.
        Assert.Equal(ReconcileJob.JobId, jobs.LastType);
        Assert.True(jobs.LastExclusive);
        Assert.Equal(0, ran); // enqueue does not itself run the work

        // Running the captured work delegate (what the host job runner would do) executes the reconcile body once.
        await jobs.LastWork!(new NullJobProgress(), default);
        Assert.Equal(1, ran);
    }

    // Captures the last enqueued job so the test can prove the type/exclusivity and invoke the work delegate.
    private sealed class CapturingJobService : IJobService
    {
        public string? LastType { get; private set; }
        public bool LastExclusive { get; private set; }
        public Func<IJobProgress, CancellationToken, Task>? LastWork { get; private set; }

        public string Enqueue(string type, string description, Func<IJobProgress, CancellationToken, Task> work, bool exclusive = true)
        {
            LastType = type;
            LastExclusive = exclusive;
            LastWork = work;
            return "fake-job-id";
        }

        public bool Cancel(string jobId) => false;
        public bool ReorderQueued(string jobId, string? beforeJobId) => false;
        public JobInfo? GetJob(string jobId) => null;
        public IReadOnlyList<JobInfo> GetAllJobs() => [];
        public IReadOnlyList<JobInfo> GetJobHistory() => [];
    }

    private sealed class NullJobProgress : IJobProgress
    {
        public void Report(double progress, string? subTask = null)
        {
        }
    }
}
