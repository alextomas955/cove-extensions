using System.Globalization;
using System.Net;
using System.Text.Json;
using WhisparrSync.Adapters;
using WhisparrSync.Client;
using WhisparrSync.Contracts;
using WhisparrSync.Options;
using WhisparrSync.Push;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.Api;

/// <summary>
/// The request-count columns of the read-path ledger: what each Whisparr-facing path costs in outbound calls,
/// and how that cost moves when the input grows.
/// </summary>
/// <remarks>
/// <para>
/// Counts are asserted as EXACT numbers against a stated formula, never as an upper bound. A greater-than
/// assertion is satisfied by a loop that stopped early, which is a truncated answer wearing a cost
/// reduction's clothes.
/// </para>
/// <para>
/// Every case takes its reading at two input sizes at least tenfold apart, because a single size cannot tell
/// a constant apart from a term that grows: the shape is the claim, and one point has no shape.
/// </para>
/// <para>
/// Scoped to the newer generation. The older one has no movie entity at all, so it has no figure here and
/// this class must not be read as implying one.
/// </para>
/// </remarks>
[Trait("Tier", "L0")]
public sealed class BoundedReadLedgerTests
{
    private const string BaseUrl = "http://localhost:6969";
    private const string ApiKey = "test-api-key";
    private const string StudioId = "a0000000-0000-4000-8000-000000000001";

    // The grain the catalogue hydration chunks at. Restated so the boundary cases below read as boundaries; a
    // disagreement with the shipped constant surfaces as a failing count rather than as a silent pass.
    private const int HydrationChunk = 1000;

    // Tenfold apart, and small enough that a fake handler answers every call without the run becoming a
    // stress test of the test host.
    private const int SmallSceneCount = 3;
    private const int LargeSceneCount = 30;

    private static WhisparrOptions Options() => new()
    {
        SelectedVersion = "v3",
        BaseUrl = BaseUrl,
        ApiKey = ApiKey,
    };

    private static HttpResponseMessage Json(string body)
        => FakeHttpMessageHandler.Respond(HttpStatusCode.OK, "application/json", body)();

    private static string SceneStashId(int n)
        => string.Create(CultureInfo.InvariantCulture, $"10000000-0000-4000-8000-{n:D12}");

    // ---- the single-scene push resolve ----

    /// <summary>
    /// A per-scene push resolve asks Whisparr about that scene and nothing else, at any number of scenes.
    /// </summary>
    /// <remarks>
    /// The positive narrow count is asserted beside the zero, because a path that stopped asking Whisparr
    /// anything satisfies the zero on its own and answers every scene "not added" — the failure the zero
    /// alone cannot distinguish from success.
    /// </remarks>
    [Theory]
    [InlineData(SmallSceneCount)]
    [InlineData(LargeSceneCount)]
    public async Task A_per_scene_push_resolve_costs_one_narrow_read_per_scene_and_no_whole_set_read(int scenes)
    {
        var handler = FakeHttpMessageHandler.Json("[]");
        var adapter = new V3Adapter(new WhisparrClient(new HttpClient(handler)), TimeSpan.Zero);

        for (var n = 1; n <= scenes; n++)
        {
            await adapter.FindSceneMovieAsync(BaseUrl, ApiKey, [SceneStashId(n)], CancellationToken.None);
        }

        var breakdown = WhisparrRequestCounter.Classify(handler);
        Assert.Equal(0, breakdown.WholeSetMovieReads);
        Assert.Equal(scenes, breakdown.NarrowMovieReads);
        Assert.Equal(0, breakdown.Writes);
    }

    // ---- the bulk add path's per-scene context re-resolution ----

