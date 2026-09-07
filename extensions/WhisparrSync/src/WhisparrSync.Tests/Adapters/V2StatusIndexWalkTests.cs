using System.Globalization;
using System.Net;
using System.Text;
using WhisparrSync.Adapters;
using WhisparrSync.Client;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.Adapters;

/// <summary>
/// The one site walk the older generation's two set reads share: that the scene sequence it hands a fold is
/// the sequence the materialized read returns, that a failure part-way through discards everything, and that
/// the walk still costs one site-list read plus two reads per site.
/// </summary>
/// <remarks>
/// The scene sequence is the SENSITIVE subject on this generation. A synthesized scene carries a null stash
/// id and an item type the keying rule does not read, so the status index is empty however the walk behaves,
/// and no comparison of counts BY STATE can tell a dropped scene from a double-folded one.
/// <para>
/// Two assertions divide that work, and neither substitutes for the other. The CORPUS-ANCHORED COUNT catches
/// a site the shared walk dropped or repeated: both reads would drop it together, so a comparison between them
/// could not see it. It was observed failing in both directions — 15 against 20 for a skipped site, 40 against
/// 20 for a double fold. ROW-SEQUENCE IDENTITY catches the streamed fold diverging from the materialized read,
/// and it reaches that fold through the adapter's own internal recording seam, so the scenes it compares are
/// the shipped method's rather than a test-local re-implementation's.
/// </para>
/// </remarks>
[Trait("Tier", "L0")]
public sealed class V2StatusIndexWalkTests
{
    private const string BaseUrl = "http://whisparr.local:6969";
    private const string ApiKey = "KEY";

    private const int Sites = 4;
    private const int ScenesPerSite = 5;

    [Fact]
    public async Task The_walk_hands_the_fold_the_scene_sequence_the_materialised_read_returns()
    {
        var materialised = await Adapter(Instance()).ListMoviesAsync(BaseUrl, ApiKey, CancellationToken.None);

        var recorded = new List<WhisparrMovie>();
        var streamed = await Adapter(Instance())
            .LoadStatusIndexAsync(BaseUrl, ApiKey, recorded.Add, CancellationToken.None);

        Assert.True(materialised.IsOk);
        Assert.True(streamed.IsOk);

        // Both sides anchored to the corpus rather than only to each other, because the two share the walk: a
        // walk that dropped or repeated a site would do so on both sides and satisfy the identity below.
        Assert.True(
            materialised.Value!.Length == Sites * ScenesPerSite,
            $"the materialised read returned {materialised.Value.Length} scenes where the corpus holds "
                + $"{Sites * ScenesPerSite}");
        Assert.True(
            recorded.Count == Sites * ScenesPerSite,
            $"the streamed fold was handed {recorded.Count} scenes where the corpus holds {Sites * ScenesPerSite}");

        // Element for element and in order, on every synthesized member — a scene whose file join or site
        // stamp differed between the two reads would pass a count comparison.
        Assert.Equal<WhisparrMovie>(materialised.Value, recorded);
    }

    [Fact]
    public async Task Both_folds_run_the_same_walk_so_neither_the_request_count_nor_its_order_can_diverge()
    {
        var materialisingHandler = Instance();
        var foldingHandler = Instance();

        await Adapter(materialisingHandler).ListMoviesAsync(BaseUrl, ApiKey, CancellationToken.None);
        await Adapter(foldingHandler).LoadStatusIndexAsync(BaseUrl, ApiKey, onFolded: null, CancellationToken.None);

        string[] expected =
        [
            "GET /api/v3/series",
            .. Enumerable.Range(1, Sites).SelectMany(site => new[]
            {
                $"GET /api/v3/episode?seriesId={site}",
                $"GET /api/v3/episodefile?seriesId={site}",
            }),
        ];

        Assert.Equal(expected, materialisingHandler.Requests.Select(Describe));
        Assert.Equal(expected, foldingHandler.Requests.Select(Describe));
        Assert.Equal(1 + (2 * Sites), foldingHandler.Requests.Count);
        Assert.Equal(0, WhisparrRequestCounter.Classify(foldingHandler).Writes);
    }

