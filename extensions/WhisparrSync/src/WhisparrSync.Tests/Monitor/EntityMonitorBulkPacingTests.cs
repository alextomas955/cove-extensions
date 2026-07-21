using System.Net;
using System.Text.Json;
using WhisparrSync.Client;
using WhisparrSync.Library;
using WhisparrSync.Monitor;
using WhisparrSync.Options;
using WhisparrSync.Tests.TestSupport;
using Ext = global::WhisparrSync.WhisparrSync;

namespace WhisparrSync.Tests.Monitor;

/// <summary>
/// The scale-safety contract for the studios/performers bulk "monitor many" action (LOOP-03). Since a v3
/// monitor-ON now fires a per-entity targeted metadata refresh + a bounded completion wait + a bulk editor
/// toggle, a parallel fan-out over many entities would originate an N-entity refresh storm on Whisparr's
/// command queue (the StashDB-hammer this phase forbids). These facts drive the host-free aggregator
/// <see cref="Ext.EntitiesBatchCoreAsync"/> over two studios against a fixture-primed
/// <see cref="FakeHttpMessageHandler"/> and prove the bulk op stays sequential and single-id-scoped: exactly
/// one targeted refresh per entity (each carrying that entity's own single id), never an empty/global refresh
/// array, and the recorded request order shows the first entity's refresh + editor completing before the
/// second entity's flip begins.
/// </summary>
public sealed class EntityMonitorBulkPacingTests
{
    private const string BaseUrl = "http://localhost:6969";
    private const string ApiKey = "test-api-key";

    // Two studios with distinct StashDB ids and distinct Whisparr ids, so a refresh/editor/flip request can be
    // attributed to A vs B by the id in its body/URL.
    private const string StudioAStash = "aaaaaaaa-1111-1111-1111-aaaaaaaaaaaa";
    private const string StudioBStash = "bbbbbbbb-2222-2222-2222-bbbbbbbbbbbb";
    private const int StudioAId = 42;
    private const int StudioBId = 43;

    private static WhisparrOptions V3Options => new()
    {
        BaseUrl = BaseUrl,
        ApiKey = ApiKey,
        SelectedVersion = "v3",
        DetectedVersion = "3.3.4.808",
        QualityProfileId = 4,
    };

    // One root, so the file-less monitor-add derives it trivially.
    private static string RootFolderList => JsonSerializer.Serialize(new[]
    {
        new { id = 2, path = "/data/media", accessible = true, freeSpace = 1L },
    });

    private static string TagListWithOrigin => JsonSerializer.Serialize(new[]
    {
        new { id = 5, label = EntityMonitor.OriginTagLabel },
    });

    private static string StudioRow(int id, string stashId, string title, bool monitored) => JsonSerializer.Serialize(new
    {
        id,
        foreignId = stashId,
        title,
        monitored,
        qualityProfileId = 4,
        rootFolderPath = "/data/media",
        tags = new[] { 5 },
    });

    // The full movie set (GET /movie returns the whole library each call). Each studio has ONE fileless
    // back-catalogue row so its NewReleases cascade issues a distinct single-id editor toggle.
    private static string Movies => JsonSerializer.Serialize(new[]
    {
        new { id = 100, studioTitle = "StudioA", monitored = true, hasFile = false },
        new { id = 200, studioTitle = "StudioB", monitored = true, hasFile = false },
    });

    private static Func<HttpResponseMessage> Ok(string body)
        => FakeHttpMessageHandler.Respond(HttpStatusCode.OK, "application/json", body);

    private static Func<HttpResponseMessage> Accepted()
        => FakeHttpMessageHandler.Respond(HttpStatusCode.Accepted, "application/json", "{}");

    private static string CommandQueued(int id) => JsonSerializer.Serialize(new { id, status = "queued" });

    private static string CommandCompleted(int id) => JsonSerializer.Serialize(new { id, status = "completed" });

    // The full outbound sequence for two EXISTING-but-unmonitored studios monitored ON under NewReleases. Each
    // studio: resolve root, (studio A also ensures the origin tag — cached for B), GET+PUT flip, POST /command
    // targeted refresh + GET /command status, then the cascade (studio GET + movie GET + PUT /movie/editor).
    private static FakeHttpMessageHandler TwoStudioMonitorSequence()
        => FakeHttpMessageHandler.Sequence(
            // --- Studio A ---
            Ok(RootFolderList),                                                 // GET /rootfolder
            Ok(TagListWithOrigin),                                              // GET /tag (origin found, cached after)
            Ok($"[{StudioRow(StudioAId, StudioAStash, "StudioA", monitored: false)}]"), // GET /studio?stashId=A
            Ok(StudioRow(StudioAId, StudioAStash, "StudioA", monitored: true)), // PUT /studio/42 flip
            Ok(CommandQueued(777)),                                            // POST /command RefreshStudios{[42]}
            Ok(CommandCompleted(777)),                                         // GET /command/777 -> completed
            Ok($"[{StudioRow(StudioAId, StudioAStash, "StudioA", monitored: true)}]"), // cascade: GET /studio
            Ok(Movies),                                                        // cascade: GET /movie
            Accepted(),                                                        // cascade: PUT /movie/editor -> 202
            // --- Studio B (origin tag cached — no GET /tag) ---
            Ok(RootFolderList),                                                 // GET /rootfolder
            Ok($"[{StudioRow(StudioBId, StudioBStash, "StudioB", monitored: false)}]"), // GET /studio?stashId=B
            Ok(StudioRow(StudioBId, StudioBStash, "StudioB", monitored: true)), // PUT /studio/43 flip
            Ok(CommandQueued(778)),                                            // POST /command RefreshStudios{[43]}
            Ok(CommandCompleted(778)),                                         // GET /command/778 -> completed
            Ok($"[{StudioRow(StudioBId, StudioBStash, "StudioB", monitored: true)}]"), // cascade: GET /studio
            Ok(Movies),                                                        // cascade: GET /movie
            Accepted());                                                       // cascade: PUT /movie/editor -> 202

