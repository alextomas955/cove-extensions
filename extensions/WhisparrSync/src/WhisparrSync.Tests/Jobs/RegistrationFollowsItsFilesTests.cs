using System.Net.Http.Json;
using System.Text;
using Cove.Core.Interfaces;
using WhisparrSync.Contracts;
using WhisparrSync.Linking;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Jobs;

// A library on two drives, and a scene whose file is on the second one while the instance still
// records it on the first.
//
// The two roots are two devices to the filesystem below these cases: it keys a volume on the
// leading segment of a path, so a link from one to the other is refused the way the platform
// refuses one. That is what makes the journey here the same journey a reader's two drives produce.
public sealed class RegistrationFollowsItsFilesTests
{
    private const string DriveA = "/dataA";

    private const string DriveB = "/dataB";

    private const string TwoRoots =
        """[{"id":1,"path":"/dataA","accessible":true},{"id":2,"path":"/dataB","accessible":true}]""";

    private const string Scene = "023bacff-8d1d-4f27-bac5-bdaf833f5616";

    private const string AlreadyHeld =
        """[{"errorCode":"MovieExistsValidator","errorMessage":"This item has already been added"}]""";

    private const string Accepted = """{"id":31,"path":"/dataB"}""";

    private const string LinksIntoPlace = """{"copyUsingHardlinks":true}""";

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    private static string FolderOn(string root)
        => TreePathGuard.EntityFolderIn(
            TreePathGuard.TreeRootUnder(root, WhisparrGeneration.V3)!, Scene)!;

    [Fact]
    public async Task AsceneWhoseFileChangedDriveIsRecordedOnTheDriveItIsOnNow()
    {
        await using var host = await HostAsync(recordedAt: FolderOn(DriveA));
        await SeedSceneOnAsync(host, DriveB);

        await RunAsync(host);

        var moved = Assert.Single(Moves(host));
        Assert.Equal(DriveB, moved.Folder);
        Assert.Equal(FolderOn(DriveB), moved.EntityFolder);
    }

    // The order is the safety invariant of this whole journey: both generations rewrite their own
    // file records to the new folder without reading it, so a move sent before the links exist
    // leaves the instance reporting a file at a path nothing holds.
    [Fact]
    public async Task TheMoveIsSentOnlyOnceTheNewDrivesLinkExists()
    {
        await using var host = await HostAsync(recordedAt: FolderOn(DriveA));
        var movedFirst = false;
        host.TreeLinks.Watching = _ => movedFirst |= Moves(host).Any();
        await SeedSceneOnAsync(host, DriveB);

        await RunAsync(host);

        Assert.Single(Moves(host));
        Assert.Single(host.TreeLinks.NamesIn(FolderOn(DriveB)));
        Assert.False(movedFirst, "the registration was moved before the link under it existed");
    }

    [Fact]
    public async Task AsceneTheInstanceRecordsAtTheFolderThisRunIntendsIsNotMoved()
    {
        await using var host = await HostAsync(recordedAt: FolderOn(DriveB));
        await SeedSceneOnAsync(host, DriveB);

        await RunAsync(host);

        Assert.Empty(Moves(host));
    }

    // The name left on the drive the file is no longer on. Nothing here removes it: no library file
    // answers to it any more, which is what the pass over the tree's own folders decides from.
    [Fact]
    public async Task TheNameLeftOnTheOldDriveIsTakenBackAndTheFilesBytesExistOnce()
    {
        await using var host = await HostAsync(recordedAt: FolderOn(DriveA));
        var onTheOldDrive = DriveA + "/scenes/the scene.mp4";
        var stranded = TreePathGuard.LinkPathIn(
            FolderOn(DriveA), host.TreeLinks.Identify(onTheOldDrive)!.Identity, onTheOldDrive)!;
        Assert.Equal(LinkOutcome.Linked, host.TreeLinks.Link(onTheOldDrive, stranded));
        host.TreeLinks.Forget(onTheOldDrive);
        var onTheNewDrive = await SeedSceneOnAsync(host, DriveB);

        await RunAsync(host);

        Assert.Empty(host.TreeLinks.NamesIn(FolderOn(DriveA)));
        Assert.Equal([onTheNewDrive], host.TreeLinks.Links.Values);
    }

