using System.Globalization;
using System.Net;
using System.Text.Json;
using WhisparrSync.Adapters;
using WhisparrSync.Client;
using WhisparrSync.Library;
using WhisparrSync.SceneStatus;
using WhisparrSync.Tests.TestSupport;
using Probe = WhisparrSync.Tests.TestSupport.V2SiteWalkProbe;

namespace WhisparrSync.Tests.SceneStatus;

/// <summary>
/// What the older generation's status-index fold retains, on a generation that has no movie entity and walks
/// <c>series → episode → episodefile</c> instead, measured at two SITE counts a tenfold apart with the
/// per-site episode count held fixed.
/// </summary>
/// <remarks>
/// <para>
/// THE TOOLBAR SUMMARY DOES NOT REACH THIS FOLD on this generation — the handler refuses on the absent
/// <c>IWhisparrStatusIndexSource</c> role, because the index below is empty for a library of any size and the
/// four-state partition built over it reported every scene as one Whisparr does not have. So the subject here is
/// the FOLD, not the summary, and what the figures below support is the property that a per-site walk hands each
/// site's scenes over and keeps one site at a time. That property still describes the SHARED walk the
/// materialised read's own callers reach; the fold measured over it is the shape an index read would take if this
/// generation's rows ever keyed.
/// </para>
/// <para>
/// The site count is the axis on purpose. Varying episodes-per-site would grow the largest single site,
/// which is the term a per-site walk is supposed to keep holding; varying the site count grows the library
/// while leaving that term alone, so it separates the two.
/// </para>
/// <para>
/// The harness is <see cref="V2SiteWalkProbe"/>, shared with the falsification class so a guard is always
/// falsified against the same probe it protects. It owns the sampling instant, the window checks and the two
/// guards, and states which allocations the instant holds; what lives here is the figure they produce and the
/// relations asserted over it. The falsifications sit in a class of their own because each builds a corpus and
/// drops it, and the class reporting a retained-heap delta must not be the source of the release its own
/// windows have to reject.
/// </para>
/// <para>
/// The shared <c>FakeHttpMessageHandler</c> is deliberately not used for the measured runs: it retains every
/// request it sees, so its own retention would grow with the site count along the axis under measurement. It
/// IS used for the request-sequence and answer pins below, where a handful of sites retains nothing that
/// matters.
/// </para>
/// <para>
/// Absolute byte figures are printed rather than asserted — they are machine-specific, and
/// <c>GC.GetTotalMemory</c> is a process-wide reading. Both samples of a comparison are taken inside one method
/// so they meet the same ambient noise, and only relations with large margins are asserted.
/// </para>
/// <para>
/// On the answer side: a synthesized row carries a null <c>StashId</c> and the item type <c>"v2scene"</c>,
/// while the projector keys a row by its stash id, or by its foreign id only for a <c>"scene"</c>-typed row.
/// So a synthesized row indexes under NO key and the index is empty whatever the walk does — pinned here as a
/// measured fact, and the reason the role is not declared on this generation at all. It makes every count-level
/// comparison here insensitive to a dropped or double-folded row, so the assertion that stays sensitive is
/// row-sequence identity, asserted in <c>V2StatusIndexWalkTests</c> rather than here.
/// </para>
/// </remarks>
[Trait("Tier", "L0")]
public sealed class V2SummaryReadCostTests
{
    private const string BaseUrl = "http://whisparr.local:6969";
    private const string ApiKey = "KEY";

    // The pinned request sequence and answers are taken over a handful of sites, where the shape is readable
    // as a literal and the corpus retains nothing worth measuring.
    private const int PinnedSites = 3;
    private const int PinnedEpisodesPerSite = 2;

