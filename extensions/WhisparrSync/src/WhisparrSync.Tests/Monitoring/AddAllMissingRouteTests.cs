using System.Net;
using System.Net.Http.Json;
using System.Text;
using Cove.Core.Auth;
using WhisparrSync.Contracts;
using WhisparrSync.Jobs;
using WhisparrSync.Monitoring;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Monitoring;

/// <summary>
/// The add-all-missing verb over the path a user reaches: the mounted route, the refusals taken
/// before anything leaves, and the background run that offers one scene at a time.
/// </summary>
/// <remarks>
/// Driven through the shipped registration rather than by calling the handler. A handler called
/// directly agrees with a route mounted at the wrong pattern, bound to a body the browser cannot
/// send, or reachable by a caller the declaration excludes.
/// </remarks>
public sealed class AddAllMissingRouteTests
{
    private const string HeldStudio =
        """{"id":9,"foreignId":"44e8ac11-9ed4-42e5-a9f4-bc2c138a5a6e","monitored":true}""";

    private const string HeldPerformer =
        """{"id":12,"foreignId":"9f0d6f27-1f3a-4a5f-8b21-6b2d3a5f9c10","monitored":true}""";

    private const string FirstScene = "023bacff-8d1d-4f27-bac5-bdaf833f5616";
    private const string SecondScene = "3c0a6b21-9f7d-4c58-a3e2-71b0d4f5e8a9";

    /// <summary>An identifier a caller put in a body this route declares nothing for.</summary>
    private const string SmuggledScene = "ffffffff-ffff-4fff-8fff-ffffffffffff";

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AMonitoredStudioAnswersAcceptedWithAJobId()
    {
        await using var host = await HoldingHost();
        var studioId = await SeededStudio(host);

        var answered = await host.AddAllMissingAsync("studio", studioId);
        var enqueued = await ReadEnqueued(answered);

        Assert.Equal(HttpStatusCode.Accepted, answered.StatusCode);
        Assert.NotNull(enqueued.JobId);
        Assert.Equal(MonitorRefusalKind.None, enqueued.Refusal);

        var job = Assert.Single(host.Jobs.Enqueued);
        Assert.StartsWith("ext:" + host.ExtensionId + ":", job.Type, StringComparison.Ordinal);
        Assert.EndsWith(AddAllMissingJob.JobId, job.Type, StringComparison.Ordinal);
    }

    /// <summary>The performer arm reaches the same route through its own held read.</summary>
    [Fact]
    public async Task AMonitoredPerformerAnswersAcceptedWithAJobId()
    {
        await using var host = await HoldingHost();
        var performerId = await host.SeedPerformerAsync(
            MonitorHost.StoredEndpoint, MonitorHost.PerformerRemoteIdValue);

        var enqueued = await host.AddAllMissingViewAsync("performer", performerId);

        Assert.NotNull(enqueued.JobId);
        Assert.Equal(MonitorRefusalKind.None, enqueued.Refusal);
    }

    /// <summary>
    /// The older generation refuses because it registers no role, not because anything compared a
    /// version.
    /// </summary>
    /// <remarks>
    /// The capability table is the evidence. A handler asking which generation is connected would
    /// answer the same refusal here and would go on answering it after the generation gained a
    /// route.
    /// </remarks>
    [Fact]
    public async Task TheOlderGenerationRefusesFromTheAbsentRegistrationRatherThanAVersionCheck()
    {
        Assert.DoesNotContain(
            WhisparrCapability.RegisterMissingScenes,
            GenerationCapabilities.CapabilitiesOf(WhisparrGeneration.V2));

        await using var host = await MonitorHost.CreateAsync(generation: WhisparrGeneration.V2);
        var studioId = await host.SeedStudioAsync(
            MonitorHost.StoredEndpoint, MonitorHost.StudioRemoteIdValue);

        var enqueued = await host.AddAllMissingViewAsync("studio", studioId);

        Assert.Equal(MonitorRefusalKind.CapabilityAbsentOnThisGeneration, enqueued.Refusal);
        Assert.Null(enqueued.JobId);
        Assert.Empty(host.Client.Verbs);
        Assert.Empty(host.Jobs.Enqueued);
    }

