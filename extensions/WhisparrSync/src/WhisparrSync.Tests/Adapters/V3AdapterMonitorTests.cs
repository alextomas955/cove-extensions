using System.Net;
using System.Text.Json;
using WhisparrSync.Adapters;
using WhisparrSync.Client;
using WhisparrSync.Monitor;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.Adapters;

/// <summary>
/// The v3 monitor-ON catalogue-population + scope-reconciliation wire contract. Drives <see cref="V3Adapter"/>
/// directly (root/origin-tag already resolved by the caller, so these sequences start at the studio/performer
/// GET) with a zero settle so the command-wait loop runs its logic without sleeping. Proves the loop-safety
/// invariants of Phase 31: a monitor-ON flip fires a TARGETED single-id metadata refresh (never an empty/global
/// array, never a search), WAITS for the command to report completed, then reconciles the discovered
/// back-catalogue's <c>monitored</c> flag to the chosen scope — NewReleases unmonitors the fileless
/// back-catalogue (grab-defused), AllScenes leaves it monitored.
/// </summary>
public sealed class V3AdapterMonitorTests
{
    private const string BaseUrl = "http://localhost:6969";
    private const string ApiKey = "test-api-key";
    private const string StudioStashId = "157c9e0d-5f8e-446a-b1c5-dddf3cb5b2d1";
    private const string PerformerStashId = "9a4c1e2b-3d5f-4a6b-8c7d-1e2f3a4b5c6d";

    private static readonly IReadOnlyList<int> OriginTag = [5];

    // Zero settle so the command-wait loop exercises its poll logic without real waiting.
    private static V3Adapter AdapterOn(FakeHttpMessageHandler handler)
        => new(new WhisparrClient(new HttpClient(handler)), TimeSpan.Zero);

    private static Func<HttpResponseMessage> Ok(string body)
        => FakeHttpMessageHandler.Respond(HttpStatusCode.OK, "application/json", body);

    private static Func<HttpResponseMessage> Accepted()
        => FakeHttpMessageHandler.Respond(HttpStatusCode.Accepted, "application/json", "{}");

    private static string StudioRow(int id, bool monitored) => JsonSerializer.Serialize(new
    {
        id,
        foreignId = StudioStashId,
        title = "IEnergy",
        monitored,
        qualityProfileId = 4,
        rootFolderPath = "/data/media",
        tags = new[] { 5 },
    });

    private static string PerformerRow(int id, bool monitored) => JsonSerializer.Serialize(new
    {
        id,
        foreignId = PerformerStashId,
        fullName = "Miyu Aizawa",
        monitored,
        qualityProfileId = 4,
        rootFolderPath = "/data/media",
        tags = new[] { 5 },
    });

    // A queued command handle, then its completed status — the population refresh's POST /command reply and the
    // GET /command/{id} the wait loop polls.
    private static string CommandQueued(int id) => JsonSerializer.Serialize(new { id, status = "queued" });

    private static string CommandCompleted(int id) => JsonSerializer.Serialize(new { id, status = "completed" });

    // The studio movie set: id 11 is owned (hasFile), id 12 is the discovered fileless back-catalogue, id 99
    // belongs to another studio (not attributed).
    private static string StudioMovies => JsonSerializer.Serialize(new[]
    {
        new { id = 11, studioTitle = "IEnergy", monitored = false, hasFile = true },
        new { id = 12, studioTitle = "IEnergy", monitored = true, hasFile = false },
        new { id = 99, studioTitle = "Other Studio", monitored = false, hasFile = true },
    });

