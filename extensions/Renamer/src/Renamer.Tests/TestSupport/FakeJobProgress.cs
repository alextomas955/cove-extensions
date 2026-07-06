using Cove.Plugins;

namespace Renamer.Tests.TestSupport;

/// <summary>
/// A recording <see cref="IJobProgress"/> fake (the Cove.Plugins extension flavor, whose only
/// member is <c>Report(double, string?)</c>) so the batch/job/event tests can assert the per-item
/// progress sequence and the final <c>1.0</c> report without a running host (the host bridges only
/// <c>Report</c> for extensions).
/// </summary>
public sealed class FakeJobProgress : IJobProgress
{
    /// <summary>Every <c>Report</c> call, in order, as (percent, message).</summary>
    public List<(double Percent, string? Message)> Reports { get; } = [];

    /// <summary>The last reported percent, or null when nothing has been reported yet.</summary>
    public double? LastPercent => Reports.Count > 0 ? Reports[^1].Percent : null;

    public void Report(double percent, string? message = null) => Reports.Add((percent, message));
}
