using System.Net;
using System.Text.Json;
using WhisparrSync.Adapters;
using WhisparrSync.Client;
using WhisparrSync.Contracts;
using WhisparrSync.Library;
using WhisparrSync.Options;
using WhisparrSync.Push;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.Push;

/// <summary>
/// The executable contract for the scene SERVICE orchestration (add / monitor / bulk / availability register):
/// the origin-tag + root/profile resolve before an add, the local-diff bulk add-all-missing (NO StashDB call), its
/// idempotency, the monitored-only bulk search, and — the load-bearing invariant — that NO add,
/// register, monitor-add, or bulk-add path ever grabs (no <c>/api/v3/command</c>, no <c>searchForMovie:true</c>),
/// so a Cove-owned scene registered in Whisparr never triggers the v1 auto-import. Drives the full
/// <see cref="SceneActions"/> → <see cref="V3Adapter"/> → <see cref="WhisparrClient"/> composition
/// against a programmable <see cref="FakeHttpMessageHandler"/> and a seeded <see cref="FakeCoveLibraryPort"/>.
/// </summary>
[Trait("Tier", "L0")]
public sealed class SceneActionsTests
{
    private const string BaseUrl = "http://localhost:6969";
    private const string ApiKey = "test-api-key";
    private const string SceneStashId = "3f2a1b4c-5d6e-4f70-8a9b-0c1d2e3f4a5b";
    private const string StudioStashId = "157c9e0d-5f8e-446a-b1c5-dddf3cb5b2d1";
    private const string RootPath = "/data/media";
    private const int ProfileId = 4;
    private const int StudioCoveId = 7;
    private static readonly int[] OriginTags = [5];

    // The seeded v2 site's own ThePornDB id (V2Fixtures' series row) — what a v2 catalogue read resolves by.
    private const string V2SiteTpdbId = "3372";

    private static WhisparrOptions V3Options => new()
    {
        BaseUrl = BaseUrl,
        ApiKey = ApiKey,
        SelectedVersion = "v3",
        DetectedVersion = "3.3.4.808",
    };

    private static SceneActions ActionsFor(
        FakeHttpMessageHandler handler, FakeCoveLibraryPort? library = null, WhisparrOptions? options = null)
        => SceneActionsFactory.Build(new WhisparrClient(new HttpClient(handler)), options ?? V3Options, library ?? new FakeCoveLibraryPort());

    // The per-entity read resolves the entity's OWN remote id server-side, in the connected version's family.
    private static FakeCoveLibraryPort WithStudioIdentity(FakeCoveLibraryPort library, string? tpdbId = null)
    {
        library.SeedEntityIdentity(
            EntityKind.Studio, StudioCoveId,
            new CoveEntityIdentity([StudioStashId], tpdbId is null ? [] : [tpdbId]));
        return library;
    }

    private static Func<HttpResponseMessage> Ok(string body)
        => FakeHttpMessageHandler.Respond(HttpStatusCode.OK, "application/json", body);

    private static Func<HttpResponseMessage> Created(string body)
        => FakeHttpMessageHandler.Respond(HttpStatusCode.Created, "application/json", body);

    private static string RootFolderList => JsonSerializer.Serialize(new[]
    {
        new { id = 1, path = "/other", accessible = true, freeSpace = 1L },
        new { id = 2, path = RootPath, accessible = true, freeSpace = 1L },
    });

    private static string TagListWithOrigin => JsonSerializer.Serialize(new[]
    {
        new { id = 5, label = AddContextResolver.OriginTagLabel },
    });

    // Two rows: a sole-offered id would satisfy the first-offered assertions below by accident.
    private static string QualityProfileList => JsonSerializer.Serialize(new[]
    {
        new { id = ProfileId, name = "HD-1080p" },
        new { id = 9, name = "Any" },
    });

    private static string Movie(
        int id, string stashId, bool monitored, bool hasFile = false,
        string? studioTitle = null, string? studioForeignId = null)
        => JsonSerializer.Serialize(new
        {
            id,
            foreignId = stashId,
            stashId,
            title = "A Scene",
            monitored,
            hasFile,
            studioTitle,
            studioForeignId,
            qualityProfileId = ProfileId,
            rootFolderPath = RootPath,
            tags = OriginTags,
        });

    private static string MovieList(params string[] rows) => $"[{string.Join(",", rows)}]";

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

    private static CoveVideo CoveScene(string stashId, int coveId = 0)
        => new(coveId, "A Cove Scene", null, [stashId], [], [], []);

    // A scene with NO Cove title — optionally file-backed so the title fallback can use its basename.
    private static CoveVideo NullTitleScene(string stashId, params string[] filePaths)
        => new(0, null, null, [stashId], [], filePaths, []);