    [Fact]
    public async Task The_streamed_walk_retains_one_site_where_the_materialising_composition_retained_the_library()
    {
        // All four samples inside one method so they meet the same ambient noise. Classes in this assembly are
        // serialized and cannot overlap, but GC.GetTotalMemory is still a process-wide reading and a completed
        // neighbour's graph is reclaimed at an instant nothing here controls, so a figure taken in a method of
        // its own is still compared across a different process state. The small figures are taken FIRST: a
        // sample taken in the shadow of a preceding tens-of-megabyte release reads as the collector rather than
        // as the read, and came out negative when it was.
        var streamedSmall = await Probe.MeasureUncontaminatedAsync(Probe.SmallSites, Probe.Read.Streamed);
        var streamedLarge = await Probe.MeasureUncontaminatedAsync(Probe.LargeSites, Probe.Read.Streamed);
        var materialisingSmall = await Probe.MeasureUncontaminatedAsync(Probe.SmallSites, Probe.Read.Materialising);
        var materialisingLarge = await Probe.MeasureUncontaminatedAsync(Probe.LargeSites, Probe.Read.Materialising);

        Probe.Report("materialising v2", materialisingSmall, materialisingLarge);
        Probe.Report("streamed v2", streamedSmall, streamedLarge);

        Probe.AssertBaselineDoesNotScale(materialisingSmall, materialisingLarge);
        Probe.AssertBaselineDoesNotScale(streamedSmall, streamedLarge);

        // The noise-robust statement, and the one the change claims: ten times the sites, streamed, retains
        // less than a tenth of them did while the whole set was being held.
        Assert.True(
            streamedLarge.RetainedBytes < materialisingSmall.RetainedBytes,
            $"streamed at {streamedLarge.Sites} sites retained {streamedLarge.RetainedBytes} B, which is not "
                + $"below the {materialisingSmall.RetainedBytes} B the materialising composition retained at "
                + $"{materialisingSmall.Sites}");

        // The materialising side grows several-fold across the tenfold change; the streamed side does not,
        // with an additive floor so collector noise cannot decide it. The floor is what the site LIST costs:
        // the walk still reads it once and holds it, which is one row per site rather than one per scene.
        Assert.True(
            materialisingLarge.RetainedBytes > materialisingSmall.RetainedBytes * 5,
            $"materialising: {materialisingSmall.RetainedBytes} B over {materialisingSmall.Sites} sites, "
                + $"{materialisingLarge.RetainedBytes} B over {materialisingLarge.Sites} sites");
        Assert.True(
            streamedLarge.RetainedBytes < streamedSmall.RetainedBytes + Probe.StreamedFloorBytes,
            $"streamed: {streamedSmall.RetainedBytes} B over {streamedSmall.Sites} sites, "
                + $"{streamedLarge.RetainedBytes} B over {streamedLarge.Sites} sites — it scales with the site "
                + "count, so it is not holding one site");
    }

    /// <summary>The tolerance both window checks share, pinned in each direction on each window.</summary>
    /// <remarks>
    /// The only assertion here that does not rest on a process-wide reading, which is what makes the tolerance a
    /// stated boundary rather than a tuned number: a sample is discarded when a window's floor fell PAST the
    /// tolerance, not when it fell at all.
    /// </remarks>
    [Fact]
    public void A_window_whose_floor_fell_past_the_tolerance_is_not_a_clean_window()
    {
        Assert.True(Clean(walkDrop: Probe.FloorDriftBytes, baselineDrop: 0));
        Assert.False(Clean(walkDrop: Probe.FloorDriftBytes + 1, baselineDrop: 0));

        Assert.True(Clean(walkDrop: 0, baselineDrop: Probe.FloorDriftBytes));
        Assert.False(Clean(walkDrop: 0, baselineDrop: Probe.FloorDriftBytes + 1));

        static bool Clean(long walkDrop, long baselineDrop) => new Probe.Sample(
            Sites: 1,
            RetainedBytes: 1,
            BaselineBytes: 1,
            FloorDropBytes: walkDrop,
            BaselineFloorDropBytes: baselineDrop).WindowWasClean;
    }

