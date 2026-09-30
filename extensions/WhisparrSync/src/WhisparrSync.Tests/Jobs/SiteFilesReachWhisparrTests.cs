using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Cove.Core.Interfaces;
using WhisparrSync.Contracts;
using WhisparrSync.Linking;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Jobs;

// The generation that registers studios rather than scenes. Every name in the folder this product
// builds is the identity of the file it points at, so this generation's own parse of a name answers
// nothing and it records no file from the folder at all. What the library run hands it instead is
// the entry Cove identified, named as a scene row under the site's row.
public sealed class SiteFilesReachWhisparrTests
{
    private const string CoveRoot = "/config/library";

    private const string LibraryFolder = CoveRoot + "/every scene";

    // The namespace this generation identifies a studio and a scene in.
    private const string V2Endpoint = "https://theporndb.net/graphql";

    private const string SiteRemoteId = "a30bc641-6afe-4c80-9c73-ecb68104a68d";

    private const string SceneRemoteId = "023bacff-8d1d-4f27-bac5-bdaf833f5616";

    private const int SiteRow = 9;

    // The number the metadata provider issues for the scene, held apart from the row the instance
    // keys its own catalogue by: a pass matching on one of them would pass against a shared value.
    private const int SceneNumber = 1363738;

    private const int SceneRow = 77;

    private const string LinksIntoPlace = """{"copyUsingHardlinks":true}""";

    private static readonly string EntityFolder = TreePathGuard.EntityFolderIn(
        TreePathGuard.TreeRootUnder(CoveRoot, WhisparrGeneration.V2)!, SiteRemoteId)!;

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    // The whole of the gap: the run made a second name for the studio's file and the instance was
    // never asked to take it in, so it held no file for any studio however long it was left.
    [Fact]
    public async Task TheStudiosOwnFolderIsHandedOverInTheSameRun()
    {
        var arranged = await ArrangedAsync();
        await using var host = arranged.Host;

        await RunAsync(host);

        Assert.Equal(
            [EntityFolder],
            Verb(host, nameof(RecordingWhisparrCore.ListImportableFilesAsync))
                .Select(call => call.Folder));
    }

    // The entry goes out against the scene Cove identified, under the site the instance holds, so
    // nothing rests on what the instance read out of an identity-named link.
    [Fact]
    public async Task TheEntryHandedOverNamesTheSceneCoveIdentifiedUnderItsSite()
    {
        var arranged = await ArrangedAsync();
        await using var host = arranged.Host;

        await RunAsync(host);

        var entry = Assert.IsType<JsonObject>(
            Assert.Single(
                Assert.Single(
                    Verb(host, nameof(RecordingWhisparrCore.AttachOwnedFilesAsync)))
                    .Body!.AsArray()));

        Assert.Equal(arranged.LinkPath, entry["path"]!.GetValue<string>());
        Assert.Equal(SiteRow, entry["seriesId"]!.GetValue<int>());
        Assert.Equal([SceneRow], entry["episodeIds"]!.AsArray().Select(id => id!.GetValue<int>()));
    }

    // The two figures the run keeps apart: a second name in the tree is Cove's own act, and a file
    // recorded is the instance holding it against an entry of its own. This generation now does
    // both, and before this it could only ever do the first.
    [Fact]
    public async Task TheRunSaysBothWhatItLinkedAndWhatWhisparrRecorded()
    {
        var arranged = await ArrangedAsync();
        await using var host = arranged.Host;

        var ending = await RunAsync(host);

        Assert.Contains("1 linked, 1 recorded by Whisparr", ending, StringComparison.Ordinal);
    }

    // With that setting off the instance copies every file it takes in and reports the copy as an
    // import, so nothing is handed over. The second names are Cove's own and no setting of the
    // instance's stops them, so the run still states how many it made.
    [Fact]
    public async Task WithTheHardLinkSettingOffNothingIsHandedOverAndTheRunSaysWhy()
    {
        var arranged = await ArrangedAsync(
            host => host.Client.AnsweringThatLinkingWouldCopy());
        await using var host = arranged.Host;

        var ending = await RunAsync(host);

        Assert.DoesNotContain(
            nameof(RecordingWhisparrCore.ListImportableFilesAsync), host.Client.Verbs);
        Assert.DoesNotContain(
            nameof(RecordingWhisparrCore.AttachOwnedFilesAsync), host.Client.Verbs);
        Assert.Contains("1 linked", ending, StringComparison.Ordinal);
        Assert.Contains(
            "No files were handed to Whisparr: its hard-link setting is off.",
            ending,
            StringComparison.Ordinal);
    }

