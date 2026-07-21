using System.Net;
using System.Text.Json;
using WhisparrSync.Client;
using WhisparrSync.Options;
using WhisparrSync.Push;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.Push;

/// <summary>
/// The executable contract for the v3 scene mutation verbs (exclude/un-exclude, interactive grab,
/// upgrade search) + their batch fan-out and the add-defaults options wiring.
/// Drives the full <see cref="SceneActions"/> → V3Adapter → <see cref="WhisparrClient"/>
/// composition against a programmable <see cref="FakeHttpMessageHandler"/> so the exact outbound URL/verb/body
/// is asserted with no live Whisparr. Load-bearing invariants: the un-exclude DELETE id is resolved
/// server-side by foreignId match, the add/exclude verbs never grab, and a v2 selection
/// defers VersionMismatch with ZERO wire calls (v3-only, LOCKED).
/// </summary>
public sealed class SceneExclusionActionsTests
{
    private const string BaseUrl = "http://localhost:6969";
    private const string ApiKey = "test-api-key";
    private const string SceneStashId = "3f2a1b4c-5d6e-4f70-8a9b-0c1d2e3f4a5b";
    private const string OtherStashId = "aaaaaaaa-5d6e-4f70-8a9b-0c1d2e3f4a5b";
    private const string RootPath = "/data/media";
    private const int ProfileId = 4;

    private static WhisparrOptions V3Options => new()
    {
        BaseUrl = BaseUrl,
        ApiKey = ApiKey,
        SelectedVersion = "v3",
        DetectedVersion = "3.3.4.808",
        QualityProfileId = ProfileId,
    };

    private static SceneActions ActionsFor(FakeHttpMessageHandler handler, WhisparrOptions? options = null)
        => new(new WhisparrClient(new HttpClient(handler)), options ?? V3Options, new FakeCoveLibraryPort());

    private static Func<HttpResponseMessage> Ok(string body)
        => FakeHttpMessageHandler.Respond(HttpStatusCode.OK, "application/json", body);

    private static Func<HttpResponseMessage> Created(string body)
        => FakeHttpMessageHandler.Respond(HttpStatusCode.Created, "application/json", body);

    private static Func<HttpResponseMessage> Status(HttpStatusCode status)
        => FakeHttpMessageHandler.Respond(status, "application/json", "{}");

    private static string RootFolderList => JsonSerializer.Serialize(new[]
    {
        new { id = 2, path = RootPath, accessible = true, freeSpace = 1L },
    });

    private static string TagList(params (int Id, string Label)[] tags)
        => JsonSerializer.Serialize(tags.Select(t => new { id = t.Id, label = t.Label }));

    // Eros's GET /api/v3/exclusions returns movieTitle/movieYear (not title/year) — mirror that so the
    // read-model binding is exercised by the fake, matching the live wire.
    private static string ExclusionRow(int id, string foreignId)
        => JsonSerializer.Serialize(new { id, foreignId, movieTitle = "A Scene", movieYear = 2024 });

    private static string ExclusionList(params string[] rows) => $"[{string.Join(",", rows)}]";

    private static string Movie(int id, string stashId, bool monitored)
        => JsonSerializer.Serialize(new
        {
            id,
            foreignId = stashId,
            stashId,
            title = "A Scene",
            monitored,
            hasFile = false,
            qualityProfileId = ProfileId,
            rootFolderPath = RootPath,
            tags = new[] { 5 },
        });

