using Cove.Core.Interfaces;

namespace ScanAndIdentify.Tests.TestSupport;

// Answers GetJob from what a test configured and null for anything else, which is what lets a
// derivation test fail: a fake that invents a job for every id it is asked about agrees with the
// code whatever the code does. Null is the host's own answer for a job evicted from its hundred-job
// history, for one lost to a restart and for an id that was never real.
internal sealed class RecordingJobService : IJobService
{
    private readonly Dictionary<string, JobInfo> _jobs = [];

    public RecordingJobService With(string jobId, JobStatus status)
    {
        _jobs[jobId] = new JobInfo(
            jobId, "scan", "Scanning", status, 0, null, DateTime.UtcNow, null, null);
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