    [Fact]
    public async Task A_non_ok_read_at_a_middle_site_discards_the_sites_that_had_already_succeeded()
    {
        var handler = Instance(request =>
            request.Url.Contains("/episodefile?seriesId=3", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.BadGateway)
                : null);

        var folded = await CollectAsync(Adapter(handler));

        // The accumulator held two whole sites when the third failed, so a partial synthesis would be visible
        // here as an Ok carrying ten scenes — the assertion the empty index cannot make.
        Assert.False(folded.IsOk);
        Assert.Null(folded.Value);
        Assert.DoesNotContain(handler.Requests, r => r.Url.Contains("seriesId=4", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_index_fold_propagates_that_failure_rather_than_answering_an_empty_index()
    {
        var handler = Instance(request =>
            request.Url.Contains("/episodefile?seriesId=3", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.BadGateway)
                : null);

        var index = await Adapter(handler).LoadStatusIndexAsync(BaseUrl, ApiKey, onFolded: null, CancellationToken.None);

        // Distinct from the walk-level assertion above in what it rules out: this fold's Ok answer and its
        // failure answer must not look alike. An empty dictionary is what a SUCCESSFUL fold produces on this
        // generation, so a failure that degraded to one would be indistinguishable from a clean read of a
        // Whisparr holding nothing.
        Assert.False(index.IsOk);
        Assert.Null(index.Value);
    }

    [Fact]
    public void The_status_index_role_is_declared_only_by_the_generation_whose_rows_carry_a_scene_id()
    {
        var client = new WhisparrClient(new HttpClient(FakeHttpMessageHandler.Json("[]")));

        // Presence of the role is the whole capability decision, so the absence is asserted as directly as the
        // presence: this generation's rows key under nothing, so the index it can build is empty for a library of
        // any size, and a consumer partitioning scenes by state cannot tell that apart from a Whisparr holding
        // none of them. The summary refuses on the narrowing rather than answering that partition.
        Assert.IsNotAssignableFrom<IWhisparrStatusIndexSource>(new V2Adapter(client));
        Assert.IsAssignableFrom<IWhisparrStatusIndexSource>(new V3Adapter(client));
    }

    private static V2Adapter Adapter(HttpMessageHandler handler)
        => new(new WhisparrClient(new HttpClient(handler)));

    // Drives the shared walk directly, which is what the failure case needs: it is the only shape that exposes
    // the accumulator's own result, so a Value of null is assertable where the summary's index cannot show it.
    // It observes the walk, never the streamed fold — that has its own seam.
    private static Task<WhisparrResult<List<WhisparrMovie>>> CollectAsync(V2Adapter adapter)
        => adapter.FoldSiteWalkAsync(
            BaseUrl, ApiKey,
            new List<WhisparrMovie>(),
            (scenes, scene) =>
            {
                scenes.Add(scene);
                return scenes;
            },
            CancellationToken.None);

    private static string Describe(CapturedRequest request)
        => $"{request.Method} {new Uri(request.Url).PathAndQuery}";

    private static FakeHttpMessageHandler Instance(Func<CapturedRequest, HttpResponseMessage?>? fault = null)
        => FakeHttpMessageHandler.Json("[]").Also(request =>
        {
            if (fault?.Invoke(request) is { } faulted)
            {
                return faulted;
            }

            var uri = new Uri(request.Url);
            var site = SiteOf(uri);
            return uri.AbsolutePath switch
            {
                "/api/v3/series" => Ok(Rows(Sites, SiteRow)),
                "/api/v3/episode" => Ok(Rows(ScenesPerSite, scene => SceneRow(site, scene))),
                // Only the scenes with a file are listed, so the join the synthesis performs has both outcomes.
                "/api/v3/episodefile" => Ok("[" + string.Join(
                    ",",
                    Enumerable.Range(1, ScenesPerSite).Where(HasFile).Select(scene => SceneFileRow(site, scene))) + "]"),
                _ => null,
            };
        });

    private static bool HasFile(int scene) => scene % 2 == 0;

    private static string Rows(int count, Func<int, string> row)
        => "[" + string.Join(",", Enumerable.Range(1, count).Select(row)) + "]";

    private static string SiteRow(int site) => string.Create(
        CultureInfo.InvariantCulture,
        $$"""{"id":{{site}},"tvdbId":{{700 + site}},"title":"Site {{site}}","titleSlug":"site-{{site}}","path":"/data/Site {{site}}","monitored":true}""");

    private static string SceneRow(int site, int scene) => string.Create(
        CultureInfo.InvariantCulture,
        $$"""{"id":{{SceneId(site, scene)}},"title":"Site {{site}} scene {{scene}}","releaseDate":"2026-0{{site}}-0{{scene}}","episodeFileId":{{(HasFile(scene) ? SceneId(site, scene) : 0)}},"tvdbId":{{SceneId(site, scene)}},"seriesId":{{site}},"hasFile":{{(HasFile(scene) ? "true" : "false")}},"monitored":{{(scene % 3 == 0 ? "true" : "false")}}}""");

    private static string SceneFileRow(int site, int scene) => string.Create(
        CultureInfo.InvariantCulture,
        $$"""{"id":{{SceneId(site, scene)}},"seriesId":{{site}},"path":"/data/Site {{site}}/scene {{scene}}.mp4"}""");

    private static int SceneId(int site, int scene) => (site * 100) + scene;

    private static int SiteOf(Uri uri)
    {
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            if (pair.StartsWith("seriesId=", StringComparison.Ordinal)
                && int.TryParse(pair.AsSpan("seriesId=".Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var site))
            {
                return site;
            }
        }

        return 0;
    }

    private static HttpResponseMessage Ok(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}
