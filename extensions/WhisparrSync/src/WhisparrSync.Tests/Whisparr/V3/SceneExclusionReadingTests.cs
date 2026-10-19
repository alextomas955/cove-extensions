using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Whisparr.V3;

public sealed class SceneExclusionReadingTests
{
    private const string FixtureName = "whisparr-v3-3.4.0.1387-exclusions.json";
    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

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
    public async Task TheAnswerIsBoundedByThePageAndNotByTheResponse()
    {
        var rows = RecordedRowsElement();
        var page = ForeignIdsFrom(rows, 40);
        var handler = BodyRecordingHandler.Answering(
            HttpStatusCode.OK, ManyRowsAround(rows, 5_000));
        using var http = new HttpClient(handler);
        var client = (IWhisparrSceneExclusionReading)TestWhisparrClient.Over(http);

        var reading = await client.ReduceExclusionsAsync(page, TestCt);

        Assert.True(reading.ReadCompleted);
        Assert.True(
            reading.Excluded.Count <= page.Count,
            $"{reading.Excluded.Count} identifiers came back for a page of {page.Count}.");
        Assert.Equal([.. page.Order()], [.. reading.Excluded.Order()]);
    }

    [Fact]
    public async Task AnIdentifierTheResponseDoesNotNameIsNotExcluded()
    {
        var rows = RecordedRowsElement();
        var named = ForeignIdsFrom(rows, 3);
        var handler = BodyRecordingHandler.Answering(HttpStatusCode.OK, RecordedRows());
        using var http = new HttpClient(handler);
        var client = (IWhisparrSceneExclusionReading)TestWhisparrClient.Over(http);

        var reading = await client.ReduceExclusionsAsync([.. named, "a-scene-nothing-excluded"], TestCt);

        Assert.True(reading.ReadCompleted);
        Assert.Equal([.. named.Order()], [.. reading.Excluded.Order()]);
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

        var reading = await client.ReduceExclusionsAsync(page, TestCt);

        Assert.NotEmpty(reading.Excluded);
        Assert.All(
            reading.Excluded,
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

    // Not an empty list. A caller told nothing is excluded removes nothing from what it shows and
    // states on every card that the reader excluded none of them, which no answer supported.
    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task AnUnsuccessfulAnswerEstablishesNoExclusion(HttpStatusCode status)
    {
        var handler = BodyRecordingHandler.Answering(status, RecordedRows());
        using var http = new HttpClient(handler);
        var client = (IWhisparrSceneExclusionReading)TestWhisparrClient.Over(http);

        var reading = await client.ReduceExclusionsAsync(PageOfForty(), TestCt);

        Assert.False(reading.ReadCompleted);
        Assert.Empty(reading.Excluded);
    }

    [Fact]
    public async Task ABodyThatIsNotTheExpectedShapeEstablishesNoExclusion()
    {
        var handler = BodyRecordingHandler.Answering(HttpStatusCode.OK, "<!doctype html><html></html>");
        using var http = new HttpClient(handler);
        var client = (IWhisparrSceneExclusionReading)TestWhisparrClient.Over(http);

        var reading = await client.ReduceExclusionsAsync(PageOfForty(), TestCt);

        Assert.False(reading.ReadCompleted);
        Assert.Empty(reading.Excluded);
    }

    // The rows before the stream stopped are accurate and the rest were never seen. Answered as a
    // reading that did not complete, so a caller cannot take the identifiers it never reached as
    // ones nobody excluded.
    [Fact]
    public async Task AnAnswerCutShortEstablishesNoExclusion()
    {
        var rows = RecordedRowsElement();
        var page = ForeignIdsFrom(rows, 40);
        var whole = ManyRowsAround(rows, 5_000);
        var handler = BodyRecordingHandler.Answering(
            HttpStatusCode.OK, whole[..(whole.Length / 2)]);
        using var http = new HttpClient(handler);
        var client = (IWhisparrSceneExclusionReading)TestWhisparrClient.Over(http);

        var reading = await client.ReduceExclusionsAsync(page, TestCt);

        Assert.False(reading.ReadCompleted);
    }

    private static string[] PageOfForty()
        => [.. Enumerable.Range(0, 40).Select(index => $"scene-{index}")];

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

}
