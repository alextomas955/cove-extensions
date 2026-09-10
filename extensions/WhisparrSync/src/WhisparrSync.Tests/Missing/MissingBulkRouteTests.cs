using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cove.Core.Auth;
using WhisparrSync.Contracts;
using WhisparrSync.Jobs;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Missing;

/// <summary>
/// The bulk marking route: what it answers, what it enqueues, and that it waits for none of it.
/// </summary>
/// <remarks>
/// Driven through the shipped registration rather than by calling the handler. A handler called
/// directly agrees with a route mounted at the wrong pattern, bound to a body the browser cannot
/// send, or reachable by a caller the declaration excludes.
/// </remarks>
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

    /// <summary>
    /// The route answers a job id without waiting for the run, against a run that never completes.
    /// </summary>
    /// <remarks>
    /// The job service records the work rather than starting it, so an answer that arrives at all is
    /// an answer that did not wait: a route awaiting the run would never return here.
    /// </remarks>
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

    /// <summary>The enqueued type carries this extension's own prefix and the run's own id.</summary>
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

    /// <summary>One job for the selection rather than one per scene.</summary>
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

    /// <summary>
    /// A refusal taken before the run answers no job id and states why, and enqueues nothing.
    /// </summary>
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

    /// <summary>
    /// A generation registering no scene add refuses from the absent registration.
    /// </summary>
    /// <remarks>
    /// The capability table is the evidence. A handler asking which generation is connected would
    /// answer the same refusal and would go on answering it after the generation gained a route.
    /// </remarks>
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

    /// <summary>A body this route cannot express is refused, and nothing is enqueued.</summary>
    /// <remarks>
    /// The route names the Cove entity and the ticked scenes are the only thing the body carries, so
    /// a body naming none says nothing. More than one page of them is a body no page of this surface
    /// can produce, and an identifier outside the bound reaches an outbound body.
    /// </remarks>
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

    /// <summary>The route sits at the configure tier.</summary>
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

    /// <summary>The run marks each ticked scene once and asks the instance to search for none.</summary>
    /// <remarks>
    /// Driven to COMPLETION: the route only enqueues, so the requests that would acquire are the ones
    /// the run makes rather than the ones the route does. Repeats are dropped before any request,
    /// because a selection can carry one identifier twice.
    /// </remarks>
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

    /// <summary>The run acts on the ticked scenes and on no other scene.</summary>
    /// <remarks>
    /// The assertion is the set of identifiers that reached the instance, read at the recording
    /// client. A run's own counts agree with a run that offered a different two scenes, and the set
    /// is what the tab promises a reader.
    /// </remarks>
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

    /// <summary>The run's one line reports counts and names no scene.</summary>
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