    private static (WhisparrClient Client, FakeHttpMessageHandler Handler, FakeCoveLibraryPort Library) BulkHarness()
    {
        var handler = TwoStudioMonitorSequence();
        var library = new FakeCoveLibraryPort();
        library.SeedEntityIdentity(EntityKind.Studio, 1, new CoveEntityIdentity(StashIds: [StudioAStash], TpdbIds: []));
        library.SeedEntityIdentity(EntityKind.Studio, 2, new CoveEntityIdentity(StashIds: [StudioBStash], TpdbIds: []));
        return (new WhisparrClient(new HttpClient(handler)), handler, library);
    }

    private static bool IsRefreshCommand(CapturedRequest r)
        => r.Method == HttpMethod.Post
            && r.Url.EndsWith("/api/v3/command", StringComparison.Ordinal)
            && r.Body?.Contains("\"name\":\"RefreshStudios\"", StringComparison.Ordinal) == true;

    [Fact]
    public async Task BulkMonitor_TwoStudios_IssuesOneSingleIdRefreshPerEntity()
    {
        var (client, handler, library) = BulkHarness();

        var result = await Ext.EntitiesBatchCoreAsync(
            EntityKind.Studio, Ext.EntityBatchOp.Monitor, MonitorScope.NewReleases, [1, 2],
            client, library, V3Options, CancellationToken.None);

        Assert.Equal(2, result.Total);
        Assert.Equal(2, result.Succeeded);

        // Exactly one targeted refresh per entity, each carrying that entity's OWN single id — never a batched
        // multi-id or a global array.
        var refreshes = handler.Requests.Where(IsRefreshCommand).ToList();
        Assert.Equal(2, refreshes.Count);
        Assert.Single(refreshes, r => r.Body!.Contains($"\"studioIds\":[{StudioAId}]", StringComparison.Ordinal));
        Assert.Single(refreshes, r => r.Body!.Contains($"\"studioIds\":[{StudioBId}]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BulkMonitor_TwoStudios_NeverIssuesAGlobalRefresh()
    {
        var (client, handler, library) = BulkHarness();

        await Ext.EntitiesBatchCoreAsync(
            EntityKind.Studio, Ext.EntityBatchOp.Monitor, MonitorScope.NewReleases, [1, 2],
            client, library, V3Options, CancellationToken.None);

        // An empty studioIds/performerIds array would tell Whisparr to refresh EVERY entity (the StashDB-hammer);
        // no bulk request may ever carry one.
        Assert.DoesNotContain(handler.Requests, r => r.Body?.Contains("\"studioIds\":[]", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(handler.Requests, r => r.Body?.Contains("\"performerIds\":[]", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task BulkMonitor_TwoStudios_PacesSequentially_FirstEntityRefreshAndEditorPrecedeSecondFlip()
    {
        var (client, handler, library) = BulkHarness();

        await Ext.EntitiesBatchCoreAsync(
            EntityKind.Studio, Ext.EntityBatchOp.Monitor, MonitorScope.NewReleases, [1, 2],
            client, library, V3Options, CancellationToken.None);

        var requests = handler.Requests;
        var aRefresh = IndexOf(requests, r => IsRefreshCommand(r) && r.Body!.Contains($"\"studioIds\":[{StudioAId}]", StringComparison.Ordinal));
        var aEditor = IndexOf(requests, r => r.Method == HttpMethod.Put && r.Url.EndsWith("/api/v3/movie/editor", StringComparison.Ordinal)
            && r.Body!.Contains("\"movieIds\":[100]", StringComparison.Ordinal));
        var bFlip = IndexOf(requests, r => r.Method == HttpMethod.Put && r.Url.EndsWith($"/api/v3/studio/{StudioBId}", StringComparison.Ordinal));

        Assert.True(aRefresh >= 0 && aEditor >= 0 && bFlip >= 0);

        // Sequential pacing: studio A's whole monitor op (refresh + editor) drains before studio B's flip begins.
        Assert.True(aRefresh < bFlip, "studio A's refresh must precede studio B's flip");
        Assert.True(aEditor < bFlip, "studio A's editor must precede studio B's flip");
    }

    private static int IndexOf(IReadOnlyList<CapturedRequest> requests, Func<CapturedRequest, bool> predicate)
    {
        for (var i = 0; i < requests.Count; i++)
        {
            if (predicate(requests[i]))
            {
                return i;
            }
        }

        return -1;
    }
}
