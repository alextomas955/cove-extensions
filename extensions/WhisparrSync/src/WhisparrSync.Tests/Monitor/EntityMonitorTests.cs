using System.Net;
using System.Text.Json;
using WhisparrSync.Adapters;
using WhisparrSync.Client;
using WhisparrSync.Monitor;
using WhisparrSync.Options;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.Monitor;

/// <summary>
/// The executable contract for the monitor semantics (studio/performer add + status + loop-safety): the
/// add-then-flip ordering, 409/absent-as-success (no duplicate), OFF-only-PUT, origin-tagging, the
/// count projection with its empty-attribution degrade, and the v2 graceful deferral. Drives the full
/// <see cref="EntityMonitor"/> → <see cref="V3Adapter"/> → <see cref="WhisparrClient"/> composition against a
/// programmable <see cref="FakeHttpMessageHandler"/> so the exact outbound call order + payloads are asserted
/// with no live Whisparr.
/// </summary>
public sealed class EntityMonitorTests
{
    private const string BaseUrl = "http://localhost:6969";
    private const string ApiKey = "test-api-key";
    private const string StudioStashId = "157c9e0d-5f8e-446a-b1c5-dddf3cb5b2d1";
    private const string PerformerStashId = "9a4c1e2b-3d5f-4a6b-8c7d-1e2f3a4b5c6d";

    // On v2 the outward id is a TPDB site id: 3372 (Vixen) is present in V2Fixtures.SeriesArray, 3417 (Tushy) absent.
    private const string V2AddedTpdb = "3372";
    private const string V2AbsentTpdb = "3417";

    // The create-path verify read-back: the just-added site (id 3, the SeriesAddResponse id) now monitored:true,
    // so the create-path monitor verify passes on the first attempt (no re-PUT).
    private const string CreatedV2SiteMonitored = """
        [ { "id": 3, "tvdbId": 3417, "title": "Tushy", "titleSlug": "tushy", "path": "/config/media/Tushy", "monitored": true, "tags": [] } ]
        """;

    private static WhisparrOptions V3Options => new()
    {
        BaseUrl = BaseUrl,
        ApiKey = ApiKey,
        SelectedVersion = "v3",
        DetectedVersion = "3.3.4.808",
        QualityProfileId = 4,
    };

    // Zero settle delay so the studio create-path verify loop exercises its re-assert LOGIC without
    // sleeping (the delay only governs real-world timing, not the loop's correctness).
    private static EntityMonitor MonitorFor(FakeHttpMessageHandler handler, WhisparrOptions? options = null)
        => new(new WhisparrClient(new HttpClient(handler)), options ?? V3Options, TimeSpan.Zero);

    // A single root: the file-less monitor-add derives its root from the fallback rule, and one root resolves
    // trivially to that root (no stored RootFolderId to select among several).
    private static string RootFolderList => JsonSerializer.Serialize(new[]
    {
        new { id = 2, path = "/data/media", accessible = true, freeSpace = 1L },
    });

    private static string TagListWithOrigin => JsonSerializer.Serialize(new[]
    {
        new { id = 5, label = EntityMonitor.OriginTagLabel },
    });

    private static string StudioRow(int id, bool monitored, int sceneCount = 0, int totalSceneCount = 0) => JsonSerializer.Serialize(new
    {
        id,
        foreignId = StudioStashId,
        title = "IEnergy",
        monitored,
        qualityProfileId = 4,
        rootFolderPath = "/data/media",
        tags = new[] { 5 },
        sceneCount,
        totalSceneCount,
    });

    private static string PerformerRow(int id, bool monitored, int sceneCount = 0, int totalSceneCount = 0) => JsonSerializer.Serialize(new
    {
        id,
        foreignId = PerformerStashId,
        fullName = "Miyu Aizawa",
        monitored,
        qualityProfileId = 4,
        rootFolderPath = "/data/media",
        tags = new[] { 5 },
        sceneCount,
        totalSceneCount,
    });