    // loop-safety: NO add/register/monitor-add/bulk-add flow targets /command or sets
    // searchForMovie:true (an add legitimately carries searchForMovie:FALSE, so the assertion is on :true).
    private static void AssertNoGrab(FakeHttpMessageHandler handler)
    {
        Assert.DoesNotContain(handler.Requests, r => r.Url.Contains("/command", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            handler.Requests,
            r => r.Body?.Replace(" ", "", StringComparison.Ordinal)
                .Contains("\"searchForMovie\":true", StringComparison.OrdinalIgnoreCase) == true);
    }

    // The bulk diff never reaches StashDB / stashbox — it is a local Cove-vs-Whisparr comparison.
    private static void AssertNoStashDbCall(FakeHttpMessageHandler handler)
        => Assert.DoesNotContain(
            handler.Requests,
            r => r.Url.Contains("stashdb", StringComparison.OrdinalIgnoreCase)
                || r.Url.Contains("graphql", StringComparison.OrdinalIgnoreCase));

    // ---- (1) AddScene registers monitored:true + searchForMovie:false, origin-tagged ----

    [Fact]
    public async Task AddScene_registers_monitored_false_by_default_search_false_with_origin_tag()
    {
        var handler = FakeHttpMessageHandler.Sequence(
            Ok(RootFolderList),                          // root resolve
            Ok(TagListWithOrigin),                       // origin-tag ensure (found)
            Ok(QualityProfileList),                      // quality-profile resolve
            Created(Movie(42, SceneStashId, monitored: false)));

        var result = await ActionsFor(handler).AddSceneAsync(SceneStashId, "A Scene", CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.True(result.Value!.Added);
        Assert.Equal(42, result.Value.MovieId);

        var post = Assert.Single(handler.Requests, r => r.Method == HttpMethod.Post && r.Url.EndsWith("/api/v3/movie", StringComparison.Ordinal));
        // Whisparr treats monitored-with-no-file as WANTED, and this scene's file is one Cove already holds —
        // monitoring before the file is attached asks Whisparr to grab a duplicate.
        Assert.Contains("\"monitored\":false", post.Body);
        Assert.Contains("\"searchForMovie\":false", post.Body);   // loop-safe: register, never grab
        Assert.Contains("\"tags\":[5]", post.Body);                // origin tag (attribution)
        Assert.Contains($"\"foreignId\":\"{SceneStashId}\"", post.Body);
        AssertNoGrab(handler);
    }

    [Fact]
    public async Task AddScene_monitors_when_the_add_default_is_enabled()
    {
        var handler = FakeHttpMessageHandler.Sequence(
            Ok(RootFolderList),
            Ok(TagListWithOrigin),
            Ok(QualityProfileList),                      // quality-profile resolve
            Created(Movie(42, SceneStashId, monitored: true)));

        var options = V3Options with { MonitorNewByDefault = true };
        var result = await ActionsFor(handler, options: options).AddSceneAsync(SceneStashId, "A Scene", CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.True(result.Value!.Monitored);

        var post = Assert.Single(handler.Requests, r => r.Method == HttpMethod.Post && r.Url.EndsWith("/api/v3/movie", StringComparison.Ordinal));
        Assert.Contains("\"monitored\":true", post.Body);
        Assert.Contains("\"searchForMovie\":false", post.Body);   // still never grabs on add
        AssertNoGrab(handler);
    }

    // ---- (2) SetSceneMonitor ON + absent -> add(monitored:false) then PUT(true), in order ----

    [Fact]
    public async Task SetSceneMonitor_on_absent_adds_monitored_false_then_puts_true()
    {
        var handler = FakeHttpMessageHandler.Sequence(
            Ok(RootFolderList),
            Ok(TagListWithOrigin),
            Ok(QualityProfileList),                      // quality-profile resolve
            Ok("[]"),                                     // GET movie -> absent
            Created(Movie(99, SceneStashId, monitored: false)), // POST add (monitored:false)
            Ok(Movie(99, SceneStashId, monitored: true)));       // PUT flip (monitored:true)

        var result = await ActionsFor(handler).SetSceneMonitorAsync(SceneStashId, "A Scene", monitored: true, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.True(result.Value!.Added);
        Assert.True(result.Value.Monitored);

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

    /// <summary>
    /// A tag's owned set is not tag-scoped, so enumerating "this entity's scenes" for a tag is a whole-library
    /// read. The UI mounts these actions on studio/performer only, but the kind arrives in the REQUEST BODY, so
    /// the refusal has to live server-side. It is classified before any wire call and before the library seam is
    /// touched — the two counters below are what prove nothing was read or sent, rather than merely that the
    /// result was Rejected.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Tag_is_refused_by_the_bulk_entity_actions_before_any_read_or_wire_call(bool addMissing)
    {
        var library = new FakeCoveLibraryPort();
        var handler = FakeHttpMessageHandler.Sequence();
        var actions = ActionsFor(handler, library);

        var result = addMissing
            ? await actions.AddAllMissingAsync(EntityKind.Tag, StudioCoveId, CancellationToken.None)
            : await actions.ReflectOwnedAsync(EntityKind.Tag, StudioCoveId, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Rejected, result.State);
        Assert.Equal(0, library.LoadVideosForEntityCallCount);
        Assert.Empty(handler.Requests);
    }

    // ---- (3) local diff — 2 of 4 Cove scenes are missing -> exactly 2 POSTs, no StashDB ----

    [Fact]
    public async Task AddAllMissing_registers_only_the_missing_scenes_via_a_local_diff_with_no_stashdb_call()
    {
        var library = new FakeCoveLibraryPort();
        library.SeedForEntity(
            EntityKind.Studio, StudioCoveId,
            CoveScene("uuid-1"), CoveScene("uuid-2"), CoveScene("uuid-3"), CoveScene("uuid-4"));

        var handler = FakeHttpMessageHandler.Sequence(
            Ok(RootFolderList),
            Ok(TagListWithOrigin),
            Ok("[]"),                                    // the parent studio's row: not present in Whisparr
            Ok(QualityProfileList),                      // quality-profile resolve
            Created(Movie(101, "uuid-3", monitored: false)),
            Created(Movie(102, "uuid-4", monitored: false)));

        // The studio's own catalogue holds the 2 scenes Cove already shares with Whisparr.
        handler.ServingCatalogue(MovieList(Movie(1, "uuid-1", monitored: true), Movie(2, "uuid-2", monitored: true)));

        var result = await ActionsFor(handler, WithStudioIdentity(library)).AddAllMissingAsync(
            EntityKind.Studio, StudioCoveId, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.Equal(2, result.Value!.Total);      // only the 2 missing are work
        Assert.Equal(2, result.Value.Succeeded);
        Assert.Equal(0, result.Value.Failed);

        // Exactly the 2 missing scenes POST a movie, each monitored:false + searchForMovie:false.
        var posts = handler.Requests.Where(r => r.Method == HttpMethod.Post && r.Url.EndsWith("/api/v3/movie", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, posts.Count);
        Assert.All(posts, p => Assert.Contains("\"monitored\":false", p.Body));
        Assert.All(posts, p => Assert.Contains("\"searchForMovie\":false", p.Body));
        Assert.Contains(posts, p => p.Body!.Contains("\"foreignId\":\"uuid-3\"", StringComparison.Ordinal));
        Assert.Contains(posts, p => p.Body!.Contains("\"foreignId\":\"uuid-4\"", StringComparison.Ordinal));

        Assert.Equal(1, library.LoadVideosForEntityCallCount); // the entity's scenes read once
        AssertNoStashDbCall(handler);                          // local diff, no StashDB egress
        AssertNoGrab(handler);
    }

    // ---- (4) bulk idempotency: a re-run where every scene is now present adds nothing ----

    [Fact]
    public async Task AddAllMissing_is_idempotent_when_every_scene_is_already_present()
    {
        var library = new FakeCoveLibraryPort();
        library.SeedForEntity(
            EntityKind.Studio, StudioCoveId,
            CoveScene("uuid-1"), CoveScene("uuid-2"), CoveScene("uuid-3"), CoveScene("uuid-4"));

        // The studio's catalogue now contains all four (the state after a first add-all-missing).
        var handler = FakeHttpMessageHandler.Json("[]").ServingCatalogue(MovieList(
            Movie(1, "uuid-1", monitored: false), Movie(2, "uuid-2", monitored: false),
            Movie(3, "uuid-3", monitored: false), Movie(4, "uuid-4", monitored: false)));

        var result = await ActionsFor(handler, WithStudioIdentity(library)).AddAllMissingAsync(
            EntityKind.Studio, StudioCoveId, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.Equal(BulkActionResult.Empty, result.Value);
        // No duplicate registration. The one POST is the by-id hydration, which reads rather than writes.
        Assert.DoesNotContain(
            handler.Requests,
            r => r.Method == HttpMethod.Post && !r.Url.EndsWith("/api/v3/movie/bulk", StringComparison.Ordinal));
        // The whole cost of a no-op run: the API description, the sibling read, one hydration. Nothing else is
        // fetched, because nothing is registered.
        Assert.Equal(3, handler.CallCount);
        AssertNoStashDbCall(handler);
    }

    // ---- (4b) every add carries a NON-EMPTY title derived from Cove (never null) ----

    [Fact]
    public async Task AddAllMissing_derives_a_non_empty_title_per_scene_from_cove()
    {
        // Whisparr Eros rejects an add whose title is null ("'Title' must not be empty."). Each add must
        // carry a Cove-derived non-empty title: the file
        // basename when the scene has no Cove title, else a stable Scene {stashId} last resort.
        var library = new FakeCoveLibraryPort();
        library.SeedForEntity(
            EntityKind.Studio, StudioCoveId,
            NullTitleScene("uuid-file", "/data/media/studio/My Scene File.mp4"), // -> basename
            NullTitleScene("uuid-bare"));                                          // -> Scene {stashId}

        var handler = FakeHttpMessageHandler.Sequence(
            Ok(RootFolderList),
            Ok(TagListWithOrigin),
            Ok("[]"),                                    // the parent studio's row: not present in Whisparr
            Ok(QualityProfileList),                      // quality-profile resolve
            Created(Movie(101, "uuid-file", monitored: false)),
            Created(Movie(102, "uuid-bare", monitored: false)));

        // The studio is KNOWN to Whisparr and holds nothing, so both scenes are missing.
        handler.ServingCatalogue(MovieList());

        var result = await ActionsFor(handler, WithStudioIdentity(library)).AddAllMissingAsync(
            EntityKind.Studio, StudioCoveId, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.Equal(2, result.Value!.Succeeded);
        Assert.Equal(0, result.Value.Failed);

        var posts = handler.Requests.Where(r => r.Method == HttpMethod.Post && r.Url.EndsWith("/api/v3/movie", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, posts.Count);
        Assert.All(posts, p => Assert.DoesNotContain("\"title\":null", p.Body));
        Assert.All(posts, p => Assert.DoesNotContain("\"title\":\"\"", p.Body));
        Assert.Contains(posts, p => p.Body!.Contains("\"title\":\"My Scene File.mp4\"", StringComparison.Ordinal));
        Assert.Contains(posts, p => p.Body!.Contains("\"title\":\"Scene uuid-bare\"", StringComparison.Ordinal));
        AssertNoStashDbCall(handler);
        AssertNoGrab(handler);
    }

    // ---- (4b) A studio the user import-excluded in Whisparr is SKIPPED, never counted a failure ----

    // The refusal Whisparr 3.3.4 answers when the scene's studio is on its import-exclusion list.
    private static Func<HttpResponseMessage> ExcludedStudio(string studioTitle) => FakeHttpMessageHandler.Respond(
        HttpStatusCode.InternalServerError,
        "application/json",
        JsonSerializer.Serialize(new
        {
            message = $"Studio: [{studioTitle}] has been excluded",
            description = $"NzbDrone.Core.Exceptions.ExcludedException: Studio: [{studioTitle}] has been excluded",
        }));

    private static Func<HttpResponseMessage> ServerFault() => FakeHttpMessageHandler.Respond(
        HttpStatusCode.InternalServerError,
        "application/json",
        JsonSerializer.Serialize(new { message = "Object reference not set to an instance of an object." }));

    [Fact]
    public async Task AddScenes_counts_an_import_excluded_scene_as_skipped_not_failed()
    {
        var handler = FakeHttpMessageHandler.Sequence(
            Ok(RootFolderList),
            Ok(TagListWithOrigin),
            Ok(QualityProfileList),
            Created(Movie(101, "uuid-1", monitored: false)),
            ExcludedStudio("Hussie Pass"));

        var result = await ActionsFor(handler).AddScenesAsync(
            [new SceneRef("uuid-1", "One"), new SceneRef("uuid-2", "Two")], CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.Equal(2, result.Value!.Total);
        Assert.Equal(1, result.Value.Succeeded);
        Assert.Equal(0, result.Value.Failed);
        Assert.Equal(1, result.Value.Skipped);
        Assert.Contains("excluded in Whisparr", result.Value.Message, StringComparison.Ordinal);
        AssertNoGrab(handler);
    }

    [Fact]
    public async Task AddScenes_still_counts_a_genuine_transport_failure_as_failed()
    {
        var handler = FakeHttpMessageHandler.Sequence(
            Ok(RootFolderList),
            Ok(TagListWithOrigin),
            Ok(QualityProfileList),
            Created(Movie(101, "uuid-1", monitored: false)),
            ServerFault());

        var result = await ActionsFor(handler).AddScenesAsync(
            [new SceneRef("uuid-1", "One"), new SceneRef("uuid-2", "Two")], CancellationToken.None);

        Assert.Equal(1, result.Value!.Succeeded);
        Assert.Equal(1, result.Value.Failed);
        Assert.Equal(0, result.Value.Skipped);
        Assert.Null(result.Value.Message);
    }

    [Fact]
    public async Task AddAllMissing_counts_an_import_excluded_scene_as_skipped_not_failed()
    {
        var library = new FakeCoveLibraryPort();
        library.SeedForEntity(EntityKind.Studio, StudioCoveId, CoveScene("uuid-1"), CoveScene("uuid-2"));

        var handler = FakeHttpMessageHandler.Sequence(
            Ok(RootFolderList),
            Ok(TagListWithOrigin),
            Ok("[]"),                                    // the parent studio's row: not present in Whisparr
            Ok(QualityProfileList),
            Created(Movie(101, "uuid-1", monitored: false)),
            ExcludedStudio("Hussie Pass")).ServingCatalogue(MovieList());

        var result = await ActionsFor(handler, WithStudioIdentity(library)).AddAllMissingAsync(
            EntityKind.Studio, StudioCoveId, CancellationToken.None);

        Assert.Equal(1, result.Value!.Succeeded);
        Assert.Equal(0, result.Value.Failed);
        Assert.Equal(1, result.Value.Skipped);
        AssertNoGrab(handler);
    }

    [Fact]
    public async Task AddScene_single_reports_excluded_rather_than_a_transport_failure()
    {
        var handler = FakeHttpMessageHandler.Sequence(
            Ok(RootFolderList),
            Ok(TagListWithOrigin),
            Ok(QualityProfileList),
            ExcludedStudio("Hussie Pass"));

        var result = await ActionsFor(handler).AddSceneAsync(SceneStashId, "A Scene", CancellationToken.None);

        Assert.Equal(WhisparrResultState.Excluded, result.State);
        Assert.Equal("Studio: [Hussie Pass] has been excluded", result.Reason);
    }

    [Fact]
    public async Task MarkScenesWanted_counts_an_import_excluded_scene_as_skipped_not_failed()
    {
        // An excluded studio refuses the add leg even at monitored:false; the flip PUT is unreachable.
        var handler = FakeHttpMessageHandler.Sequence(
            Ok(RootFolderList),
            Ok(TagListWithOrigin),
            Ok(QualityProfileList),
            Ok(MovieList()),
            ExcludedStudio("Hussie Pass"));

        var result = await ActionsFor(handler).MarkScenesWantedAsync(
            [new SceneRef("uuid-1", "One")], CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.Equal(0, result.Value!.Succeeded);
        Assert.Equal(0, result.Value.Failed);
        Assert.True(result.Value.AllSkipped);
        AssertNoGrab(handler);
    }

    // ---- (5) SearchAllMonitored posts one MoviesSearch over the monitored attributed ids only ----

    [Fact]
    public async Task SearchAllMonitored_posts_one_movies_search_over_monitored_attributed_ids()
    {
        var movies = MovieList(
            Movie(1, "s1", monitored: true, studioTitle: "IEnergy", studioForeignId: StudioStashId),
            Movie(2, "s2", monitored: false, studioTitle: "IEnergy", studioForeignId: StudioStashId),  // unmonitored -> excluded
            Movie(3, "s3", monitored: true, studioTitle: "Other", studioForeignId: "another-studio-id")); // different studio -> excluded

        var handler = FakeHttpMessageHandler.Sequence(
            Ok($"[{StudioRow(StudioCoveId, "IEnergy")}]"),  // GET studio by stashId
            Ok(movies),                                      // GET movie set (attribution)
            Ok("{}"));                                        // POST command

        var result = await ActionsFor(handler).SearchAllMonitoredAsync(EntityKind.Studio, StudioStashId, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.Equal(1, result.Value!.Total);

        var command = Assert.Single(handler.Requests, r => r.Method == HttpMethod.Post && r.Url.EndsWith("/api/v3/command", StringComparison.Ordinal));
        Assert.Contains("\"name\":\"MoviesSearch\"", command.Body);
        Assert.Contains("\"movieIds\":[1]", command.Body);
    }

    // ---- (6) LOOP-SAFETY: no add/monitor-add/bulk-add path issues a grab ----

    [Fact]
    public async Task No_add_register_monitor_or_bulk_path_ever_grabs()
    {
        // AddScene
        var add = FakeHttpMessageHandler.Sequence(
            Ok(RootFolderList), Ok(TagListWithOrigin), Ok(QualityProfileList),
            Created(Movie(1, SceneStashId, monitored: true)));
        await ActionsFor(add).AddSceneAsync(SceneStashId, "S", CancellationToken.None);
        AssertNoGrab(add);

        // SetSceneMonitor ON of an absent scene (the add-then-flip leg)
        var monitor = FakeHttpMessageHandler.Sequence(
            Ok(RootFolderList), Ok(TagListWithOrigin), Ok(QualityProfileList), Ok("[]"),
            Created(Movie(2, SceneStashId, monitored: false)), Ok(Movie(2, SceneStashId, monitored: true)));
        await ActionsFor(monitor).SetSceneMonitorAsync(SceneStashId, "S", monitored: true, CancellationToken.None);
        AssertNoGrab(monitor);

        // AddAllMissing (registers a missing scene)
        var library = new FakeCoveLibraryPort();
        library.SeedForEntity(EntityKind.Studio, StudioCoveId, CoveScene("uuid-9"));
        var bulk = FakeHttpMessageHandler.Sequence(
            Ok(MovieList()), Ok(RootFolderList), Ok(TagListWithOrigin), Ok(QualityProfileList),
            Created(Movie(3, "uuid-9", monitored: false)));
        await ActionsFor(bulk, library).AddAllMissingAsync(EntityKind.Studio, StudioCoveId, CancellationToken.None);
        AssertNoGrab(bulk);
    }

    // ---- (7) an add derives + sends the fallback root path from Whisparr's own list (no stored root id) ----

    [Fact]
    public async Task Add_sends_the_fallback_root_path_derived_from_whisparr()
    {
        // There is no Cove root setting: the add's rootFolderPath is derived per-add. With one root the
        // file-less fallback resolves trivially to it, and that path MUST reach the movie POST — the routing the
        // derivation controls (SceneActions -> AddContextResolver -> ListRootFolders).
        var singleRoot = JsonSerializer.Serialize(new[]
        {
            new { id = 9, path = RootPath, accessible = true, freeSpace = 1L },
        });
        var handler = FakeHttpMessageHandler.Sequence(
            Ok(singleRoot),
            Ok(TagListWithOrigin),
            Ok(QualityProfileList),                      // quality-profile resolve
            Created(Movie(7, SceneStashId, monitored: true)));

        var result = await ActionsFor(handler).AddSceneAsync(SceneStashId, "A Scene", CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        var post = Assert.Single(
            handler.Requests, r => r.Method == HttpMethod.Post && r.Url.EndsWith("/api/v3/movie", StringComparison.Ordinal));
        Assert.Contains($"\"rootFolderPath\":\"{RootPath}\"", post.Body);
        AssertNoGrab(handler);
    }

    // ---- (8) v2 options: the per-scene ADD paths defer wire-free; the studio SEARCH paths GO (21-02) ----

    // v2 has no per-scene add (no POST /episode), so add / register / monitor-add / bulk-add-missing defer
    // BEFORE resolving the root + origin tag (gated on adapter.SupportsSceneAdd) — zero wire calls, no stray
    // tag/root request against the v2 host, and no port read for the bulk diff.
    [Fact]
    public async Task V2_options_defer_the_add_paths_without_a_wire_call()
    {
        var v2 = V3Options with { SelectedVersion = "v2" };
        var library = new FakeCoveLibraryPort();
        library.SeedForEntity(EntityKind.Studio, StudioCoveId, CoveScene("uuid-1"));

        var addHandler = FakeHttpMessageHandler.Json("{}");
        var add = await ActionsFor(addHandler, options: v2).AddSceneAsync(SceneStashId, "S", CancellationToken.None);
        Assert.Equal(WhisparrResultState.VersionMismatch, add.State);
        Assert.Equal(0, addHandler.CallCount);

        var monitorHandler = FakeHttpMessageHandler.Json("{}");
        var monitor = await ActionsFor(monitorHandler, options: v2).SetSceneMonitorAsync(SceneStashId, "S", monitored: true, CancellationToken.None);
        Assert.Equal(WhisparrResultState.VersionMismatch, monitor.State);
        Assert.Equal(0, monitorHandler.CallCount);

        var bulkHandler = FakeHttpMessageHandler.Json("{}");
        var bulk = await ActionsFor(bulkHandler, library, v2).AddAllMissingAsync(EntityKind.Studio, StudioCoveId, CancellationToken.None);
        Assert.Equal(WhisparrResultState.VersionMismatch, bulk.State);
        Assert.Equal(0, bulkHandler.CallCount);
        Assert.Equal(0, library.LoadVideosForEntityCallCount); // deferral is BEFORE the port read too
    }

    // The v2 studio SEARCH surface GOes: search-now posts an EpisodeSearch (the sole grab-capable v2 verb).
    [Fact]
    public async Task V2_search_scene_routes_to_the_episode_search_command()
    {
        var v2 = V3Options with { SelectedVersion = "v2" };
        var handler = FakeHttpMessageHandler.Json(V2Fixtures.EpisodeSearchCommandResponse);

        var result = await ActionsFor(handler, options: v2).SearchSceneAsync(11, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.Contains(handler.Requests, r => r.Url.Contains("/api/v3/command", StringComparison.Ordinal));
    }

    // Search-all-monitored for a studio resolves the v2 SITE by its TPDB id and searches its episodes.
    [Fact]
    public async Task V2_search_all_monitored_studio_routes_to_the_site_adapter()
    {
        var v2 = V3Options with { SelectedVersion = "v2" };
        var handler = FakeHttpMessageHandler.Sequence(
            Ok(V2Fixtures.SeriesArray),                        // resolve the site by tpdb id 3372
            Ok(V2Fixtures.EpisodesSeries1),                    // its episode ids (the search-all input)
            Created(V2Fixtures.EpisodeSearchCommandResponse)); // EpisodeSearch over any monitored ids

        var result = await ActionsFor(handler, options: v2)
            .SearchAllMonitoredAsync(EntityKind.Studio, "3372", CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.Contains(handler.Requests, r => r.Url.Contains("/api/v3/series", StringComparison.Ordinal));
    }

    // ---- (9) ReflectOwned: v2 attaches a Cove-owned file to its matching fileless scene, in place ----

    // Episode 1 in V2Fixtures.EpisodesSeries1 is fileless (tvdbId 1010276, hasFile:false), so an owned Cove
    // video carrying that TPDB id + a file matches it. The file path matches the manualimport listing below.
    private const string OwnedTpdb = "1010276";
    private const string OwnedFilePath = "/config/media/Vixen/scene1.mkv";

    private static CoveVideo OwnedVideo(string tpdbId, string filePath, int coveId = 0)
        => new(coveId, "A Cove Scene", null, [], [tpdbId], [filePath], []);

    private static string ManualImportListing(string path) => JsonSerializer.Serialize(new[]
    {
        new
        {
            path,
            quality = new { quality = new { id = 7, name = "WEB-DL 1080p" }, revision = new { version = 1 } },
            languages = new[] { new { id = 1, name = "English" } },
            rejections = new[] { new { reason = "Invalid season or episode", type = "permanent" } },
        },
    });

    // Episode 1 re-read after the import: now hasFile:true (the ManualImport linked the file in place).
    private const string EpisodeOneLinked = """
        [ { "id": 1, "seriesId": 1, "tvdbId": 1010276, "episodeFileId": 6001, "title": "Payment Extension", "releaseDate": "2016-06-13", "hasFile": true, "monitored": true } ]
        """;

    [Fact]
    public async Task ReflectOwned_v2_imports_the_owned_file_to_its_fileless_scene_in_place()
    {
        var v2 = V3Options with { SelectedVersion = "v2" };
        var library = new FakeCoveLibraryPort();
        library.SeedForEntity(EntityKind.Studio, StudioCoveId, OwnedVideo(OwnedTpdb, OwnedFilePath));

        var handler = FakeHttpMessageHandler.Sequence(
            Ok(V2Fixtures.SeriesArray),           // ListMovies: GET /series
            Ok(V2Fixtures.EpisodesSeries1),       //             GET /episode?seriesId=1 (ep1 fileless)
            Ok(V2Fixtures.EpisodeFilesSeries1),   //             GET /episodefile?seriesId=1
            Ok(ManualImportListing(OwnedFilePath)), // import: GET /manualimport -> lists the owned file
            Ok("{}"),                             //          POST /command (ManualImport)
            Ok(EpisodeOneLinked));                //          GET /episode?seriesId=1 re-read -> hasFile:true

        var result = await ActionsFor(handler, WithStudioIdentity(library, V2SiteTpdbId), v2)
            .ReflectOwnedAsync(EntityKind.Studio, StudioCoveId, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.Equal(1, result.Value!.Total);       // one owned+matched scene attempted
        Assert.Equal(1, result.Value.Succeeded);
        Assert.Equal(0, result.Value.Failed);

        // The import is a targeted ManualImport at the matched episode — NEVER a search (loop-safe, in place).
        var command = Assert.Single(handler.Requests, r => r.Url.EndsWith("/api/v3/command", StringComparison.Ordinal));
        Assert.Contains("\"name\":\"ManualImport\"", command.Body);
        Assert.Contains("\"episodeIds\":[1]", command.Body);
        Assert.DoesNotContain("Search", command.Body!);
        AssertNoStashDbCall(handler);
    }

    // v3 owned-import: the movie is fileless (hasFile:false) and carries the scene's StashDB id, so an owned Cove
    // video with that StashDB id + a file — alone in its own folder — is ADOPTED in place (the movie path is
    // re-pointed to Cove's folder + a rescan), never a copy.
    private const string OwnedV3Folder = "/data/media/A Scene";
    private const string OwnedV3FilePath = OwnedV3Folder + "/scene.mkv";

    private static CoveVideo OwnedV3Video(string stashId, string filePath, int coveId = 0)
        => new(coveId, "A Cove Scene", null, [stashId], [], [filePath], []);

    [Fact]
    public async Task ReflectOwned_v3_adopts_the_owned_file_in_place_repoint_then_rescan()
    {
        var library = new FakeCoveLibraryPort();
        library.SeedForEntity(EntityKind.Studio, StudioCoveId, OwnedV3Video(SceneStashId, OwnedV3FilePath));

        var handler = FakeHttpMessageHandler.Sequence(
            Ok(Movie(42, SceneStashId, monitored: true, hasFile: false)),            // adopt: PUT /movie/42 (re-point path)
            Ok("{}"),                                                                //        POST /command (RescanMovie)
            Ok(MovieList(Movie(42, SceneStashId, monitored: true, hasFile: true)))); //        GET /movie?stashId re-read -> hasFile:true

        handler.ServingCatalogue(MovieList(Movie(42, SceneStashId, monitored: true, hasFile: false)));

        var result = await ActionsFor(handler, WithStudioIdentity(library))
            .ReflectOwnedAsync(EntityKind.Studio, StudioCoveId, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.Equal(1, result.Value!.Total);
        Assert.Equal(1, result.Value.Succeeded);
        Assert.Equal(0, result.Value.Failed);
        Assert.Null(result.Value.Message);   // a folder-per-scene adopt reports no flat-layout fall-back

        // The re-point PUT carries the owned file's folder as the movie path; the ONLY command is a RescanMovie
        // (never a ManualImport copy, never a search) — loop-safe, zero duplication.
        var put = Assert.Single(handler.Requests, r => r.Method == HttpMethod.Put && r.Url.EndsWith("/api/v3/movie/42", StringComparison.Ordinal));
        Assert.Contains($"\"path\":\"{OwnedV3Folder}\"", put.Body);
        var command = Assert.Single(handler.Requests, r => r.Url.EndsWith("/api/v3/command", StringComparison.Ordinal));
        Assert.Contains("\"name\":\"RescanMovie\"", command.Body);
        Assert.Contains("\"movieIds\":[42]", command.Body);
        Assert.DoesNotContain(handler.Requests, r => r.Url.Contains("/api/v3/manualimport", StringComparison.Ordinal));
        Assert.DoesNotContain("ManualImport", command.Body!);
        Assert.DoesNotContain("Search", command.Body!);
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Post && r.Url.EndsWith("/api/v3/movie", StringComparison.Ordinal));
        AssertNoStashDbCall(handler);
    }

    [Fact]
    public async Task ReflectOwned_v2_skips_a_video_with_no_tpdb_or_no_file()
    {
        var v2 = V3Options with { SelectedVersion = "v2" };
        var library = new FakeCoveLibraryPort();
        library.SeedForEntity(
            EntityKind.Studio, StudioCoveId,
            new CoveVideo(1, "no tpdb", null, [], [], [OwnedFilePath], []), // has a file but no TPDB id
            OwnedVideo(OwnedTpdb, filePath: ""));                            // has a TPDB id but no file

        var handler = FakeHttpMessageHandler.Sequence(
            Ok(V2Fixtures.SeriesArray),
            Ok(V2Fixtures.EpisodesSeries1),
            Ok(V2Fixtures.EpisodeFilesSeries1));

        var result = await ActionsFor(handler, WithStudioIdentity(library, V2SiteTpdbId), v2)
            .ReflectOwnedAsync(EntityKind.Studio, StudioCoveId, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.Equal(BulkActionResult.Empty, result.Value);
        Assert.DoesNotContain(handler.Requests, r => r.Url.Contains("/api/v3/manualimport", StringComparison.Ordinal));
        Assert.DoesNotContain(handler.Requests, r => r.Url.EndsWith("/api/v3/command", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReflectOwned_v2_skips_a_scene_whisparr_already_has()
    {
        // Episode 2 (tvdbId 1010277) already hasFile:true in the fixture, so it is NOT indexed as fileless — an
        // owned video matching it is skipped (loop-safe: never re-import a scene Whisparr already has).
        var v2 = V3Options with { SelectedVersion = "v2" };
        var library = new FakeCoveLibraryPort();
        library.SeedForEntity(EntityKind.Studio, StudioCoveId, OwnedVideo("1010277", "/config/media/Vixen/second-scene.mkv"));

        var handler = FakeHttpMessageHandler.Sequence(
            Ok(V2Fixtures.SeriesArray),
            Ok(V2Fixtures.EpisodesSeries1),
            Ok(V2Fixtures.EpisodeFilesSeries1));

        var result = await ActionsFor(handler, WithStudioIdentity(library, V2SiteTpdbId), v2)
            .ReflectOwnedAsync(EntityKind.Studio, StudioCoveId, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.Equal(BulkActionResult.Empty, result.Value);
        Assert.DoesNotContain(handler.Requests, r => r.Url.EndsWith("/api/v3/command", StringComparison.Ordinal));
    }

    // ---- the add's quality profile is derived per add, never stored ----

    private static string StudioRowWithProfile(int profileId) => JsonSerializer.Serialize(new
    {
        id = 3,
        foreignId = StudioStashId,
        title = "A Studio",
        monitored = true,
        qualityProfileId = profileId,
        rootFolderPath = RootPath,
        tags = OriginTags,
    });

    private static FakeCoveLibraryPort StudioLibraryWithMissingScene()
    {
        var library = new FakeCoveLibraryPort();
        library.SeedForEntity(EntityKind.Studio, StudioCoveId, CoveScene("uuid-3"));
        library.SeedEntityIdentity(EntityKind.Studio, StudioCoveId, new CoveEntityIdentity([StudioStashId], []));
        return library;
    }

    [Fact]
    public async Task AddAllMissing_adds_a_studios_scenes_with_that_studios_own_profile()
    {
        var handler = FakeHttpMessageHandler.Sequence(
            Ok(RootFolderList),
            Ok(TagListWithOrigin),
            Ok($"[{StudioRowWithProfile(11)}]"),          // the parent studio's Whisparr row
            Created(Movie(101, "uuid-3", monitored: false))).ServingCatalogue(MovieList());

        var result = await ActionsFor(handler, StudioLibraryWithMissingScene())
            .AddAllMissingAsync(EntityKind.Studio, StudioCoveId, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        var post = Assert.Single(handler.Requests, r => r.Method == HttpMethod.Post && r.Url.EndsWith("/api/v3/movie", StringComparison.Ordinal));
        Assert.Contains("\"qualityProfileId\":11", post.Body);
        // The by-id studio read answers it; the instance-wide list is never fetched.
        Assert.DoesNotContain(handler.Requests, r => r.Url.Contains("/api/v3/qualityprofile", StringComparison.Ordinal));
        AssertNoGrab(handler);
    }

    [Fact]
    public async Task AddAllMissing_falls_back_to_the_first_offered_profile_when_whisparr_has_no_such_studio()
    {
        var handler = FakeHttpMessageHandler.Sequence(
            Ok(RootFolderList),
            Ok(TagListWithOrigin),
            Ok("[]"),                                    // the parent studio is not in Whisparr
            Ok(QualityProfileList),
            Created(Movie(101, "uuid-3", monitored: false))).ServingCatalogue(MovieList());

        var result = await ActionsFor(handler, StudioLibraryWithMissingScene())
            .AddAllMissingAsync(EntityKind.Studio, StudioCoveId, CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        var post = Assert.Single(handler.Requests, r => r.Method == HttpMethod.Post && r.Url.EndsWith("/api/v3/movie", StringComparison.Ordinal));
        Assert.Contains($"\"qualityProfileId\":{ProfileId}", post.Body);
    }

    [Fact]
    public async Task AddScene_uses_the_first_offered_profile_when_no_parent_studio_is_known()
    {
        var handler = FakeHttpMessageHandler.Sequence(
            Ok(RootFolderList),
            Ok(TagListWithOrigin),
            Ok(QualityProfileList),
            Created(Movie(42, SceneStashId, monitored: false)));

        var result = await ActionsFor(handler).AddSceneAsync(SceneStashId, "A Scene", CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        var post = Assert.Single(handler.Requests, r => r.Method == HttpMethod.Post && r.Url.EndsWith("/api/v3/movie", StringComparison.Ordinal));
        Assert.Contains($"\"qualityProfileId\":{ProfileId}", post.Body);
        Assert.DoesNotContain(handler.Requests, r => r.Url.Contains("/api/v3/studio", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AddScene_on_an_instance_offering_no_profile_is_classified_and_creates_nothing()
    {
        var handler = FakeHttpMessageHandler.Sequence(
            Ok(RootFolderList),
            Ok(TagListWithOrigin),
            Ok("[]"));                                   // the instance offers no quality profile at all

        var result = await ActionsFor(handler).AddSceneAsync(SceneStashId, "A Scene", CancellationToken.None);

        // Whisparr's create validator would reject the add; the configuration gap is nameable, its rejection is not.
        Assert.Equal(WhisparrResultState.Unreachable, result.State);
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Post && r.Url.EndsWith("/api/v3/movie", StringComparison.Ordinal));
        AssertNoGrab(handler);
    }

    [Fact]
    public async Task AddScenes_batch_reads_the_profile_list_once_for_the_whole_batch()
    {
        var handler = FakeHttpMessageHandler.Sequence(
            Ok(RootFolderList),
            Ok(TagListWithOrigin),
            Ok(QualityProfileList),
            Created(Movie(101, "uuid-a", monitored: false)),
            Created(Movie(102, "uuid-b", monitored: false)));

        var result = await ActionsFor(handler).AddScenesAsync(
            [new SceneRef("uuid-a", "A"), new SceneRef("uuid-b", "B")], CancellationToken.None);

        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.Equal(2, result.Value!.Succeeded);
        Assert.Single(handler.Requests, r => r.Url.Contains("/api/v3/qualityprofile", StringComparison.Ordinal));
    }
}
