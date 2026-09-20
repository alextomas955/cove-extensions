using System.Globalization;
using Cove.Core.Interfaces;
using WhisparrSync.Contracts;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Jobs;

// Whisparr answers an add with a created status and an echo that drops the monitored field. A
// click recovers because the browser reads the entity back; a batch has no browser, so the unit is
// classified from a read rather than from the write's status.
public sealed class BulkReadBackTests
{
    private const string Studios = "studios";

    private const string HeldMonitored =
        """{"id":1,"foreignId":"44e8ac11-9ed4-42e5-a9f4-bc2c138a5a6e","monitored":true}""";

    private const string HeldUnmonitored =
        """{"id":1,"foreignId":"44e8ac11-9ed4-42e5-a9f4-bc2c138a5a6e","monitored":false}""";

    // This Whisparr generation's add answer drops the monitored field.
    private const string AcceptedWithTheFieldDropped =
        """{"id":1,"foreignId":"44e8ac11-9ed4-42e5-a9f4-bc2c138a5a6e"}""";

    private const string NotHeld = "";

    [Fact]
    public async Task AnAcceptedAddWhoseReadBackReportsNotMonitoredIsNotSucceeded()
    {
        await using var host = await MonitorHost.CreateAsync();
        host.Client
            .Answering(
                nameof(IWhisparrStudioActing.AddMonitoredStudioAsync),
                MonitorHost.Json(201, AcceptedWithTheFieldDropped))
            .Answering(
                nameof(IWhisparrStudioActing.ReadStudioAsync),
                MonitorHost.Json(404, NotHeld),
                MonitorHost.Json(200, HeldUnmonitored));
        var studio = await SeedAsync(host);
        var progress = new RecordingJobProgress();

        await host.PostBulkAsync(BodyOf(Studios, "monitor", [studio]));
        await host.RunEnqueuedBatchAsync(progress);

        var unit = Assert.Single(progress.Units);
        Assert.NotEqual(JobUnitOutcome.Succeeded, unit.Outcome);
        Assert.Equal(nameof(MonitorRefusalKind.InstanceDidNotReportTheChange), unit.Message);
    }

    [Fact]
    public async Task ThatUnitIsCountedAsRefusedInTheRunsOwnSummary()
    {
        await using var host = await MonitorHost.CreateAsync();
        host.Client
            .Answering(
                nameof(IWhisparrStudioActing.AddMonitoredStudioAsync),
                MonitorHost.Json(201, AcceptedWithTheFieldDropped))
            .Answering(
                nameof(IWhisparrStudioActing.ReadStudioAsync),
                MonitorHost.Json(404, NotHeld),
                MonitorHost.Json(200, HeldUnmonitored));
        var studio = await SeedAsync(host);
        var progress = new RecordingJobProgress();

        await host.PostBulkAsync(BodyOf(Studios, "monitor", [studio]));
        await host.RunEnqueuedBatchAsync(progress);

        Assert.Equal((1d, "0 applied, 1 refused."), Assert.Single(progress.Reports));
    }

    [Fact]
    public async Task AnAcceptedAddWhoseReadBackReportsMonitoredIsSucceeded()
    {
        await using var host = await MonitorHost.CreateAsync();
        host.Client.Answering(nameof(RecordingWhisparrClient.ReadHardlinkSettingAsync), MonitorHost.Json(200, "{}"));
        host.Client.Answering(
            nameof(IWhisparrStudioActing.ReadStudioAsync),
            MonitorHost.Json(404, NotHeld),
            MonitorHost.Json(200, HeldMonitored));
        var studio = await SeedAsync(host);
        var progress = new RecordingJobProgress();

        await host.PostBulkAsync(BodyOf(Studios, "monitor", [studio]));
        await host.RunEnqueuedBatchAsync(progress);

        Assert.Equal(JobUnitOutcome.Succeeded, Assert.Single(progress.Units).Outcome);
        Assert.Equal(
            (1d, "1 applied, 0 refused. No files were linked: Whisparr's hard-link setting could not be read."),
            Assert.Single(progress.Reports));
    }

    // A failed read-back leaves the instance state unknown, which is not evidence the monitor took.
    [Fact]
    public async Task AReadBackThatItselfFailsIsNotSucceededAndDoesNotFailTheBatch()
    {
        await using var host = await MonitorHost.CreateAsync();
        host.Client.Answering(
            nameof(IWhisparrStudioActing.ReadStudioAsync),
            MonitorHost.Json(404, NotHeld),
            MonitorHost.Json(500, NotHeld));
        var studio = await SeedAsync(host);
        var progress = new RecordingJobProgress();

        await host.PostBulkAsync(BodyOf(Studios, "monitor", [studio]));
        await host.RunEnqueuedBatchAsync(progress);

        Assert.NotEqual(JobUnitOutcome.Succeeded, Assert.Single(progress.Units).Outcome);
        Assert.Equal((1d, "0 applied, 1 refused."), Assert.Single(progress.Reports));
    }