    /// <summary>
    /// The six counts the two compositions over this walk produce, compared member by member over a fixed Cove
    /// video set: the materialising baseline against the per-site fold.
    /// </summary>
    /// <remarks>
    /// This comparison is WEAK on this generation, and the weakness is measured rather than suspected: a
    /// synthesized scene indexes under no key at all, so both sides answer from an empty index and a fold that
    /// dropped every scene would satisfy it. It is taken anyway because the two compositions must agree wherever
    /// they are both reachable, and it is paired with two things that are sensitive — the scene-sequence identity
    /// in <c>V2StatusIndexWalkTests</c>, and the control below, which shows this same counts path DOES move
    /// scenes between states once the index keys, so "all six equal" is an answer rather than a tautology. What it
    /// is NOT evidence of is a user-visible count on this generation: the toolbar reads neither side here.
    /// </remarks>
    [Fact]
    public async Task The_six_counts_are_identical_between_the_materialising_baseline_and_the_per_site_fold()
    {
        var before = await MaterialisingCompositionAsync(Adapter(PinnedInstance()), CancellationToken.None);
        var after = await FoldedIndexAsync(Adapter(PinnedInstance()));

        AssertSameCounts(
            SceneStatusProjector.SummaryCounts(PinnedVideos, before, NoExclusions),
            SceneStatusProjector.SummaryCounts(PinnedVideos, after, NoExclusions));
    }

    [Fact]
    public void The_same_counts_path_moves_scenes_between_states_once_the_index_keys_at_all()
    {
        // The one member that decides it: the same ids under a "scene"-typed row key, where the synthesized
        // "v2scene" literal keys under nothing. So the equal-counts result above is a property of what this
        // generation can identify, not of a comparison that cannot fail.
        var keyed = SceneStatusProjector.BuildMovieIndex<WhisparrMovieFacts>(
        [
            .. PinnedVideos.SelectMany(video => video.StashIds).Select((id, i) =>
                new WhisparrMovieFacts(StashId: null, id, "scene", Monitored: i % 2 == 0, HasFile: i % 2 == 0)),
        ]);

        var empty = SceneStatusProjector.SummaryCounts(
            PinnedVideos, SceneStatusProjector.BuildMovieIndex<WhisparrMovieFacts>([]), NoExclusions);
        var moved = SceneStatusProjector.SummaryCounts(PinnedVideos, keyed, NoExclusions);

        Assert.Equal(new SceneStatusCounts(0, 0, PinnedVideos.Length, 0, 0, PinnedVideos.Length), empty);
        Assert.Equal(new SceneStatusCounts(3, 3, 0, 0, 3, PinnedVideos.Length), moved);
    }

    [Fact]
    public async Task The_older_generations_index_fold_issues_one_series_read_and_two_reads_per_site()
    {
        var handler = PinnedInstance();

        await FoldedIndexAsync(Adapter(handler));

        Assert.Equal(
            [
                "GET /api/v3/series",
                "GET /api/v3/episode?seriesId=1",
                "GET /api/v3/episodefile?seriesId=1",
                "GET /api/v3/episode?seriesId=2",
                "GET /api/v3/episodefile?seriesId=2",
                "GET /api/v3/episode?seriesId=3",
                "GET /api/v3/episodefile?seriesId=3",
            ],
            handler.Requests.Select(Describe));

        // The absence of any movie read is already entailed by the literal sequence above — the counter keys
        // those classifications on /api/v3/movie, which that sequence forbids. This line is the falsifiable
        // one: a write would fail it.
        Assert.Equal(0, WhisparrRequestCounter.Classify(handler).Writes);
        Assert.Equal(1 + (2 * PinnedSites), handler.Requests.Count);
    }

