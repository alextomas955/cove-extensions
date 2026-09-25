using System.Net.Http.Json;
using System.Text;
using Cove.Core.Interfaces;
using WhisparrSync.Contracts;
using WhisparrSync.Linking;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Jobs;

// The library the instance refuses today: two entities whose files sit in one folder. The instance
// holds one entity per folder, so without a folder of its own the second entity registers nowhere.
//
// Both roots are spelled the same in Cove and on the instance, so what the entity is registered at
// is the tree's own doing rather than the addressing chain's.
public sealed class RegisteredInItsOwnFolderTests
{
    private const string AcceptedFixture = "whisparr-v3-3.3.8.1097-scene-add-accepted.json";

    private const string CoveRoot = "/config/library";

    private const string SharedFolder = CoveRoot + "/every scene";

    private const string FirstScene = "023bacff-8d1d-4f27-bac5-bdaf833f5616";

    private const string SecondScene = "3c0a6b21-9f7d-4c58-a3e2-71b0d4f5e8a9";

    private const string SiteRemoteId = "a30bc641-6afe-4c80-9c73-ecb68104a68d";

    // The namespace v2 identifies a studio in.
    private const string V2Endpoint = "https://theporndb.net/graphql";

    private const string LinksIntoPlace = """{"copyUsingHardlinks":true}""";

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    [Fact]
    public async Task EachSceneSharingOneLibraryFolderRegistersAtAFolderOfItsOwn()
    {
        await using var host = await SceneHostAsync();
        var studioId = await host.SeedStudioAsync(null, null);
        await SeedSceneAsync(host, studioId, FirstScene);
        await SeedSceneAsync(host, studioId, SecondScene);

        await RunAsync(host);

        Assert.Equal(
            [FolderOf(FirstScene), FolderOf(SecondScene)],
            Adds(host).Select(call => call.Defaults!.EntityFolderPath).Order(StringComparer.Ordinal));
    }

    // A link adds a name and not a file: one link per library file, each pointing at the file the
    // library already held.
    [Fact]
    public async Task EachScenesOwnFileIsReachableInThatFolderUnderASecondName()
    {
        await using var host = await SceneHostAsync();
        var studioId = await host.SeedStudioAsync(null, null);
        var first = await SeedSceneAsync(host, studioId, FirstScene);
        var second = await SeedSceneAsync(host, studioId, SecondScene);

        await RunAsync(host);

        Assert.Equal(
            [first, second],
            host.TreeLinks.Links.Values.Order(StringComparer.Ordinal));
        Assert.Single(host.TreeLinks.NamesIn(FolderOf(FirstScene)));
        Assert.Single(host.TreeLinks.NamesIn(FolderOf(SecondScene)));
    }

    [Fact]
    public async Task TheHostsOwnScanIsKeptOutOfTheWholeTree()
    {
        await using var host = await SceneHostAsync();
        var studioId = await host.SeedStudioAsync(null, null);
        await SeedSceneAsync(host, studioId, FirstScene);
        await SeedSceneAsync(host, studioId, SecondScene);

        await RunAsync(host);

        Assert.Equal(
            "*",
            Assert.Contains(
                TreePathGuard.TreeRootUnder(CoveRoot, WhisparrGeneration.V3)!,
                host.TreeLinks.IgnoreFiles));
    }

    // A second run over an unchanged library writes nothing: the name each link carries is the
    // identity of the file it points at, so the first run's names are the ones this one composes.
    [Fact]
    public async Task ASecondRunOverTheSameLibraryAddsNoSecondName()
    {
        await using var host = await SceneHostAsync();
        var studioId = await host.SeedStudioAsync(null, null);
        await SeedSceneAsync(host, studioId, FirstScene);

        await RunAsync(host);
        var afterOne = host.TreeLinks.Links.Keys.Order(StringComparer.Ordinal).ToList();
        await RunAsync(host);

        Assert.Single(afterOne);
        Assert.Equal(afterOne, host.TreeLinks.Links.Keys.Order(StringComparer.Ordinal));
    }

    // The entry goes out against the entity the run registered, addressed inside that entity's own
    // folder, so the instance is never asked to parse a scene out of a file name.
    [Fact]
    public async Task TheScenesFileIsAttachedFromItsOwnFolderInTheSameRun()
    {
        await using var host = await SceneHostAsync();
        var studioId = await host.SeedStudioAsync(null, null);
        await SeedSceneAsync(host, studioId, FirstScene);

        await RunAsync(host);

        var read = Assert.Single(
            host.Client.Acting,
            call => call.Verb == nameof(IWhisparrOwnedFileReading.ReadFileAsync));
        Assert.StartsWith(FolderOf(FirstScene) + "/", read.Folder!, StringComparison.Ordinal);
        Assert.Contains(
            host.Client.Acting,
            call => call.Verb == nameof(RecordingWhisparrCore.AttachOwnedFilesAsync));
    }