    [Fact]
    public async Task AnEntityCarryingNoIdentityIsRefusedWithNothingSentOutbound()
    {
        await using var host = await HoldingHost();
        var studioId = await host.SeedStudioAsync(null, null);

        var enqueued = await host.AddAllMissingViewAsync("studio", studioId);

        Assert.Equal(MonitorRefusalKind.NoIdentityInThisNamespace, enqueued.Refusal);
        Assert.Null(enqueued.JobId);
        Assert.Empty(host.Client.Verbs);
        Assert.Empty(host.Jobs.Enqueued);
    }

    /// <summary>
    /// An entity the instance does not hold has no catalogue to add to, so nothing is sent, and the
    /// refusal names the absence rather than the instance declining.
    /// </summary>
    [Fact]
    public async Task AnEntityTheInstanceDoesNotHoldIsRefusedWithNoSceneSent()
    {
        await using var host = await MonitorHost.CreateAsync();
        var studioId = await SeededStudio(host);

        var enqueued = await host.AddAllMissingViewAsync("studio", studioId);

        Assert.Equal(MonitorRefusalKind.InstanceHoldsNoSuchEntity, enqueued.Refusal);
        Assert.Null(enqueued.JobId);
        Assert.DoesNotContain(nameof(IWhisparrMissingSceneActing.AddSceneAsync), host.Client.Verbs);
        Assert.Empty(host.Jobs.Enqueued);
    }

    /// <summary>
    /// An instance offering no quality profile is a stop taken before anything is composed.
    /// </summary>
    /// <remarks>
    /// This generation accepts a quality profile id of zero, echoes it back and then never acquires
    /// anything, so refusing here is the only guard there is.
    /// </remarks>
    [Fact]
    public async Task AnInstanceOfferingNoQualityProfileIsRefusedBeforeAnySceneIsSent()
    {
        await using var host = await HoldingHost();
        host.Client.Answering(
            nameof(IWhisparrClient.ReadQualityProfilesAsync), MonitorHost.Json(200, "[]"));
        var studioId = await SeededStudio(host);

        var enqueued = await host.AddAllMissingViewAsync("studio", studioId);

        Assert.Equal(MonitorRefusalKind.NoQualityProfile, enqueued.Refusal);
        Assert.Null(enqueued.JobId);
        Assert.DoesNotContain(nameof(IWhisparrMissingSceneActing.AddSceneAsync), host.Client.Verbs);
        Assert.Empty(host.Jobs.Enqueued);
    }

    [Fact]
    public async Task AnInstanceOfferingNoRootFolderIsRefusedBeforeAnySceneIsSent()
    {
        await using var host = await HoldingHost();
        host.Client.Answering(
            nameof(IWhisparrClient.ReadRootFoldersAsync), MonitorHost.Json(200, "[]"));
        var studioId = await SeededStudio(host);

        var enqueued = await host.AddAllMissingViewAsync("studio", studioId);

        Assert.Equal(MonitorRefusalKind.NoRootFolder, enqueued.Refusal);
        Assert.Null(enqueued.JobId);
        Assert.DoesNotContain(nameof(IWhisparrMissingSceneActing.AddSceneAsync), host.Client.Verbs);
        Assert.Empty(host.Jobs.Enqueued);
    }

    /// <summary>
    /// The route creates items in the reader's Whisparr, so a caller who cannot configure the
    /// extension is out of reach of it.
    /// </summary>
    [Fact]
    public async Task ACallerHoldingOnlyReadIsRefused()
    {
        await using var host = await MonitorHost.CreateAsync(
            FakePrincipalAccessor.WithPermissions(Permissions.VideosRead));

        var answered = await host.AddAllMissingAsync("studio", 1);

        Assert.Equal(HttpStatusCode.Forbidden, answered.StatusCode);
        Assert.Empty(host.Client.Verbs);
    }

    [Fact]
    public async Task AKindTheRouteCannotParseIsABadRequest()
    {
        await using var host = await HoldingHost();

        var answered = await host.AddAllMissingAsync("gallery", 1);

        Assert.Equal(HttpStatusCode.BadRequest, answered.StatusCode);
        Assert.Empty(host.Client.Verbs);
    }