    [Fact]
    public async Task A_synthesized_row_indexes_under_no_key_at_all_so_every_scene_would_report_not_added()
    {
        var handler = PinnedInstance();

        var index = await FoldedIndexAsync(Adapter(handler));
        var counts = SceneStatusProjector.SummaryCounts(PinnedVideos, index, NoExclusions);

        // The measured fact behind the role's absence, over a real three-site corpus rather than an empty one:
        // six rows are synthesized and none of them keys — a null stash id, and a foreign id the keying rule only
        // reads for a "scene"-typed row. These counts are what the toolbar WOULD paint on this generation, and
        // being uniformly wrong for every library is why it is refused the read instead.
        Assert.Empty(index);

        // Member by member, so a failure names which one moved rather than only that the total did.
        Assert.Equal(0, counts.Monitored);
        Assert.Equal(0, counts.Unmonitored);
        Assert.Equal(PinnedVideos.Length, counts.NotAdded);
        Assert.Equal(0, counts.Excluded);
        Assert.Equal(0, counts.InLibrary);
        Assert.Equal(PinnedVideos.Length, counts.Total);
    }

    [Fact]
    public async Task A_non_ok_read_part_way_through_the_walk_stops_the_walk_where_it_failed()
    {
        // Site 2's episode read fails after site 1 was walked whole. What is assertable HERE is the request
        // scope — site 1 reached, site 3 never — because the answer cannot show it: a synthesized row keys under
        // nothing, so a partial synthesis indexes empty exactly like a clean read does. That the fold's own result
        // is non-Ok rather than an empty index is asserted in V2StatusIndexWalkTests, where the result is the
        // subject.
        var handler = PinnedInstance(request =>
            request.Url.Contains("/episode?seriesId=2", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.BadGateway)
                : null);

        var folded = await Adapter(handler)
            .LoadStatusIndexAsync(BaseUrl, ApiKey, onFolded: null, CancellationToken.None);

        Assert.False(folded.IsOk);
        Assert.Contains(handler.Requests, r => r.Url.Contains("/episode?seriesId=1", StringComparison.Ordinal));
        Assert.DoesNotContain(handler.Requests, r => r.Url.Contains("seriesId=3", StringComparison.Ordinal));
    }

