using System.Globalization;
using System.Net;
using Cove.Core.Interfaces;
using WhisparrSync.Contracts;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests;

/// <summary>
/// What reaches a caller when an instance answers its headers and then stops sending.
/// </summary>
/// <remarks>
/// The client reads a body out of the response stream rather than letting the send buffer it, so a
/// connection dropped part way through an answer raises an I/O failure and not a request one. Every
/// containment filter has to name both, and these cases are what say so: a route whose declared
/// results hold no failure answers a refusal, and a batch keeps the record it had already built.
/// <para>
/// Driven through the shipped client over a transport double rather than through the recording seam.
/// The seam answers a response object and can express no failure at all, so a case taken there could
/// not tell a contained failure from a refusal the product chose.
/// </para>
/// </remarks>
public sealed class ContainedTransportFailureTests
{
    /// <summary>A second identifier, so two seeded studios do not share one.</summary>
    private const string SecondStudioRemoteId = "b1c2d3e4-f5a6-4708-9192-a3b4c5d6e7f8";

    /// <summary>
    /// One entity's press answers a stated refusal rather than a server failure.
    /// </summary>
    /// <remarks>
    /// The status is asserted before the view is read. An escaped failure answers 500, which the
    /// control shows as a failed request instead of as a reason, and reading the view first would
    /// throw on the deserialization rather than name what happened.
    /// </remarks>
    [Fact]
    public async Task APressWhoseAnswerStopsPartWayIsAnsweredAsARefusalRatherThanAServerFailure()
    {
        await using var host = await MonitorHost.CreateAsync(
            bytes: BodyRecordingHandler.AnsweringWithABodyThatStopsPartWay());
        var studio = await host.SeedStudioAsync(
            MonitorHost.StoredEndpoint, MonitorHost.StudioRemoteIdValue);

        var answered = await host.PostRawAsync(
            "studio", studio, "monitor", """{"scope":"futureScenes"}""");

        Assert.Equal(HttpStatusCode.OK, answered.StatusCode);
        var view = await MonitorHost.ReadViewAsync(answered);
        Assert.False(view.Monitored);
        Assert.Equal(MonitorRefusalKind.InstanceRefused, view.Refusal);
    }

    /// <summary>
    /// A batch whose answers stop part way completes, reports every unit, and reports its own
    /// closing count.
    /// </summary>
    /// <remarks>
    /// The closing report is the assertion that matters. A failure escaping the per-entity containment
    /// leaves the run before its count is reported, so a selection loses the record of every entity it
    /// had already acted on and the host marks the whole run failed. Both units are asserted too, so a
    /// run that stopped at the first entity is reported here rather than passing on the count alone.
    /// </remarks>
    [Fact]
    public async Task ABatchWhoseAnswersStopPartWayKeepsEveryUnitAndItsClosingCount()
    {
        await using var host = await MonitorHost.CreateAsync(
            bytes: BodyRecordingHandler.AnsweringWithABodyThatStopsPartWay());
        var first = await host.SeedStudioAsync(
            MonitorHost.StoredEndpoint, MonitorHost.StudioRemoteIdValue);
        var second = await host.SeedStudioAsync(MonitorHost.StoredEndpoint, SecondStudioRemoteId);
        var progress = new RecordingJobProgress();

        await host.PostBulkAsync(BodyOf("studios", "monitor", [first, second]));
        await host.RunEnqueuedBatchAsync(progress);

        Assert.Equal(
            [
                new ReportedUnit(UnitOf(first), JobUnitOutcome.Failed, nameof(MonitorRefusalKind.InstanceRefused)),
                new ReportedUnit(UnitOf(second), JobUnitOutcome.Failed, nameof(MonitorRefusalKind.InstanceRefused)),
            ],
            progress.Units);
        Assert.Contains(progress.Reports, report => report.Fraction >= 1d && report.SubTask is not null);
    }

    private static string UnitOf(int coveId) => coveId.ToString(CultureInfo.InvariantCulture);

    private static string BodyOf(string entityType, string verb, IReadOnlyList<int> ids)
        => string.Create(
            CultureInfo.InvariantCulture,
            $$"""
            {"entityType":"{{entityType}}","verb":"{{verb}}","scope":"futureScenes","entityIds":[{{string.Join(',', ids)}}]}
            """);
}
