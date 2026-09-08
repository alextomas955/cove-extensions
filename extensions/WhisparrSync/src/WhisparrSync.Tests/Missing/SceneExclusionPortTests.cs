using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using WhisparrSync.Missing;
using WhisparrSync.Monitoring;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Missing;

/// <summary>
/// What one page's exclusion read costs, what it composes, and what it keeps.
/// </summary>
/// <remarks>
/// The cost and the retention are the claims. Every answer here is also derivable from a shape that
/// holds the whole response, so the composed request, the request count and what survives the call
/// are asserted rather than the returned set alone.
/// <para>
/// The recorded rows are real answers from the instance the fixture names, so a member read under a
/// name that instance does not use fails here.
/// </para>
/// </remarks>
public sealed class SceneExclusionPortTests
{
    private const string FixtureName = "whisparr-v3-3.4.0.1387-exclusions.json";
    private const string SomeKey = "0e2e0e2e0e2e0e2e0e2e0e2e0e2e0e2e";

    private static Uri SomeInstance => new("http://whisparr.invalid:6969");

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    /// <summary>The recording states the build and the day it came from.</summary>
    [Fact]
    public void TheFixtureStatesItsOwnProvenance()
    {
        var fixture = Fixture();

        Assert.Equal("2026-09-06", fixture.GetProperty("recordedOn").GetString());
        Assert.Equal("Whisparr 3.4.0.1387", fixture.GetProperty("recordedAgainst").GetString());
    }

    /// <summary>
    /// The instance narrows this route by no parameter, so the request composes none.
    /// </summary>
    /// <remarks>
    /// Asserted on the request that was sent, not on the source. The three narrowing spellings the
    /// fixture records were each measured against the instance: a filter key and a bare foreign id
    /// answer the whole list under a success, and a foreign id as a further segment is a not-found.
    /// An ignored parameter answering a success is indistinguishable from one that narrowed.
    /// </remarks>
    [Fact]
    public async Task TheComposedRequestCarriesNoQueryStringAtAll()
    {
        var handler = BodyRecordingHandler.Answering(HttpStatusCode.OK, RecordedRows());
        using var http = new HttpClient(handler);
        var client = TestWhisparrClient.Over(http);

        await client.ReduceExclusionsAsync(SomeInstance, SomeKey, ["a-scene"], TestCt);

        var target = Assert.Single(handler.Targets);
        Assert.EndsWith("/api/v3/exclusions", target, StringComparison.Ordinal);
        Assert.DoesNotContain("?", target, StringComparison.Ordinal);
    }

    /// <summary>The measured shapes that made a narrowed read impossible are recorded, not assumed.</summary>
    [Fact]
    public void NoNarrowingSpellingTheFixtureRecordsNarrowedAnything()
    {
        var attempts = Fixture().GetProperty("noNarrowingParameter");
        var whole = Fixture().GetProperty("wholeSet").GetProperty("rows").GetInt32();

        Assert.Equal(
            whole,
            attempts.GetProperty("pagedFilterKey").GetProperty("totalRecords").GetInt32());
        Assert.Equal(whole, attempts.GetProperty("bareForeignId").GetProperty("rows").GetInt32());
        Assert.Equal(404, attempts.GetProperty("foreignIdAsSegment").GetProperty("status").GetInt32());
    }

    /// <summary>
    /// The answer is bounded by the page and never by the response, whatever the response holds.
    /// </summary>
    [Fact]
    public async Task TheAnswerIsBoundedByThePageAndNotByTheResponse()
    {
        var rows = RecordedRowsElement();
        var page = ForeignIdsFrom(rows, 40);
        var handler = BodyRecordingHandler.Answering(
            HttpStatusCode.OK, ManyRowsAround(rows, 5_000));
        using var http = new HttpClient(handler);
        var client = TestWhisparrClient.Over(http);

        var excluded = await client.ReduceExclusionsAsync(SomeInstance, SomeKey, page, TestCt);

        Assert.True(
            excluded.Count <= page.Count,
            $"{excluded.Count} identifiers came back for a page of {page.Count}.");
        Assert.Equal([.. page.Order()], [.. excluded.Order()]);
    }

    /// <summary>
    /// A page identifier the response does not name is not excluded, however large the response is.
    /// </summary>
    [Fact]
    public async Task AnIdentifierTheResponseDoesNotNameIsNotExcluded()
    {
        var rows = RecordedRowsElement();
        var named = ForeignIdsFrom(rows, 3);
        var handler = BodyRecordingHandler.Answering(HttpStatusCode.OK, RecordedRows());
        using var http = new HttpClient(handler);
        var client = TestWhisparrClient.Over(http);

        var excluded = await client.ReduceExclusionsAsync(
            SomeInstance, SomeKey, [.. named, "a-scene-nothing-excluded"], TestCt);

        Assert.Equal([.. named.Order()], [.. excluded.Order()]);
    }

