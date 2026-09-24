using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using Cove.Core.Auth;
using WhisparrSync.Contracts;
using WhisparrSync.Jobs;
using WhisparrSync.Scene;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Api;

// Driven through the shipped registration rather than by calling the handler. A handler called
// directly agrees with a route mounted at the wrong pattern, bound to a body the browser cannot
// send, or reachable by a caller the declaration excludes.
public sealed class SyncLibraryRouteTests
{
    private const string AcceptedFixture = "whisparr-v3-3.3.8.1097-scene-add-accepted.json";

    private const string AlreadyHeldFixture = "whisparr-v3-3.3.8.1097-scene-add-already-held.json";

    // The instance's own numeric scene id, as the accepted add fixture names it.
    private const int AcceptedSceneId = 569;

    private const int HeldSceneId = 412;

    private const string FirstScene = "023bacff-8d1d-4f27-bac5-bdaf833f5616";
    private const string SecondScene = "3c0a6b21-9f7d-4c58-a3e2-71b0d4f5e8a9";

    private const string LibraryFolder = "/library/one";

    private const string FirstFolder = "/library/one";

    private const string SecondFolder = "/library/two";

    private const string LinksIntoPlace = """{"copyUsingHardlinks":true}""";

    private const string CopiesInstead = """{"copyUsingHardlinks":false}""";

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    // The one non-exclusive enqueue in this extension. A library-wide run enqueued exclusive holds
    // Cove's own scans and refreshes behind it for as long as the library is large.
    [Fact]
    public async Task ARunIsEnqueuedNonExclusiveAndAnswersItsJobId()
    {
        await using var host = await HoldingHost();

        var enqueued = await RunAsync(host);

        Assert.Equal(SyncRefusalKind.None, enqueued.Refusal);
        Assert.NotNull(enqueued.JobId);

        var job = Assert.Single(host.Jobs.Enqueued);
        Assert.False(job.Exclusive);
        Assert.StartsWith("ext:" + host.ExtensionId + ":", job.Type, StringComparison.Ordinal);
        Assert.EndsWith(SyncLibraryJob.JobId, job.Type, StringComparison.Ordinal);
        // One title stands over both passes, and which one a run takes is settled after the enqueue,
        // so it names neither generation's noun.
        Assert.NotEmpty(job.Description);
        Assert.DoesNotContain("scene", job.Description, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("site", job.Description, StringComparison.OrdinalIgnoreCase);
    }

    // In-flight is read off the host's job list rather than a flag of this product's, so it clears
    // when the run ends and after a process restart. The refusal carries the running job's id.
    [Fact]
    public async Task ASecondRunIsRefusedByNameWhileTheFirstIsStillInFlight()
    {
        await using var host = await HoldingHost();

        var first = await RunAsync(host);
        var second = await RunAsync(host);

        Assert.Equal(SyncRefusalKind.AlreadyRunning, second.Refusal);
        Assert.Equal(first.JobId, second.JobId);
        Assert.Single(host.Jobs.Enqueued);
    }

    // The route declares the configure tier and the handler re-checks it, because the host's
    // permission filter is inert on a minimal-API endpoint.
    [Fact]
    public async Task ACallerWithoutTheConfigureTierIsRefusedBeforeAnythingIsEnqueued()
    {
        await using var host = await MonitorHost.CreateAsync(
            FakePrincipalAccessor.WithPermissions(Permissions.VideosRead));

        var answered = await PostAsync(host, alsoMonitor: false);

        Assert.Equal(HttpStatusCode.Forbidden, answered.StatusCode);
        Assert.Empty(host.Jobs.Enqueued);
        Assert.Empty(host.Client.Verbs);
    }

    [Fact]
    public async Task WithNoInstanceConfiguredNothingIsEnqueued()
    {
        await using var host = await MonitorHost.CreateAsync(apiKey: null);

        var enqueued = await RunAsync(host);

        Assert.Equal(SyncRefusalKind.NoInstanceConnected, enqueued.Refusal);
        Assert.Null(enqueued.JobId);
        Assert.Empty(host.Jobs.Enqueued);
    }

    // Whisparr v2 is aimed at its own pass from the roles the target obtains, not from a version
    // check. It registers no scene-status read and does register the site add, so the run is a site
    // pass. A target obtaining neither role is what the keeps-no-scene-records refusal is for.
    [Fact]
    public async Task V2IsAimedAtItsOwnPassRatherThanRefused()
    {
        await using var host = await MonitorHost.CreateAsync(generation: WhisparrGeneration.V2);

        var enqueued = await RunAsync(host);

        Assert.Equal(SyncRefusalKind.None, enqueued.Refusal);
        Assert.NotNull(enqueued.JobId);
        Assert.Single(host.Jobs.Enqueued);
    }

    // The seeded scene carrying no identifier in the connected namespace is not offered, because
    // there is nothing to name it by. The verb log covers every verb this product can issue, not
    // only the ones one interface declares.
    [Fact]
    public async Task TheRunOffersEveryIdentifiedSceneOnceAndAsksForNoAcquisition()
    {
        await using var host = await HoldingHost();
        var studioId = await host.SeedStudioAsync(null, null);
        await host.SeedStudioSceneAsync(studioId, MonitorHost.StoredEndpoint, FirstScene);
        await host.SeedStudioSceneAsync(studioId, MonitorHost.StoredEndpoint, SecondScene);
        await host.SeedStudioSceneAsync(studioId, null, null);

        await RunAsync(host);
        var progress = new RecordingJobProgress();
        await host.RunEnqueuedBatchAsync(progress);

        Assert.Equal(
            [FirstScene, SecondScene],
            host.Client.Acting
                .Where(call => call.Verb == nameof(IWhisparrMissingSceneActing.AddSceneAsync))
                .Select(call => call.ForeignId));
        Assert.Equal([2], progress.DeclaredUnitCounts);
        Assert.All(
            host.Client.Verbs,
            verb => Assert.DoesNotContain("Search", verb, StringComparison.Ordinal));
        Assert.All(
            host.Client.Verbs,
            verb => Assert.DoesNotContain("Grab", verb, StringComparison.Ordinal));
    }

    // The numeric id is not an identifier this product holds. It comes off the accepted add's
    // answer, or off one read of the scene where the instance already held it, so an already-held
    // scene costs one extra request and a just-registered one costs none.
    [Fact]
    public async Task WithMonitoringOnBothANewSceneAndOneAlreadyHeldAreMonitoredByTheInstancesOwnId()
    {
        await using var host = await HoldingHost();
        host.Client
            .Answering(
                nameof(IWhisparrMissingSceneActing.AddSceneAsync),
                MonitorHost.Json(201, ProbeFixtures.Read(AcceptedFixture)),
                MonitorHost.Json(400, ProbeFixtures.Read(AlreadyHeldFixture)))
            .Answering(
                nameof(IWhisparrSceneStatusReading.ReadSceneByRemoteIdAsync),
                MonitorHost.Json(200, HeldRow))
            .Answering(nameof(IWhisparrSceneMonitorActing.SetSceneMonitoredAsync), MonitorHost.Json(202, "{}"));

        var studioId = await host.SeedStudioAsync(null, null);
        await host.SeedStudioSceneAsync(studioId, MonitorHost.StoredEndpoint, FirstScene);
        await host.SeedStudioSceneAsync(studioId, MonitorHost.StoredEndpoint, SecondScene);

        await RunAsync(host, alsoMonitor: true);
        await host.RunEnqueuedBatchAsync(new RecordingJobProgress());

        var monitored = host.Client.Acting
            .Where(call => call.Verb == nameof(IWhisparrSceneMonitorActing.SetSceneMonitoredAsync))
            .ToList();

        Assert.Equal([AcceptedSceneId, HeldSceneId], monitored.Select(call => call.EntityId));
        Assert.All(monitored, call => Assert.True(call.Monitored));
    }

    [Fact]
    public async Task WithMonitoringOffNoFlagIsSetOnAnything()
    {
        await using var host = await HoldingHost();
        var studioId = await host.SeedStudioAsync(null, null);
        await host.SeedStudioSceneAsync(studioId, MonitorHost.StoredEndpoint, FirstScene);

        await RunAsync(host, alsoMonitor: false);
        await host.RunEnqueuedBatchAsync(new RecordingJobProgress());

        Assert.DoesNotContain(
            nameof(IWhisparrSceneMonitorActing.SetSceneMonitoredAsync), host.Client.Verbs);
    }

    // One row of the shape the per-scene read answers for a scene the instance holds.
    private static string HeldRow
        => string.Create(
            CultureInfo.InvariantCulture,
            $$"""[{"id":{{HeldSceneId}},"foreignId":"{{SecondScene}}","monitored":false}]""");

    // A host whose instance takes every scene it is offered.
    // Registering a scene and linking its file are one gesture on this generation: a run that
    // registered and stopped leaves every entry reading as missing while the reader owns the file.
    [Fact]
    public async Task TheRunLinksTheFilesItOwnsOnceItHasRegisteredThem()
    {
        await using var host = await LinkingHost();
        var studioId = await host.SeedStudioAsync(null, null);
        var videoId = await host.SeedStudioSceneAsync(studioId, MonitorHost.StoredEndpoint, FirstScene);
        await host.SeedSceneFileAsync(videoId, LibraryFolder);

        await RunAsync(host);
        await host.RunEnqueuedBatchAsync(new RecordingJobProgress());

        // The file the library owns is read, and the entry goes out addressed to the scene the run
        // registered rather than to anything the instance matched.
        Assert.Contains(
            host.Client.Acting,
            call => call.Verb == nameof(IWhisparrOwnedFileReading.ReadFileAsync));
        Assert.Contains(
            host.Client.Acting,
            call => call.Verb == nameof(RecordingWhisparrCore.AttachOwnedFilesAsync));
    }

    // The setting is the instance's, and with it off a matched file would be copied in full rather
    // than linked. The registering half still runs.
    [Fact]
    public async Task WithTheHardLinkSettingOffNothingIsLinkedAndTheScenesAreStillRegistered()
    {
        await using var host = await LinkingHost(CopiesInstead);
        var studioId = await host.SeedStudioAsync(null, null);
        var videoId = await host.SeedStudioSceneAsync(studioId, MonitorHost.StoredEndpoint, FirstScene);
        await host.SeedSceneFileAsync(videoId, LibraryFolder);

        await RunAsync(host);
        await host.RunEnqueuedBatchAsync(new RecordingJobProgress());

        Assert.Equal(
            [FirstScene],
            host.Client.Acting
                .Where(call => call.Verb == nameof(IWhisparrMissingSceneActing.AddSceneAsync))
                .Select(call => call.ForeignId));
        Assert.DoesNotContain(
            host.Client.Acting,
            call => call.Verb == nameof(RecordingWhisparrCore.AttachOwnedFilesAsync));
    }

    // The linking follows the walk out of each folder rather than the whole registration pass, so
    // a run stopped part way has linked the folders it already left.
    [Fact]
    public async Task EachFolderIsLinkedAsTheWalkLeavesItRatherThanAfterEveryScene()
    {
        await using var host = await LinkingHost();
        var studioId = await host.SeedStudioAsync(null, null);
        var first = await host.SeedStudioSceneAsync(studioId, MonitorHost.StoredEndpoint, FirstScene);
        var second = await host.SeedStudioSceneAsync(studioId, MonitorHost.StoredEndpoint, SecondScene);
        await host.SeedSceneFileAsync(first, FirstFolder);
        await host.SeedSceneFileAsync(second, SecondFolder);

        await RunAsync(host);
        await host.RunEnqueuedBatchAsync(new RecordingJobProgress());

        // One attach per folder the walk left: a folder attached twice would be one it re-entered,
        // and a folder missing would be one it left without linking.
        Assert.Equal(
            2,
            host.Client.Acting.Count(
                call => call.Verb == nameof(RecordingWhisparrCore.AttachOwnedFilesAsync)));
    }

    private static async Task<MonitorHost> HoldingHost()
    {
        var host = await MonitorHost.CreateAsync();
        host.Client
            .Answering(
                nameof(IWhisparrMissingSceneActing.AddSceneAsync),
                MonitorHost.Json(201, ProbeFixtures.Read(AcceptedFixture)))
            .AnsweringThatLinkingWouldCopy();
        return host;
    }

    private static async Task<MonitorHost> LinkingHost(string setting = LinksIntoPlace)
    {
        var host = await MonitorHost.CreateAsync();
        host.Client
            .Answering(
                nameof(IWhisparrMissingSceneActing.AddSceneAsync),
                MonitorHost.Json(201, ProbeFixtures.Read(AcceptedFixture)))
            .Answering(
                nameof(IWhisparrReflectOwnedActing.ReadHardlinkSettingAsync),
                MonitorHost.Json(200, setting))
            .Answering(
                nameof(RecordingWhisparrCore.AttachOwnedFilesAsync),
                MonitorHost.Json(200, "{}"));
        return host;
    }

    private static async Task<SyncEnqueued> RunAsync(MonitorHost host, bool alsoMonitor = false)
    {
        var answered = await PostAsync(host, alsoMonitor);
        answered.EnsureSuccessStatusCode();
        return (await answered.Content.ReadFromJsonAsync<SyncEnqueued>(TestCt))!;
    }

    private static async Task<HttpResponseMessage> PostAsync(MonitorHost host, bool alsoMonitor)
    {
        using var content = new StringContent(
            $$"""{"alsoMonitor":{{(alsoMonitor ? "true" : "false")}}}""",
            Encoding.UTF8,
            "application/json");

        return await host.Http.PostAsync(
            "/api/extensions/" + host.ExtensionId + "/sync/run", content, TestCt);
    }
}
