using System.Text.Json.Serialization;
using Cove.Core.Interfaces;
using Cove.Extensions.Shared;
using ScanAndIdentify.Options;

namespace ScanAndIdentify.Contracts;

/// <summary>The four passes a run performs, in the order it performs them.</summary>
/// <remarks>
/// The converter is declared on the type, never on a serializer options object, because an
/// options-level converter outranks a type attribute. This spelling is both the wire spelling and
/// the stored one, declared once so the two cannot drift.
/// </remarks>
[JsonConverter(typeof(CamelCaseStringEnumConverter))]
public enum RunStage
{
    Scan,

    Fingerprints,

    Identify,

    Images,
}

/// <summary>Where one stage of a run has got to.</summary>
/// <remarks>
/// <c>Pending</c> means the run has not reached this stage; <c>Queued</c> means Cove holds the job
/// and has not started it, which a user's own scan can cause for hours and is healthy.
/// <c>Starting</c> is the window between the stage being claimed and its job id arriving.
/// <c>Indeterminate</c> covers every reason a job id stops resolving: evicted from the host's
/// hundred-job history, lost to a host restart, or never real. The three are indistinguishable from
/// the record, which is why one state carries them all.
/// </remarks>
[JsonConverter(typeof(CamelCaseStringEnumConverter))]
public enum RunStageState
{
    Pending,

    Starting,

    Queued,

    Running,

    Succeeded,

    Failed,

    Cancelled,

    Skipped,

    Indeterminate,
}

/// <summary>Why a run stopped.</summary>
/// <remarks>
/// <c>Completed</c> and <c>Interrupted</c> are derived from the stage states and are not assertable
/// by a caller; the end route accepts only the other two.
/// </remarks>
[JsonConverter(typeof(CamelCaseStringEnumConverter))]
public enum RunEndReason
{
    Completed,

    Failed,

    Cancelled,

    Interrupted,
}

/// <summary>What the host job behind one stage reports about itself.</summary>
/// <remarks>
/// Every member is nullable because the host leaves most of them unset: only a job run through
/// <c>RunBatchAsync</c> counts units, which the scan and identify passes do not, so their tallies are
/// absent rather than zero.
/// <para>
/// <c>Progress</c> runs 0 to 1 as the host reports it, and <c>SubTask</c> is the host's free-text
/// line, which can name a file. The three text members are truncated at <see cref="MaxTextLength"/>:
/// a terminal stage's detail is stored, Cove serializes every stored value on its bulk data route,
/// and a host error naming every failed file would otherwise grow that value until the whole
/// settings page fails to load.
/// </para>
/// <para>
/// The host's estimate, result url, job type and description are deliberately absent. Nothing here
/// presents an estimate, and the last two name the owning extension.
/// </para>
/// </remarks>
public sealed record RunStageDetail(
    double? Progress,
    string? SubTask,
    int? UnitsTotal,
    int? UnitsCompleted,
    int? UnitsSucceeded,
    int? UnitsFailed,
    int? UnitsSkipped,
    string? Summary,
    string? Error)
{
    /// <summary>How much of a host text line is kept.</summary>
    public const int MaxTextLength = 500;

    /// <summary>Projects a host <see cref="JobInfo"/> onto this contract.</summary>
    public static RunStageDetail From(JobInfo job)
    {
        ArgumentNullException.ThrowIfNull(job);

        return new(
            job.Progress,
            Truncate(job.SubTask),
            job.UnitsTotal,
            job.UnitsCompleted,
            job.UnitsSucceeded,
            job.UnitsFailed,
            job.UnitsSkipped,
            Truncate(job.Summary),
            Truncate(job.Error));
    }

    private static string? Truncate(string? text)
        => text is not null && text.Length > MaxTextLength ? text[..MaxTextLength] : text;
}

/// <summary>One stage of a run as the page reads it.</summary>
/// <remarks>
/// <c>Detail</c> is what the stage's host job reported: read live while the stage is still moving,
/// and answered from the record once it has settled, because the host forgets a finished job once a
/// hundred others have finished.
/// </remarks>
public sealed record RunStageView(RunStage Stage, RunStageState State, string? JobId, RunStageDetail? Detail)
{
    /// <summary>
    /// Maps a host job status onto a stage state.
    /// </summary>
    /// <remarks>
    /// Every member is named and there is no discard arm, so a status added to the host's enum stops
    /// this build instead of reaching the page as a stage that is still running. A queued job is a
    /// healthy one here: the next exclusive stage waits behind whatever else Cove is draining.
    /// </remarks>
    public static RunStageState StateFor(JobStatus status) => status switch
    {
        JobStatus.Pending => RunStageState.Queued,
        JobStatus.Running => RunStageState.Running,
        JobStatus.Completed => RunStageState.Succeeded,
        JobStatus.Failed => RunStageState.Failed,
        JobStatus.Cancelled => RunStageState.Cancelled,
    };
}

/// <summary>One run as the page reads it: where every stage stands and what the run was started with.</summary>
/// <remarks>
/// <c>Options</c> is the configuration read when the run was claimed, not the form's current values.
/// The page builds every host request body from it, so a switch flipped mid-run cannot change what a
/// later stage does and a reloaded page has a source for the body.
/// <para>
/// <c>Stages</c> carries all four stages in run order, whatever state each is in.
/// </para>
/// </remarks>
public sealed record RunView(
    string RunId,
    DateTime StartedAt,
    DateTime? EndedAt,
    RunEndReason? EndedReason,
    RunOptions Options,
    IReadOnlyList<RunStageView> Stages);

/// <summary>A caller reporting that it is starting a stage, or that the stage's job now exists.</summary>
/// <remarks>
/// A body with no <c>JobId</c> moves the stage into <c>Starting</c>; one carrying it moves the stage
/// into <c>Running</c>. Both are transitions from a named current state, so a second caller racing
/// the same stage is refused rather than starting a second host job.
/// </remarks>
public sealed record RunStageStarted(string? RunId, RunStage Stage, string? JobId);

/// <summary>A caller ending a run that the stage states cannot end by themselves.</summary>
public sealed record RunEnded(string? RunId, RunEndReason Reason);
