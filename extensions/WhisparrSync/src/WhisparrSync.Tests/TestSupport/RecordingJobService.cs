using System.Globalization;
using Cove.Core.Interfaces;

namespace WhisparrSync.Tests.TestSupport;

/// <summary>One enqueue this service was asked for, with its arguments.</summary>
/// <param name="Type">The job type, which carries the owning extension's prefix.</param>
/// <param name="Description">The line the host's Job Drawer shows.</param>
/// <param name="Exclusive">Whether it was asked to run one at a time.</param>
/// <param name="Work">The delegate the host would run.</param>
public sealed record EnqueuedJob(
    string Type, string Description, bool Exclusive, Func<IJobProgress, CancellationToken, Task> Work);

/// <summary>
/// A host job service that records what it was asked to enqueue rather than running it.
/// </summary>
/// <remarks>
/// The work is kept rather than started, so a case can assert that a request was REFUSED before
/// anything was enqueued, and a case that wants the batch itself runs the delegate by hand.
/// </remarks>
internal sealed class RecordingJobService : IJobService
{
    private readonly Dictionary<string, JobInfo> _jobs = new(StringComparer.Ordinal);
    private int _minted;

    /// <summary>Every enqueue this service was asked for, in order.</summary>
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

    /// <summary>Records a job of any type, so a status read can be driven for a foreign one.</summary>
    public JobInfo Holding(string jobId, string type)
    {
        var job = new JobInfo(
            jobId, type, "a job", JobStatus.Pending, 0, null, DateTime.UnixEpoch, null, null);
        _jobs[jobId] = job;
        return job;
    }

    /// <summary>Runs the most recently enqueued job's own work.</summary>
    public Task RunLastAsync(IJobProgress progress, CancellationToken ct)
        => Enqueued[^1].Work(progress, ct);

    public bool Cancel(string jobId) => false;

    public bool ReorderQueued(string jobId, string? beforeJobId) => false;

    public JobInfo? GetJob(string jobId) => _jobs.GetValueOrDefault(jobId);

    public IReadOnlyList<JobInfo> GetAllJobs() => [.. _jobs.Values];

    public IReadOnlyList<JobInfo> GetJobHistory() => [];
}
