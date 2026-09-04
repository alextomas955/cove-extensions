using System.Globalization;
using Cove.Core.Interfaces;
using WhisparrSync.Contracts;
using WhisparrSync.Monitoring;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Jobs;

/// <summary>
/// What one selected entity's unit is classified from: the state a read reports, never the status of
/// the write.
/// </summary>
/// <remarks>
/// The measured behaviour these cases stand over is an add answered with a created status and an
/// echo showing the monitored field dropped. On a click the browser reads the entity back and the
/// screen corrects itself; a batch has no browser, so the correction has to be in the verb.
/// </remarks>
public sealed class BulkReadBackTests
{
    private const string Studios = "studios";

    /// <summary>The studio as an instance reports it once it holds and monitors it.</summary>
    private const string HeldMonitored =
        """{"id":1,"foreignId":"44e8ac11-9ed4-42e5-a9f4-bc2c138a5a6e","monitored":true}""";

    /// <summary>The same studio, held and not monitored.</summary>
    private const string HeldUnmonitored =
        """{"id":1,"foreignId":"44e8ac11-9ed4-42e5-a9f4-bc2c138a5a6e","monitored":false}""";

    /// <summary>The add's own answer with the field dropped, which is what this generation sends.</summary>
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
        Assert.Equal(nameof(MonitorRefusalKind.InstanceRefused), unit.Message);
    }

    /// <summary>A run reporting the unit above as applied is the record a reader cannot act on.</summary>
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
        host.Client.Answering(
            nameof(IWhisparrStudioActing.ReadStudioAsync),
            MonitorHost.Json(404, NotHeld),
            MonitorHost.Json(200, HeldMonitored));
        var studio = await SeedAsync(host);
        var progress = new RecordingJobProgress();

        await host.PostBulkAsync(BodyOf(Studios, "monitor", [studio]));
        await host.RunEnqueuedBatchAsync(progress);

        Assert.Equal(JobUnitOutcome.Succeeded, Assert.Single(progress.Units).Outcome);
        Assert.Equal((1d, "1 applied, 0 refused."), Assert.Single(progress.Reports));
    }

    /// <summary>
    /// A read-back that fails leaves what the instance holds unknown, which is not evidence that the
    /// monitor took.
    /// </summary>
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

    /// <summary>
    /// An entity the instance already holds is classified from a read too, so the flip is not
    /// reported from its own status either.
    /// </summary>
    [Fact]
    public async Task AFlipOnAnEntityTheInstanceHoldsIsAlsoClassifiedFromAReadBack()
    {
        await using var host = await MonitorHost.CreateAsync();
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

    /// <summary>
    /// The read-back is ONE more request per entity, so a batch of a thousand gains a thousand reads
    /// rather than a multiple of them.
    /// </summary>
    [Fact]
    public async Task AOneEntityBatchIssuesExactlyOneMoreReadThanTheAddPathAlreadyIssued()
    {
        await using var host = await MonitorHost.CreateAsync();
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
            ],
            host.Client.Verbs);
    }

    /// <summary>
    /// The single-entity route reaches the same statement of the verb, so the click inherits the
    /// correction rather than depending on the browser's own read.
    /// </summary>
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
        Assert.Equal(MonitorRefusalKind.InstanceRefused, view.Refusal);
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
