using Cove.Core.Auth;
using Cove.Core.Interfaces;
using Cove.Plugins;
using Microsoft.AspNetCore.Http;
using WhisparrSync.Reconcile;
using WhisparrSync.State;
using WhisparrSync.Tests.TestSupport;
using Ext = global::WhisparrSync.WhisparrSync;
using IJobProgress = Cove.Core.Interfaces.IJobProgress;

namespace WhisparrSync.Tests.Api;

/// <summary>
/// Security-critical: the <c>/import-log</c> endpoint enforces its permission itself (the host
/// <c>[RequiresPermission]</c> filter is inert on minimal-API endpoints). These prove it is 403-first on
/// <c>extensions.read</c> and, when authorized, returns the audit entries + imported/skipped/flagged/total
/// counts. They also prove the reconcile scheduler hands <see cref="IJobService"/> an EXCLUSIVE reconcile
/// job whose work delegate, when run, executes the reconcile body.
/// </summary>
public sealed class ImportLogEndpointAuthTests
{
    private static Ext NewExtension(FakeStore? store = null)
    {
        var ext = new Ext();
        ((IStatefulExtension)ext).SetStore(store ?? new FakeStore());
        return ext;
    }

    private static int StatusOf(IResult result)
        => Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode ?? 0;

    private static ImportLogEntry Entry(string result)
        => new(DateTime.UtcNow.Ticks, "webhook", "Download", "/data/media/A.mkv", "Video", 1, result, null, "key-" + result);

    [Fact]
    public async Task ImportLog_WithoutRead_Returns403()
    {
        var result = await NewExtension().ImportLogAsync(FakePrincipalAccessor.None(), default);
        Assert.Equal(403, StatusOf(result));
    }

    [Fact]
    public async Task ImportLog_NullPrincipal_Returns403()
    {
        var result = await NewExtension().ImportLogAsync(FakePrincipalAccessor.NullPrincipal(), default);
        Assert.Equal(403, StatusOf(result));
    }

    [Fact]
    public async Task ImportLog_WithRead_Returns200_WithEntriesAndCounts()
    {
        var store = new FakeStore();
        var log = new ImportLog(store);
        await log.AppendAsync(Entry("Imported"));
        await log.AppendAsync(Entry("Skipped"));
        await log.AppendAsync(Entry("Flagged"));
        await log.AppendAsync(Entry("Imported"));

        var result = await NewExtension(store).ImportLogAsync(
            FakePrincipalAccessor.WithPermissions(Permissions.ExtensionsRead), default);

        Assert.NotEqual(403, StatusOf(result));
        var value = Assert.IsAssignableFrom<IValueHttpResult>(result).Value!;
        var counts = value.GetType().GetProperty("counts")!.GetValue(value)!;
        Assert.Equal(2, Prop(counts, "Imported"));
        Assert.Equal(1, Prop(counts, "Skipped"));
        Assert.Equal(1, Prop(counts, "Flagged"));
        Assert.Equal(4, Prop(counts, "Total"));
    }

    private static int Prop(object o, string name) => (int)o.GetType().GetProperty(name)!.GetValue(o)!;

    [Fact]
    public async Task Scheduler_EnqueuesExclusiveReconcileJob_WhoseWorkRunsTheReconcileBody()
    {
        var jobs = new CapturingJobService();
        var ran = 0;
        var scheduler = new ReconcileScheduler(jobs, (_, _) => { ran++; return Task.CompletedTask; }, TimeSpan.FromMinutes(15));

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