    /// <summary>
    /// Every identified scene is offered once, and the catalogue refresh follows.
    /// </summary>
    /// <remarks>
    /// The refresh names the instance's OWN row id for the entity, read from its own record, so no
    /// value a caller could supply reaches it.
    /// </remarks>
    [Fact]
    public async Task TheEnqueuedRunOffersEachIdentifiedSceneOnceAndRefreshesTheCatalogueOnce()
    {
        await using var host = await HoldingHost();
        host.Client.Answering(
            nameof(IWhisparrMissingSceneActing.AddSceneAsync), MonitorHost.Json(201, """{"id":31}"""));
        var studioId = await SeededStudio(host);
        await host.SeedStudioSceneAsync(studioId, MonitorHost.StoredEndpoint, FirstScene);
        await host.SeedStudioSceneAsync(studioId, MonitorHost.StoredEndpoint, SecondScene);
        await host.SeedStudioSceneAsync(studioId, null, null);

        await host.AddAllMissingViewAsync("studio", studioId);
        var progress = new RecordingJobProgress();
        await host.Jobs.RunLastAsync(progress, TestCt);

        Assert.Equal(
            [FirstScene, SecondScene],
            host.Client.Acting
                .Where(call => call.Verb == nameof(IWhisparrMissingSceneActing.AddSceneAsync))
                .Select(call => call.ForeignId));
        var refresh = Assert.Single(
            host.Client.Acting,
            call => call.Verb == nameof(IWhisparrMissingSceneActing.RefreshCatalogueAsync));
        Assert.Equal(9, refresh.EntityId);
        Assert.Equal(WhisparrEntityKind.Studio, refresh.Kind);
        Assert.Contains(progress.Reports, report => report.SubTask is not null);
    }

    /// <summary>
    /// An identifier a caller put in a body reaches nothing.
    /// </summary>
    /// <remarks>
    /// The route declares no request record at all, so the value has nowhere to bind. This asserts
    /// what LEFT rather than what bound: every identifier the run offered came from the library's
    /// own rows.
    /// </remarks>
    [Fact]
    public async Task AnIdentifierACallerPutInABodyReachesNoOutboundRequest()
    {
        await using var host = await HoldingHost();
        host.Client.Answering(
            nameof(IWhisparrMissingSceneActing.AddSceneAsync), MonitorHost.Json(201, """{"id":31}"""));
        var studioId = await SeededStudio(host);
        await host.SeedStudioSceneAsync(studioId, MonitorHost.StoredEndpoint, FirstScene);

        using var smuggled = new StringContent(
            $$"""{"foreignId":"{{SmuggledScene}}","entityId":4242}""",
            Encoding.UTF8,
            "application/json");
        var answered = await host.Http.PostAsync(
            host.RouteFor("studio", studioId, "add-all-missing"), smuggled, TestCt);
        answered.EnsureSuccessStatusCode();
        await host.Jobs.RunLastAsync(new RecordingJobProgress(), TestCt);

        Assert.All(
            host.Client.Acting,
            call => Assert.NotEqual(SmuggledScene, call.ForeignId));
        Assert.All(host.Client.Acting, call => Assert.NotEqual(4242, call.EntityId));
    }

    /// <summary>An entity naming no identified scene registers nothing and refreshes nothing.</summary>
    [Fact]
    public async Task AnEntityNamingNoIdentifiedSceneOffersNothingAndRefreshesNothing()
    {
        await using var host = await HoldingHost();
        var studioId = await SeededStudio(host);

        await host.AddAllMissingViewAsync("studio", studioId);
        await host.Jobs.RunLastAsync(new RecordingJobProgress(), TestCt);

        Assert.DoesNotContain(nameof(IWhisparrMissingSceneActing.AddSceneAsync), host.Client.Verbs);
        Assert.DoesNotContain(
            nameof(IWhisparrMissingSceneActing.RefreshCatalogueAsync), host.Client.Verbs);
    }

    private static async Task<MonitorHost> HoldingHost()
    {
        var host = await MonitorHost.CreateAsync();
        host.Client
            .Answering(nameof(IWhisparrStudioActing.ReadStudioAsync), MonitorHost.Json(200, HeldStudio))
            .Answering(
                nameof(IWhisparrPerformerActing.ReadPerformerAsync),
                MonitorHost.Json(200, HeldPerformer));
        return host;
    }

    private static Task<int> SeededStudio(MonitorHost host)
        => host.SeedStudioAsync(MonitorHost.StoredEndpoint, MonitorHost.StudioRemoteIdValue);

    private static async Task<AddAllMissingEnqueued> ReadEnqueued(HttpResponseMessage answered)
        => (await answered.Content.ReadFromJsonAsync<AddAllMissingEnqueued>(TestCt))!;
}