    // loop-safety: NO exclude/un-exclude/grab path targets /command or sets searchForMovie:true.
    private static void AssertNoGrabCommand(FakeHttpMessageHandler handler)
    {
        Assert.DoesNotContain(handler.Requests, r => r.Url.Contains("/api/v3/command", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            handler.Requests,
            r => r.Body?.Replace(" ", "", StringComparison.Ordinal)
                .Contains("\"searchForMovie\":true", StringComparison.OrdinalIgnoreCase) == true);
    }

    // ---- ExcludeScene POSTs the exclusion, idempotent ----

    [Fact]
    public async Task ExcludeScene_posts_exclusion_with_foreign_id_and_api_key()
    {
        var handler = FakeHttpMessageHandler.Sequence(Created(ExclusionRow(9, SceneStashId)));

        var result = await ActionsFor(handler).ExcludeSceneAsync(SceneStashId, "A Scene", 2024, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.True(result.Value);
        var post = Assert.Single(handler.Requests, r => r.Method == HttpMethod.Post);
        Assert.EndsWith("/api/v3/exclusions", post.Url);
        Assert.Contains($"\"foreignId\":\"{SceneStashId}\"", post.Body);
        // Eros validates the exclusion title/year as movieTitle/movieYear — a plain "title" 400s
        // ("'Movie Title' must not be empty"). Pin the real field names so the wrong shape can't pass.
        Assert.Contains("\"movieTitle\":\"A Scene\"", post.Body);
        Assert.Contains("\"movieYear\":2024", post.Body);
        Assert.DoesNotContain("\"title\":", post.Body);
        AssertNoGrabCommand(handler);
    }

    [Fact]
    public async Task ExcludeScene_duplicate_is_idempotent_success()
    {
        var handler = FakeHttpMessageHandler.Sequence(Status(HttpStatusCode.Conflict));

        var result = await ActionsFor(handler).ExcludeSceneAsync(SceneStashId, "A Scene", 2024, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.True(result.Value);
    }

    // ---- UnExcludeScene resolves the DELETE id server-side by foreignId match ----

    [Fact]
    public async Task UnExcludeScene_resolves_the_delete_id_server_side_by_foreign_id_match()
    {
        // The exclusion list carries a decoy row first; the DELETE must target the row whose foreignId equals
        // the scene's StashDB id (77), NEVER a caller-supplied id.
        var handler = FakeHttpMessageHandler.Sequence(
            Ok(ExclusionList(ExclusionRow(13, OtherStashId), ExclusionRow(77, SceneStashId))),
            Status(HttpStatusCode.OK));

        var result = await ActionsFor(handler).UnExcludeSceneAsync(SceneStashId, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        var del = Assert.Single(handler.Requests, r => r.Method == HttpMethod.Delete);
        Assert.EndsWith("/api/v3/exclusions/77", del.Url);
        AssertNoGrabCommand(handler);
    }

    [Fact]
    public async Task UnExcludeScene_not_excluded_is_an_ok_no_op_with_no_delete()
    {
        var handler = FakeHttpMessageHandler.Sequence(Ok(ExclusionList(ExclusionRow(13, OtherStashId))));

        var result = await ActionsFor(handler).UnExcludeSceneAsync(SceneStashId, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.True(result.Value);
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Delete);
        Assert.Equal(1, handler.CallCount); // only the exclusion list read
    }

    [Fact]
    public async Task UnExcludeScene_404_on_delete_is_still_success()
    {
        var handler = FakeHttpMessageHandler.Sequence(
            Ok(ExclusionList(ExclusionRow(77, SceneStashId))),
            Status(HttpStatusCode.NotFound));

        var result = await ActionsFor(handler).UnExcludeSceneAsync(SceneStashId, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.True(result.Value);
    }

    // ---- GrabRelease POSTs /api/v3/release with the guid+indexerId+movieId body ----

    [Fact]
    public async Task GrabRelease_posts_release_with_guid_indexer_id_and_movie_id()
    {
        var handler = FakeHttpMessageHandler.Sequence(Status(HttpStatusCode.OK));

        var result = await ActionsFor(handler).GrabReleaseAsync("indexer-guid-1", 3, 42, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        var post = Assert.Single(handler.Requests, r => r.Method == HttpMethod.Post);
        Assert.EndsWith("/api/v3/release", post.Url);
        Assert.Contains("\"guid\":\"indexer-guid-1\"", post.Body);
        Assert.Contains("\"indexerId\":3", post.Body);
        // movieId is REQUIRED: without it Whisparr answers 404 "Unable to find matching movie".
        Assert.Contains("\"movieId\":42", post.Body);
    }

    // ---- SearchForUpgrades posts MoviesSearch when allowed; no-op when disabled ----

    [Fact]
    public async Task SearchForUpgrades_posts_movies_search_when_upgrades_allowed()
    {
        var handler = FakeHttpMessageHandler.Sequence(Status(HttpStatusCode.OK));

        var result = await ActionsFor(handler).SearchForUpgradesAsync(11, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        var post = Assert.Single(handler.Requests, r => r.Method == HttpMethod.Post);
        Assert.EndsWith("/api/v3/command", post.Url);
        Assert.Contains("\"name\":\"MoviesSearch\"", post.Body);
        Assert.Contains("\"movieIds\":[11]", post.Body);
    }

    [Fact]
    public async Task SearchForUpgrades_is_a_no_op_when_upgrades_disabled()
    {
        var options = V3Options with { AllowQualityUpgrades = false };
        var handler = FakeHttpMessageHandler.Json("{}");

        var result = await ActionsFor(handler, options).SearchForUpgradesAsync([11, 12], CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.Equal(BulkActionResult.Empty, result.Value);
        Assert.Equal(0, handler.CallCount); // upgrades off -> no command issued
    }

    // ---- Add-defaults wiring: monitor-new default + extra tags flow into the add path ----

    [Fact]
    public async Task AddScene_uses_monitor_new_default_false_and_still_never_grabs()
    {
        var options = V3Options with { MonitorNewByDefault = false };
        var handler = FakeHttpMessageHandler.Sequence(
            Ok(RootFolderList),
            Ok(TagList((5, AddContextResolver.OriginTagLabel))),
            Created(Movie(42, SceneStashId, monitored: false)));

        var result = await ActionsFor(handler, options).AddSceneAsync(SceneStashId, "A Scene", CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        var post = Assert.Single(handler.Requests, r => r.Method == HttpMethod.Post && r.Url.EndsWith("/api/v3/movie", StringComparison.Ordinal));
        Assert.Contains("\"monitored\":false", post.Body);
        Assert.Contains("\"searchForMovie\":false", post.Body);
        AssertNoGrabCommand(handler);
    }

    [Fact]
    public async Task AddScene_applies_extra_tags_plus_the_always_present_origin_tag()
    {
        // TagsOnAdd carries a user label "cove"; the origin "cove-sync" is applied unconditionally. Both are
        // resolved from a single tag-list read (no per-tag round trip) and both ids appear on the add body.
        var options = V3Options with { TagsOnAdd = new[] { "cove" } };
        var handler = FakeHttpMessageHandler.Sequence(
            Ok(RootFolderList),
            Ok(TagList((5, AddContextResolver.OriginTagLabel), (8, "cove"))),
            Created(Movie(42, SceneStashId, monitored: true)));

        var result = await ActionsFor(handler, options).AddSceneAsync(SceneStashId, "A Scene", CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        var post = Assert.Single(handler.Requests, r => r.Method == HttpMethod.Post && r.Url.EndsWith("/api/v3/movie", StringComparison.Ordinal));
        Assert.Contains("\"tags\":[5,8]", post.Body!.Replace(" ", "", StringComparison.Ordinal));
        // A single tag-list read served both labels — no second list, no create for present tags.
        Assert.Single(handler.Requests, r => r.Method == HttpMethod.Get && r.Url.EndsWith("/api/v3/tag", StringComparison.Ordinal));
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Post && r.Url.EndsWith("/api/v3/tag", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AddScene_creates_a_missing_extra_tag_then_applies_it()
    {
        var options = V3Options with { TagsOnAdd = new[] { "favorites" } };
        var handler = FakeHttpMessageHandler.Sequence(
            Ok(RootFolderList),
            Ok(TagList((5, AddContextResolver.OriginTagLabel))), // "favorites" absent -> create
            Created(JsonSerializer.Serialize(new { id = 12, label = "favorites" })),
            Created(Movie(42, SceneStashId, monitored: true)));

        var result = await ActionsFor(handler, options).AddSceneAsync(SceneStashId, "A Scene", CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        var tagPost = Assert.Single(handler.Requests, r => r.Method == HttpMethod.Post && r.Url.EndsWith("/api/v3/tag", StringComparison.Ordinal));
        Assert.Contains("\"label\":\"favorites\"", tagPost.Body);
        var moviePost = Assert.Single(handler.Requests, r => r.Method == HttpMethod.Post && r.Url.EndsWith("/api/v3/movie", StringComparison.Ordinal));
        Assert.Contains("\"tags\":[5,12]", moviePost.Body!.Replace(" ", "", StringComparison.Ordinal));
    }

    // ---- Batch fan-out aggregates total/succeeded/failed ----

    [Fact]
    public async Task ExcludeScenes_batch_aggregates_total_succeeded_failed()
    {
        // Three scenes: first excludes (201), second is a duplicate (409 = success), third fails (500).
        var handler = FakeHttpMessageHandler.Sequence(
            Created(ExclusionRow(1, "a")),
            Status(HttpStatusCode.Conflict),
            Status(HttpStatusCode.InternalServerError));

        var scenes = new[] { new SceneRef("a", "A"), new SceneRef("b", "B"), new SceneRef("c", "C") };
        var result = await ActionsFor(handler).ExcludeScenesAsync(scenes, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.Equal(3, result.Value!.Total);
        Assert.Equal(2, result.Value.Succeeded);
        Assert.Equal(1, result.Value.Failed);
        AssertNoGrabCommand(handler);
    }

    [Fact]
    public async Task AddScenes_batch_adds_each_missing_scene_once_never_grabbing()
    {
        var handler = FakeHttpMessageHandler.Sequence(
            Ok(RootFolderList),
            Ok(TagList((5, AddContextResolver.OriginTagLabel))),
            Created(Movie(101, "a", monitored: true)),
            Created(Movie(102, "b", monitored: true)));

        var scenes = new[] { new SceneRef("a", "A"), new SceneRef("b", "B") };
        var result = await ActionsFor(handler).AddScenesAsync(scenes, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.Equal(2, result.Value!.Succeeded);
        var posts = handler.Requests.Where(r => r.Method == HttpMethod.Post && r.Url.EndsWith("/api/v3/movie", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, posts.Count);
        Assert.All(posts, p => Assert.Contains("\"searchForMovie\":false", p.Body));
        AssertNoGrabCommand(handler);
    }

    // ---- v2 defers every new verb + batch with ZERO wire calls (v3-only, LOCKED) ----

    [Fact]
    public async Task V2_options_defer_every_new_verb_without_a_wire_call()
    {
        var v2 = V3Options with { SelectedVersion = "v2" };

        var excludeHandler = FakeHttpMessageHandler.Json("{}");
        var exclude = await ActionsFor(excludeHandler, v2).ExcludeSceneAsync(SceneStashId, "S", 2024, CancellationToken.None);
        Assert.Equal(WhisparrResultState.VersionMismatch, exclude.State);
        Assert.Equal(0, excludeHandler.CallCount);

        var unExcludeHandler = FakeHttpMessageHandler.Json("{}");
        var unExclude = await ActionsFor(unExcludeHandler, v2).UnExcludeSceneAsync(SceneStashId, CancellationToken.None);
        Assert.Equal(WhisparrResultState.VersionMismatch, unExclude.State);
        Assert.Equal(0, unExcludeHandler.CallCount);

        var grabHandler = FakeHttpMessageHandler.Json("{}");
        var grab = await ActionsFor(grabHandler, v2).GrabReleaseAsync("g", 1, 1, CancellationToken.None);
        Assert.Equal(WhisparrResultState.VersionMismatch, grab.State);
        Assert.Equal(0, grabHandler.CallCount);

        var upgradeHandler = FakeHttpMessageHandler.Json("{}");
        var upgrade = await ActionsFor(upgradeHandler, v2).SearchForUpgradesAsync(11, CancellationToken.None);
        Assert.Equal(WhisparrResultState.VersionMismatch, upgrade.State);
        Assert.Equal(0, upgradeHandler.CallCount);

        var excludeBatchHandler = FakeHttpMessageHandler.Json("{}");
        var excludeBatch = await ActionsFor(excludeBatchHandler, v2).ExcludeScenesAsync(new[] { new SceneRef("a") }, CancellationToken.None);
        Assert.Equal(WhisparrResultState.VersionMismatch, excludeBatch.State);
        Assert.Equal(0, excludeBatchHandler.CallCount);

        var addBatchHandler = FakeHttpMessageHandler.Json("{}");
        var addBatch = await ActionsFor(addBatchHandler, v2).AddScenesAsync(new[] { new SceneRef("a", "A") }, CancellationToken.None);
        Assert.Equal(WhisparrResultState.VersionMismatch, addBatch.State);
        Assert.Equal(0, addBatchHandler.CallCount);
    }
}
