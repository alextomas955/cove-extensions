using Cove.Core.Interfaces;

namespace WhisparrSync.Tests.TestSupport;

/// <summary>One unit a run reported, with the outcome it was completed under.</summary>
/// <param name="UnitId">Which entity the unit was about.</param>
/// <param name="Outcome">How it was completed, or null while it is still open.</param>
/// <param name="Message">What the completion named, or null where it named nothing.</param>
/// <param name="Disposed">
/// Whether the run disposed the unit. Separate from <paramref name="Outcome"/> because the host
/// removes a completed unit's state only on disposal: a run that completes every unit and disposes
/// none is correct by outcome and still leaves an entry per entity in a host dictionary.
/// </param>
public sealed record ReportedUnit(
    string UnitId,
    JobUnitOutcome? Outcome,
    string? Message = null,
    bool Disposed = false);

/// <summary>
/// A host job progress that records what a run reported, in order.
/// </summary>
/// <remarks>
/// The order is the whole point: a batch reporting its entities in any order other than the one the
/// ids were supplied in cannot be matched against the selection a user made, and a recorder that
/// counted rather than kept order would agree with a run that grouped them.
/// </remarks>
internal sealed class RecordingJobProgress : IJobProgress
{
    /// <summary>Every unit started, in the order it was started.</summary>
    public List<ReportedUnit> Units { get; } = [];

    /// <summary>Every parent-level report, with the arguments it carried, in order.</summary>
    public List<(double Fraction, string? SubTask)> Reports { get; } = [];

    /// <summary>
    /// Every declared unit count, in the order it was declared.
    /// </summary>
    /// <remarks>
    /// A list rather than a count or a last value, because the host refuses a declaration made after
    /// the first unit starts and refuses a second one. Order against <see cref="Units"/> is what
    /// shows a run declared once and declared first; a counter would agree with a run that declared
    /// late.
    /// </remarks>
    public List<int> DeclaredUnitCounts { get; } = [];

    /// <summary>
    /// Every summary the run set, in the order it set them.
    /// </summary>
    /// <remarks>
    /// Kept apart from <see cref="Reports"/> because the host does the same: a summary survives on
    /// the job where a final report's sub-task is overwritten by the unit line. Order is what shows
    /// the last thing a run said was its own summary.
    /// </remarks>
    public List<string> Summaries { get; } = [];

    public void Report(double progress, string? subTask = null) => Reports.Add((progress, subTask));

    public void SetSummary(string summary) => Summaries.Add(summary);

    public void DeclareUnitCount(int totalUnits) => DeclaredUnitCounts.Add(totalUnits);

    public IJobUnit StartUnit(string unitId, string? label = null)
    {
        var unit = new RecordedUnit(unitId);
        Units.Add(new ReportedUnit(unitId, null));
        var index = Units.Count - 1;
        unit.OnComplete = (outcome, message) =>
            Units[index] = Units[index] with { Outcome = outcome, Message = message };
        unit.OnDispose = () => Units[index] = Units[index] with { Disposed = true };
        return unit;
    }

    private sealed class RecordedUnit(string unitId) : IJobUnit
    {
        public string UnitId { get; } = unitId;

        public JobUnitOutcome? Outcome { get; private set; }

        public Action<JobUnitOutcome, string?>? OnComplete { get; set; }

        public Action? OnDispose { get; set; }

        public void Report(double progress, string? message = null)
        {
        }

        public void Complete(JobUnitOutcome outcome, string? message = null)
        {
            if (Outcome is not null)
            {
                return;
            }

            Outcome = outcome;
            OnComplete?.Invoke(outcome, message);
        }

        public void Dispose() => OnDispose?.Invoke();
    }
}
