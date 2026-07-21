using System.Net;
using System.Runtime.CompilerServices;
using Cove.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using WhisparrSync.Adapters;
using WhisparrSync.Client;
using WhisparrSync.Ingest;
using WhisparrSync.Options;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.Safety;

/// <summary>
/// The never-mutate-a-Whisparr-root contract. The ingest coordinator turns an imported path into a
/// Cove entity by IMPORTING IT IN PLACE: the only side effects it ever produces on the host are
/// <c>ImportDownloaded*</c> / <c>StartScan</c> calls (Cove records the path; the bytes stay where Whisparr
/// put them). These tests prove that two ways: a BEHAVIORAL assertion (driving a full webhook Download and a
/// fallback records only import/scan calls on the recording fake), and a STRUCTURAL source guard (the
/// coordinator source contains no filesystem move/delete API at all).
/// </summary>
public sealed class NoMutationTests
{
    private const string InRootVideo = "/data/media/Scene (2024)/Scene.mkv";

    private static IngestCoordinator Coordinator(FakeScanService scan, params string[] roots)
    {
        roots = roots.Length == 0 ? ["/data/media"] : roots;
        var services = new ServiceCollection();
        services.AddScoped<IScanService>(_ => scan);
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        return new IngestCoordinator(
            scopeFactory, _ => ValueTask.FromResult<IReadOnlyList<string>>(roots));
    }

    [Fact]
    public async Task WebhookDownload_RecordsOnlyAnImport_NeverAFilesystemMutation()
    {
        var scan = new FakeScanService();
        var store = new FakeStore();
        await new OptionsStore(store).SaveAsync(new WhisparrOptions { WebhookSecret = "sec" });
        var receiver = new WebhookReceiver(store, Coordinator(scan));

        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        http.Request.Method = "POST";
        http.Request.ContentType = "application/json";
        http.Request.Headers["X-Cove-Token"] = "sec";
        http.Request.Body = new MemoryStream(
            System.Text.Encoding.UTF8.GetBytes(WebhookPayloads.Download(InRootVideo)));

        await receiver.HandleAsync(http, default);

        // The ONLY host side effect is an in-place import — no scan fallback, and (by construction) the fake
        // exposes no relocation surface at all.
        var import = Assert.Single(scan.Imports);
        Assert.Equal("Video", import.Kind);
        Assert.Equal(InRootVideo, import.Path);
        Assert.Empty(scan.Scans);
    }

    [Fact]
    public async Task InRootIngestFailure_FallsBackToAScopedScan_AndStillNeverMutates()
    {
        var scan = new FakeScanService();
        scan.ThrowOnNextImport(new FileNotFoundException()); // the imported path is gone/not-yet-visible
        var coordinator = Coordinator(scan);

        var outcome = await coordinator.IngestAsync(InRootVideo, existingId: null, identity: null, default);

        // The fallback is a scoped StartScan (a read/index), never a move or delete.
        Assert.Equal(IngestResult.Flagged, outcome.Result);
        Assert.Empty(scan.Imports);
        var scanned = Assert.Single(scan.Scans);
        Assert.Contains("/data/media/Scene (2024)", scanned.Paths ?? []);
    }

    [Fact]
    public async Task V3OwnedAdopt_IssuesOnlyARepointPutAndRescan_NeverAnAddSearchOrFilesystemMutation()
    {
        const string ownedFolder = "/data/media/A Scene [uuid]";
        var movieRow = System.Text.Json.JsonSerializer.Serialize(new
        {
            id = 7, foreignId = "uuid", stashId = "uuid", title = "A Scene",
            monitored = true, hasFile = false, qualityProfileId = 3, rootFolderPath = "/data/media", path = "/data/whisparr/A Scene",
        });
        var linkedRow = System.Text.Json.JsonSerializer.Serialize(new
        {
            id = 7, foreignId = "uuid", stashId = "uuid", title = "A Scene", monitored = true, hasFile = true,
        });

        var handler = FakeHttpMessageHandler.Sequence(
            FakeHttpMessageHandler.Respond(HttpStatusCode.OK, "application/json", movieRow),  // PUT re-point
            FakeHttpMessageHandler.Respond(HttpStatusCode.OK, "application/json", "{}"),       // RescanMovie command
            FakeHttpMessageHandler.Respond(HttpStatusCode.OK, "application/json", $"[{linkedRow}]")); // verify GET
        var adapter = new V3Adapter(new WhisparrClient(new HttpClient(handler)), TimeSpan.Zero);

        var scene = new WhisparrMovie(
            Id: 7, Title: "A Scene", Year: 2024, StashId: "uuid", ForeignId: "uuid", ItemType: "scene",
            Monitored: true, HasFile: false, MovieFile: null, QualityProfileId: 3, RootFolderPath: "/data/media");

        var result = await adapter.ImportOwnedSceneAsync(
            "http://localhost:6969", "key", scene, ownedFolder + "/scene.mkv", OwnedImportMode.InPlaceAdopt, default);

        // In-place adopt is a Whisparr DB-row re-point only: a movie PUT + a rescan command, then a verify read —
        // NO /movie POST (add), NO MoviesSearch (grab). This is the behavioral half of the never-mutate contract;
        // the adapter reaches the filesystem through no API at all (the calls above are pure HTTP).
        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.Equal(HttpMethod.Put, handler.Requests[0].Method);
        Assert.Contains("\"name\":\"RescanMovie\"", handler.Requests[1].Body);
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Post && r.Url.EndsWith("/api/v3/movie", StringComparison.Ordinal));
        Assert.DoesNotContain(handler.Requests, r => r.Body?.Contains("MoviesSearch", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(handler.Requests, r => r.Body?.Contains("ManualImport", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void CoordinatorSource_ContainsNoFilesystemMoveOrDeleteApi()
    {
        var source = File.ReadAllLines(CoordinatorSourcePath())
            .Select(StripComment)
            .Where(line => !string.IsNullOrWhiteSpace(line));
        var code = string.Join('\n', source);

        // Any relocation/removal API violates the never-mutate contract: the coordinator must only import/scan in place.
        string[] forbidden =
        [
            "File.Move", "File.Delete", "File.Copy", "File.Replace",
            "Directory.Move", "Directory.Delete", ".MoveTo(", ".Delete(",
        ];
        foreach (var api in forbidden)
        {
            Assert.DoesNotContain(api, code);
        }
    }

    // Strip line + inline comments (covers `//` and `///`) so a doc comment mentioning "moved/deleted" can
    // never satisfy or invalidate the source guard — only real code lines are inspected.
    private static string StripComment(string line)
    {
        var idx = line.IndexOf("//", StringComparison.Ordinal);
        return idx >= 0 ? line[..idx] : line;
    }

    // The coordinator source sits beside this test project: ../../WhisparrSync/Ingest/IngestCoordinator.cs
    // (this test file lives one concern-subfolder deep under the test project root).
    private static string CoordinatorSourcePath([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(thisFile)!, "..", "..", "WhisparrSync", "Ingest", "IngestCoordinator.cs"));
}
