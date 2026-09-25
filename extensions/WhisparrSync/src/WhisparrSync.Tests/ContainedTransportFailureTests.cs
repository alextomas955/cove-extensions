using System.Globalization;
using System.Net;
using Cove.Core.Interfaces;
using WhisparrSync.Contracts;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests;

// An instance that answers its headers and then stops sending raises an I/O failure, not a request
// one, so every containment filter has to name both. Driven through the shipped client over a
// transport double: the recording seam answers a response object and can express no failure at all,
// so a case taken there could not tell containment from a refusal the product chose.
public sealed class ContainedTransportFailureTests
{
    // A second identifier, so two seeded studios do not share one.
    private const string SecondStudioRemoteId = "b1c2d3e4-f5a6-4708-9192-a3b4c5d6e7f8";

    // The status is asserted before the view is read. An escaped failure answers 500, and reading
    // the view first would throw on the deserialization rather than name what happened.
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

    // A failure escaping the per-entity containment leaves the run before its closing count, losing
    // the record of every entity already acted on. Both units are asserted too, so a run that
    // stopped at the first entity fails here rather than passing on the count alone.
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
                new ReportedUnit(
                    UnitOf(first),
                    JobUnitOutcome.Failed,
                    nameof(MonitorRefusalKind.InstanceRefused),
                    Disposed: true),
                new ReportedUnit(
                    UnitOf(second),
                    JobUnitOutcome.Failed,
                    nameof(MonitorRefusalKind.InstanceRefused),
                    Disposed: true),
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