    // Without the site's own row there is no catalogue to name a scene row inside, so the folder is
    // left as it is rather than handed over on whatever the instance parsed out of the names in it.
    [Fact]
    public async Task ASiteTheInstanceHoldsNoRowForHandsNothingOver()
    {
        var arranged = await ArrangedAsync(
            host => host.Client.Answering(
                nameof(IWhisparrStudioActing.ReadStudioAsync),
                MonitorHost.Json(500, string.Empty)));
        await using var host = arranged.Host;

        await RunAsync(host);

        Assert.Single(host.TreeLinks.Links);
        Assert.DoesNotContain(
            nameof(RecordingWhisparrCore.ListImportableFilesAsync), host.Client.Verbs);
    }

    // One studio holding one scene, whose one file sits in a library folder, and an instance that
    // already holds the studio at the folder this product builds for it, so the run registers
    // nothing and moves nothing and the hand-over is all that is left to observe.
    private static async Task<(MonitorHost Host, string LinkPath)> ArrangedAsync(
        Action<MonitorHost>? answering = null)
    {
        var host = await MonitorHost.CreateAsync(
            generation: WhisparrGeneration.V2,
            libraryConfig: new CoveConfiguration { CovePaths = [new CovePath { Path = CoveRoot }] },
            catalogue: new RecordingProviderCatalogue(
                new Dictionary<string, int?>(StringComparer.Ordinal)
                {
                    [SceneRemoteId] = SceneNumber,
                }),
            foldersAddressThemselves: true);

        host.Client.SiteSceneRowIds[SceneNumber] = SceneRow;
        host.Client
            .Answering(nameof(IWhisparrStudioActing.ReadStudioAsync), MonitorHost.Json(200, HeldRow))
            .Answering(
                nameof(IWhisparrReflectOwnedActing.ReadHardlinkSettingAsync),
                MonitorHost.Json(200, LinksIntoPlace))
            .Answering(
                nameof(IWhisparrReflectOwnedActing.ReadNamingSettingsAsync),
                MonitorHost.Json(200, RecordingWhisparrCore.LeavesNamesAlone))
            .Answering(
                nameof(RecordingWhisparrCore.AttachOwnedFilesAsync), MonitorHost.Json(200, "{}"));

        var studioId = await host.SeedStudioAsync(V2Endpoint, SiteRemoteId);
        var videoId = await host.SeedStudioSceneAsync(studioId, V2Endpoint, SceneRemoteId);
        var file = await host.SeedSceneFileAsync(videoId, LibraryFolder);

        // Composed from the file the way the run composes it, so the listing the instance answers
        // names the same second name the run will have made by the time it asks.
        var linkPath = TreePathGuard.LinkPathIn(
            EntityFolder, host.TreeLinks.Identify(file)!.Identity, file)!;

        host.Client.Answering(
            nameof(RecordingWhisparrCore.ListImportableFilesAsync),
            MonitorHost.Json(200, Importable(linkPath)));

        answering?.Invoke(host);

        return (host, linkPath);
    }

    // Already at the folder this product builds for it, with a file count above zero, so the pass
    // neither moves it nor asks for the catalogue re-read an unread one gets.
    private static string HeldRow
        => new JsonObject
        {
            ["id"] = SiteRow,
            ["title"] = "Wicked",
            ["rootFolderPath"] = CoveRoot,
            ["path"] = EntityFolder,
            ["statistics"] = new JsonObject { ["episodeFileCount"] = 1 },
        }.ToJsonString();

    // What the instance answers about the folder. The quality and the languages are its own
    // reading, which an import is refused without, and it reads no scene out of the name at all.
    private static string Importable(string path)
        => new JsonArray(
            new JsonObject
            {
                ["path"] = path,
                ["quality"] = new JsonObject { ["quality"] = new JsonObject { ["id"] = 7 } },
                ["languages"] = new JsonArray(new JsonObject { ["id"] = 1 }),
            }).ToJsonString();

    private static IEnumerable<ActingCall> Verb(MonitorHost host, string verb)
        => host.Client.Acting.Where(call => call.Verb == verb);

    // The ending the host shows, which is the last summary the run set.
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
