using System.Globalization;
using System.Net;
using System.Net.Http.Json;
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

    // One line per swallowed failure. The count is asserted, not merely a presence: a second line
    // for one failure is the defect the rule names.
    [Fact]
    public async Task ATrackRequestThatStopsPartWayEmitsOneLineAndStartsNothing()
    {
        await using var host = await MonitorHost.CreateAsync(bytes: TrackAddStoppingPartWay());
        var studio = await host.SeedStudioAsync(
            MonitorHost.StoredEndpoint, MonitorHost.StudioRemoteIdValue);

        var result = await TrackAsync(host, studio);

        Assert.Equal(MissingTrackOutcome.NotStarted, result.Outcome);
        Assert.Single(host.Log.Entries);
    }

    [Fact]
    public async Task AQualityProfileReadThatStopsPartWayEmitsOneLine()
    {
        await using var host = await MonitorHost.CreateAsync(
            bytes: Composed(
                (_, path) => path.EndsWith("/qualityprofile", StringComparison.Ordinal)));
        var studio = await host.SeedStudioAsync(
            MonitorHost.StoredEndpoint, MonitorHost.StudioRemoteIdValue);

        var result = await TrackAsync(host, studio);

        Assert.Equal(MissingTrackOutcome.NotStarted, result.Outcome);
        Assert.Single(host.Log.Entries);
    }

    // The two reads share one member, so a case driving only the first could not tell whether the
    // second reaches the line at all.
    [Fact]
    public async Task ARootFolderReadThatStopsPartWayEmitsOneLineOfItsOwn()
    {
        await using var host = await MonitorHost.CreateAsync(
            bytes: Composed((_, path) => path.EndsWith("/rootfolder", StringComparison.Ordinal)));
        var studio = await host.SeedStudioAsync(
            MonitorHost.StoredEndpoint, MonitorHost.StudioRemoteIdValue);

        var result = await TrackAsync(host, studio);

        Assert.Equal(MissingTrackOutcome.NotStarted, result.Outcome);
        Assert.Single(host.Log.Entries);
    }

    // The control for the three above: a run every call answers whole logs nothing.
    [Fact]
    public async Task ATrackRequestEveryCallAnswersWholeEmitsNoLine()
    {
        await using var host = await MonitorHost.CreateAsync(bytes: Composed((_, _) => false));
        var studio = await host.SeedStudioAsync(
            MonitorHost.StoredEndpoint, MonitorHost.StudioRemoteIdValue);

        var result = await TrackAsync(host, studio);

        Assert.Equal(MissingTrackOutcome.Added, result.Outcome);
        Assert.Empty(host.Log.Entries);
    }

    private static BodyRecordingHandler TrackAddStoppingPartWay()
        => Composed((method, path) =>
            method == HttpMethod.Post && path.EndsWith("/studio", StringComparison.Ordinal));

    // The reads an add composes its defaults from, answered so a case reaches the call it is about.
    private static BodyRecordingHandler Composed(Func<HttpMethod, string, bool> stopsPartWay)
        => BodyRecordingHandler.AnsweringEachAndStoppingPartWayOn(
            (_, path) => path switch
            {
                var route when route.EndsWith("/qualityprofile", StringComparison.Ordinal)
                    => (HttpStatusCode.OK, """[{"id":4,"name":"Any"}]"""),
                var route when route.EndsWith("/rootfolder", StringComparison.Ordinal)
                    => (HttpStatusCode.OK, """[{"id":1,"path":"/config/library"}]"""),
                _ => (HttpStatusCode.Created, """{"id":1}"""),
            },
            stopsPartWay);

    private static async Task<MissingTrackResult> TrackAsync(MonitorHost host, int coveId)
    {
        var answered = await host.Http.PostAsync(
            host.RouteFor("studio", coveId, "missing/track"), content: null, TestCt);

        answered.EnsureSuccessStatusCode();
        return (await answered.Content.ReadFromJsonAsync<MissingTrackResult>(TestCt))!;
    }

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    private static string UnitOf(int coveId) => coveId.ToString(CultureInfo.InvariantCulture);

    private static string BodyOf(string entityType, string verb, IReadOnlyList<int> ids)
        => string.Create(
            CultureInfo.InvariantCulture,
            $$"""
            {"entityType":"{{entityType}}","verb":"{{verb}}","scope":"futureScenes","entityIds":[{{string.Join(',', ids)}}]}
            """);
}
