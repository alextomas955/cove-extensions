using System.Text.Json.Serialization;
using Cove.Core.Interfaces;
using Cove.Extensions.Shared;

namespace WhisparrSync.Contracts;

/// <summary>The job id an enqueue answered with.</summary>
public sealed record JobEnqueued(string JobId);

/// <summary>Where one of this extension's own runs has got to.</summary>
/// <remarks>
/// Declared here rather than reusing the host's own state, because this is a wire type and its
/// spelling is part of this extension's contract.
/// </remarks>
[JsonConverter(typeof(CamelCaseStringEnumConverter))]
public enum BulkJobState
{
    /// <summary>Queued, not yet started.</summary>
    Pending,

    /// <summary>Started and not yet finished.</summary>
    Running,

    /// <summary>Finished, having had its turn at every selected entity.</summary>
    Completed,

    /// <summary>Stopped on an error the host named.</summary>
    Failed,

    /// <summary>Stopped because it was cancelled, host shutdown included.</summary>
    Cancelled,
}

/// <summary>One read of a bulk run's progress, served by this extension rather than by the host.</summary>
/// <remarks>
/// Cove gates its own job route on unrestricted read, so a scoped account cannot watch a run through
/// it even when the run is one it started. This carries only what a poller reads, and never the
/// host's <c>Type</c> or <c>Description</c>: those name the owning extension, and a caller that
/// cannot be told a foreign job exists must not be handed its identity either.
/// <para>
/// <c>Progress</c> is a fraction, 0 to 1, as the host reports it. The four entity counts are
/// applied, refused by the instance, and passed over for a reason of this product's own, against the
/// total the run has to get through.
/// </para>
/// </remarks>
public sealed record BulkJobStatus(
    string Id,
    BulkJobState Status,
    double Progress,
    string? SubTask,
    string? Error,
    string? Summary,
    int? EntitiesTotal,
    int? EntitiesApplied,
    int? EntitiesRefused,
    int? EntitiesPassedOver)
{
    /// <summary>Projects a host <see cref="JobInfo"/> onto this contract.</summary>
    public static BulkJobStatus From(JobInfo job)
    {
        ArgumentNullException.ThrowIfNull(job);
        return new BulkJobStatus(
            job.Id,
            StateFor(job.Status),
            job.Progress,
            job.SubTask,
            job.Error,
            job.Summary,
            job.UnitsTotal,
            job.UnitsSucceeded,
            job.UnitsFailed,
            job.UnitsSkipped);
    }

    // A status added to the host's enum throws here rather than reaching a poller as a value it was
    // never told about.
    private static BulkJobState StateFor(JobStatus status) => status switch
    {
        JobStatus.Pending => BulkJobState.Pending,
        JobStatus.Running => BulkJobState.Running,
        JobStatus.Completed => BulkJobState.Completed,
        JobStatus.Failed => BulkJobState.Failed,
        JobStatus.Cancelled => BulkJobState.Cancelled,
        _ => throw new ArgumentOutOfRangeException(
            nameof(status), status, "The host reports a job status this product has no wire state for."),
    };
}
