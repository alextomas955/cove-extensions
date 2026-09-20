using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cove.Core.Auth;
using WhisparrSync.Contracts;
using WhisparrSync.Jobs;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Missing;

public sealed class MissingBulkRouteTests
{
    private const string BulkVerb = "missing/bulk-monitor";

    private const string FirstScene = "023bacff-8d1d-4f27-bac5-bdaf833f5616";
    private const string SecondScene = "3c0a6b21-9f7d-4c58-a3e2-71b0d4f5e8a9";

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    private static string Ticking(params string[] providerSceneIds)
        => JsonSerializer.Serialize(new { providerSceneIds });

    private static Task<int> StudioIn(MonitorHost host)
        => host.SeedStudioAsync(MonitorHost.StoredEndpoint, MonitorHost.StudioRemoteIdValue);

    private static async Task<MissingBulkEnqueued> ReadEnqueuedAsync(HttpResponseMessage answered)
    {
        answered.EnsureSuccessStatusCode();
        return (await answered.Content.ReadFromJsonAsync<MissingBulkEnqueued>(TestCt))!;
    }

    [Fact]
    public async Task TheRouteAnswersAJobIdWithoutWaitingForTheRun()
    {
        await using var host = await MonitorHost.CreateAsync();
        var studioId = await StudioIn(host);

        var enqueued = await ReadEnqueuedAsync(
            await host.PostRawAsync(
                "studio", studioId, BulkVerb, Ticking(FirstScene, SecondScene)));

        Assert.NotNull(enqueued.JobId);
        Assert.Equal(MissingRefusalKind.None, enqueued.Refusal);
        Assert.Empty(host.Client.Acting);
    }

    [Fact]
    public async Task TheEnqueuedTypeCarriesTheExtensionsOwnPrefix()
    {
        await using var host = await MonitorHost.CreateAsync();
        var studioId = await StudioIn(host);

        await ReadEnqueuedAsync(
            await host.PostRawAsync("studio", studioId, BulkVerb, Ticking(FirstScene)));

        var job = Assert.Single(host.Jobs.Enqueued);
        Assert.StartsWith("ext:" + host.ExtensionId + ":", job.Type, StringComparison.Ordinal);
        Assert.EndsWith(MissingBulkJob.JobId, job.Type, StringComparison.Ordinal);
        Assert.True(job.Exclusive);
    }

    [Fact]
    public async Task AWholePageOfTicksIsOneJob()
    {
        await using var host = await MonitorHost.CreateAsync();
        var studioId = await StudioIn(host);

        var page = Enumerable.Range(0, 40)
            .Select(index => $"023bacff-8d1d-4f27-bac5-bdaf833f{index:D5}")
            .ToArray();

        await ReadEnqueuedAsync(
            await host.PostRawAsync("studio", studioId, BulkVerb, Ticking(page)));

        Assert.Single(host.Jobs.Enqueued);
    }

    [Fact]
    public async Task ARefusalBeforeTheRunAnswersNoJobIdAndStatesWhy()
    {
        await using var host = await MonitorHost.CreateAsync(apiKey: null);
        var studioId = await StudioIn(host);

        var enqueued = await ReadEnqueuedAsync(
            await host.PostRawAsync("studio", studioId, BulkVerb, Ticking(FirstScene)));

        Assert.Null(enqueued.JobId);
        Assert.Equal(MissingRefusalKind.NoInstanceConnected, enqueued.Refusal);
        Assert.Empty(host.Jobs.Enqueued);
    }

    [Fact]
    public async Task AGenerationRegisteringNoSceneAddRefusesAndEnqueuesNothing()
    {
        Assert.DoesNotContain(
            WhisparrCapability.RegisterMissingScenes,
            GenerationCapabilities.CapabilitiesOf(WhisparrGeneration.V2));

        await using var host = await MonitorHost.CreateAsync(generation: WhisparrGeneration.V2);
        var studioId = await StudioIn(host);

        var enqueued = await ReadEnqueuedAsync(
            await host.PostRawAsync("studio", studioId, BulkVerb, Ticking(FirstScene)));

        Assert.Null(enqueued.JobId);
        Assert.Equal(MissingRefusalKind.WhisparrKeepsNoSceneRecords, enqueued.Refusal);
        Assert.Empty(host.Jobs.Enqueued);
    }