    private static Func<HttpResponseMessage> Ok(string body)
        => FakeHttpMessageHandler.Respond(HttpStatusCode.OK, "application/json", body);

    private static Func<HttpResponseMessage> Created(string body)
        => FakeHttpMessageHandler.Respond(HttpStatusCode.Created, "application/json", body);

    private static Func<HttpResponseMessage> Status(HttpStatusCode status)
        => FakeHttpMessageHandler.Respond(status, "application/json", "{}");

    // The catalogue-population refresh's POST /command reply (a queued command handle) and the GET /command/{id}
    // status the wait loop polls to completed.
    private static string CommandQueued(int id) => JsonSerializer.Serialize(new { id, status = "queued" });

    private static string CommandCompleted(int id) => JsonSerializer.Serialize(new { id, status = "completed" });

    // The loop-safety invariant asserted on every mutating flow. A v3 monitor-ON now legitimately rides
    // POST /command for a TARGETED metadata refresh (RefreshStudios/RefreshPerformers), so the assertion is
    // narrower than "no /command": no request NAMES a search verb, and no add/flip body opts INTO a grab
    // (searchFor*:true). A metadata refresh carries no search intent and can never grab.
    private static void AssertNoSearchTriggered(FakeHttpMessageHandler handler)
    {
        Assert.DoesNotContain(handler.Requests, r => IsSearchCommand(r.Body));
        Assert.DoesNotContain(handler.Requests, r => r.Body?.Contains("searchForMovie\":true", StringComparison.OrdinalIgnoreCase) == true);
        Assert.DoesNotContain(handler.Requests, r => r.Body?.Contains("searchForMissingEpisodes\":true", StringComparison.OrdinalIgnoreCase) == true);
    }

    private static bool IsSearchCommand(string? body)
        => body is not null
            && (body.Contains("\"name\":\"MoviesSearch\"", StringComparison.OrdinalIgnoreCase)
                || body.Contains("\"name\":\"EpisodeSearch\"", StringComparison.OrdinalIgnoreCase)
                || body.Contains("\"name\":\"SeriesSearch\"", StringComparison.OrdinalIgnoreCase));

    // No status/count path ever calls StashDB: counts come only from Whisparr's movie set.
    private static void AssertNoStashDbCall(FakeHttpMessageHandler handler)
        => Assert.DoesNotContain(
            handler.Requests,
            r => r.Url.Contains("stashdb", StringComparison.OrdinalIgnoreCase)
                || r.Url.Contains("graphql", StringComparison.OrdinalIgnoreCase));

    // ---- monitor ON, absent studio -> add(false) then PUT(true) ----

