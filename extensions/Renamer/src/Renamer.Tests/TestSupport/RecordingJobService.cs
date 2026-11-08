using Cove.Core.Interfaces;

namespace Renamer.Tests.TestSupport;

// Records each enqueue and never runs the work on its own. GetJob answers with the one job it was
// given. The enqueued delegate is kept so a test can run what the handler captured, which is the only
// way to observe the permissions and options an enqueue closes over.
//
// Ownership is modelled because the extension reads a job by owner: the host stamps the enqueueing
// principal on every job started from a request, and answers a reader who is not that principal with
// nothing. A fake that ignored the owner would pass a test of a route that refuses everyone.
public sealed class RecordingJobService(JobInfo? job = null, JobOwner? jobOwner = null) : IJobService
{
    public List<(string type, string description, bool exclusive)> Enqueued { get; } = [];

    public List<Func<IJobProgress, CancellationToken, Task>> Work { get; } = [];

    public string Enqueue(string type, string description, Func<IJobProgress, CancellationToken, Task> work, bool exclusive = true)
    {
        Enqueued.Add((type, description, exclusive));
        Work.Add(work);
        return "job-123";
    }

    public Task RunTheOnlyJobAsync() => Assert.Single(Work)(new NullProgress(), CancellationToken.None);

    public JobInfo? GetJob(string jobId) => job is not null && job.Id == jobId ? job : null;

    // The host's own rule: without includeAll, a job is returned only to the owner it was enqueued
    // under, and a job with no owner is returned to nobody.
    public JobInfo? GetJobFor(JobOwner? owner, string jobId, bool includeAll)
    {
        if (!includeAll && (jobOwner is null || jobOwner != owner))
        {
            return null;
        }

        return GetJob(jobId);
    }

    public bool Cancel(string jobId) => throw new NotSupportedException();
    public bool ReorderQueued(string jobId, string? beforeJobId) => throw new NotSupportedException();
    public IReadOnlyList<JobInfo> GetAllJobs() => throw new NotSupportedException();
    public IReadOnlyList<JobInfo> GetJobHistory() => throw new NotSupportedException();

    private sealed class NullProgress : IJobProgress
    {
        public void Report(double progress, string? subTask = null)
        {
        }
    }
}