    /// <summary>
    /// The bulk mark-wanted path re-resolves its add context per scene, so its non-movie read count carries a
    /// per-scene term rather than a fixed one.
    /// </summary>
    /// <remarks>
    /// Read at two scene counts a tenfold apart and asserted as the SLOPE between them, so the figure is the
    /// per-scene multiplier itself rather than a total that a one-off preamble could inflate. A path that had
    /// memoised the context would show a slope of zero and fail here, which is the point: this is the term the
    /// ledger names as the next ceiling, and it must fail loudly the day it moves.
    /// </remarks>
    [Fact]
    public async Task The_bulk_add_path_re_resolves_its_context_once_per_scene()
    {
        var small = await MarkWantedAsync(SmallSceneCount);
        var large = await MarkWantedAsync(LargeSceneCount);

        var slope =
            (double)(large.ContextReads - small.ContextReads) / (LargeSceneCount - SmallSceneCount);
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"ADD CONTEXT {SmallSceneCount,4} scenes -> {small.ContextReads,4} context reads   "
                + $"{LargeSceneCount,4} scenes -> {large.ContextReads,4}   slope {slope:0.00}/scene"));

        Assert.Equal(AddContextReadsPerScene, slope);
        Assert.Equal((AddContextReadsPerScene * SmallSceneCount) + AddContextReadsOnce, small.ContextReads);
        Assert.Equal((AddContextReadsPerScene * LargeSceneCount) + AddContextReadsOnce, large.ContextReads);
        Assert.Equal(0, small.Breakdown.WholeSetMovieReads);
        Assert.Equal(0, large.Breakdown.WholeSetMovieReads);
        Assert.True(LargeSceneCount >= SmallSceneCount * 10);
    }

    /// <summary>
    /// The sibling batch path resolves its context once, so its context-read count does not move with the
    /// scene count at all.
    /// </summary>
    /// <remarks>
    /// This is the falsification of the case above. Both paths add scenes through the same resolver and the
    /// same counting rule; only one of them re-resolves. A slope that could not come out zero would be
    /// measuring the harness rather than the path, and the number above would carry no information.
    /// </remarks>
    [Fact]
    public async Task The_batch_add_path_resolves_its_context_once_for_the_whole_batch()
    {
        var small = await AddScenesAsync(SmallSceneCount);
        var large = await AddScenesAsync(LargeSceneCount);

        Assert.Equal(small.ContextReads, large.ContextReads);
        Assert.Equal(AddContextReadsPerScene + AddContextReadsOnce, large.ContextReads);
    }

    // The root folder and the tag list are read again for every scene; the quality profile is read once per
    // operation and cached from then on. Both figures are stated so a change to either fails with a message
    // that names which one moved.
    private const int AddContextReadsPerScene = 2;
    private const int AddContextReadsOnce = 1;

    private static Task<(int ContextReads, WhisparrRequestBreakdown Breakdown)> MarkWantedAsync(int scenes)
        => RunAddAsync(scenes, (actions, refs, ct) => actions.MarkScenesWantedAsync(refs, ct));

    private static Task<(int ContextReads, WhisparrRequestBreakdown Breakdown)> AddScenesAsync(int scenes)
        => RunAddAsync(scenes, (actions, refs, ct) => actions.AddScenesAsync(refs, ct));

    private static async Task<(int ContextReads, WhisparrRequestBreakdown Breakdown)> RunAddAsync(
        int scenes,
        Func<SceneActions, IReadOnlyList<SceneRef>, CancellationToken, Task<WhisparrResult<BulkActionResult>>> add)
    {
        var handler = AddCapableInstance();
        var actions = new SceneActions(
            new WhisparrClient(new HttpClient(handler)),
            Options(),
            new FakeCoveLibraryPort(),
            new WhisparrCapabilityPort(new WhisparrClient(new HttpClient(handler))));

        var refs = Enumerable
            .Range(1, scenes)
            .Select(n => new SceneRef(SceneStashId(n), $"Scene {n}", null))
            .ToArray();
        var result = await add(actions, refs, CancellationToken.None);

        Assert.True(result.IsOk);
        return (CountContextReads(handler), WhisparrRequestCounter.Classify(handler));
    }

    // The three reads an add's context is derived from, counted together because they are one concern: what
    // root, what tags and what profile this add carries.
    private static int CountContextReads(FakeHttpMessageHandler handler)
        => handler.Requests.Count(r =>
            r.Method == HttpMethod.Get
            && (r.Url.EndsWith("/api/v3/rootfolder", StringComparison.Ordinal)
                || r.Url.EndsWith("/api/v3/tag", StringComparison.Ordinal)
                || r.Url.EndsWith("/api/v3/qualityprofile", StringComparison.Ordinal)));

    private static FakeHttpMessageHandler AddCapableInstance()
        => FakeHttpMessageHandler.Json("[]").Also(request =>
        {
            if (request.Url.EndsWith("/api/v3/rootfolder", StringComparison.Ordinal))
            {
                return Json(JsonSerializer.Serialize(
                    new[] { new { id = 1, path = "/data/media", accessible = true, freeSpace = 1L } }));
            }

            if (request.Url.EndsWith("/api/v3/qualityprofile", StringComparison.Ordinal))
            {
                return Json(JsonSerializer.Serialize(new[] { new { id = 1, name = "Any" } }));
            }

            if (request.Url.EndsWith("/api/v3/tag", StringComparison.Ordinal))
            {
                return Json(JsonSerializer.Serialize(new[] { new { id = 1, label = "cove-sync" } }));
            }

            if (request.Url.EndsWith("/api/v3/movie", StringComparison.Ordinal)
                && request.Method == HttpMethod.Post)
            {
                return Json(JsonSerializer.Serialize(new { id = 99, monitored = true }));
            }

            return null;
        });

    // ---- the per-entity catalogue read ----

    /// <summary>
    /// The per-entity catalogue costs one sibling read plus one hydration per chunk of ids, driven by the end
    /// of the id list.
    /// </summary>
    /// <remarks>
    /// Taken exactly at a chunk boundary and one id past it, because those are the two inputs a loop bounded
    /// by a chunk COUNT rather than by the id list answers identically — and answers with a short row set that
    /// no total-request assertion above the boundary would notice.
    /// </remarks>
    [Theory]
    [InlineData(HydrationChunk)]
    [InlineData(HydrationChunk + 1)]
    [InlineData(2 * HydrationChunk)]
    public async Task The_per_entity_catalogue_costs_one_sibling_read_plus_one_hydration_per_chunk(int ids)
    {
        var handler = CatalogueInstance(ids);
        var adapter = new V3EntityCatalogueAdapter(
            new WhisparrClient(new HttpClient(handler)), TimeSpan.Zero);

        var result = await adapter.ListEntityMoviesAsync(
            BaseUrl, ApiKey, EntityKind.Studio, StudioId, CancellationToken.None);

        var expected = 1 + (int)Math.Ceiling((double)ids / HydrationChunk);
        var breakdown = WhisparrRequestCounter.Classify(handler);
        Assert.Equal(expected, breakdown.Total);
        Assert.Equal(0, breakdown.WholeSetMovieReads);
        // The rows are asserted beside the count: a loop that issued the right number of requests but dropped
        // the last chunk's rows would satisfy the count alone.
        Assert.Equal(ids, result.Value!.Movies.Length);
    }

    private static FakeHttpMessageHandler CatalogueInstance(int ids)
        => FakeHttpMessageHandler.Json("[]").Also(request =>
        {
            if (request.Url.Contains("/movie/listby", StringComparison.OrdinalIgnoreCase))
            {
                return Json(JsonSerializer.Serialize(Enumerable.Range(1, ids)));
            }

            if (request.Url.EndsWith("/api/v3/movie/bulk", StringComparison.Ordinal))
            {
                var asked = JsonSerializer.Deserialize<int[]>(request.Body!)!;
                return Json(JsonSerializer.Serialize(asked.Select(id => new
                {
                    id,
                    foreignId = $"row-{id}",
                    stashId = $"row-{id}",
                    studioForeignId = StudioId,
                    hasFile = false,
                    monitored = true,
                })));
            }

            return null;
        });
}