    [Fact]
    public async Task ABodyThisRouteCannotExpressIsRefusedBeforeAnythingIsEnqueued()
    {
        await using var host = await MonitorHost.CreateAsync();
        var studioId = await StudioIn(host);

        string[] refused =
        [
            Ticking(),
            Ticking(" "),
            Ticking(new string('a', 129)),
            Ticking([.. Enumerable.Range(0, 41).Select(index => $"scene-{index}")]),
        ];

        foreach (var body in refused)
        {
            var answered = await host.PostRawAsync("studio", studioId, BulkVerb, body);
            Assert.Equal(HttpStatusCode.BadRequest, answered.StatusCode);
        }

        Assert.Empty(host.Jobs.Enqueued);
    }

    [Fact]
    public async Task TheRouteIsNotReachableBelowTheConfigureTier()
    {
        await using var host = await MonitorHost.CreateAsync(
            FakePrincipalAccessor.WithPermissions(Permissions.VideosRead));
        var studioId = await StudioIn(host);

        var answered = await host.PostRawAsync(
            "studio", studioId, BulkVerb, Ticking(FirstScene));

        Assert.Equal(HttpStatusCode.Forbidden, answered.StatusCode);
        Assert.Empty(host.Jobs.Enqueued);
    }

    [Fact]
    public async Task TheRunOffersEachTickedSceneOnceAndReachesNoGrabbingVerb()
    {
        await using var host = await MonitorHost.CreateAsync();
        host.Client.Answering(nameof(RecordingWhisparrClient.AddSceneAsync), MonitorHost.Json(200, "{}"));
        var studioId = await StudioIn(host);

        await ReadEnqueuedAsync(
            await host.PostRawAsync(
                "studio", studioId, BulkVerb, Ticking(FirstScene, SecondScene, FirstScene)));

        await host.Jobs.RunLastAsync(new RecordingJobProgress(), TestCt);

        var offered = host.Client.Acting
            .Where(call => call.Verb == nameof(IWhisparrMissingSceneActing.AddSceneAsync))
            .Select(call => call.ForeignId)
            .ToList();

        Assert.Equal([FirstScene, SecondScene], offered);
        Assert.NotEmpty(host.Client.Verbs);
        Assert.All(
            host.Client.Verbs,
            sent => Assert.NotEqual(
                WhisparrVerbClass.Grab, Invariants.OutboundSeam.VerbClassByMember[sent]));
    }

    [Fact]
    public async Task TheRunActsOnTheTickedScenesAndOnNoOther()
    {
        await using var host = await MonitorHost.CreateAsync();
        host.Client.Answering(nameof(RecordingWhisparrClient.AddSceneAsync), MonitorHost.Json(200, "{}"));
        var studioId = await StudioIn(host);

        await ReadEnqueuedAsync(
            await host.PostRawAsync("studio", studioId, BulkVerb, Ticking(FirstScene, SecondScene)));

        await host.Jobs.RunLastAsync(new RecordingJobProgress(), TestCt);

        var reached = host.Client.Acting
            .Where(call => call.ForeignId is not null)
            .Select(call => call.ForeignId)
            .ToList();

        Assert.Equal([FirstScene, SecondScene], reached);
    }

    [Fact]
    public async Task TheRunsOneLineReportsCountsAndNamesNoScene()
    {
        var progress = new RecordingJobProgress();
        await using var host = await MonitorHost.CreateAsync();
        host.Client.Answering(nameof(RecordingWhisparrClient.AddSceneAsync), MonitorHost.Json(200, "{}"));
        var studioId = await StudioIn(host);

        await ReadEnqueuedAsync(
            await host.PostRawAsync("studio", studioId, BulkVerb, Ticking(FirstScene, SecondScene)));
        await host.Jobs.RunLastAsync(progress, TestCt);

        var reported = string.Join('\n', progress.Reports.Select(report => report.SubTask));

        Assert.Contains("2 marked wanted", reported, StringComparison.Ordinal);
        Assert.DoesNotContain(FirstScene, reported, StringComparison.Ordinal);
        Assert.DoesNotContain(SecondScene, reported, StringComparison.Ordinal);
    }
}
