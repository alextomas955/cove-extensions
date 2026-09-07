using System.Net;
using System.Text.Json;
using Cove.Plugins;
using Microsoft.AspNetCore.Http;
using WhisparrSync.Adapters;
using WhisparrSync.Client;
using WhisparrSync.Contracts;
using WhisparrSync.Library;
using WhisparrSync.SceneStatus;
using WhisparrSync.Tests.TestSupport;
using Ext = global::WhisparrSync.WhisparrSync;

namespace WhisparrSync.Tests.SceneStatus;

/// <summary>
/// That the videos toolbar answers the same six counts after the summary's movie read stopped materialising
/// Whisparr's row array — and that the read count did not move while the bytes did.
/// </summary>
/// <remarks>
/// The equivalence is the assertion that matters, because the failure this change can produce is a WRONG
/// ANSWER rather than a crash: a projection missing one member silently loses a key, and every scene keyed
/// only by that key reports as not-added.
/// </remarks>
[Trait("Tier", "L0")]
public sealed class SummaryIndexEquivalenceTests
{
    private const string BaseUrl = "http://whisparr.local:6969";
    private const string ApiKey = "KEY";

    private static WhisparrMovie Row(int id, string stashId, string foreignId, string itemType, bool monitored, bool hasFile)
        => new(
            Id: id,
            Title: $"Movie {id}",
            Year: 2026,
            StashId: stashId,
            ForeignId: foreignId,
            ItemType: itemType,
            Monitored: monitored,
            HasFile: hasFile,
            MovieFile: null);

    // Deliberately mixed: scene-typed rows (which key on BOTH ids) beside a movie-typed row (which keys on one),
    // so a projection that lost the item type would change the answer rather than leave it alone.
    private static WhisparrMovie[] Rows(int count) =>
    [
        .. Enumerable.Range(1, count).Select(i => i % 3 == 0
            ? Row(i, $"stash-{i}", $"99{i}", "movie", monitored: i % 2 == 0, hasFile: i % 4 == 0)
            : Row(i, $"stash-{i}", $"foreign-{i}", "scene", monitored: i % 2 == 0, hasFile: i % 4 == 0)),
    ];

    // Every scene keyed the way a real library's are: some by a stash id, some by the scene-typed foreign id
    // that only exists when the item type survived the projection, and some by nothing Whisparr holds.
    private static CoveVideo[] Videos(int count) =>
    [
        .. Enumerable.Range(1, count).Select(i => new CoveVideo(
            i,
            $"Video {i}",
            null,
            i % 5 == 0 ? [$"unknown-{i}"] : i % 2 == 0 ? [$"stash-{i}"] : [$"foreign-{i}"],
            [],
            [],
            [])),
    ];

    private static FakeHttpMessageHandler V3Instance(WhisparrMovie[] rows)
        => FakeHttpMessageHandler.Json(JsonSerializer.Serialize(rows.Select(row => new
        {
            id = row.Id,
            title = row.Title,
            stashId = row.StashId,
            foreignId = row.ForeignId,
            itemType = row.ItemType,
            monitored = row.Monitored,
            hasFile = row.HasFile,
        })));

    [Theory]
    [InlineData(3)]
    [InlineData(300)]
    public async Task The_streamed_index_and_the_materialised_one_answer_the_same_six_counts(int rowCount)
    {
        var rows = Rows(rowCount);
        var videos = Videos(rowCount);
        var client = new WhisparrClient(new HttpClient(V3Instance(rows)));

        var streamed = await Ext.StatusIndexAsync(new V3Adapter(client), BaseUrl, ApiKey, CancellationToken.None);
        var materialised = SceneStatusProjector.BuildMovieIndex(rows);

        Assert.Equal(
            SceneStatusProjector.SummaryCounts(videos, materialised, NoExclusions),
            SceneStatusProjector.SummaryCounts(videos, streamed, NoExclusions));
    }

    [Fact]
    public async Task A_failed_movie_read_leaves_the_summary_counting_every_scene_as_not_added()
    {
        var client = new WhisparrClient(new HttpClient(FakeHttpMessageHandler.Status(HttpStatusCode.BadGateway)));

        var index = await Ext.StatusIndexAsync(new V3Adapter(client), BaseUrl, ApiKey, CancellationToken.None);
        var counts = SceneStatusProjector.SummaryCounts(Videos(6), index, NoExclusions);

        Assert.Empty(index);
        Assert.Equal(new SceneStatusCounts(0, 0, 6, 0, 0, 6), counts);
    }

