using System.Text.Json;
using WhisparrSync.Contracts;
using WhisparrSync.Missing;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Missing;

/// <summary>
/// What each card on a page reads as, and how many requests a page of them costs.
/// </summary>
/// <remarks>
/// The cost claim is the point. Every value here is also derivable from a shape that asks the
/// instance for its whole catalogue, so the request count is asserted rather than the answers alone.
/// <para>
/// The recorded row is a real answer from the instance the fixture names, so a field read under a
/// name that instance does not use fails here.
/// </para>
/// </remarks>
public sealed class SceneStatusPortTests
{
    private const string FixtureName = "whisparr-v3-3.4.0.1387-movie-by-stashid.json";
    private const string SomeKey = "0e2e0e2e0e2e0e2e0e2e0e2e0e2e0e2e";
    private const string EntityForeignId = "5ee16943-0da6-4ee4-94c1-54172e3d0b7e";

    private static Uri SomeInstance => new("http://whisparr.invalid:6969");

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    /// <summary>The recording states the build and the day it came from.</summary>
    [Fact]
    public void TheFixtureStatesItsOwnProvenance()
    {
        var fixture = JsonDocument.Parse(ProbeFixtures.Read(FixtureName)).RootElement;

        Assert.Equal("2026-09-06", fixture.GetProperty("recordedOn").GetString());
        Assert.Equal("Whisparr 3.4.0.1387", fixture.GetProperty("recordedAgainst").GetString());
    }

    /// <summary>
    /// An instance holding no entry for the entity holds none for a scene under it, so one request
    /// settles the page.
    /// </summary>
    [Fact]
    public async Task AnAbsentEntityIsFortyNotAddedCardsAndZeroPerCardRequests()
    {
        var reading = new RecordingSceneStatusReading(presence: 404);
        var ids = PageOfForty();

        var states = await new SceneStatusPort().ReadStatesAsync(
            reading,
            SomeInstance, SomeKey, WhisparrEntityKind.Studio, EntityForeignId, ids, TestCt);

        Assert.Equal(40, states.Count);
        Assert.All(states.Values, state => Assert.Equal(MissingSceneState.NotAdded, state));
        Assert.Equal(1, reading.PresenceCalls);
        Assert.Equal(0, reading.SceneCalls);
    }

    /// <summary>A page costs one request per card and no more.</summary>
    [Fact]
    public async Task APresentEntityCostsAtMostOneRequestPerCard()
    {
        var reading = new RecordingSceneStatusReading(presence: 200, sceneAnswer: RecordedRow());
        var ids = PageOfForty();

        await new SceneStatusPort().ReadStatesAsync(
            reading,
            SomeInstance, SomeKey, WhisparrEntityKind.Studio, EntityForeignId, ids, TestCt);

        Assert.Equal(1, reading.PresenceCalls);
        Assert.Equal(40, reading.SceneCalls);
    }

    /// <summary>
    /// The identifier travels on the one key that narrows. Two other spellings this instance accepts
    /// are ignored and answer with the whole catalogue.
    /// </summary>
    [Fact]
    public async Task TheComposedQueryCarriesExactlyOneNarrowingKey()
    {
        var handler = BodyRecordingHandler.Answering(
            System.Net.HttpStatusCode.OK, RecordedRow());
        using var http = new HttpClient(handler);
        var client = TestWhisparrClient.Over(http, handler);

        await client.ReadSceneByRemoteIdAsync(SomeInstance, SomeKey, "a-scene", TestCt);

        var target = handler.Targets[0];
        Assert.Equal(1, CountOf(target, "stashId="));
        Assert.DoesNotContain("stashIds=", target, StringComparison.Ordinal);
        Assert.DoesNotContain("foreignId=", target, StringComparison.Ordinal);
    }

    /// <summary>Each of the four values comes from a real recorded row shape.</summary>
    [Theory]
    [InlineData(true, MissingSceneState.Monitored)]
    [InlineData(false, MissingSceneState.Unmonitored)]
    public async Task AHeldRowReadsAsMonitoredOrUnmonitoredFromItsOwnField(
        bool monitored, MissingSceneState expected)
    {
        var row = JsonDocument.Parse(RecordedRow()).RootElement[0];
        var rewritten = RowWithMonitored(row, monitored);
        var reading = new RecordingSceneStatusReading(presence: 200, sceneAnswer: rewritten);

        var states = await new SceneStatusPort().ReadStatesAsync(
            reading,
            SomeInstance, SomeKey, WhisparrEntityKind.Studio, EntityForeignId, ["a-scene"], TestCt);

        Assert.Equal(expected, states["a-scene"]);
    }

    /// <summary>
    /// The instance answers a scene it does not hold with a success and an empty list rather than a
    /// not-found, so the absence is read out of the body.
    /// </summary>
    [Fact]
    public async Task AnEmptyListForAHeldEntityIsNotAddedRatherThanUnknown()
    {
        var absent = JsonDocument.Parse(ProbeFixtures.Read(FixtureName))
            .RootElement.GetProperty("absentResponse")
            .GetRawText();
        var reading = new RecordingSceneStatusReading(presence: 200, sceneAnswer: absent);

        var states = await new SceneStatusPort().ReadStatesAsync(
            reading,
            SomeInstance, SomeKey, WhisparrEntityKind.Studio, EntityForeignId, ["a-scene"], TestCt);

        Assert.Equal(MissingSceneState.NotAdded, states["a-scene"]);
    }

    /// <summary>
    /// A probe that answered nothing claims nothing about the instance, and no per-card request is
    /// spent finding out again.
    /// </summary>
    [Fact]
    public async Task AnUnreadProbeIsUnknownForTheWholePageAndCostsNoPerCardRequest()
    {
        var reading = new RecordingSceneStatusReading(presence: 500);

        var states = await new SceneStatusPort().ReadStatesAsync(
            reading,
            SomeInstance, SomeKey, WhisparrEntityKind.Studio, EntityForeignId, PageOfForty(), TestCt);

        Assert.All(states.Values, state => Assert.Equal(MissingSceneState.StatusUnknown, state));
        Assert.Equal(0, reading.SceneCalls);
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

    private static string[] PageOfForty()
        => [.. Enumerable.Range(0, 40).Select(index => $"scene-{index}")];

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

    private sealed class RecordingSceneStatusReading(int presence, string sceneAnswer = "[]")
        : IWhisparrSceneStatusReading
    {
        public int PresenceCalls { get; private set; }

        public int SceneCalls { get; private set; }

        public Task<WhisparrResponse> ReadEntityPresenceAsync(
            Uri baseAddress,
            string apiKey,
            WhisparrEntityKind kind,
            string foreignId,
            CancellationToken ct)
        {
            PresenceCalls++;
            return Task.FromResult(new WhisparrResponse(presence, "application/json", "{}"));
        }

        public Task<WhisparrResponse> ReadSceneByRemoteIdAsync(
            Uri baseAddress, string apiKey, string remoteId, CancellationToken ct)
        {
            SceneCalls++;
            return Task.FromResult(new WhisparrResponse(200, "application/json", sceneAnswer));
        }

        public Task<IReadOnlySet<string>> ReduceHeldScenesAsync(
            Uri baseAddress,
            string apiKey,
            IReadOnlyCollection<string> foreignIds,
            CancellationToken ct)
            => throw new InvalidOperationException(
                "This surface asks about one scene at a time and never about a batch of them.");
    }
}
