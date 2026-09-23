using Cove.Core.Interfaces;

namespace Renamer.Tests.TestSupport;

// Records each enqueue and never runs the work. GetJob answers with the one job it was given.
public sealed class RecordingJobService(JobInfo? job = null) : IJobService
{
    public List<(string type, string description, bool exclusive)> Enqueued { get; } = [];

    public string Enqueue(string type, string description, Func<IJobProgress, CancellationToken, Task> work, bool exclusive = true)
    {
        Enqueued.Add((type, description, exclusive));
        return "job-123";
    }

    public JobInfo? GetJob(string jobId) => job is not null && job.Id == jobId ? job : null;

    public bool Cancel(string jobId) => throw new NotSupportedException();
    public bool ReorderQueued(string jobId, string? beforeJobId) => throw new NotSupportedException();
    public IReadOnlyList<JobInfo> GetAllJobs() => throw new NotSupportedException();
    public IReadOnlyList<JobInfo> GetJobHistory() => throw new NotSupportedException();
}
