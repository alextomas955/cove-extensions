using System.Globalization;
using System.Net;
using System.Text;
using WhisparrSync.Adapters;
using WhisparrSync.Client;
using WhisparrSync.SceneStatus;

namespace WhisparrSync.Tests.TestSupport;

/// <summary>
/// The retained-heap probe over the older generation's site walk: a generated corpus, a transport that composes
/// each site's body at call time, and one sample taken from inside the fold while a site is open.
/// </summary>
/// <remarks>
/// <para>
/// It is shared because a guard and the figure it protects have to be measured through the SAME harness. A
/// falsification that builds its own version of the probe reports on that version.
/// </para>
/// <para>
/// The sample is taken at the first scene the fold is handed from the target site, which is where the per-site
/// term is entirely live: the site list, that site's <c>WhisparrEpisode[]</c>, its <c>WhisparrEpisodeFile[]</c>,
/// the <c>pathByFileId</c> dictionary the synthesis builds from them, the scene being folded, and the
/// accumulator. The synthesis builds that dictionary in full before it yields anything, so the first yield
/// already holds the whole term.
/// </para>
/// <para>
/// The streamed arm calls the adapter's fold-observing index read directly, which is also the only way to reach
/// it: this generation does not declare the status-index role, so no handler composes that read here. What the
/// figure describes is the FOLD's retention, not a shipped summary's.
/// </para>
/// <para>
/// A figure is a measurement only if nothing else was released inside its window, because
/// <c>GC.GetTotalMemory</c> is a process-wide reading. A neighbouring class's corpus stays reported live past
/// the test that built it — the framework holds the completed test's object graph — and is reclaimed at an
/// instant nothing here controls; when that instant falls inside a window, the neighbour's release is
/// subtracted from this read's retention. Measured on a full-suite run it produced a NEGATIVE figure of about
/// −15 MB, and on other runs a plausible-looking one an order too small. Repeated collection does not prevent
/// it: the floor is stable across six successive full collections, because the graph is still rooted at that
/// point. So BOTH windows a sample spans — the harness construction and the walk itself — are checked for a
/// floor that fell, against one tolerance, and a sample that fails either check is discarded and re-taken
/// rather than reported.
/// </para>
/// <para>
/// A sample where the fold was never handed the target site at all is rejected outright: every assertion over
/// the streamed figure is an upper bound, so a reading of zero would satisfy all of them.
/// </para>
/// </remarks>
internal static class V2SiteWalkProbe
{
    internal const string BaseUrl = "http://whisparr.local:6969";
    internal const string ApiKey = "KEY";

    // Tenfold apart in SITES, with the per-site episode count fixed so the largest single site is the same
    // at both ends.
    internal const int SmallSites = 20;
    internal const int LargeSites = 200;
    internal const int EpisodesPerSite = 300;

    // Where in the walk the sample is taken. Short of the end on purpose: at the last site "still walking"
    // would be true by one request.
    internal const double SampleAtFraction = 0.9;

    // The additive slack on "the pre-walk baseline does not scale with the site count". A walk-shaped harness
    // holds a few counters; a corpus-holding one holds tens of megabytes, so a megabyte separates them without
    // letting collector noise decide the answer.
    internal const long BaselineFloorBytes = 1L << 20;

    // The additive slack on "the streamed retained figure does not scale with the site count". What it absorbs
    // is the site LIST, which the walk still reads once and holds at one row per site rather than one per scene
    // (measured at roughly 285 KB across the tenfold change), plus allocator noise. A foreign release is NOT
    // absorbed here — a window that saw one is discarded — so the slack stays an order below the tens of
    // megabytes the materialising side moves by, which is what makes the relation an answer rather than a
    // formality.
    internal const long StreamedFloorBytes = 4L << 20;

    // How far the process floor may fall across a measurement window before the delta stops being attributable
    // to the read. Nothing this walk holds is released before the post-walk floor is read, so a real drop is
    // near zero; the tolerance is there for allocator bookkeeping, not for a foreign release, which arrives in
    // megabytes. One tolerance covers both windows: the question it answers — did something outside this
    // measurement go away inside it — is the same question on each.
    internal const long FloorDriftBytes = 1L << 20;

    // How many times a contaminated window is re-measured before the sample is reported as unobtainable. Each
    // foreign graph is reclaimed once, so the rejections cluster — two consecutive have been observed in a full
    // suite run, and the budget is a margin over that. A run that cannot find one clean window is reporting
    // something other than this read and must say so rather than print a figure.
    internal const int MeasurementAttempts = 6;

