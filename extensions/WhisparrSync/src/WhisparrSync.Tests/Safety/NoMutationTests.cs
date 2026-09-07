using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Cove.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using WhisparrSync.Adapters;
using WhisparrSync.Client;
using WhisparrSync.Contracts;
using WhisparrSync.Ingest;
using WhisparrSync.Options;
using WhisparrSync.Push;
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
[Trait("Tier", "L1")]
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
            id = 7,
            foreignId = "uuid",
            stashId = "uuid",
            title = "A Scene",
            monitored = true,
            hasFile = false,
            qualityProfileId = 3,
            rootFolderPath = "/data/media",
            path = "/data/whisparr/A Scene",
        });
        var linkedRow = System.Text.Json.JsonSerializer.Serialize(new
        {
            id = 7,
            foreignId = "uuid",
            stashId = "uuid",
            title = "A Scene",
            monitored = true,
            hasFile = true,
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

    // ---- mark-wanted (per-card Monitor): monitored:true + searchForMovie:false + cove-sync tag, no grab ----

    private const string WantedBaseUrl = "http://localhost:6969";
    private const string WantedSceneStashId = "9c8b7a6d-1e2f-4a3b-8c9d-0e1f2a3b4c5d";
    private const string WantedRootPath = "/data/media";
    private static readonly int[] WantedOriginTags = [5];

    private static WhisparrOptions WantedV3Options => new()
    {
        BaseUrl = WantedBaseUrl,
        ApiKey = "test-api-key",
        SelectedVersion = "v3",
        DetectedVersion = "3.3.4.808",
    };

    private static SceneActions WantedActions(FakeHttpMessageHandler handler)
        => SceneActionsFactory.Build(new WhisparrClient(new HttpClient(handler)), WantedV3Options, new FakeCoveLibraryPort());

    private static Func<HttpResponseMessage> WantedOk(string body)
        => FakeHttpMessageHandler.Respond(HttpStatusCode.OK, "application/json", body);

    private static string WantedRootFolderList => JsonSerializer.Serialize(new[]
    {
        new { id = 2, path = WantedRootPath, accessible = true, freeSpace = 1L },
    });

    private static string WantedTagList => JsonSerializer.Serialize(new[]
    {
        new { id = 5, label = AddContextResolver.OriginTagLabel },
    });

    private static string WantedProfileList => JsonSerializer.Serialize(new[]
    {
        new { id = 4, name = "HD-1080p" },
    });

    private static string WantedMovie(bool monitored) => JsonSerializer.Serialize(new
    {
        id = 42,
        foreignId = WantedSceneStashId,
        stashId = WantedSceneStashId,
        title = "A Scene",
        monitored,
        hasFile = false,
        qualityProfileId = 4,
        rootFolderPath = WantedRootPath,
        tags = WantedOriginTags,
    });

    // Loop-safety assertion shared by the mark-wanted cases: no add leg ever carries searchForMovie:true and no
    // MoviesSearch command is ever posted — marking wanted arms acquisition without an immediate grab.
    private static void AssertMarkWantedNeverGrabs(FakeHttpMessageHandler handler)
    {
        Assert.DoesNotContain(handler.Requests, r => r.Url.Contains("/command", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            handler.Requests,
            r => r.Body?.Replace(" ", "", StringComparison.Ordinal)
                .Contains("\"searchForMovie\":true", StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public async Task MarkWanted_OnAnAlreadyAddedUnmonitoredScene_FlipsItMonitoredTrue_WithNoGrab()
    {
        // A monitored studio's discovery "missing" scenes are already added-but-unmonitored Whisparr movies, so
        // marking one wanted must FLIP it monitored:true (a PUT) — not a 409-no-op add. It joins the wanted list
        // and fires NO grab command.
        var handler = FakeHttpMessageHandler.Sequence(
            WantedOk(WantedRootFolderList),                          // root resolve (monitor-ON add leg prereq)
            WantedOk(WantedTagList),                                 // origin-tag ensure (found)
            WantedOk(WantedProfileList),                             // quality-profile resolve
            WantedOk($"[{WantedMovie(monitored: false)}]"),         // GET movie -> present, unmonitored
            WantedOk(WantedMovie(monitored: true)));                // PUT flip -> monitored:true

        var result = await WantedActions(handler).MarkScenesWantedAsync(
            [new SceneRef(WantedSceneStashId, "A Scene", 2024)], default);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.Equal(1, result.Value!.Succeeded);

        // The flip is a PUT carrying monitored:true; no POST /movie (the movie already exists), so no add either.
        var put = Assert.Single(
            handler.Requests, r => r.Method == HttpMethod.Put && r.Url.Contains("/api/v3/movie/", StringComparison.Ordinal));
        Assert.Contains("\"monitored\":true", put.Body);
        Assert.DoesNotContain(
            handler.Requests, r => r.Method == HttpMethod.Post && r.Url.EndsWith("/api/v3/movie", StringComparison.Ordinal));
        AssertMarkWantedNeverGrabs(handler);
    }

    [Fact]
    public async Task MarkWanted_OnAnAbsentScene_AddsMonitoredFalseThenFlipsTrue_SearchFalseOriginTagged_NoGrab()
    {
        // A scene not yet in Whisparr (the direct/StashDB path) is added monitored:false + searchForMovie:false,
        // origin-tagged, then flipped monitored:true — it ends wanted, with NO grab.
        var handler = FakeHttpMessageHandler.Sequence(
            WantedOk(WantedRootFolderList),
            WantedOk(WantedTagList),
            WantedOk(WantedProfileList),                                                // quality-profile resolve
            WantedOk("[]"),                                                             // GET movie -> absent
            FakeHttpMessageHandler.Respond(HttpStatusCode.Created, "application/json", WantedMovie(monitored: false)),
            WantedOk(WantedMovie(monitored: true)));                                    // PUT flip -> monitored:true

        var result = await WantedActions(handler).MarkScenesWantedAsync(
            [new SceneRef(WantedSceneStashId, "A Scene", 2024)], default);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.Equal(1, result.Value!.Succeeded);

        var post = Assert.Single(
            handler.Requests, r => r.Method == HttpMethod.Post && r.Url.EndsWith("/api/v3/movie", StringComparison.Ordinal));
        Assert.Contains("\"searchForMovie\":false", post.Body);      // NO immediate grab on the add
        Assert.Contains("\"tags\":[5]", post.Body);                  // origin-tagged (cove-sync)
        var put = Assert.Single(
            handler.Requests, r => r.Method == HttpMethod.Put && r.Url.Contains("/api/v3/movie/", StringComparison.Ordinal));
        Assert.Contains("\"monitored\":true", put.Body);             // ends wanted
        AssertMarkWantedNeverGrabs(handler);
    }

    [Fact]
    public async Task ReMarkWanted_OnAnAlreadyMonitoredScene_IsIdempotent_WithNoGrab()
    {
        // Re-Monitoring an already-monitored scene is a safe idempotent no-op: the PUT re-asserts monitored:true
        // (never a duplicate add), and still no grab command fires.
        var handler = FakeHttpMessageHandler.Sequence(
            WantedOk(WantedRootFolderList),
            WantedOk(WantedTagList),
            WantedOk(WantedProfileList),                            // quality-profile resolve
            WantedOk($"[{WantedMovie(monitored: true)}]"),          // GET movie -> present, already monitored
            WantedOk(WantedMovie(monitored: true)));                // PUT (idempotent)

        var result = await WantedActions(handler).MarkScenesWantedAsync(
            [new SceneRef(WantedSceneStashId, "A Scene", 2024)], default);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.Equal(1, result.Value!.Succeeded);
        Assert.Equal(0, result.Value.Failed);
        Assert.DoesNotContain(
            handler.Requests, r => r.Method == HttpMethod.Post && r.Url.EndsWith("/api/v3/movie", StringComparison.Ordinal));
        AssertMarkWantedNeverGrabs(handler);
    }

    // ---- bulk mark-wanted (the selection / a whole-entity mark-all) issues no grab over MANY scenes ----

    private const string BulkSceneStashIdA = "aaaa1111-2222-3333-4444-555566667777";
    private const string BulkSceneStashIdB = "bbbb1111-2222-3333-4444-555566667777";

    private static string WantedMovieAt(int id, string stashId, bool monitored) => JsonSerializer.Serialize(new
    {
        id,
        foreignId = stashId,
        stashId,
        title = "A Scene",
        monitored,
        hasFile = false,
        qualityProfileId = 4,
        rootFolderPath = WantedRootPath,
        tags = WantedOriginTags,
    });

    [Fact]
    public async Task BulkMarkWanted_OverManyScenes_FlipsEachMonitoredTrue_WithNoGrab()
    {
        // The bulk core over a two-scene missing set: each present-unmonitored scene is flipped monitored:true via
        // the shipped mark-wanted spine, and NO MoviesSearch/grab command fires for the whole run. Each scene's
        // monitor-ON add leg resolves the root + the origin tag before the GET/PUT.
        var handler = FakeHttpMessageHandler.Sequence(
            WantedOk(WantedRootFolderList),                                 // scene A: root resolve
            WantedOk(WantedTagList),                                        // scene A: origin-tag ensure
            WantedOk(WantedProfileList),                                    // scene A: quality-profile resolve (cached after)
            WantedOk($"[{WantedMovieAt(42, BulkSceneStashIdA, false)}]"),   // scene A: GET movie -> unmonitored
            WantedOk(WantedMovieAt(42, BulkSceneStashIdA, true)),           // scene A: PUT flip -> monitored:true
            WantedOk(WantedRootFolderList),                                 // scene B: root resolve
            WantedOk(WantedTagList),                                        // scene B: origin-tag ensure
            WantedOk($"[{WantedMovieAt(43, BulkSceneStashIdB, false)}]"),   // scene B: GET movie -> unmonitored
            WantedOk(WantedMovieAt(43, BulkSceneStashIdB, true)));          // scene B: PUT flip -> monitored:true

        MissingScene[] missing =
        [
            new(BulkSceneStashIdA, "Scene A", "2021-01-01", "A Studio", PosterUrl: null),
            new(BulkSceneStashIdB, "Scene B", "2021-02-02", "A Studio", PosterUrl: null),
        ];

        var result = await global::WhisparrSync.WhisparrSync.DiscoveryActionAllCoreAsync(
            missing, sourceIds: null, WantedActions(handler), default);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.Equal(2, result.Value!.Succeeded);
        // Both scenes flipped via a PUT (never a duplicate add), and the whole bulk run issued no grab.
        Assert.Equal(2, handler.Requests.Count(
            r => r.Method == HttpMethod.Put && r.Url.Contains("/api/v3/movie/", StringComparison.Ordinal)));
        AssertMarkWantedNeverGrabs(handler);
    }

    // ---- the discovery grab boundary is LOCKED: unmonitor never grabs; search is the sole immediate grab ----

    private static MissingScene WantedMissingScene =>
        new(WantedSceneStashId, "A Scene", "2024-01-01", "A Studio", PosterUrl: null);

    [Fact]
    public async Task DiscoveryUnmonitor_OnAWantedScene_FlipsMonitoredFalse_WithNoGrab()
    {
        // The un-path reads the present monitored movie then PUTs monitored:false — a bare flip, no add, no grab.
        var handler = FakeHttpMessageHandler.Sequence(
            WantedOk($"[{WantedMovie(monitored: true)}]"), // GET movie -> present, monitored
            WantedOk(WantedMovie(monitored: false)));      // PUT flip -> monitored:false

        var result = await global::WhisparrSync.WhisparrSync.DiscoveryActionAllUnitCoreAsync(
            global::WhisparrSync.WhisparrSync.DiscoverySceneOp.Unmonitor,
            [WantedMissingScene], sourceIds: null,
            new Dictionary<string, WhisparrMovie>(StringComparer.OrdinalIgnoreCase), WantedActions(handler), default);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.Equal(1, result.Value!.Succeeded);
        var put = Assert.Single(
            handler.Requests, r => r.Method == HttpMethod.Put && r.Url.Contains("/api/v3/movie/", StringComparison.Ordinal));
        Assert.Contains("\"monitored\":false", put.Body);
        // No add POST and, above all, no grab: unmonitor is search-free (the loop-safety boundary holds).
        Assert.DoesNotContain(
            handler.Requests, r => r.Method == HttpMethod.Post && r.Url.EndsWith("/api/v3/movie", StringComparison.Ordinal));
        AssertMarkWantedNeverGrabs(handler);
    }

    [Fact]
    public async Task DiscoverySearch_IsTheSoleGrab_IssuesExactlyOneMoviesSearch()
    {
        // Search is the ONE discovery op that crosses the immediate-grab boundary: an in-set, added scene issues
        // exactly one MoviesSearch. Every other discovery path (open/refresh/monitor/unmonitor/bulk) issues none.
        var handler = FakeHttpMessageHandler.Json("{}"); // POST /command (MoviesSearch) -> queued
        var index = new Dictionary<string, WhisparrMovie>(StringComparer.OrdinalIgnoreCase)
        {
            [WantedSceneStashId] = new WhisparrMovie(
                Id: 42, Title: "A Scene", Year: 2024, StashId: WantedSceneStashId, ForeignId: WantedSceneStashId,
                ItemType: "scene", Monitored: true, HasFile: false, MovieFile: null),
        };

        var result = await global::WhisparrSync.WhisparrSync.DiscoverySearchCoreAsync(
            [WantedMissingScene], index, WantedSceneStashId, WantedActions(handler), default);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.True(result.Value);
        var command = Assert.Single(
            handler.Requests, r => r.Method == HttpMethod.Post && r.Url.EndsWith("/api/v3/command", StringComparison.Ordinal));
        Assert.Contains("\"name\":\"MoviesSearch\"", command.Body);
        Assert.Contains("\"movieIds\":[42]", command.Body);
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
