using Cove.Core.Interfaces;
using WhisparrSync.Matching;
using IJobProgress = Cove.Core.Interfaces.IJobProgress;

namespace WhisparrSync.Tests.Matching;

[Trait("Tier", "L1")]
public sealed class ReconcileSchedulerTests
{
    private static readonly TimeSpan FastTick = TimeSpan.FromMilliseconds(5);

    [Fact]
    public async Task FaultingTick_ReportsExactlyOneReason_AndTheFollowingTickStillEnqueues()
    {
        var reasons = new List<string>();
        using var cts = new CancellationTokenSource();
        var jobs = new ScriptedJobService(call =>
        {
            if (call == 1)
            {
                throw new InvalidOperationException("job service unavailable");
            }

            cts.Cancel();
        });
        var scheduler = new ReconcileScheduler(jobs, (_, _) => Task.CompletedTask, reasons.Add, FastTick);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scheduler.RunLoopAsync(cts.Token));

        // Single pins the count at exactly one: it fails at zero reported reasons and fails again at two.
        Assert.Single(reasons);
        Assert.Equal(2, jobs.Calls);
    }

    [Fact]
    public async Task FaultReason_CarriesTheExceptionMessage_AndNoPathSeparator()
    {
        var reasons = new List<string>();
        using var cts = new CancellationTokenSource();
        var jobs = new ScriptedJobService(call =>
        {
            if (call == 1)
            {
                throw new InvalidOperationException("job service unavailable");
            }

            cts.Cancel();
        });
        var scheduler = new ReconcileScheduler(jobs, (_, _) => Task.CompletedTask, reasons.Add, FastTick);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scheduler.RunLoopAsync(cts.Token));

        string reason = Assert.Single(reasons);
        Assert.Equal("job service unavailable", reason);
        Assert.DoesNotContain('/', reason);
        Assert.DoesNotContain('\\', reason);
    }

    [Fact]
    public async Task SucceedingTicks_NeverReportAFault()
    {
        var reasons = new List<string>();
        using var cts = new CancellationTokenSource();
        var jobs = new ScriptedJobService(call =>
        {
            if (call == 2)
            {
                cts.Cancel();
            }
        });
        var scheduler = new ReconcileScheduler(jobs, (_, _) => Task.CompletedTask, reasons.Add, FastTick);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scheduler.RunLoopAsync(cts.Token));

        Assert.Empty(reasons);
        Assert.Equal(2, jobs.Calls);
    }

    [Fact]
    public async Task ACancelledToken_EndsTheLoop_WithoutReachingTheEnqueueOrReportingAFault()
    {
        var reasons = new List<string>();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var jobs = new ScriptedJobService(_ => { });
        var scheduler = new ReconcileScheduler(jobs, (_, _) => Task.CompletedTask, reasons.Add, FastTick);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scheduler.RunLoopAsync(cts.Token));

        Assert.Empty(reasons);
        Assert.Equal(0, jobs.Calls);
    }

    // The catch filter's contract: a cancellation raised by the enqueue itself is shutdown, not a fault, so it
    // propagates unreported. Without the filter this case would log a warning on every clean shutdown.
    [Fact]
    public async Task AnEnqueueCancellation_PropagatesUnreported()
    {
        var reasons = new List<string>();
        using var cts = new CancellationTokenSource();
        var jobs = new ScriptedJobService(_ => throw new OperationCanceledException());
        var scheduler = new ReconcileScheduler(jobs, (_, _) => Task.CompletedTask, reasons.Add, FastTick);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scheduler.RunLoopAsync(cts.Token));

        Assert.Empty(reasons);
        Assert.Equal(1, jobs.Calls);
    }

    // Counts enqueues and runs a caller-supplied script against the 1-based call number, so a test can make one
    // chosen tick throw and end the loop on the next without a wall-clock wait.
    private sealed class ScriptedJobService(Action<int> onEnqueue) : IJobService
    {
        public int Calls { get; private set; }

        public string Enqueue(string type, string description, Func<IJobProgress, CancellationToken, Task> work, bool exclusive = true)
        {
            Calls++;
            onEnqueue(Calls);
            return "fake-job-id";
        }

        public bool Cancel(string jobId) => false;
        public bool ReorderQueued(string jobId, string? beforeJobId) => false;
        public JobInfo? GetJob(string jobId) => null;
        public IReadOnlyList<JobInfo> GetAllJobs() => [];
        public IReadOnlyList<JobInfo> GetJobHistory() => [];
    }
}