    [Fact]
    public async Task AFlipOnAnEntityTheInstanceHoldsIsAlsoClassifiedFromAReadBack()
    {
        await using var host = await MonitorHost.CreateAsync();
        host.Client.Answering(nameof(RecordingWhisparrClient.SetStudioMonitoredAsync), MonitorHost.Json(200, "{}"));
        host.Client.Answering(
            nameof(IWhisparrStudioActing.ReadStudioAsync),
            MonitorHost.Json(200, HeldUnmonitored),
            MonitorHost.Json(200, HeldUnmonitored));
        var studio = await SeedAsync(host);
        var progress = new RecordingJobProgress();

        await host.PostBulkAsync(BodyOf(Studios, "monitor", [studio]));
        await host.RunEnqueuedBatchAsync(progress);

        Assert.Contains(
            host.Client.Acting,
            call => call.Verb == nameof(IWhisparrStudioActing.SetStudioMonitoredAsync)
                && call.Monitored == true);
        Assert.NotEqual(JobUnitOutcome.Succeeded, Assert.Single(progress.Units).Outcome);
    }

    [Fact]
    public async Task AFlipTheInstanceThenReportsMonitoredIsSucceeded()
    {
        await using var host = await MonitorHost.CreateAsync();
        host.Client.Answering(nameof(RecordingWhisparrClient.ReadHardlinkSettingAsync), MonitorHost.Json(200, "{}"));
        host.Client.Answering(nameof(RecordingWhisparrClient.SetStudioMonitoredAsync), MonitorHost.Json(200, "{}"));
        host.Client.Answering(
            nameof(IWhisparrStudioActing.ReadStudioAsync),
            MonitorHost.Json(200, HeldUnmonitored),
            MonitorHost.Json(200, HeldMonitored));
        var studio = await SeedAsync(host);
        var progress = new RecordingJobProgress();

        await host.PostBulkAsync(BodyOf(Studios, "monitor", [studio]));
        await host.RunEnqueuedBatchAsync(progress);

        Assert.Equal(JobUnitOutcome.Succeeded, Assert.Single(progress.Units).Outcome);
    }

    // The whole ordered call log is asserted rather than a count of one verb, so a request added
    // anywhere in the sequence fails here.
    [Fact]
    public async Task AOneEntityBatchReadsTheEntityTwiceAndNoMore()
    {
        await using var host = await MonitorHost.CreateAsync();
        host.Client.Answering(nameof(RecordingWhisparrClient.ReadHardlinkSettingAsync), MonitorHost.Json(200, "{}"));
        host.Client.Answering(
            nameof(IWhisparrStudioActing.ReadStudioAsync),
            MonitorHost.Json(404, NotHeld),
            MonitorHost.Json(200, HeldMonitored));
        var studio = await SeedAsync(host);

        await host.PostBulkAsync(BodyOf(Studios, "monitor", [studio]));
        await host.RunEnqueuedBatchAsync(new RecordingJobProgress());

        Assert.Equal(
            [
                nameof(IWhisparrStudioActing.ReadStudioAsync),
                nameof(IWhisparrClient.ReadQualityProfilesAsync),
                nameof(IWhisparrClient.ReadRootFoldersAsync),
                nameof(IWhisparrStudioActing.AddMonitoredStudioAsync),
                nameof(IWhisparrStudioActing.ReadStudioAsync),
                nameof(IWhisparrReflectOwnedActing.ReadHardlinkSettingAsync),
            ],
            host.Client.Verbs);
    }

    [Fact]
    public async Task TheSingleEntityRouteIsClassifiedFromTheSameReadBack()
    {
        await using var host = await MonitorHost.CreateAsync();
        host.Client
            .Answering(
                nameof(IWhisparrStudioActing.AddMonitoredStudioAsync),
                MonitorHost.Json(201, AcceptedWithTheFieldDropped))
            .Answering(
                nameof(IWhisparrStudioActing.ReadStudioAsync),
                MonitorHost.Json(404, NotHeld),
                MonitorHost.Json(200, HeldUnmonitored));
        var studio = await SeedAsync(host);

        var view = await host.MonitorAsync(studio);

        Assert.False(view.Monitored);
        Assert.Equal(MonitorRefusalKind.InstanceDidNotReportTheChange, view.Refusal);
    }

    private static Task<int> SeedAsync(MonitorHost host)
        => host.SeedStudioAsync(MonitorHost.StoredEndpoint, MonitorHost.StudioRemoteIdValue);

    private static string BodyOf(string entityType, string verb, IReadOnlyList<int> ids)
        => string.Create(
            CultureInfo.InvariantCulture,
            $$"""
            {"entityType":"{{entityType}}","verb":"{{verb}}","scope":"futureScenes","entityIds":[{{string.Join(',', ids)}}]}
            """);
}
