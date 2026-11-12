using Cove.Core.Interfaces;

namespace ScanAndIdentify.Tests.TestSupport;

// Answers GetJob from what a test configured and null for anything else, which is what lets a
// derivation test fail: a fake that invents a job for every id it is asked about agrees with the
// code whatever the code does. Null is the host's own answer for a job evicted from its hundred-job
// history, for one lost to a restart and for an id that was never real.
internal sealed class RecordingJobService : IJobService
{
    private readonly Dictionary<string, JobInfo> _jobs = [];

    public RecordingJobService With(
        string jobId,
        JobStatus status,
        // Cove's own type for the pass: "scan", "generate" or "identify". The stage report route
        // refuses an id the host does not hold as the pass that stage runs, so a test reporting one
        // has to say which pass it is.
        string type = "scan",
        double progress = 0,
        string? subTask = null,
        string? summary = null,
        string? error = null,
        int? unitsTotal = null,
        int? unitsCompleted = null,
        int? unitsSucceeded = null,
        int? unitsFailed = null,
        int? unitsSkipped = null)
    {
        _jobs[jobId] = new JobInfo(
            jobId, type, "Scanning", status, progress, subTask, DateTime.UtcNow, null, error,
            unitsTotal, unitsCompleted, unitsSucceeded, unitsFailed, unitsSkipped, summary);
        return this;
    }

    public JobInfo? GetJob(string jobId) => _jobs.GetValueOrDefault(jobId);

    public string Enqueue(
        string type, string description, Func<IJobProgress, CancellationToken, Task> work, bool exclusive = true)
        => throw new NotSupportedException();

    public bool Cancel(string jobId) => throw new NotSupportedException();

    public bool ReorderQueued(string jobId, string? beforeJobId) => throw new NotSupportedException();

    public IReadOnlyList<JobInfo> GetAllJobs() => throw new NotSupportedException();

    public IReadOnlyList<JobInfo> GetJobHistory() => throw new NotSupportedException();
}