    [Fact]
    public async Task Monitor_on_absent_studio_adds_monitored_false_then_puts_true()
    {
        var handler = FakeHttpMessageHandler.Sequence(
            Ok(RootFolderList),          // EntityMonitor resolves the root path
            Ok(TagListWithOrigin),       // EntityMonitor ensures the origin tag (found, no create)
            Ok("[]"),                    // studio GET by stashId -> absent
            Created(StudioRow(42, monitored: false)), // POST add (monitored:false)
            Ok(StudioRow(42, monitored: true)),        // PUT flip (monitored:true)
            Ok($"[{StudioRow(42, monitored: true)}]"), // verify read-back -> stable true
            Ok(CommandQueued(777)),                     // population: POST /command (targeted refresh)
            Ok(CommandCompleted(777)),                  // population: GET /command/777 -> completed
            Ok($"[{StudioRow(42, monitored: true)}]"), // cascade: studio GET (attribution)
            Ok("[]"));                                  // cascade: empty movie set -> no editor call

        var result = await MonitorFor(handler).SetMonitorAsync(EntityKind.Studio, StudioStashId, monitored: true, MonitorScope.NewReleases, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.True(result.Value!.Added);
        Assert.True(result.Value.Monitored);

        // Ordered add-then-flip: ... -> GET studio -> POST studio(false) -> PUT studio/42(true).
        var studioCalls = handler.Requests.Where(r => r.Url.Contains("/api/v3/studio")).ToList();
        Assert.Equal(HttpMethod.Get, studioCalls[0].Method);
        Assert.Equal(HttpMethod.Post, studioCalls[1].Method);
        Assert.EndsWith("/api/v3/studio", studioCalls[1].Url);
        Assert.Contains("\"monitored\":false", studioCalls[1].Body);
        Assert.Contains("\"rootFolderPath\":\"/data/media\"", studioCalls[1].Body); // resolved root path
        Assert.Contains("\"qualityProfileId\":4", studioCalls[1].Body);            // stored quality profile
        Assert.Contains("\"tags\":[5]", studioCalls[1].Body);                       // origin tag
        Assert.Equal(HttpMethod.Put, studioCalls[2].Method);
        Assert.EndsWith("/api/v3/studio/42", studioCalls[2].Url);
        Assert.Contains("\"monitored\":true", studioCalls[2].Body);

        // A stable studio (read-back confirms true) does NOT trigger an unnecessary extra PUT.
        Assert.Single(handler.Requests, r => r.Method == HttpMethod.Put);
        AssertNoSearchTriggered(handler);
    }

    // ---- (regression): fresh create whose monitored is reset by the post-create RefreshStudios ----
    // The extension must re-read after the flip, observe the revert, re-assert monitored, and return the
    // VERIFIED read-back (true) — never the optimistic requested value the PUT response reported.

    [Fact]
    public async Task Monitor_on_absent_studio_reverted_by_refresh_reasserts_and_returns_verified_true()
    {
        var handler = FakeHttpMessageHandler.Sequence(
            Ok(RootFolderList),
            Ok(TagListWithOrigin),
            Ok("[]"),                                     // studio GET -> absent
            Created(StudioRow(42, monitored: false)),     // POST add
            Ok(StudioRow(42, monitored: true)),           // flip PUT -> responds true (but not authoritative)
            Ok($"[{StudioRow(42, monitored: false)}]"),   // verify read-back #1 -> RefreshStudios reset it
            Ok(StudioRow(42, monitored: true)),           // re-assert PUT
            Ok($"[{StudioRow(42, monitored: true)}]"),    // verify read-back #2 -> now durable true
            Ok(CommandQueued(777)),                        // population: POST /command
            Ok(CommandCompleted(777)),                     // population: GET /command/777 -> completed
            Ok($"[{StudioRow(42, monitored: true)}]"),    // cascade: studio GET (attribution)
            Ok("[]"));                                     // cascade: empty movie set -> no editor call

        var result = await MonitorFor(handler).SetMonitorAsync(EntityKind.Studio, StudioStashId, monitored: true, MonitorScope.NewReleases, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.True(result.Value!.Added);
        Assert.True(result.Value.Monitored); // VERIFIED durable, not the requested optimistic value

        // Retried: the initial flip PUT + one re-assert PUT = exactly two PUTs to /studio/42.
        var studioPuts = handler.Requests
            .Where(r => r.Method == HttpMethod.Put && r.Url.EndsWith("/api/v3/studio/42", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(2, studioPuts.Count);
        Assert.All(studioPuts, p => Assert.Contains("\"monitored\":true", p.Body));

        // The verify pass re-reads (at least two studio GETs after the create).
        Assert.True(handler.Requests.Count(r => r.Method == HttpMethod.Get && r.Url.Contains("/api/v3/studio")) >= 2);

        // Loop-safety holds through the retries: still no /command and no searchForMovie.
        AssertNoSearchTriggered(handler);
    }

    // ---- monitor ON, existing studio (POST 409) -> re-read + PUT, NO duplicate, Added:false ----

    [Fact]
    public async Task Monitor_on_studio_post_409_rereads_and_puts_without_duplicating()
    {
        var handler = FakeHttpMessageHandler.Sequence(
            Ok(RootFolderList),
            Ok(TagListWithOrigin),
            Ok("[]"),                                   // initial GET -> appears absent
            Status(HttpStatusCode.Conflict),            // POST -> 409 (already exists)
            Ok($"[{StudioRow(42, monitored: false)}]"), // re-read GET -> the existing row
            Ok(StudioRow(42, monitored: true)),          // PUT flip
            Ok(CommandQueued(777)),                      // population: POST /command
            Ok(CommandCompleted(777)),                   // population: GET /command/777 -> completed
            Ok($"[{StudioRow(42, monitored: true)}]"),  // cascade: studio GET (attribution)
            Ok("[]"));                                   // cascade: empty movie set -> no editor call

        var result = await MonitorFor(handler).SetMonitorAsync(EntityKind.Studio, StudioStashId, monitored: true, MonitorScope.NewReleases, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.False(result.Value!.Added);   // 409 = it already existed; not created by us
        Assert.True(result.Value.Monitored); // still ends monitored

        // No duplicate: exactly one POST to /studio across the whole flow.
        Assert.Single(handler.Requests, r => r.Method == HttpMethod.Post && r.Url.EndsWith("/api/v3/studio", StringComparison.Ordinal));
        Assert.Single(handler.Requests, r => r.Method == HttpMethod.Put);
        AssertNoSearchTriggered(handler);
    }

    // ---- monitor OFF: PUT(false) only, no POST, no delete ----

    [Fact]
    public async Task Monitor_off_puts_false_only_no_post_no_delete()
    {
        var handler = FakeHttpMessageHandler.Sequence(
            Ok($"[{StudioRow(42, monitored: true)}]"), // studio GET -> present, currently monitored
            Ok(StudioRow(42, monitored: false)));       // PUT flip to false

        var result = await MonitorFor(handler).SetMonitorAsync(EntityKind.Studio, StudioStashId, monitored: false, MonitorScope.NewReleases, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.False(result.Value!.Monitored);
        Assert.False(result.Value.Added);

        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Post);
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Delete);
        Assert.Single(handler.Requests, r => r.Method == HttpMethod.Put);
        // OFF does no tag/root work: no rootfolder or tag lookup happens.
        Assert.DoesNotContain(handler.Requests, r => r.Url.Contains("/api/v3/rootfolder"));
        Assert.DoesNotContain(handler.Requests, r => r.Url.Contains("/api/v3/tag"));
    }

    // ---- performer GET 500 treated as absent -> add path runs ----

    [Fact]
    public async Task Monitor_on_performer_get_500_is_absent_and_runs_add_path()
    {
        var handler = FakeHttpMessageHandler.Sequence(
            Ok(RootFolderList),
            Ok(TagListWithOrigin),
            Status(HttpStatusCode.InternalServerError), // performer GET 500 -> absent
            Created(PerformerRow(7, monitored: false)),  // POST add
            Ok(PerformerRow(7, monitored: true)),         // PUT flip
            Ok(CommandQueued(888)),                       // population: POST /command (RefreshPerformers)
            Ok(CommandCompleted(888)),                    // population: GET /command/888 -> completed
            Ok(PerformerRow(7, monitored: true)),         // cascade: performer GET (existence)
            Ok("[]"));                                    // cascade: empty movie set -> no editor call

        var result = await MonitorFor(handler).SetMonitorAsync(EntityKind.Performer, PerformerStashId, monitored: true, MonitorScope.NewReleases, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.True(result.Value!.Added);
        Assert.True(result.Value.Monitored);
        Assert.Single(handler.Requests, r => r.Method == HttpMethod.Post && r.Url.EndsWith("/api/v3/performer", StringComparison.Ordinal));
        AssertNoSearchTriggered(handler);
    }

    // ---- origin tag created when absent, then applied to the add payload ----

    [Fact]
    public async Task Monitor_on_creates_origin_tag_when_absent_and_applies_its_id()
    {
        var handler = FakeHttpMessageHandler.Sequence(
            Ok(RootFolderList),
            Ok("[]"),                                                    // tag list -> origin tag absent
            Created(JsonSerializer.Serialize(new { id = 9, label = EntityMonitor.OriginTagLabel })), // POST tag
            Ok("[]"),                                                    // studio GET -> absent
            Created(StudioRow(42, monitored: false)),
            Ok(StudioRow(42, monitored: true)),                          // PUT flip
            Ok($"[{StudioRow(42, monitored: true)}]"),                   // verify read-back -> stable
            Ok(CommandQueued(777)),                                      // population: POST /command
            Ok(CommandCompleted(777)),                                   // population: GET /command/777 -> completed
            Ok($"[{StudioRow(42, monitored: true)}]"),                   // cascade: studio GET (attribution)
            Ok("[]"));                                                   // cascade: empty movie set -> no editor call

        var result = await MonitorFor(handler).SetMonitorAsync(EntityKind.Studio, StudioStashId, monitored: true, MonitorScope.NewReleases, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        var tagCreate = Assert.Single(handler.Requests, r => r.Method == HttpMethod.Post && r.Url.EndsWith("/api/v3/tag", StringComparison.Ordinal));
        Assert.Contains(EntityMonitor.OriginTagLabel, tagCreate.Body);
        var studioPost = Assert.Single(handler.Requests, r => r.Method == HttpMethod.Post && r.Url.EndsWith("/api/v3/studio", StringComparison.Ordinal));
        Assert.Contains("\"tags\":[9]", studioPost.Body); // the created tag id flows into the add
    }

    // ---- AllScenes scope cascade: ON+AllScenes ALSO bulk-monitors the existing attributed scenes ----
    // Against an already-present (monitored:false) studio so the flip is a single PUT with no create-path
    // verify loop, leaving the cascade (studio GET + movie GET + movie/editor PUT) as the tail to assert.

    [Fact]
    public async Task StudioMonitor_V3_AllScenes_BulkMonitorsAttributedScenes_NoSearch()
    {
        var movies = JsonSerializer.Serialize(new[]
        {
            new { id = 11, studioTitle = "IEnergy", monitored = false, hasFile = true },
            new { id = 12, studioTitle = "IEnergy", monitored = true, hasFile = false },
            new { id = 99, studioTitle = "Other Studio", monitored = false, hasFile = true },
        });
        var handler = FakeHttpMessageHandler.Sequence(
            Ok(RootFolderList),
            Ok(TagListWithOrigin),
            Ok($"[{StudioRow(42, monitored: false)}]"), // studio GET -> present, not yet monitored
            Ok(StudioRow(42, monitored: true)),          // PUT flip -> true (existing row: no verify loop)
            Ok(CommandQueued(777)),                       // population: POST /command (targeted refresh)
            Ok(CommandCompleted(777)),                    // population: GET /command/777 -> completed
            Ok($"[{StudioRow(42, monitored: true)}]"),   // cascade: studio GET (resolve title for attribution)
            Ok(movies),                                   // cascade: movie set
            Status(HttpStatusCode.Accepted));             // cascade: PUT /movie/editor -> 202

        var result = await MonitorFor(handler).SetMonitorAsync(EntityKind.Studio, StudioStashId, monitored: true, MonitorScope.AllScenes, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.True(result.Value!.Monitored);

        // The cascade fired: one bulk toggle over the attributed movie ids (11,12 — "Other Studio" excluded),
        // monitored:true. monitoredOnly:false, so both the unmonitored and monitored attributed rows are included.
        var editor = Assert.Single(handler.Requests, r => r.Method == HttpMethod.Put && r.Url.EndsWith("/api/v3/movie/editor", StringComparison.Ordinal));
        Assert.Contains("\"movieIds\":[11,12]", editor.Body);
        Assert.Contains("\"monitored\":true", editor.Body);
        AssertNoSearchTriggered(handler); // the editor never searches -> loop-safe
    }

    // ---- NewReleases scope: ON now OWNS the population moment (targeted refresh + wait) then ACTIVELY
    // unmonitors the discovered fileless back-catalogue, so "New releases only" never silently drifts to
    // "All scenes" via Whisparr's own scheduled refresh. The container studio stays monitored. ----

    [Fact]
    public async Task StudioMonitor_V3_NewReleases_RefreshesThenUnmonitorsFilelessBackCatalogue()
    {
        var movies = JsonSerializer.Serialize(new[]
        {
            new { id = 11, studioTitle = "IEnergy", monitored = false, hasFile = true },  // owned -> untouched
            new { id = 12, studioTitle = "IEnergy", monitored = true, hasFile = false },  // back-catalogue -> unmonitor
            new { id = 99, studioTitle = "Other Studio", monitored = false, hasFile = true },
        });
        var handler = FakeHttpMessageHandler.Sequence(
            Ok(RootFolderList),
            Ok(TagListWithOrigin),
            Ok($"[{StudioRow(42, monitored: false)}]"), // studio GET -> present
            Ok(StudioRow(42, monitored: true)),          // PUT flip -> true
            Ok(CommandQueued(777)),                       // population: POST /command (RefreshStudios{[42]})
            Ok(CommandCompleted(777)),                    // population: GET /command/777 -> completed
            Ok($"[{StudioRow(42, monitored: true)}]"),   // cascade: studio GET (attribution)
            Ok(movies),                                   // cascade: movie set
            Status(HttpStatusCode.Accepted));             // cascade: PUT /movie/editor monitored:false -> 202

        var result = await MonitorFor(handler).SetMonitorAsync(EntityKind.Studio, StudioStashId, monitored: true, MonitorScope.NewReleases, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.True(result.Value!.Monitored);

        var refresh = Assert.Single(handler.Requests, r => r.Method == HttpMethod.Post && r.Url.EndsWith("/api/v3/command", StringComparison.Ordinal));
        Assert.Contains("\"name\":\"RefreshStudios\"", refresh.Body);
        Assert.Contains("\"studioIds\":[42]", refresh.Body);
        Assert.DoesNotContain("\"studioIds\":[]", refresh.Body);

        // The fileless back-catalogue (12) is unmonitored; the owned row (11) and the other studio (99) are excluded.
        var editor = Assert.Single(handler.Requests, r => r.Method == HttpMethod.Put && r.Url.EndsWith("/api/v3/movie/editor", StringComparison.Ordinal));
        Assert.Contains("\"movieIds\":[12]", editor.Body);
        Assert.Contains("\"monitored\":false", editor.Body);
        AssertNoSearchTriggered(handler);
    }

    [Fact]
    public async Task StudioMonitor_V3_Off_DoesNotCascade()
    {
        var handler = FakeHttpMessageHandler.Sequence(
            Ok($"[{StudioRow(42, monitored: true)}]"), // studio GET -> present, monitored
            Ok(StudioRow(42, monitored: false)));       // PUT flip -> false

        // Even AllScenes cascades only on ON; an OFF toggle leaves the scene-level flags alone.
        var result = await MonitorFor(handler).SetMonitorAsync(EntityKind.Studio, StudioStashId, monitored: false, MonitorScope.AllScenes, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.False(result.Value!.Monitored);
        Assert.DoesNotContain(handler.Requests, r => r.Url.Contains("/api/v3/movie/editor", StringComparison.Ordinal));
    }

    // ---- status counts read off Whisparr's own entity resource, no movie set, no StashDB call ----

    [Fact]
    public async Task Studio_status_reports_whisparr_scene_counts_and_makes_no_stashdb_call()
    {
        // Whisparr's own sceneCount (present in library) / totalSceneCount (full StashDB catalog); no movie fetch.
        var handler = FakeHttpMessageHandler.Json($"[{StudioRow(42, monitored: true, sceneCount: 1, totalSceneCount: 147)}]");

        var result = await MonitorFor(handler).GetStatusAsync(EntityKind.Studio, StudioStashId, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.True(result.Value!.Added);
        Assert.True(result.Value.Monitored);
        Assert.Equal(1, result.Value.ScenesPresent);
        Assert.Equal(147, result.Value.ScenesTotal);
        Assert.True(result.Value.HasCounts);
        AssertNoStashDbCall(handler);
    }

    [Fact]
    public async Task Performer_status_reports_whisparr_scene_counts()
    {
        var handler = FakeHttpMessageHandler.Json(PerformerRow(7, monitored: true, sceneCount: 1, totalSceneCount: 1));

        var result = await MonitorFor(handler).GetStatusAsync(EntityKind.Performer, PerformerStashId, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.Equal(1, result.Value!.ScenesPresent);
        Assert.Equal(1, result.Value.ScenesTotal);
        AssertNoStashDbCall(handler);
    }

    // ---- empty catalog degrades to no count fragment (HasCounts false) ----

    [Fact]
    public async Task Studio_status_with_no_catalog_degrades_to_no_counts()
    {
        var handler = FakeHttpMessageHandler.Json($"[{StudioRow(42, monitored: true, sceneCount: 0, totalSceneCount: 0)}]");

        var result = await MonitorFor(handler).GetStatusAsync(EntityKind.Studio, StudioStashId, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.True(result.Value!.Added);
        Assert.True(result.Value.Monitored);
        Assert.Equal(0, result.Value.ScenesTotal);
        Assert.False(result.Value.HasCounts); // degrade: render bare "Monitored in Whisparr", not "0 of 0"
    }

    [Fact]
    public async Task Absent_studio_status_is_not_added_and_has_no_counts()
    {
        var handler = FakeHttpMessageHandler.Json("[]"); // studio GET -> absent

        var result = await MonitorFor(handler).GetStatusAsync(EntityKind.Studio, StudioStashId, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.False(result.Value!.Added);
        Assert.False(result.Value.Monitored);
        Assert.False(result.Value.HasCounts);
    }

    // ---- v2 performer defers gracefully: a classified non-Ok, never a throw or a wire call ----
    // (a studio GOes on v2 — it maps to a v2 SITE keyed on the TPDB id; see V2AdapterTests. A performer has
    // no v2 entity, so it stays a clean deferral.)

    [Fact]
    public async Task V2_set_entity_monitor_performer_defers_gracefully_without_any_call()
    {
        var handler = FakeHttpMessageHandler.Json("{}");
        var adapter = new V2Adapter(new WhisparrClient(new HttpClient(handler)));

        var result = await adapter.SetEntityMonitorAsync(
            BaseUrl, ApiKey, EntityKind.Performer, PerformerStashId, monitored: true,
            MonitorScope.NewReleases, "/data/media", 4, [5], CancellationToken.None);

        Assert.Equal(WhisparrResultState.VersionMismatch, result.State);
        Assert.Equal(0, handler.CallCount); // no throw, and NO wire call at all
    }

    [Fact]
    public async Task V2_get_entity_status_defers_gracefully_without_any_call()
    {
        var handler = FakeHttpMessageHandler.Json("{}");
        var adapter = new V2Adapter(new WhisparrClient(new HttpClient(handler)));

        var result = await adapter.GetEntityStatusAsync(BaseUrl, ApiKey, EntityKind.Performer, PerformerStashId, CancellationToken.None);

        Assert.Equal(WhisparrResultState.VersionMismatch, result.State);
        Assert.Equal(0, handler.CallCount);
    }

    // A v2 studio routes to the real SITE adapter path (21-02): a studio status resolves the site by its TPDB
    // id and counts its episodes — no longer the pre-reversal blanket deferral.
    [Fact]
    public async Task EntityMonitor_on_v2_options_routes_studio_status_to_the_site_adapter()
    {
        var handler = FakeHttpMessageHandler.Sequence(
            Ok(V2Fixtures.SeriesArray),        // GET /series -> Vixen (tvdbId 3372) present, monitored
            Ok(V2Fixtures.EpisodesSeries1));   // GET /episode?seriesId=1 -> its episodes

        var result = await MonitorFor(handler, V3Options with { SelectedVersion = "v2" })
            .GetStatusAsync(EntityKind.Studio, V2AddedTpdb, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.True(result.Value!.Added);
        Assert.Contains(handler.Requests, r => r.Url.Contains("/api/v3/series", StringComparison.Ordinal));
    }

    // A v2 studio monitor-ON routes to the SITE add-then-flip (resolve root + origin tag, then add
    // non-grabbing and flip). Loop-safety holds: the add is search-free (no /command).
    [Fact]
    public async Task EntityMonitor_on_v2_options_routes_studio_monitor_on_to_the_site_add_then_flip()
    {
        var handler = FakeHttpMessageHandler.Sequence(
            Ok(RootFolderList),                                                            // resolve root path
            Ok(TagListWithOrigin),                                                         // ensure origin tag
            Ok(V2Fixtures.SeriesArray),                                                    // GET /series -> 3417 absent
            Ok(V2Fixtures.SeriesLookup),                                                   // lookup tpdb:3417
            Created(V2Fixtures.SeriesAddResponse),                                         // POST /series (non-grabbing)
            FakeHttpMessageHandler.Respond(HttpStatusCode.Accepted, "application/json", V2Fixtures.SeriesPutResponse), // PUT flip
            Ok(CreatedV2SiteMonitored));                                                   // create-path verify read-back -> true

        var result = await MonitorFor(handler, V3Options with { SelectedVersion = "v2" })
            .SetMonitorAsync(EntityKind.Studio, V2AbsentTpdb, monitored: true, MonitorScope.NewReleases, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.True(result.Value!.Added);
        Assert.True(result.Value.Monitored);
        Assert.Contains(handler.Requests, r => r.Method == HttpMethod.Post && r.Url.EndsWith("/api/v3/series", StringComparison.Ordinal));
        AssertNoSearchTriggered(handler);
    }

    [Fact]
    public async Task EntityMonitor_on_v2_options_defers_monitor_off_without_any_call()
    {
        var handler = FakeHttpMessageHandler.Json("{}");
        var options = V3Options with { SelectedVersion = "v2" };

        var result = await MonitorFor(handler, options)
            .SetMonitorAsync(EntityKind.Performer, PerformerStashId, monitored: false, MonitorScope.NewReleases, CancellationToken.None);

        Assert.Equal(WhisparrResultState.VersionMismatch, result.State);
        Assert.Empty(handler.Requests);
        Assert.Equal(0, handler.CallCount);
    }

    // A v2 performer has no monitorable entity, so a monitor-ON MUST defer BEFORE resolving the root / origin
    // tag — no stray GET /rootfolder, GET /tag, or above all a POST /tag creating cove-sync on the v2 host.
    [Fact]
    public async Task EntityMonitor_on_v2_options_defers_performer_monitor_on_without_any_call()
    {
        var handler = FakeHttpMessageHandler.Json("{}");
        var options = V3Options with { SelectedVersion = "v2" };

        var result = await MonitorFor(handler, options)
            .SetMonitorAsync(EntityKind.Performer, PerformerStashId, monitored: true, MonitorScope.NewReleases, CancellationToken.None);

        Assert.Equal(WhisparrResultState.VersionMismatch, result.State);
        Assert.Empty(handler.Requests);
        Assert.Equal(0, handler.CallCount);
    }
}
