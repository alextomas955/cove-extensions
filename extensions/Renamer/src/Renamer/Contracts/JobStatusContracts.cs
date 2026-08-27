using System.Text.Json.Serialization;
using Cove.Core.Interfaces;
using Cove.Extensions.Shared;

namespace Renamer.Contracts;

/// <summary>Where a Renamer run has got to, as the panel's poller understands it.</summary>
/// <remarks>
/// Declared here rather than reusing the host's <see cref="JobStatus"/> because this is a wire type:
/// its spelling is part of the extension's own contract. The converter is declared on the TYPE, never
/// on an options object, since an options-level converter outranks a type attribute and a second
/// declaration could then drift and win silently.
/// </remarks>
[JsonConverter(typeof(CamelCaseStringEnumConverter))]
public enum RenamerJobState
{
    /// <summary>Queued, not yet started.</summary>
    Pending,

    /// <summary>Started and not yet finished.</summary>
    Running,

    /// <summary>Finished, having done its work.</summary>
    Completed,

    /// <summary>Stopped on an error. <see cref="RenamerJobStatus.Error"/> carries the reason when the host named one.</summary>
    Failed,

    /// <summary>Stopped because it was cancelled, host shutdown included.</summary>
    Cancelled,
}

/// <summary>
/// One read of a Renamer run's progress, served by the extension rather than by the host.
/// </summary>
/// <remarks>
/// Cove restricts its own job route to callers holding unrestricted read, so a scoped account cannot
/// watch a run through it even when the run is one it started itself. This projection carries only
/// what the panel's poller reads, and never the host's <c>Type</c> or <c>Description</c>: those name
/// the owning extension, and a caller that cannot be told a foreign job exists must not be handed its
/// identity either.
/// </remarks>
/// <param name="Id">The job id the enqueue returned.</param>
/// <param name="Status">Where the run has got to.</param>
/// <param name="Progress">Fraction complete, 0 to 1 as the host reports it.</param>
/// <param name="SubTask">The host's free-text phase line, when it set one.</param>
/// <param name="Error">Why the run failed, when the host named a reason.</param>
/// <param name="EtaSeconds">The host's own estimate of seconds remaining; null until it has one.</param>
public sealed record RenamerJobStatus(
    string Id,
    RenamerJobState Status,
    double Progress,
    string? SubTask,
    string? Error,
    double? EtaSeconds)
{
    /// <summary>Projects a host <see cref="JobInfo"/> onto this contract.</summary>
    public static RenamerJobStatus From(JobInfo job) => new(
        job.Id,
        StateFor(job.Status),
        job.Progress,
        job.SubTask,
        job.Error,
        job.EtaSeconds);

    /// <summary>
    /// The wire state a host <paramref name="status"/> is reported under.
    /// </summary>
    /// <remarks>
    /// Every member is named and there is NO discard arm, so a status added to the host's enum stops
    /// this build instead of arriving as a value the panel's poller has never been told about. The
    /// poller reads an unrecognised status as "still going" rather than as success, so a passthrough
    /// would strand a run rather than mis-report one — still worth refusing to compile over.
    /// </remarks>
    private static RenamerJobState StateFor(JobStatus status) => status switch
    {
        JobStatus.Pending => RenamerJobState.Pending,
        JobStatus.Running => RenamerJobState.Running,
        JobStatus.Completed => RenamerJobState.Completed,
        JobStatus.Failed => RenamerJobState.Failed,
        JobStatus.Cancelled => RenamerJobState.Cancelled,
    };
}
