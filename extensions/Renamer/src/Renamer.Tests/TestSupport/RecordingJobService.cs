using Cove.Core.Interfaces;

namespace Renamer.Tests.TestSupport;

// Records each enqueue and never runs the work on its own. GetJob answers with the one job it was
// given. The enqueued delegate is kept so a test can run what the handler captured, which is the only
// way to observe the permissions and options an enqueue closes over.
public sealed class RecordingJobService(JobInfo? job = null) : IJobService
{
    public List<(string type, string description, bool exclusive)> Enqueued { get; } = [];

    public List<Func<IJobProgress, CancellationToken, Task>> Work { get; } = [];

    public string Enqueue(string type, string description, Func<IJobProgress, CancellationToken, Task> work, bool exclusive = true)
    {
        Enqueued.Add((type, description, exclusive));
        Work.Add(work);
        return "job-123";
    }

    // Runs the single enqueued job to completion, as the host's worker would.
    public Task RunTheOnlyJobAsync() => Assert.Single(Work)(new NullProgress(), CancellationToken.None);

    public JobInfo? GetJob(string jobId) => job is not null && job.Id == jobId ? job : null;

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