    /// <summary>
    /// Which composition is under the sample: the materialising baseline the streamed figure is measured
    /// against, or the streamed fold itself.
    /// </summary>
    internal enum Read
    {
        Materialising,
        Streamed,
    }

    /// <summary>One retained-byte reading and the two windows it was taken across.</summary>
    /// <remarks>
    /// <c>BaselineFloorDropBytes</c> is the negation of <c>BaselineBytes</c> — both come from one pair of reads
    /// — and is carried separately because it is what turns a baseline that read low or negative into a
    /// rejected sample rather than a satisfied guard.
    /// </remarks>
    internal sealed record Sample(
        int Sites, long RetainedBytes, long BaselineBytes, long FloorDropBytes, long BaselineFloorDropBytes)
    {
        internal bool WindowWasClean
            => FloorDropBytes <= FloorDriftBytes && BaselineFloorDropBytes <= FloorDriftBytes;
    }

    internal static void Report(string label, Sample small, Sample large)
        => Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"RETAINED {label,-18} {small.Sites,5} sites -> {small.RetainedBytes,12} B "
                + $"(baseline {small.BaselineBytes,11} B, floor drop {small.FloorDropBytes,11} B, "
                + $"baseline floor drop {small.BaselineFloorDropBytes,11} B)   "
                + $"{large.Sites,5} sites -> {large.RetainedBytes,12} B (baseline {large.BaselineBytes,11} B, "
                + $"floor drop {large.FloorDropBytes,11} B, baseline floor drop "
                + $"{large.BaselineFloorDropBytes,11} B)"));

    // The second guard, as its own assertion so it can be falsified without also falsifying the first: what
    // the transport holds before a single row is folded must not grow with the site count, or the "after"
    // figure is a statement about the fixture.
    internal static void AssertBaselineDoesNotScale(Sample small, Sample large)
        => Assert.True(
            large.BaselineBytes < small.BaselineBytes + BaselineFloorBytes,
            $"the pre-walk baseline scales with the site count: {small.BaselineBytes} B at {small.Sites} sites, "
                + $"{large.BaselineBytes} B at {large.Sites} sites — the harness is holding the corpus it claims "
                + "the read does not");

    // The first guard, stated as what BOTH reads actually hold rather than as a populated accumulator: on this
    // generation the index is legitimately empty, so asserting it is populated would fail a correct
    // implementation. What must be true instead is that the target site is OPEN — both of its reads served, so
    // its whole per-site term is live — while the walk still has a site left to go.
    internal static void AssertASiteIsOpen(SiteWalkHandler handler, int sampleAtSite)
        => Assert.True(
            handler.SitesFullyServed == sampleAtSite && sampleAtSite < handler.Sites,
            $"no site was open at the sampling instant: {handler.SitesFullyServed} of {handler.Sites} sites "
                + $"served against site {sampleAtSite} as the target — the target site's two reads must both "
                + "have been served and at least one site must remain unwalked");

    internal static async Task<Sample> MeasureAsync(
        int sites, Read read, bool preBuiltBodies = false, bool sampleAfterTheWalk = false)
    {
        var beforeHarness = Settled();
        var handler = preBuiltBodies
            ? new PreBuiltSiteWalkHandler(sites, EpisodesPerSite)
            : new SiteWalkHandler(sites, EpisodesPerSite);

        // Read with the handler rooted, so the pair brackets a window whose only addition is the harness: the
        // baseline is what it holds, and the same pair says whether the floor fell while it was being built.
        var afterHarness = Settled();

        var adapter = new V2Adapter(new WhisparrClient(new HttpClient(handler)));
        var sampleAtSite = (int)(sites * SampleAtFraction);
        var walkBaseline = Settled();
        long retained = 0;

        var sampled = false;

        Action<WhisparrMovie>? onFolded = null;
        if (!sampleAfterTheWalk)
        {
            onFolded = scene =>
            {
                if (sampled || scene.SeriesId != sampleAtSite)
                {
                    return;
                }

                sampled = true;
                AssertASiteIsOpen(handler, sampleAtSite);
                retained = Settled() - walkBaseline;
            };
        }

        var index = read == Read.Materialising
            ? await MaterialisingIndexAsync(adapter, onFolded, CancellationToken.None)
            : (await adapter.LoadStatusIndexAsync(BaseUrl, ApiKey, onFolded, CancellationToken.None)).Value!;

        if (sampleAfterTheWalk)
        {
            AssertASiteIsOpen(handler, sampleAtSite);
            retained = Settled() - walkBaseline;
        }

        // Read while the walk's own result is still rooted, so it cannot itself account for a fall: the closing
        // floor holds everything the opening one did plus what the read produced. A floor BELOW the opening one
        // is therefore something outside this measurement being reclaimed inside its window.
        var closingFloor = Settled();

        if (!sampleAfterTheWalk)
        {
            // Scoped to the sampling path so the after-the-walk falsification still fails on the open-site
            // message rather than on this one. Every assertion over the streamed figure is an upper bound, so a
            // sample the fold never took would otherwise pass all of them as a confident zero.
            Assert.True(
                retained != 0,
                $"no retained figure was taken for the {read} read at {sites} sites: the fold was never handed "
                    + $"a scene from site {sampleAtSite}, so nothing was measured and the sample describes "
                    + "nothing");
        }

        // Rooted past the sample so the figure describes what the read holds rather than what the collector
        // reached first.
        GC.KeepAlive(index);
        GC.KeepAlive(handler);
        return new Sample(
            sites, retained, afterHarness - beforeHarness, walkBaseline - closingFloor,
            beforeHarness - afterHarness);
    }

    // A sample whose window was contaminated is not a small measurement, it is not a measurement — the figure
    // carries a neighbour's release rather than this read's retention, in either direction. Discard and re-take,
    // reporting every rejected window so a run log shows what was thrown away rather than only what was kept.
    internal static async Task<Sample> MeasureUncontaminatedAsync(int sites, Read read)
    {
        var rejected = new List<Sample>();
        for (var attempt = 0; attempt < MeasurementAttempts; attempt++)
        {
            var sample = await MeasureAsync(sites, read);
            if (sample.WindowWasClean)
            {
                return sample;
            }

            rejected.Add(sample);
            var whichWindow = sample.FloorDropBytes > FloorDriftBytes
                ? sample.BaselineFloorDropBytes > FloorDriftBytes ? "both windows" : "the walk window"
                : "the harness window";
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"WINDOW REJECTED {read} at {sample.Sites} sites: the process floor fell past the tolerance in "
                    + $"{whichWindow} ({sample.FloorDropBytes} B across the walk, "
                    + $"{sample.BaselineFloorDropBytes} B across the harness construction), so the "
                    + $"{sample.RetainedBytes} B reading is another release rather than this read"));
        }

        throw new InvalidOperationException(string.Create(
            CultureInfo.InvariantCulture,
            $"no uncontaminated window for {read} at {sites} sites in {MeasurementAttempts} attempts; the walk "
                + $"window's floor fell {string.Join(", ", rejected.Select(sample => sample.FloorDropBytes))} B "
                + $"and the harness window's fell "
                + $"{string.Join(", ", rejected.Select(sample => sample.BaselineFloorDropBytes))} B"));
    }

    // The BASELINE composition the streamed figure is measured against: the whole scene array, its projection to
    // the members the status index reads, and the index build over them. It is written out over the shipped
    // internal walk rather than reached through a shipped path because no shipped path composes it any more, and
    // because the fold observer has to fire from inside the walk — which the array-returning read cannot host.
    private static async Task<IReadOnlyDictionary<string, WhisparrMovieFacts>> MaterialisingIndexAsync(
        V2Adapter adapter, Action<WhisparrMovie>? onFolded, CancellationToken ct)
    {
        var folded = await adapter.FoldSiteWalkAsync(
            BaseUrl, ApiKey,
            new List<WhisparrMovie>(),
            (scenes, scene) =>
            {
                scenes.Add(scene);
                onFolded?.Invoke(scene);
                return scenes;
            },
            ct);

        WhisparrMovie[] rows = folded.IsOk ? [.. folded.Value!] : [];
        return SceneStatusProjector.BuildMovieIndex(Array.ConvertAll(rows, row => row.Facts));
    }

    internal static long Settled()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        return GC.GetTotalMemory(forceFullCollection: true);
    }

    internal static HttpResponseMessage Json(string body)
    {
        var content = new StringContent(body, Encoding.UTF8, "application/json");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    internal static int SeriesIdOf(string url)
    {
        var query = new Uri(url).Query.TrimStart('?');
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            if (pair.StartsWith("seriesId=", StringComparison.Ordinal)
                && int.TryParse(pair.AsSpan("seriesId=".Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            {
                return id;
            }
        }

        return 0;
    }

    internal static string SeriesArray(int sites)
        => "[" + string.Join(",", Enumerable.Range(1, sites).Select(SeriesRow)) + "]";

    internal static string SeriesRow(int site) => string.Create(
        CultureInfo.InvariantCulture,
        $$"""{"id":{{site}},"tvdbId":{{9000 + site}},"title":"Generated Site {{site}}","titleSlug":"generated-site-{{site}}","path":"/data/media/sites/Generated Site {{site}}","monitored":true,"monitorNewItems":"all","qualityProfileId":1,"rootFolderPath":"/data/media","tags":[]}""");

    internal static string EpisodeArray(int site, int episodes)
        => "[" + string.Join(",", Enumerable.Range(1, episodes).Select(i => EpisodeRow(site, i))) + "]";

    // One episode row, carrying the members the synthesis reads plus the ones it does not, so a per-site
    // body is realistically sized rather than a stub.
    internal static string EpisodeRow(int site, int episode) => string.Create(
        CultureInfo.InvariantCulture,
        $$"""{"id":{{(site * 1_000_000) + episode}},"title":"Scene number {{episode}} of Generated Site {{site}}","releaseDate":"2026-01-01","episodeFileId":{{(episode % 4 == 0 ? (site * 1_000_000) + episode : 0)}},"tvdbId":{{(site * 1_000_000) + episode}},"seriesId":{{site}},"hasFile":{{(episode % 4 == 0 ? "true" : "false")}},"monitored":{{(episode % 2 == 0 ? "true" : "false")}},"overview":"","seasonNumber":1,"episodeNumber":{{episode}},"absoluteEpisodeNumber":{{episode}}}""");

    internal static string EpisodeFileArray(int site, int episodes)
        => "[" + string.Join(
            ",",
            Enumerable.Range(1, episodes).Where(i => i % 4 == 0).Select(i => EpisodeFileRow(site, i))) + "]";

    internal static string EpisodeFileRow(int site, int episode) => string.Create(
        CultureInfo.InvariantCulture,
        $$"""{"id":{{(site * 1_000_000) + episode}},"seriesId":{{site}},"path":"/data/media/sites/Generated Site {{site}}/2026-01-01 - Scene number {{episode}}.mp4"}""");

    /// <summary>
    /// A transport that composes each site's body at call time from the series id in the request URL, so it
    /// holds counters and nothing per site.
    /// </summary>
    internal class SiteWalkHandler(int sites, int episodesPerSite) : HttpMessageHandler
    {
        /// <summary>Sites for which BOTH per-site reads have been served — the still-in-progress reading.</summary>
        public int SitesFullyServed { get; private set; }

        public int Sites => sites;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            var seriesId = SeriesIdOf(uri.ToString());
            switch (uri.AbsolutePath)
            {
                case "/api/v3/series":
                    return Task.FromResult(Json(SeriesBody(sites)));
                case "/api/v3/episode":
                    return Task.FromResult(Json(EpisodeBody(seriesId, episodesPerSite)));
                case "/api/v3/episodefile":
                    SitesFullyServed++;
                    return Task.FromResult(Json(EpisodeFileBody(seriesId, episodesPerSite)));
                default:
                    return Task.FromResult(Json("[]"));
            }
        }

        protected virtual string SeriesBody(int siteCount) => SeriesArray(siteCount);

        protected virtual string EpisodeBody(int site, int episodes) => EpisodeArray(site, episodes);

        protected virtual string EpisodeFileBody(int site, int episodes) => EpisodeFileArray(site, episodes);
    }

    /// <summary>
    /// The same transport with every site's body built up front — the shape the baseline guard exists to
    /// reject, since it holds the corpus the measurement claims the read does not.
    /// </summary>
    internal sealed class PreBuiltSiteWalkHandler : SiteWalkHandler
    {
        private readonly string _series;
        private readonly Dictionary<int, string> _episodes = [];
        private readonly Dictionary<int, string> _files = [];

        public PreBuiltSiteWalkHandler(int sites, int episodesPerSite)
            : base(sites, episodesPerSite)
        {
            _series = SeriesArray(sites);
            for (var site = 1; site <= sites; site++)
            {
                _episodes[site] = EpisodeArray(site, episodesPerSite);
                _files[site] = EpisodeFileArray(site, episodesPerSite);
            }
        }

        protected override string SeriesBody(int siteCount) => _series;

        protected override string EpisodeBody(int site, int episodes) => _episodes[site];

        protected override string EpisodeFileBody(int site, int episodes) => _files[site];
    }
}
