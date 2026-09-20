using System.Globalization;
using Cove.Core.Interfaces;

namespace WhisparrSync.Tests.TestSupport;

public sealed record EnqueuedJob(
    string Type, string Description, bool Exclusive, Func<IJobProgress, CancellationToken, Task> Work);

// Keeps the work rather than starting it, so a case can assert a request was refused before
// anything was enqueued, and a case that wants the batch runs the delegate by hand.
internal sealed class RecordingJobService : IJobService
{
    private readonly Dictionary<string, JobInfo> _jobs = new(StringComparer.Ordinal);
    private int _minted;

    public List<EnqueuedJob> Enqueued { get; } = [];

    public string Enqueue(
        string type,
        string description,
        Func<IJobProgress, CancellationToken, Task> work,
        bool exclusive = true)
    {
        var jobId = "job-" + (++_minted).ToString(CultureInfo.InvariantCulture);
        Enqueued.Add(new EnqueuedJob(type, description, exclusive, work));
        _jobs[jobId] = Holding(jobId, type);
        return jobId;
    }

    // Records a job of any type, so a status read can be driven for a foreign one.
    public JobInfo Holding(string jobId, string type)
    {
        var job = new JobInfo(
            jobId, type, "a job", JobStatus.Pending, 0, null, DateTime.UnixEpoch, null, null);
        _jobs[jobId] = job;
        return job;
    }

    public Task RunLastAsync(IJobProgress progress, CancellationToken ct)
        => Enqueued[^1].Work(progress, ct);

    public bool Cancel(string jobId) => false;

    public bool ReorderQueued(string jobId, string? beforeJobId) => false;

    public JobInfo? GetJob(string jobId) => _jobs.GetValueOrDefault(jobId);

    public IReadOnlyList<JobInfo> GetAllJobs() => [.. _jobs.Values];

    public IReadOnlyList<JobInfo> GetJobHistory() => [];
}
