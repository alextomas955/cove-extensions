using System.Net;
using System.Text.Json;
using WhisparrSync.Adapters;
using WhisparrSync.Client;
using WhisparrSync.Monitor;
using WhisparrSync.Push;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.Client;

/// <summary>
/// The executable contract for the v3 scene semantics (add / monitor / search + availability register): the
/// per-scene add (idempotent, origin-tagged, StashDB id in foreignId AND stashId), the add-then-flip monitor
/// toggle, the sole grab-capable <c>MoviesSearch</c> verb, entity attribution over the movie set, and the v2
/// graceful deferral. Drives <see cref="V3Adapter"/>/<see cref="V2Adapter"/> → <see cref="WhisparrClient"/>
/// against a programmable <see cref="FakeHttpMessageHandler"/> so the exact outbound call order + payloads are
/// asserted with no live Whisparr. The load-bearing invariant: NO add/monitor flow targets
/// <c>/api/v3/command</c> or sets <c>searchForMovie:true</c> — only <see cref="V3Adapter.SearchScenesAsync"/> grabs.
/// </summary>
public sealed class SceneAdapterTests
{
    private const string BaseUrl = "http://localhost:6969";
    private const string ApiKey = "test-api-key";
    private const string SceneStashId = "3f2a1b4c-5d6e-4f70-8a9b-0c1d2e3f4a5b";
    private const string StudioStashId = "157c9e0d-5f8e-446a-b1c5-dddf3cb5b2d1";
    private const string PerformerStashId = "9a4c1e2b-3d5f-4a6b-8c7d-1e2f3a4b5c6d";
    private const string RootPath = "/data/media";
    private const int ProfileId = 4;
    private static readonly int[] OriginTags = [5];

    private static V3Adapter V3(FakeHttpMessageHandler handler) => new(new WhisparrClient(new HttpClient(handler)), TimeSpan.Zero);

    private static V2Adapter V2(FakeHttpMessageHandler handler) => new(new WhisparrClient(new HttpClient(handler)));

    private static Func<HttpResponseMessage> Ok(string body)
        => FakeHttpMessageHandler.Respond(HttpStatusCode.OK, "application/json", body);

    private static Func<HttpResponseMessage> Created(string body)
        => FakeHttpMessageHandler.Respond(HttpStatusCode.Created, "application/json", body);

    private static Func<HttpResponseMessage> Status(HttpStatusCode status)
        => FakeHttpMessageHandler.Respond(status, "application/json", "{}");

    private const string MoviePath = "/data/media/A Scene (2024)";

    private static string MovieRow(int id, bool monitored, bool hasFile = false) => JsonSerializer.Serialize(new
    {
        id,
        foreignId = SceneStashId,
        stashId = SceneStashId,
        title = "A Scene",
        monitored,
        hasFile,
        qualityProfileId = ProfileId,
        rootFolderPath = RootPath,
        path = MoviePath,
        tags = OriginTags,
    });

    private static string StudioRow(int id, string title) => JsonSerializer.Serialize(new
    {
        id,
        foreignId = StudioStashId,
        title,
        monitored = true,
        qualityProfileId = ProfileId,
        rootFolderPath = RootPath,
        tags = OriginTags,
    });

    private static string PerformerRow(int id) => JsonSerializer.Serialize(new
    {
        id,
        foreignId = PerformerStashId,
        fullName = "A Performer",
        monitored = true,
        qualityProfileId = ProfileId,
        rootFolderPath = RootPath,
        tags = OriginTags,
    });