    // In the words the other pass already uses, so one act reads one way whichever generation is
    // connected.
    [Fact]
    public async Task TheRunSaysHowManyRegistrationsFollowedTheirFiles()
    {
        await using var host = await HostAsync(recordedAt: FolderOn(DriveA));
        await SeedSceneOnAsync(host, DriveB);

        var ending = await RunAsync(host);

        Assert.Contains(
            "1 moved to the root holding their files", ending, StringComparison.Ordinal);
        Assert.Contains("0 refused", ending, StringComparison.Ordinal);
    }

    // A refusal is a state a reader acts on: that scene is recorded where none of its files sit
    // until a later run moves it. It is counted rather than reported as a scene already held.
    [Fact]
    public async Task ARunTheInstanceDeclinedTheMoveOnCountsItRefusedAndNamesNoScene()
    {
        await using var host = await HostAsync(
            recordedAt: FolderOn(DriveA), moveAnswer: MonitorHost.Json(400, "{}"));
        await SeedSceneOnAsync(host, DriveB);

        var ending = await RunAsync(host);

        Assert.Contains("1 refused", ending, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "moved to the root holding their files", ending, StringComparison.Ordinal);
        Assert.DoesNotContain(Scene, ending, StringComparison.Ordinal);
    }

    private static IEnumerable<ActingCall> Moves(MonitorHost host)
        => host.Client.Acting.Where(
            call => call.Verb == nameof(IWhisparrEntityRelocationActing.MoveEntityFolderAsync));

    private static async Task<string> SeedSceneOnAsync(MonitorHost host, string root)
    {
        var studioId = await host.SeedStudioAsync(null, null);
        var videoId = await host.SeedStudioSceneAsync(
            studioId, MonitorHost.StoredEndpoint, Scene);
        return await host.SeedSceneFileAsync(videoId, root + "/scenes");
    }

    private static async Task<MonitorHost> HostAsync(
        string recordedAt, WhisparrResponse? moveAnswer = null)
    {
        var host = await MonitorHost.CreateAsync(
            generation: WhisparrGeneration.V3,
            libraryConfig: new CoveConfiguration
            {
                CovePaths = [new CovePath { Path = DriveA }, new CovePath { Path = DriveB }],
            },
            foldersAddressThemselves: true);

        host.Client
            .Answering(nameof(IWhisparrClient.ReadRootFoldersAsync), MonitorHost.Json(200, TwoRoots))
            .Answering(
                nameof(IWhisparrReflectOwnedActing.ReadHardlinkSettingAsync),
                MonitorHost.Json(200, LinksIntoPlace))
            .Answering(
                nameof(RecordingWhisparrCore.AttachOwnedFilesAsync), MonitorHost.Json(200, "{}"))
            .Answering(
                nameof(IWhisparrMissingSceneActing.AddSceneAsync),
                MonitorHost.Json(400, AlreadyHeld))
            .Answering(
                nameof(IWhisparrEntityRelocationActing.MoveEntityFolderAsync),
                moveAnswer ?? MonitorHost.Json(202, Accepted));

        // The row the per-scene read answers, which is the only thing that says where the instance
        // records a scene: an add for one it already holds is refused and says nothing about it.
        host.Client.Answering(
            nameof(IWhisparrSceneStatusReading.ReadSceneByRemoteIdAsync),
            MonitorHost.Json(
                200,
                $$"""[{"id":31,"monitored":true,"path":"{{recordedAt}}"}]"""));

        return host;
    }

    private static async Task<string> RunAsync(MonitorHost host)
    {
        using var content = new StringContent(
            """{"alsoMonitor":false}""", Encoding.UTF8, "application/json");

        var answered = await host.Http.PostAsync(
            "/api/extensions/" + host.ExtensionId + "/sync/run", content, TestCt);
        answered.EnsureSuccessStatusCode();
        Assert.NotNull(await answered.Content.ReadFromJsonAsync<SyncEnqueued>(TestCt));

        var progress = new RecordingJobProgress();
        await host.RunEnqueuedBatchAsync(progress);

        return progress.Summaries[^1];
    }
}
