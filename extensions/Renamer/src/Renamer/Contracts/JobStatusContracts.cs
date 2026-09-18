using System.Text.Json.Serialization;
using Cove.Core.Interfaces;
using Cove.Extensions.Shared;

namespace Renamer.Contracts;

/// <summary>Where a Renamer run has got to, as the panel's poller understands it.</summary>
/// <remarks>
/// This is a wire type, so its camelCase spelling is part of the extension's own contract and it does
/// not reuse the host's <see cref="JobStatus"/>. The converter is declared on the type, never on an
/// options object, because an options-level converter outranks a type attribute.
/// </remarks>
[JsonConverter(typeof(CamelCaseStringEnumConverter))]
public enum RenamerJobState
{
    Pending,

    Running,

    Completed,

    /// <summary>Stopped on an error, with the reason in <see cref="RenamerJobStatus.Error"/> when the host named one.</summary>
    Failed,

    /// <summary>Stopped because it was cancelled, host shutdown included.</summary>
    Cancelled,
}

/// <summary>One read of a Renamer run's progress, served by the extension rather than by the host.</summary>
/// <remarks>
/// Cove restricts its own job route to callers holding unrestricted read, so a scoped account cannot
/// watch a run through it even when it started the run. This projection carries only what the panel's
/// poller reads, and never the host's <c>Type</c> or <c>Description</c>, which name the owning
/// extension. <c>Progress</c> runs 0 to 1 as the host reports it, <c>SubTask</c> is the host's
/// free-text line, and <c>EtaSeconds</c> is null until the host has an estimate.
/// </remarks>
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

    // Every member is named and there is no discard arm, so a status added to the host's enum stops
    // this build instead of reaching the panel's poller, which reads an unrecognised status as still
    // going and would strand the run.
    private static RenamerJobState StateFor(JobStatus status) => status switch
    {
        JobStatus.Pending => RenamerJobState.Pending,
        JobStatus.Running => RenamerJobState.Running,
        JobStatus.Completed => RenamerJobState.Completed,
        JobStatus.Failed => RenamerJobState.Failed,
        JobStatus.Cancelled => RenamerJobState.Cancelled,
    };
}
