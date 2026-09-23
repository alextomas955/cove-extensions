using Cove.Plugins;

namespace Renamer.Tests.TestSupport;

// A recording IJobProgress fake (the Cove.Plugins extension flavor, whose only member is
// Report(double, string?)) so the batch/job/event tests can assert the per-item progress sequence
// and the final 1.0 report without a running host (the host bridges only Report for extensions).
public sealed class FakeJobProgress : IJobProgress
{
    // Every Report call, in order, as (percent, message).
    public List<(double Percent, string? Message)> Reports { get; } = [];

    // The last reported percent, or null when nothing has been reported yet.
    public double? LastPercent => Reports.Count > 0 ? Reports[^1].Percent : null;

    public void Report(double percent, string? message = null) => Reports.Add((percent, message));
}
