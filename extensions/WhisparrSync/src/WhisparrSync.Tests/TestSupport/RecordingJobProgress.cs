using Cove.Core.Interfaces;

namespace WhisparrSync.Tests.TestSupport;

/// <summary>One unit a run reported, with the outcome it was completed under.</summary>
/// <param name="UnitId">Which entity the unit was about.</param>
/// <param name="Outcome">How it was completed, or null while it is still open.</param>
/// <param name="Message">What the completion named, or null where it named nothing.</param>
public sealed record ReportedUnit(string UnitId, JobUnitOutcome? Outcome, string? Message = null);

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

    public void Report(double progress, string? subTask = null) => Reports.Add((progress, subTask));

    public IJobUnit StartUnit(string unitId, string? label = null)
    {
        var unit = new RecordedUnit(unitId);
        Units.Add(new ReportedUnit(unitId, null));
        var index = Units.Count - 1;
        unit.OnComplete = (outcome, message) =>
            Units[index] = Units[index] with { Outcome = outcome, Message = message };
        return unit;
    }

    private sealed class RecordedUnit(string unitId) : IJobUnit
    {
        public string UnitId { get; } = unitId;

        public JobUnitOutcome? Outcome { get; private set; }

        public Action<JobUnitOutcome, string?>? OnComplete { get; set; }

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

        public void Dispose()
        {
        }
    }
}