    // Loop-safety: no request may name a search command, and no refresh body may carry an empty id array (which
    // Whisparr reads as "refresh ALL entities"). A metadata refresh (RefreshStudios/RefreshPerformers) is allowed.
    private static void AssertLoopSafe(FakeHttpMessageHandler handler)
    {
        Assert.DoesNotContain(handler.Requests, r => r.Body?.Contains("\"name\":\"MoviesSearch\"", StringComparison.OrdinalIgnoreCase) == true);
        Assert.DoesNotContain(handler.Requests, r => r.Body?.Contains("Search", StringComparison.OrdinalIgnoreCase) == true);
        Assert.DoesNotContain(handler.Requests, r => r.Body?.Contains("\"studioIds\":[]", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(handler.Requests, r => r.Body?.Contains("\"performerIds\":[]", StringComparison.Ordinal) == true);
    }

    // ---- NewReleases: targeted refresh + wait + unmonitor the fileless back-catalogue ----

    [Fact]
    public async Task StudioMonitor_V3_NewReleases_RefreshesThenUnmonitorsFilelessBackCatalogue()
    {
        var handler = FakeHttpMessageHandler.Sequence(
            Ok($"[{StudioRow(42, monitored: false)}]"), // studio GET -> present, not yet monitored
            Ok(StudioRow(42, monitored: true)),          // PUT flip -> true (existing row: no verify loop)
            Ok(CommandQueued(777)),                       // POST /command (targeted refresh) -> queued id 777
            Ok(CommandCompleted(777)),                    // GET /command/777 -> completed
            Ok($"[{StudioRow(42, monitored: true)}]"),   // cascade: studio GET (resolve title for attribution)
            Ok(StudioMovies),                             // cascade: movie set
            Accepted());                                  // cascade: PUT /movie/editor monitored:false -> 202

        var result = await AdapterOn(handler).SetEntityMonitorAsync(
            BaseUrl, ApiKey, EntityKind.Studio, StudioStashId, monitored: true,
            MonitorScope.NewReleases, "/data/media", 4, OriginTag, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.True(result.Value!.Monitored);

        // A single-id targeted refresh, scoped to the resolved studio id 42 — never an empty/global array.
        var refresh = Assert.Single(handler.Requests, r => r.Method == HttpMethod.Post && r.Url.EndsWith("/api/v3/command", StringComparison.Ordinal));
        Assert.Contains("\"name\":\"RefreshStudios\"", refresh.Body);
        Assert.Contains("\"studioIds\":[42]", refresh.Body);
        Assert.DoesNotContain("\"studioIds\":[]", refresh.Body);

        // The command's status is read before the reconcile.
        Assert.Contains(handler.Requests, r => r.Method == HttpMethod.Get && r.Url.EndsWith("/api/v3/command/777", StringComparison.Ordinal));

        // Only the fileless attributed row (12) is unmonitored; the owned row (11) and the other studio (99) are left out.
        var editor = Assert.Single(handler.Requests, r => r.Method == HttpMethod.Put && r.Url.EndsWith("/api/v3/movie/editor", StringComparison.Ordinal));
        Assert.Contains("\"movieIds\":[12]", editor.Body);
        Assert.Contains("\"monitored\":false", editor.Body);

        AssertLoopSafe(handler);
    }

    // ---- AllScenes: targeted refresh + wait + keep the back-catalogue monitored ----

    [Fact]
    public async Task StudioMonitor_V3_AllScenes_RefreshesThenKeepsBackCatalogueMonitored()
    {
        var handler = FakeHttpMessageHandler.Sequence(
            Ok($"[{StudioRow(42, monitored: false)}]"),
            Ok(StudioRow(42, monitored: true)),
            Ok(CommandQueued(777)),
            Ok(CommandCompleted(777)),
            Ok($"[{StudioRow(42, monitored: true)}]"),
            Ok(StudioMovies),
            Accepted());

        var result = await AdapterOn(handler).SetEntityMonitorAsync(
            BaseUrl, ApiKey, EntityKind.Studio, StudioStashId, monitored: true,
            MonitorScope.AllScenes, "/data/media", 4, OriginTag, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.True(result.Value!.Monitored);

        var refresh = Assert.Single(handler.Requests, r => r.Method == HttpMethod.Post && r.Url.EndsWith("/api/v3/command", StringComparison.Ordinal));
        Assert.Contains("\"studioIds\":[42]", refresh.Body);

        // AllScenes keeps everything monitored: both attributed rows (11,12), monitored:true.
        var editor = Assert.Single(handler.Requests, r => r.Method == HttpMethod.Put && r.Url.EndsWith("/api/v3/movie/editor", StringComparison.Ordinal));
        Assert.Contains("\"movieIds\":[11,12]", editor.Body);
        Assert.Contains("\"monitored\":true", editor.Body);

        AssertLoopSafe(handler);
    }

    // ---- Performer NewReleases: the refresh names RefreshPerformers with the single performer id ----

    [Fact]
    public async Task PerformerMonitor_V3_NewReleases_FiresTargetedPerformerRefresh()
    {
        var movies = JsonSerializer.Serialize(new[]
        {
            new { id = 21, performerForeignIds = new[] { PerformerStashId }, monitored = true, hasFile = false },
            new { id = 22, performerForeignIds = new[] { PerformerStashId }, monitored = false, hasFile = true },
        });
        var handler = FakeHttpMessageHandler.Sequence(
            Ok(PerformerRow(7, monitored: false)),  // performer GET -> present
            Ok(PerformerRow(7, monitored: true)),    // PUT flip -> true
            Ok(CommandQueued(888)),                   // POST /command refresh -> queued 888
            Ok(CommandCompleted(888)),                // GET /command/888 -> completed
            Ok(PerformerRow(7, monitored: true)),    // cascade: performer GET (existence)
            Ok(movies),                               // cascade: movie set
            Accepted());                              // cascade: PUT /movie/editor monitored:false

        var result = await AdapterOn(handler).SetEntityMonitorAsync(
            BaseUrl, ApiKey, EntityKind.Performer, PerformerStashId, monitored: true,
            MonitorScope.NewReleases, "/data/media", 4, OriginTag, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.True(result.Value!.Monitored);

        var refresh = Assert.Single(handler.Requests, r => r.Method == HttpMethod.Post && r.Url.EndsWith("/api/v3/command", StringComparison.Ordinal));
        Assert.Contains("\"name\":\"RefreshPerformers\"", refresh.Body);
        Assert.Contains("\"performerIds\":[7]", refresh.Body);
        Assert.DoesNotContain("\"performerIds\":[]", refresh.Body);

        // Only the fileless attributed row (21) is unmonitored.
        var editor = Assert.Single(handler.Requests, r => r.Method == HttpMethod.Put && r.Url.EndsWith("/api/v3/movie/editor", StringComparison.Ordinal));
        Assert.Contains("\"movieIds\":[21]", editor.Body);
        Assert.Contains("\"monitored\":false", editor.Body);

        AssertLoopSafe(handler);
    }

    // ---- An empty back-catalogue (no fileless attributed rows) issues NO editor call ----

    [Fact]
    public async Task StudioMonitor_V3_NewReleases_EmptyBackCatalogue_IssuesNoEditorCall()
    {
        var ownedOnly = JsonSerializer.Serialize(new[]
        {
            new { id = 11, studioTitle = "IEnergy", monitored = true, hasFile = true }, // owned, not back-catalogue
        });
        var handler = FakeHttpMessageHandler.Sequence(
            Ok($"[{StudioRow(42, monitored: false)}]"),
            Ok(StudioRow(42, monitored: true)),
            Ok(CommandQueued(777)),
            Ok(CommandCompleted(777)),
            Ok($"[{StudioRow(42, monitored: true)}]"),
            Ok(ownedOnly));

        var result = await AdapterOn(handler).SetEntityMonitorAsync(
            BaseUrl, ApiKey, EntityKind.Studio, StudioStashId, monitored: true,
            MonitorScope.NewReleases, "/data/media", 4, OriginTag, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.DoesNotContain(handler.Requests, r => r.Url.Contains("/api/v3/movie/editor", StringComparison.Ordinal));
        AssertLoopSafe(handler);
    }

    // ---- Monitor-OFF fires NO refresh and NO editor call ----

    [Fact]
    public async Task StudioMonitor_V3_Off_FiresNoRefreshAndNoEditor()
    {
        var handler = FakeHttpMessageHandler.Sequence(
            Ok($"[{StudioRow(42, monitored: true)}]"), // studio GET -> present, monitored
            Ok(StudioRow(42, monitored: false)));       // PUT flip -> false

        var result = await AdapterOn(handler).SetEntityMonitorAsync(
            BaseUrl, ApiKey, EntityKind.Studio, StudioStashId, monitored: false,
            MonitorScope.NewReleases, "/data/media", 4, OriginTag, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.False(result.Value!.Monitored);
        Assert.DoesNotContain(handler.Requests, r => r.Url.Contains("/api/v3/command", StringComparison.Ordinal));
        Assert.DoesNotContain(handler.Requests, r => r.Url.Contains("/api/v3/movie/editor", StringComparison.Ordinal));
    }
}