    [Fact]
    public void No_two_retained_heap_measurements_can_be_scheduled_concurrently()
    {
        // Removing the assembly's serialization would fail nothing on its own: the three memory classes would
        // simply go back to reporting each other's retention, intermittently and in either direction. This is
        // what turns that into a failing test rather than a flaky one.
        //
        // Under xUnit v3 the runner configuration file is the mechanism — v2's
        // [assembly: CollectionBehavior(DisableTestParallelization = true)] is obsolete, and its replacement
        // attribute ships only in a runner package a test assembly does not reference. So the file must EXIST
        // and must say false: an absent file means the assembly runs parallel under the runner's default,
        // which is the state this test is here to refuse.
        var assemblyName = typeof(V2SummaryReadCostTests).Assembly.GetName().Name;
        var candidates = new[] { $"{assemblyName}.xunit.runner.json", "xunit.runner.json" }
            .Select(fileName => Path.Combine(AppContext.BaseDirectory, fileName))
            .Where(File.Exists)
            .ToList();

        Assert.True(
            candidates.Count > 0,
            "no xunit.runner.json beside the test assembly, so collection parallelization runs at the runner's "
                + "default and the retained-heap classes can sample across each other's allocations");

        foreach (var path in candidates)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            Assert.True(
                document.RootElement.TryGetProperty("parallelizeTestCollections", out var flag)
                    && flag.ValueKind == JsonValueKind.False,
                $"{Path.GetFileName(path)} does not set parallelizeTestCollections to false, so the "
                    + "retained-heap measurements are not serialized against one another");
        }
    }

    private static V2Adapter Adapter(HttpMessageHandler handler)
        => new(new WhisparrClient(new HttpClient(handler)));

    private static string Describe(CapturedRequest request)
        => $"{request.Method} {new Uri(request.Url).PathAndQuery}";

    // The fold's own index, taken through the adapter rather than through the handler: no shipped path on this
    // generation composes one, so there is no handler to take it through. The Ok assertion is here rather than at
    // each call site so a corpus that stopped answering cannot be read as an empty index.
    private static async Task<IReadOnlyDictionary<string, WhisparrMovieFacts>> FoldedIndexAsync(V2Adapter adapter)
    {
        var folded = await adapter.LoadStatusIndexAsync(BaseUrl, ApiKey, onFolded: null, CancellationToken.None);
        Assert.True(folded.IsOk);
        return folded.Value!;
    }

    // The materialising composition the per-site fold is measured AGAINST: the whole movie array, its projection
    // to the five members the status index reads, and the index build over them. It is written out here because no
    // shipped path composes it, and a baseline whose subject can move under a later commit is not a baseline.
    private static async Task<IReadOnlyDictionary<string, WhisparrMovieFacts>> MaterialisingCompositionAsync(
        V2Adapter adapter, CancellationToken ct)
    {
        var movies = await adapter.ListMoviesAsync(BaseUrl, ApiKey, ct);
        WhisparrMovie[] rows = movies.IsOk ? movies.Value! : [];
        return SceneStatusProjector.BuildMovieIndex(Array.ConvertAll(rows, row => row.Facts));
    }

    // Member by member, so a failure names which count moved. A fold that drops one scene and double-counts
    // another produces a plausible total, which is what a total-only comparison would accept.
    private static void AssertSameCounts(SceneStatusCounts before, SceneStatusCounts after)
    {
        Assert.True(before.Monitored == after.Monitored, $"monitored: {before.Monitored} -> {after.Monitored}");
        Assert.True(before.Unmonitored == after.Unmonitored, $"unmonitored: {before.Unmonitored} -> {after.Unmonitored}");
        Assert.True(before.NotAdded == after.NotAdded, $"notAdded: {before.NotAdded} -> {after.NotAdded}");
        Assert.True(before.Excluded == after.Excluded, $"excluded: {before.Excluded} -> {after.Excluded}");
        Assert.True(before.InLibrary == after.InLibrary, $"inLibrary: {before.InLibrary} -> {after.InLibrary}");
        Assert.True(before.Total == after.Total, $"total: {before.Total} -> {after.Total}");
    }

    // One overlay, since a second call would replace the first rather than compose with it: a fault is
    // offered the request before the corpus answers it.
    private static FakeHttpMessageHandler PinnedInstance(Func<CapturedRequest, HttpResponseMessage?>? fault = null)
        => FakeHttpMessageHandler.Json("[]").Also(request =>
        {
            if (fault?.Invoke(request) is { } faulted)
            {
                return faulted;
            }

            var path = new Uri(request.Url).AbsolutePath;
            var seriesId = Probe.SeriesIdOf(request.Url);
            return path switch
            {
                "/api/v3/series" => Probe.Json(Probe.SeriesArray(PinnedSites)),
                "/api/v3/episode" => Probe.Json(Probe.EpisodeArray(seriesId, PinnedEpisodesPerSite)),
                "/api/v3/episodefile" => Probe.Json(Probe.EpisodeFileArray(seriesId, PinnedEpisodesPerSite)),
                _ => null,
            };
        });

    // Cove videos keyed on the ids a synthesized row carries, plus ids nothing carries, so the six counts
    // would move if those rows ever keyed.
    private static readonly CoveVideo[] PinnedVideos =
    [
        .. Enumerable.Range(1, 6).Select(i => new CoveVideo(
            i,
            $"Video {i}",
            null,
            i % 3 == 0 ? [$"unknown-{i}"] : [SceneForeignId(((i - 1) % PinnedSites) + 1, ((i - 1) % PinnedEpisodesPerSite) + 1)],
            [],
            [],
            [])),
    ];

    private static readonly IReadOnlySet<string> NoExclusions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    // The site's own identity slot is the TPDB id; a scene's is the one the synthesized row carries in its
    // foreign id, which is what a Cove video would have to match on for the index to answer anything.
    private static string SceneForeignId(int site, int episode)
        => string.Create(CultureInfo.InvariantCulture, $"{(site * 1_000_000) + episode}");
}
