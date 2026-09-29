using System.Text.Json;
using WhisparrSync.Contracts;
using WhisparrSync.Missing;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Missing;

public sealed class SceneStatusPortTests
{
    private const string FixtureName = "whisparr-v3-3.4.0.1387-movie-by-stashid.json";

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    [Fact]
    public void TheFixtureStatesItsOwnProvenance()
    {
        var fixture = JsonDocument.Parse(ProbeFixtures.Read(FixtureName)).RootElement;

        Assert.Equal("2026-09-06", fixture.GetProperty("recordedOn").GetString());
        Assert.Equal("Whisparr 3.4.0.1387", fixture.GetProperty("recordedAgainst").GetString());
    }

    // stashId is the one key that narrows. The instance accepts stashIds and foreignId and ignores
    // both, answering with the whole catalogue.
    [Fact]
    public async Task TheComposedQueryCarriesExactlyOneNarrowingKey()
    {
        var handler = BodyRecordingHandler.Answering(
            System.Net.HttpStatusCode.OK, RecordedRow());
        using var http = new HttpClient(handler);
        var client = (IWhisparrSceneStatusReading)TestWhisparrClient.Over(http, handler);

        await client.ReadSceneByRemoteIdAsync("a-scene", TestCt);

        var target = handler.Targets[0];
        Assert.Equal(1, CountOf(target, "stashId="));
        Assert.DoesNotContain("stashIds=", target, StringComparison.Ordinal);
        Assert.DoesNotContain("foreignId=", target, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, MissingSceneState.Monitored)]
    [InlineData(false, MissingSceneState.Unmonitored)]
    public void AHeldRowReadsAsMonitoredOrUnmonitoredFromItsOwnField(
        bool monitored, MissingSceneState expected)
    {
        var row = JsonDocument.Parse(RecordedRow()).RootElement[0];

        var read = SceneStatusPort.ReadRow(
            new WhisparrResponse(200, "application/json", RowWithMonitored(row, monitored)));

        Assert.Equal(expected, read.State);
    }

    // The instance answers a scene it does not hold with a success and an empty list, not a
    // not-found, so the absence is read out of the body.
    [Fact]
    public void AnEmptyListIsNotAddedRatherThanUnknown()
    {
        var absent = JsonDocument.Parse(ProbeFixtures.Read(FixtureName))
            .RootElement.GetProperty("absentResponse")
            .GetRawText();

        var read = SceneStatusPort.ReadRow(new WhisparrResponse(200, "application/json", absent));

        Assert.Equal(MissingSceneState.NotAdded, read.State);
    }

    private static string RowWithMonitored(JsonElement row, bool monitored)
    {
        var rewritten = new System.Text.Json.Nodes.JsonObject();
        foreach (var member in row.EnumerateObject())
        {
            rewritten[member.Name] = member.Name == "monitored"
                ? monitored
                : System.Text.Json.Nodes.JsonNode.Parse(member.Value.GetRawText());
        }

        return new System.Text.Json.Nodes.JsonArray(rewritten).ToJsonString();
    }

    private static string RecordedRow()
        => JsonDocument.Parse(ProbeFixtures.Read(FixtureName))
            .RootElement.GetProperty("response")
            .GetRawText();

    private static int CountOf(string text, string term)
    {
        var found = 0;
        var at = text.IndexOf(term, StringComparison.Ordinal);
        while (at >= 0)
        {
            found++;
            at = text.IndexOf(term, at + term.Length, StringComparison.Ordinal);
        }

        return found;
    }
}