    // loop-safety: across every add + monitor flow no request targets /command and no body sets
    // searchForMovie:true (an add legitimately carries searchForMovie:FALSE, so the assertion is on :true).
    private static void AssertNoGrab(FakeHttpMessageHandler handler)
    {
        Assert.DoesNotContain(handler.Requests, r => r.Url.Contains("/command", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            handler.Requests,
            r => r.Body?.Replace(" ", "", StringComparison.Ordinal)
                .Contains("\"searchForMovie\":true", StringComparison.OrdinalIgnoreCase) == true);
    }

    private static void AssertNoStashDbCall(FakeHttpMessageHandler handler)
        => Assert.DoesNotContain(
            handler.Requests,
            r => r.Url.Contains("stashdb", StringComparison.OrdinalIgnoreCase)
                || r.Url.Contains("graphql", StringComparison.OrdinalIgnoreCase));

    // ---- (1) add an absent scene, StashDB id in foreignId AND stashId, origin-tagged ----

    [Fact]
    public async Task AddScene_absent_posts_per_flags_with_origin_tag_and_stashid_in_both_fields()
    {
        var handler = FakeHttpMessageHandler.Sequence(Created(MovieRow(id: 42, monitored: true)));

        var result = await V3(handler).AddSceneAsync(
            BaseUrl, ApiKey, SceneStashId, title: "A Scene", monitored: true, searchForMovie: false,
            RootPath, ProfileId, OriginTags, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.True(result.Value!.Added);
        Assert.Equal(42, result.Value.MovieId);
        Assert.True(result.Value.Monitored);

        var post = Assert.Single(handler.Requests, r => r.Method == HttpMethod.Post);
        Assert.EndsWith("/api/v3/movie", post.Url);
        Assert.Contains($"\"foreignId\":\"{SceneStashId}\"", post.Body);   // StashDB id in foreignId
        Assert.Contains($"\"stashId\":\"{SceneStashId}\"", post.Body);     // AND in stashId (verified v3 API)
        Assert.Contains("\"monitored\":true", post.Body);                   // caller's monitored flag
        Assert.Contains("\"searchForMovie\":false", post.Body);             // loop-safe default
        Assert.Contains("\"rootFolderPath\":\"/data/media\"", post.Body);   // resolved root
        Assert.Contains("\"qualityProfileId\":4", post.Body);               // resolved profile
        Assert.Contains("\"tags\":[5]", post.Body);                          // origin tag (attribution)
        AssertNoGrab(handler);
    }

    // ---- (2) idempotency: a 409 re-reads to the existing row, Added:false, no duplicate POST ----

    [Fact]
    public async Task AddScene_conflict_rereads_existing_row_without_duplicate_post()
    {
        var handler = FakeHttpMessageHandler.Sequence(
            Status(HttpStatusCode.Conflict),          // POST -> 409 (already present)
            Ok($"[{MovieRow(id: 42, monitored: false)}]")); // re-read GET -> existing row

        var result = await V3(handler).AddSceneAsync(
            BaseUrl, ApiKey, SceneStashId, title: "A Scene", monitored: true, searchForMovie: false,
            RootPath, ProfileId, OriginTags, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.False(result.Value!.Added);   // 409 = already existed, not created by us
        Assert.Equal(42, result.Value.MovieId);

        Assert.Single(handler.Requests, r => r.Method == HttpMethod.Post && r.Url.EndsWith("/api/v3/movie", StringComparison.Ordinal));
        AssertNoGrab(handler);
    }

    // ---- (3) monitor ON of an absent scene -> POST(false, no search) then PUT(true), in order ----

    [Fact]
    public async Task SetSceneMonitor_on_absent_adds_monitored_false_then_puts_true()
    {
        var handler = FakeHttpMessageHandler.Sequence(
            Ok("[]"),                                   // GET movie -> absent
            Created(MovieRow(id: 99, monitored: false)), // POST add (monitored:false)
            Ok(MovieRow(id: 99, monitored: true)));       // PUT flip (monitored:true)

        var result = await V3(handler).SetSceneMonitorAsync(
            BaseUrl, ApiKey, SceneStashId, title: "A Scene", monitored: true,
            RootPath, ProfileId, OriginTags, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.True(result.Value!.Added);
        Assert.True(result.Value.Monitored);
        Assert.Equal(99, result.Value.MovieId);

        // Ordered add-then-flip over /api/v3/movie: GET -> POST -> PUT /movie/99.
        var movieCalls = handler.Requests.Where(r => r.Url.Contains("/api/v3/movie")).ToList();
        Assert.Equal(HttpMethod.Get, movieCalls[0].Method);
        Assert.Equal(HttpMethod.Post, movieCalls[1].Method);
        Assert.Contains("\"monitored\":false", movieCalls[1].Body);   // POST registers unmonitored
        Assert.Contains("\"searchForMovie\":false", movieCalls[1].Body);
        Assert.Equal(HttpMethod.Put, movieCalls[2].Method);
        Assert.EndsWith("/api/v3/movie/99", movieCalls[2].Url);
        Assert.Contains("\"monitored\":true", movieCalls[2].Body);    // PUT flips to monitored
        Assert.Single(handler.Requests, r => r.Method == HttpMethod.Post);
        AssertNoGrab(handler);
    }

    // ---- (4) monitor OFF PUTs false only (no POST); OFF on an absent scene is a no-op ----

    [Fact]
    public async Task SetSceneMonitor_off_present_puts_false_only_no_post()
    {
        var handler = FakeHttpMessageHandler.Sequence(
            Ok($"[{MovieRow(id: 42, monitored: true)}]"), // GET -> present, monitored
            Ok(MovieRow(id: 42, monitored: false)));       // PUT flip to false

        var result = await V3(handler).SetSceneMonitorAsync(
            BaseUrl, ApiKey, SceneStashId, title: "A Scene", monitored: false,
            RootPath, ProfileId, OriginTags, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.False(result.Value!.Monitored);
        Assert.False(result.Value.Added);
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Post);
        Assert.Single(handler.Requests, r => r.Method == HttpMethod.Put);
        AssertNoGrab(handler);
    }

    [Fact]
    public async Task SetSceneMonitor_off_absent_is_a_noop_no_put_no_post()
    {
        var handler = FakeHttpMessageHandler.Sequence(Ok("[]")); // GET -> absent

        var result = await V3(handler).SetSceneMonitorAsync(
            BaseUrl, ApiKey, SceneStashId, title: "A Scene", monitored: false,
            RootPath, ProfileId, OriginTags, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.False(result.Value!.Added);
        Assert.False(result.Value.Monitored);
        Assert.Equal(0, result.Value.MovieId);
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Post);
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Put);
        AssertNoGrab(handler);
    }

    // ---- (4b) the monitor-flip PUT body carries a non-empty `path` (present + absent legs) ----

    [Fact]
    public async Task SetSceneMonitor_present_flip_put_body_carries_non_empty_path()
    {
        // Whisparr Eros's PUT /movie/{id} rejects a body with no top-level `path`
        // ("'Path' must not be empty."), so the flip must echo the existing movie's on-disk path.
        var handler = FakeHttpMessageHandler.Sequence(
            Ok($"[{MovieRow(id: 42, monitored: false)}]"),  // GET -> present, unmonitored (carries path)
            Ok(MovieRow(id: 42, monitored: true)));           // PUT flip to monitored

        var result = await V3(handler).SetSceneMonitorAsync(
            BaseUrl, ApiKey, SceneStashId, title: "A Scene", monitored: true,
            RootPath, ProfileId, OriginTags, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        var put = Assert.Single(handler.Requests, r => r.Method == HttpMethod.Put);
        Assert.EndsWith("/api/v3/movie/42", put.Url);
        Assert.Contains($"\"path\":\"{MoviePath}\"", put.Body);
        Assert.Contains("\"monitored\":true", put.Body);
        AssertNoGrab(handler);
    }

    [Fact]
    public async Task SetSceneMonitor_on_absent_flip_put_body_carries_created_movie_path()
    {
        // The add-then-flip leg (absent + ON) must echo the just-created movie's path onto the PUT — the
        // second half of the path invariant (the created row's path, not the requested root, is what Eros validates).
        var handler = FakeHttpMessageHandler.Sequence(
            Ok("[]"),                                     // GET -> absent
            Created(MovieRow(id: 99, monitored: false)),  // POST created (carries path)
            Ok(MovieRow(id: 99, monitored: true)));        // PUT flip

        var result = await V3(handler).SetSceneMonitorAsync(
            BaseUrl, ApiKey, SceneStashId, title: "A Scene", monitored: true,
            RootPath, ProfileId, OriginTags, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        var put = Assert.Single(handler.Requests, r => r.Method == HttpMethod.Put);
        Assert.Contains($"\"path\":\"{MoviePath}\"", put.Body);
        AssertNoGrab(handler);
    }

    // ---- (4c) an add with a null Cove title still posts a NON-EMPTY title (Eros rejects empty) ----

    [Fact]
    public async Task AddScene_null_title_posts_a_non_empty_fallback_title()
    {
        var handler = FakeHttpMessageHandler.Sequence(Created(MovieRow(id: 42, monitored: false)));

        var result = await V3(handler).AddSceneAsync(
            BaseUrl, ApiKey, SceneStashId, title: null, monitored: false, searchForMovie: false,
            RootPath, ProfileId, OriginTags, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        var post = Assert.Single(handler.Requests, r => r.Method == HttpMethod.Post);
        Assert.Contains($"\"title\":\"Scene {SceneStashId}\"", post.Body);   // last-resort fallback, never null/empty
        Assert.DoesNotContain("\"title\":null", post.Body);
        Assert.DoesNotContain("\"title\":\"\"", post.Body);
        AssertNoGrab(handler);
    }

    // ---- (4d) owned-scene in-place adopt: PUT (re-point path) -> RescanMovie command -> verify GET, no copy/grab ----

    [Fact]
    public async Task ImportOwnedScene_adopt_repoints_path_then_rescans_then_verifies_no_copy_no_grab()
    {
        const string ownedFolder = "/data/media/A Scene [3f2a1b4c]";
        const string ownedFile = ownedFolder + "/scene.mkv";

        var handler = FakeHttpMessageHandler.Sequence(
            Ok(MovieRow(id: 42, monitored: true)),                 // PUT /movie/42 (re-point path) -> updated row
            Status(HttpStatusCode.OK),                             // POST /command (RescanMovie) accepted
            Ok($"[{MovieRow(id: 42, monitored: true, hasFile: true)}]")); // GET /movie?stashId= verify -> hasFile:true

        var scene = new WhisparrMovie(
            Id: 42, Title: "A Scene", Year: 2024, StashId: SceneStashId, ForeignId: SceneStashId, ItemType: "scene",
            Monitored: true, HasFile: false, MovieFile: null, QualityProfileId: ProfileId, RootFolderPath: RootPath,
            Tags: OriginTags, Path: "/data/whisparr/A Scene (2024)");

        var result = await V3(handler).ImportOwnedSceneAsync(
            BaseUrl, ApiKey, scene, ownedFile, OwnedImportMode.InPlaceAdopt, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.True(result.Value);

        // Exact call order: PUT the re-point, THEN the rescan command, THEN the verify read.
        Assert.Equal(HttpMethod.Put, handler.Requests[0].Method);
        Assert.EndsWith("/api/v3/movie/42", handler.Requests[0].Url);
        Assert.Contains($"\"path\":\"{ownedFolder}\"", handler.Requests[0].Body);  // re-point to Cove's own folder
        Assert.Contains("\"monitored\":true", handler.Requests[0].Body);           // monitored state preserved

        Assert.Equal(HttpMethod.Post, handler.Requests[1].Method);
        Assert.EndsWith("/api/v3/command", handler.Requests[1].Url);
        Assert.Contains("\"name\":\"RescanMovie\"", handler.Requests[1].Body);
        Assert.Contains("\"movieIds\":[42]", handler.Requests[1].Body);

        Assert.Equal(HttpMethod.Get, handler.Requests[2].Method);
        Assert.Contains("/api/v3/movie?stashId=", handler.Requests[2].Url);

        // No add (POST /movie), no copy (ManualImport / manualimport listing), no grab (MoviesSearch / searchForMovie:true).
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Post && r.Url.EndsWith("/api/v3/movie", StringComparison.Ordinal));
        Assert.DoesNotContain(handler.Requests, r => r.Url.Contains("/api/v3/manualimport", StringComparison.Ordinal));
        Assert.DoesNotContain(handler.Requests, r => r.Body?.Contains("ManualImport", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(handler.Requests, r => r.Body?.Contains("MoviesSearch", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(
            handler.Requests,
            r => r.Body?.Replace(" ", "", StringComparison.Ordinal)
                .Contains("\"searchForMovie\":true", StringComparison.OrdinalIgnoreCase) == true);
    }

    // ---- (5) search posts exactly one MoviesSearch; an empty id set sends no command ----

    [Fact]
    public async Task SearchScenes_posts_single_movies_search_over_the_given_ids()
    {
        var handler = FakeHttpMessageHandler.Json("{}");

        var result = await V3(handler).SearchScenesAsync(BaseUrl, ApiKey, [11, 22, 33], CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.Equal(3, result.Value!.Total);
        var command = Assert.Single(handler.Requests, r => r.Method == HttpMethod.Post && r.Url.EndsWith("/api/v3/command", StringComparison.Ordinal));
        Assert.Contains("\"name\":\"MoviesSearch\"", command.Body);
        Assert.Contains("\"movieIds\":[11,22,33]", command.Body);
    }

    [Fact]
    public async Task SearchScenes_empty_ids_sends_no_command()
    {
        var handler = FakeHttpMessageHandler.Json("{}");

        var result = await V3(handler).SearchScenesAsync(BaseUrl, ApiKey, [], CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.Equal(BulkActionResult.Empty, result.Value);
        Assert.Equal(0, handler.CallCount); // NO wire call at all
    }

    // ---- (6) attribution: studio-by-Title / performer-by-ForeignIds + monitoredOnly, no StashDB ----

    [Fact]
    public async Task ListAttributedMovieIds_studio_attributes_by_title_and_filters_monitored()
    {
        var movies = JsonSerializer.Serialize(new[]
        {
            new { id = 1, studioTitle = "IEnergy", monitored = true },
            new { id = 2, studioTitle = "IEnergy", monitored = false },
            new { id = 3, studioTitle = "Other Studio", monitored = true },
        });

        // monitoredOnly = false -> both IEnergy movies (1, 2), never the "Other Studio" row.
        var all = await V3(FakeHttpMessageHandler.Sequence(Ok($"[{StudioRow(7, "IEnergy")}]"), Ok(movies)))
            .ListAttributedMovieIdsAsync(BaseUrl, ApiKey, EntityKind.Studio, StudioStashId, monitoredOnly: false, CancellationToken.None);
        Assert.Equal(WhisparrResultState.Ok, all.State);
        Assert.Equal([1, 2], all.Value!);

        // monitoredOnly = true -> only the monitored IEnergy row (1).
        var monitoredHandler = FakeHttpMessageHandler.Sequence(Ok($"[{StudioRow(7, "IEnergy")}]"), Ok(movies));
        var monitored = await V3(monitoredHandler)
            .ListAttributedMovieIdsAsync(BaseUrl, ApiKey, EntityKind.Studio, StudioStashId, monitoredOnly: true, CancellationToken.None);
        Assert.Equal([1], monitored.Value!);
        AssertNoStashDbCall(monitoredHandler);
    }

    [Fact]
    public async Task ListAttributedMovieIds_performer_attributes_by_foreign_id()
    {
        var movies = JsonSerializer.Serialize(new[]
        {
            new { id = 10, performerForeignIds = new[] { PerformerStashId }, monitored = true },
            new { id = 11, performerForeignIds = new[] { "someone-else" }, monitored = true },
            new { id = 12, performerForeignIds = new[] { PerformerStashId }, monitored = true },
        });
        var handler = FakeHttpMessageHandler.Sequence(Ok(PerformerRow(9)), Ok(movies));

        var result = await V3(handler)
            .ListAttributedMovieIdsAsync(BaseUrl, ApiKey, EntityKind.Performer, PerformerStashId, monitoredOnly: false, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.Equal([10, 12], result.Value!);
        AssertNoStashDbCall(handler);
    }

    [Fact]
    public async Task ListAttributedMovieIds_absent_entity_returns_empty()
    {
        var result = await V3(FakeHttpMessageHandler.Json("[]"))
            .ListAttributedMovieIdsAsync(BaseUrl, ApiKey, EntityKind.Studio, StudioStashId, monitoredOnly: false, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.Empty(result.Value!);
    }

    // ---- (7) LOOP-SAFETY across a full add+monitor sequence: no /command, no searchForMovie:true ----

    [Fact]
    public async Task Add_then_monitor_flows_never_issue_a_command_or_search_true()
    {
        var addHandler = FakeHttpMessageHandler.Sequence(Created(MovieRow(id: 42, monitored: true)));
        await V3(addHandler).AddSceneAsync(
            BaseUrl, ApiKey, SceneStashId, "A Scene", monitored: true, searchForMovie: false,
            RootPath, ProfileId, OriginTags, CancellationToken.None);
        AssertNoGrab(addHandler);

        var monitorHandler = FakeHttpMessageHandler.Sequence(
            Ok("[]"), Created(MovieRow(id: 99, monitored: false)), Ok(MovieRow(id: 99, monitored: true)));
        await V3(monitorHandler).SetSceneMonitorAsync(
            BaseUrl, ApiKey, SceneStashId, "A Scene", monitored: true,
            RootPath, ProfileId, OriginTags, CancellationToken.None);
        AssertNoGrab(monitorHandler);
    }

    // ---- (8) v2 scene surface: per-scene add/monitor DEFER (no POST /episode); search + attributed GO ----

    [Fact]
    public async Task V2_add_scene_defers_without_any_call()
    {
        var handler = FakeHttpMessageHandler.Json("{}");
        var result = await V2(handler).AddSceneAsync(
            BaseUrl, ApiKey, SceneStashId, "A Scene", monitored: true, searchForMovie: false,
            RootPath, ProfileId, OriginTags, CancellationToken.None);

        Assert.Equal(WhisparrResultState.VersionMismatch, result.State);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task V2_set_scene_monitor_defers_without_any_call()
    {
        var handler = FakeHttpMessageHandler.Json("{}");
        var result = await V2(handler).SetSceneMonitorAsync(
            BaseUrl, ApiKey, SceneStashId, "A Scene", monitored: true,
            RootPath, ProfileId, OriginTags, CancellationToken.None);

        Assert.Equal(WhisparrResultState.VersionMismatch, result.State);
        Assert.Equal(0, handler.CallCount);
    }

    // Episode search is the ONE grab-capable v2 verb — it posts EpisodeSearch over the ids.
    [Fact]
    public async Task V2_search_scenes_posts_episode_search_command()
    {
        var handler = FakeHttpMessageHandler.Json(V2Fixtures.EpisodeSearchCommandResponse);
        var result = await V2(handler).SearchScenesAsync(BaseUrl, ApiKey, [1, 2, 3], CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.EndsWith("/api/v3/command", handler.LastRequest!.RequestUri!.AbsolutePath, StringComparison.Ordinal);
        Assert.Contains("EpisodeSearch", handler.LastRequestBody);
    }

    // A studio's attributed ids are its v2 SITE's episode ids (the search-all input); a performer defers.
    [Fact]
    public async Task V2_list_attributed_movie_ids_studio_enumerates_the_sites_episode_ids()
    {
        var handler = FakeHttpMessageHandler.Sequence(
            Ok(V2Fixtures.SeriesArray),
            Ok(V2Fixtures.EpisodesSeries1));
        var result = await V2(handler).ListAttributedMovieIdsAsync(
            BaseUrl, ApiKey, EntityKind.Studio, "3372", monitoredOnly: false, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.Equal([1, 2, 3], result.Value!);
    }

    [Fact]
    public async Task V2_list_attributed_movie_ids_performer_defers_without_any_call()
    {
        var handler = FakeHttpMessageHandler.Json("{}");
        var result = await V2(handler).ListAttributedMovieIdsAsync(
            BaseUrl, ApiKey, EntityKind.Performer, PerformerStashId, monitoredOnly: false, CancellationToken.None);

        Assert.Equal(WhisparrResultState.VersionMismatch, result.State);
        Assert.Equal(0, handler.CallCount);
    }
}