    // The other generation registers a studio rather than a scene, and the folder reaches its add
    // through the one place an add body's root is composed.
    [Fact]
    public async Task TheSitePassRegistersAStudioAtAFolderOfItsOwnAndLinksItsFiles()
    {
        await using var host = await SiteHostAsync();
        var studioId = await host.SeedStudioAsync(V2Endpoint, SiteRemoteId);
        var seeded = await host.SeedStudioFileAsync(studioId, SharedFolder);

        await RunAsync(host);

        var add = Assert.Single(
            host.Client.Acting,
            call => call.Verb == nameof(IWhisparrSiteRegistrationActing.RegisterSiteAsync));
        Assert.Equal(
            FolderIn(WhisparrGeneration.V2, SiteRemoteId), add.Defaults!.EntityFolderPath);
        Assert.Equal([seeded], host.TreeLinks.Links.Values);
    }

    // A library folder holds whatever else the reader keeps beside the entity's files; the entity's
    // own folder holds its files and nothing else, so the instance is asked about that instead.
    [Fact]
    public async Task AnEntitysOwnRunListsItsOwnFolderRatherThanItsLibraryFolders()
    {
        await using var host = await SiteHostAsync();
        host.Client.Answering(
            nameof(RecordingWhisparrCore.ListImportableFilesAsync), MonitorHost.Json(200, "[]"));
        var studioId = await host.SeedStudioAsync(V2Endpoint, SiteRemoteId);
        var seeded = await host.SeedStudioFileAsync(studioId, SharedFolder);
        var entityFolder = FolderIn(WhisparrGeneration.V2, SiteRemoteId);
        Assert.Equal(LinkOutcome.Linked, host.TreeLinks.Link(seeded, entityFolder + "/a.mp4"));

        Assert.NotNull((await host.ReflectOwnedViewAsync("studio", studioId)).JobId);
        await host.Jobs.RunLastAsync(new RecordingJobProgress(), TestCt);

        Assert.Equal(
            [entityFolder],
            host.Client.Acting
                .Where(call => call.Verb == nameof(RecordingWhisparrCore.ListImportableFilesAsync))
                .Select(call => call.Folder));
    }

    private static string FolderOf(string remoteId)
        => FolderIn(WhisparrGeneration.V3, remoteId);

    private static string FolderIn(WhisparrGeneration generation, string remoteId)
        => TreePathGuard.EntityFolderIn(
            TreePathGuard.TreeRootUnder(CoveRoot, generation)!, remoteId)!;

    private static IEnumerable<ActingCall> Adds(MonitorHost host)
        => host.Client.Acting
            .Where(call => call.Verb == nameof(IWhisparrMissingSceneActing.AddSceneAsync));

    // Its own file, in the folder every other scene's file sits in.
    private static async Task<string> SeedSceneAsync(
        MonitorHost host, int studioId, string remoteId)
    {
        var videoId = await host.SeedStudioSceneAsync(
            studioId, MonitorHost.StoredEndpoint, remoteId);
        return await host.SeedSceneFileAsync(videoId, SharedFolder);
    }

    private static Task<MonitorHost> SceneHostAsync()
        => HostAsync(WhisparrGeneration.V3, host => host.Client
            .Answering(
                nameof(IWhisparrMissingSceneActing.AddSceneAsync),
                MonitorHost.Json(201, ProbeFixtures.Read(AcceptedFixture))));

    private static Task<MonitorHost> SiteHostAsync()
        => HostAsync(WhisparrGeneration.V2, host => host.Client
            .Answering(
                nameof(IWhisparrStudioActing.ReadStudioAsync), MonitorHost.Json(404, string.Empty))
            .Answering(
                nameof(IWhisparrSiteRegistrationActing.RegisterSiteAsync),
                MonitorHost.Json(201, """{"id":31,"tvdbId":3372,"path":"/config/library/x"}""")));

    private static async Task<MonitorHost> HostAsync(
        WhisparrGeneration generation, Action<MonitorHost> answering)
    {
        var host = await MonitorHost.CreateAsync(
            generation: generation,
            libraryConfig: new CoveConfiguration { CovePaths = [new CovePath { Path = CoveRoot }] },
            foldersAddressThemselves: true);

        host.Client
            .Answering(
                nameof(IWhisparrReflectOwnedActing.ReadHardlinkSettingAsync),
                MonitorHost.Json(200, LinksIntoPlace))
            .Answering(
                nameof(RecordingWhisparrCore.AttachOwnedFilesAsync), MonitorHost.Json(200, "{}"));

        answering(host);
        return host;
    }

    private static async Task RunAsync(MonitorHost host)
    {
        using var content = new StringContent(
            """{"alsoMonitor":false}""", Encoding.UTF8, "application/json");

        var answered = await host.Http.PostAsync(
            "/api/extensions/" + host.ExtensionId + "/sync/run", content, TestCt);
        answered.EnsureSuccessStatusCode();
        Assert.NotNull(await answered.Content.ReadFromJsonAsync<SyncEnqueued>(TestCt));

        await host.RunEnqueuedBatchAsync(new RecordingJobProgress());
    }
}
