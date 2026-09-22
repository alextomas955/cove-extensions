using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using WhisparrSync.Missing;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Missing;

public sealed class SceneExclusionPortTests
{
    private const string FixtureName = "whisparr-v3-3.4.0.1387-exclusions.json";
    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    [Fact]
    public void TheFixtureStatesItsOwnProvenance()
    {
        var fixture = Fixture();

        Assert.Equal("2026-09-06", fixture.GetProperty("recordedOn").GetString());
        Assert.Equal("Whisparr 3.4.0.1387", fixture.GetProperty("recordedAgainst").GetString());
    }

    // The instance narrows this route by no parameter. A filter key and a bare foreign id each
    // answer the whole list under a success, and a foreign id as a further segment is a not-found.
    [Fact]
    public async Task TheComposedRequestCarriesNoQueryStringAtAll()
    {
        var handler = BodyRecordingHandler.Answering(HttpStatusCode.OK, RecordedRows());
        using var http = new HttpClient(handler);
        var client = (IWhisparrSceneExclusionReading)TestWhisparrClient.Over(http);

        await client.ReduceExclusionsAsync(["a-scene"], TestCt);

        var target = Assert.Single(handler.Targets);
        Assert.EndsWith("/api/v3/exclusions", target, StringComparison.Ordinal);
        Assert.DoesNotContain("?", target, StringComparison.Ordinal);
    }

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

    [Fact]
    public async Task TheAnswerIsBoundedByThePageAndNotByTheResponse()
    {
        var rows = RecordedRowsElement();
        var page = ForeignIdsFrom(rows, 40);
        var handler = BodyRecordingHandler.Answering(
            HttpStatusCode.OK, ManyRowsAround(rows, 5_000));
        using var http = new HttpClient(handler);
        var client = (IWhisparrSceneExclusionReading)TestWhisparrClient.Over(http);

        var excluded = await client.ReduceExclusionsAsync(page, TestCt);

        Assert.True(
            excluded.Count <= page.Count,
            $"{excluded.Count} identifiers came back for a page of {page.Count}.");
        Assert.Equal([.. page.Order()], [.. excluded.Order()]);
    }

    [Fact]
    public async Task AnIdentifierTheResponseDoesNotNameIsNotExcluded()
    {
        var rows = RecordedRowsElement();
        var named = ForeignIdsFrom(rows, 3);
        var handler = BodyRecordingHandler.Answering(HttpStatusCode.OK, RecordedRows());
        using var http = new HttpClient(handler);
        var client = (IWhisparrSceneExclusionReading)TestWhisparrClient.Over(http);

        var excluded = await client.ReduceExclusionsAsync([.. named, "a-scene-nothing-excluded"], TestCt);

        Assert.Equal([.. named.Order()], [.. excluded.Order()]);
    }

    [Fact]
    public async Task EveryIdentifierTheAnswerCarriesIsTheCallersOwnInstance()
    {
        var rows = RecordedRowsElement();
        var page = ForeignIdsFrom(rows, 40);
        var handler = BodyRecordingHandler.Answering(
            HttpStatusCode.OK, ManyRowsAround(rows, 5_000));
        using var http = new HttpClient(handler);
        var client = (IWhisparrSceneExclusionReading)TestWhisparrClient.Over(http);

        var excluded = await client.ReduceExclusionsAsync(page, TestCt);

        Assert.NotEmpty(excluded);
        Assert.All(
            excluded,
            named => Assert.Contains(page, asked => ReferenceEquals(asked, named)));
    }

    [Fact]
    public void TheOutboundSeamDeclaresNoFieldACollectionCouldSurviveIn()
        => Assert.Empty(
            OutboundSeamTypes.All
                .SelectMany(type => type.GetFields(
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
                .Where(field => typeof(System.Collections.IEnumerable).IsAssignableFrom(field.FieldType))
                .Select(field => $"{field.DeclaringType?.Name}.{field.Name}"));

    [Fact]
    public async Task ThePortSpendsOneRequestPerPageAndNeverOnePerCard()
    {
        var reading = new RecordingExclusionReading();
        var page = PageOfForty();

        await SceneExclusionPort.ReadExcludedAsync(reading, page, TestCt);

        Assert.Equal(1, reading.Calls);
        Assert.Equal(page, reading.AskedAbout[0]);
    }

    [Fact]
    public async Task AnEmptyPageIssuesNoRequestAtAll()
    {
        var reading = new RecordingExclusionReading();

        var excluded = await SceneExclusionPort.ReadExcludedAsync(reading, [], TestCt);

        Assert.Empty(excluded);
        Assert.Equal(0, reading.Calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task AnUnsuccessfulAnswerExcludesNothing(HttpStatusCode status)
    {
        var handler = BodyRecordingHandler.Answering(status, RecordedRows());
        using var http = new HttpClient(handler);
        var client = (IWhisparrSceneExclusionReading)TestWhisparrClient.Over(http);

        var excluded = await client.ReduceExclusionsAsync(PageOfForty(), TestCt);

        Assert.Empty(excluded);
    }

    [Fact]
    public async Task ABodyThatIsNotTheExpectedShapeExcludesNothing()
    {
        var handler = BodyRecordingHandler.Answering(HttpStatusCode.OK, "<!doctype html><html></html>");
        using var http = new HttpClient(handler);
        var client = (IWhisparrSceneExclusionReading)TestWhisparrClient.Over(http);

        var excluded = await client.ReduceExclusionsAsync(PageOfForty(), TestCt);

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
            IReadOnlyCollection<string> providerSceneIds,
            CancellationToken ct)
        {
            Calls++;
            AskedAbout.Add([.. providerSceneIds]);
            return Task.FromResult<IReadOnlySet<string>>(
                new HashSet<string>(StringComparer.Ordinal));
        }

        public Task<SceneExclusionLookup> FindSceneExclusionAsync(
            string foreignId, CancellationToken ct)
            => throw new NotSupportedException(
                "The port under test reduces a page and never asks for one exclusion row's own "
                    + "identifier.");
    }
}