    /// <summary>
    /// Nothing derived from the response survives the call: every identifier the answer carries is
    /// the caller's own string instance, not one parsed out of the response.
    /// </summary>
    /// <remarks>
    /// Reference identity rather than value equality, because the two are indistinguishable by value
    /// and only one of them proves that no parsed row outlives the read. A source assertion that no
    /// field holds a collection would agree with itself whatever the method allocated, so the
    /// declared-field check below is the second half of the claim and not the whole of it.
    /// </remarks>
    [Fact]
    public async Task EveryIdentifierTheAnswerCarriesIsTheCallersOwnInstance()
    {
        var rows = RecordedRowsElement();
        var page = ForeignIdsFrom(rows, 40);
        var handler = BodyRecordingHandler.Answering(
            HttpStatusCode.OK, ManyRowsAround(rows, 5_000));
        using var http = new HttpClient(handler);
        var client = TestWhisparrClient.Over(http);

        var excluded = await client.ReduceExclusionsAsync(SomeInstance, SomeKey, page, TestCt);

        Assert.NotEmpty(excluded);
        Assert.All(
            excluded,
            named => Assert.Contains(page, asked => ReferenceEquals(asked, named)));
    }

    /// <summary>
    /// The outbound client holds no field a response-sized collection could survive in.
    /// </summary>
    /// <remarks>
    /// The answer is returned rather than stored, so the only other place a parsed response could
    /// outlive the call is a field on the one type that reads it.
    /// </remarks>
    [Fact]
    public void TheOutboundClientDeclaresNoFieldACollectionCouldSurviveIn()
        => Assert.Empty(
            typeof(WhisparrClient)
                .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                .Where(field => typeof(System.Collections.IEnumerable).IsAssignableFrom(field.FieldType))
                .Select(field => field.Name));

    /// <summary>One request per page derivation, whatever the page holds.</summary>
    [Fact]
    public async Task ThePortSpendsOneRequestPerPageAndNeverOnePerCard()
    {
        var reading = new RecordingExclusionReading();
        var page = PageOfForty();

        await new SceneExclusionPort().ReadExcludedAsync(
            reading, SomeInstance, SomeKey, page, TestCt);

        Assert.Equal(1, reading.Calls);
        Assert.Equal(page, reading.AskedAbout[0]);
    }

    /// <summary>An empty page asks the instance nothing.</summary>
    [Fact]
    public async Task AnEmptyPageIssuesNoRequestAtAll()
    {
        var reading = new RecordingExclusionReading();

        var excluded = await new SceneExclusionPort().ReadExcludedAsync(
            reading, SomeInstance, SomeKey, [], TestCt);

        Assert.Empty(excluded);
        Assert.Equal(0, reading.Calls);
    }

    /// <summary>An answer that did not arrive excludes nothing rather than everything.</summary>
    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task AnUnsuccessfulAnswerExcludesNothing(HttpStatusCode status)
    {
        var handler = BodyRecordingHandler.Answering(status, RecordedRows());
        using var http = new HttpClient(handler);
        var client = TestWhisparrClient.Over(http);

        var excluded = await client.ReduceExclusionsAsync(
            SomeInstance, SomeKey, PageOfForty(), TestCt);

        Assert.Empty(excluded);
    }

    /// <summary>A body this could not read excludes nothing rather than failing the page.</summary>
    [Fact]
    public async Task ABodyThatIsNotTheExpectedShapeExcludesNothing()
    {
        var handler = BodyRecordingHandler.Answering(HttpStatusCode.OK, "<!doctype html><html></html>");
        using var http = new HttpClient(handler);
        var client = TestWhisparrClient.Over(http);

        var excluded = await client.ReduceExclusionsAsync(
            SomeInstance, SomeKey, PageOfForty(), TestCt);

        Assert.Empty(excluded);
    }

    private static JsonElement Fixture()
        => JsonDocument.Parse(ProbeFixtures.Read(FixtureName)).RootElement;

    private static string RecordedRows()
        => Fixture().GetProperty("response").GetRawText();

    private static JsonElement RecordedRowsElement() => Fixture().GetProperty("response");

    private static List<string> ForeignIdsFrom(JsonElement rows, int count)
        => [.. rows.EnumerateArray()
            .Take(count)
            .Select(row => row.GetProperty("foreignId").GetString()!)];

    // The recorded rows, repeated with fresh identifiers until the answer holds the row count asked
    // for. The recorded rows come first, so the identifiers a case asks about are named.
    private static string ManyRowsAround(JsonElement rows, int total)
    {
        var composed = new JsonArray();
        var recorded = rows.EnumerateArray().ToArray();
        for (var index = 0; index < total; index++)
        {
            var row = (JsonObject)JsonNode.Parse(recorded[index % recorded.Length].GetRawText())!;
            if (index >= recorded.Length)
            {
                row["foreignId"] = $"padding-{index}";
            }

            composed.Add(row);
        }

        return composed.ToJsonString();
    }

    private static string[] PageOfForty()
        => [.. Enumerable.Range(0, 40).Select(index => $"scene-{index}")];

    private sealed class RecordingExclusionReading : IWhisparrSceneExclusionReading
    {
        public int Calls { get; private set; }

        public List<IReadOnlyList<string>> AskedAbout { get; } = [];

        public Task<IReadOnlySet<string>> ReduceExclusionsAsync(
            Uri baseAddress,
            string apiKey,
            IReadOnlyCollection<string> providerSceneIds,
            CancellationToken ct)
        {
            Calls++;
            AskedAbout.Add([.. providerSceneIds]);
            return Task.FromResult<IReadOnlySet<string>>(
                new HashSet<string>(StringComparer.Ordinal));
        }

        public Task<SceneExclusionLookup> FindSceneExclusionAsync(
            Uri baseAddress, string apiKey, string foreignId, CancellationToken ct)
            => throw new NotSupportedException(
                "The port under test reduces a page and never asks for one exclusion row's own "
                    + "identifier.");
    }
}