    [Fact]
    public async Task The_older_generation_with_no_sites_to_walk_folds_to_the_same_empty_index_type()
    {
        var rows = Rows(4);
        var client = new WhisparrClient(new HttpClient(FakeHttpMessageHandler.Json("[]")));
        var v2 = new V2Adapter(client);

        var folded = await v2.LoadStatusIndexAsync(BaseUrl, ApiKey, onFolded: null, CancellationToken.None);

        // The handler answers an empty site list, so no site is walked and no row is synthesized: the emptiness
        // here is the EMPTY CORPUS, not the keying rule. The keying rule's own emptiness is pinned over a real
        // three-site corpus by V2SummaryReadCostTests, which is also the reason that generation does not declare
        // the status-index role and is not reached through the handler below.
        //
        // The type parity is what is asserted: the fold lands on the same index type the whole-row build does,
        // over the same keying rule, so the narrow projection cannot lose a key the full row would have kept.
        Assert.IsNotAssignableFrom<IWhisparrStatusIndexSource>(v2);
        Assert.Empty(folded.Value!);
        Assert.Equal(
            SceneStatusProjector.BuildMovieIndex(rows).Keys.Order(StringComparer.Ordinal),
            SceneStatusProjector.BuildMovieIndex(Array.ConvertAll(rows, row => row.Facts)).Keys.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(300)]
    public async Task The_summary_costs_one_whole_set_movie_read_and_one_exclusion_read_at_any_row_count(int rowCount)
    {
        var handler = V3Instance(Rows(rowCount));
        var extension = await ExtensionWithStoredCreds("v3");

        var result = await extension.SceneStatusSummaryAsync(new WhisparrClient(new HttpClient(handler)), CancellationToken.None);

        Assert.NotNull(result);
        var breakdown = WhisparrRequestCounter.Classify(handler);

        // The read narrowed its PAYLOAD, not its scope: it still asks for the whole set, and the instrument must
        // keep saying so. Reclassifying it because the bytes shrank would make the instrument report on a
        // substitute for the thing under test.
        Assert.Equal(1, breakdown.WholeSetMovieReads);
        Assert.Equal(0, breakdown.NarrowMovieReads);
        Assert.Equal(0, breakdown.Writes);
        Assert.Equal(1, handler.Requests.Count(r => r.Url.Contains("/api/v3/exclusions", StringComparison.Ordinal)));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task The_older_generations_summary_declines_before_it_contacts_whisparr()
    {
        var handler = FakeHttpMessageHandler.Json("[]");
        var extension = await ExtensionWithStoredCreds("v2");

        var result = await extension.SceneStatusSummaryAsync(
            new WhisparrClient(new HttpClient(handler)), CancellationToken.None);

        // The refusal, not a partition. Every row that generation synthesizes keys under nothing, so the counts it
        // used to answer read Monitored 0 · Unmonitored 0 · Not added N for every library — and "not added" means
        // Whisparr does not have the scene, which is the one conclusion those counts cannot support. A classified
        // 400 is what the toolbar reads as "this connection does not offer it" and paints nothing for.
        //
        // Asserted on the declared response record rather than on serialized text, so the code is pinned by value
        // and the assertion needs no serializer of its own to be right about.
        Assert.Equal(400, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        Assert.Equal(
            new ErrorResponse("VERSION_UNSUPPORTED"),
            Assert.IsAssignableFrom<IValueHttpResult>(result).Value);

        // Before the transport, so a refusal costs no request and leaves nothing behind on the instance.
        Assert.Empty(handler.Requests);
    }

    private static async Task<Ext> ExtensionWithStoredCreds(string version)
    {
        var store = new FakeStore();
        await store.SetAsync(
            "options",
            $"{{\"BaseUrl\":\"{BaseUrl}\",\"ApiKey\":\"{ApiKey}\",\"SelectedVersion\":\"{version}\"}}");
        var extension = new Ext();
        ((IStatefulExtension)extension).SetStore(store);
        return extension;
    }

    private static readonly IReadOnlySet<string> NoExclusions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}
