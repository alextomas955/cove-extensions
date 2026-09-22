using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using WhisparrSync.Contracts;
using WhisparrSync.Monitoring;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Whisparr;

// Asserted on parsed bodies and served answers rather than on source text. A text assertion
// passes on a body carrying a member the instance discards, and this generation discards the other
// one's spellings without saying so.
// The identifiers are the ones measured against whisparr:v2-2.2.0-release.231: the site's own
// number is 5999 and a scene there is named by the number the metadata provider issued.
public sealed class V2SceneRowTests
{
    private const int SiteId = 5999;

    private const int FirstSceneNumber = 1363738;

    private const int SecondSceneNumber = 1363739;

    private const int UnlistedSceneNumber = 11025224;

    private const int FirstRowId = 812;

    private const int SecondRowId = 813;

    private const int SecondSiteNumber = 5998;

    private const int UnheldSiteNumber = 4242;

    // A stored studio identifier of the shape the metadata source mints.
    private const string StoredSiteId = "3c0a6b21-9f7d-4c58-a3e2-71b0d4f5e8a9";

    private static readonly AddDefaults Defaults = new(1, "/config/library");

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    // The instance's own search proxy answers a server failure after a hundred seconds for studios
    // the metadata source knows, so a site path that took it could never register them.
    [Fact]
    public async Task RegisteringASiteSendsOneCreateCarryingTheNumberAndNothingElse()
    {
        var handler = BodyRecordingHandler.Answering(HttpStatusCode.Created, """{"id":1}""");
        var client = SiteClient(handler, TestSiteNumbers.Numbering(StoredSiteId, SiteId));

        var answered = await ((IWhisparrSiteRegistrationActing)client).RegisterSiteAsync(StoredSiteId, Defaults, TestCt);

        Assert.Equal(MonitorRefusalKind.None, MonitoringProjector.Accepted(answered));
        var sent = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal("/api/v3/series", sent.Path);
        Assert.Equal(SiteId, Sent(sent.Body)["tvdbId"]!.GetValue<int>());
    }

    [Fact]
    public async Task TheHeldReadAsksTheLookupByTheStoredIdentifierAndCarriesTheRowItRankedFirst()
    {
        const string answered = """
            [{"id":11,"tvdbId":5999,"title":"Jay Bank Presents","monitored":true},
             {"id":12,"tvdbId":247,"title":"Tushy Raw","monitored":false}]
            """;

        var handler = BodyRecordingHandler.Answering(HttpStatusCode.OK, answered);
        var client = SiteClient(handler, new TestSiteNumbers());

        var read = await ((IWhisparrStudioActing)client).ReadStudioAsync(StoredSiteId, TestCt);

        // The recorded query, never the recorded path: AbsolutePath alone cannot tell a read of one
        // site from one that asked the instance for its whole catalogue.
        var target = Assert.Single(handler.Targets);
        Assert.StartsWith("/api/v3/series/lookup", target, StringComparison.Ordinal);
        Assert.Contains(StoredSiteId, target, StringComparison.Ordinal);

        Assert.Equal(
            MonitoringProjector.EntityReading.Held, MonitoringProjector.Classify(read).Reading);
        Assert.Equal(11, MonitoringProjector.EntityIdIn(read.Body));
        Assert.DoesNotContain("Tushy Raw", read.Body, StringComparison.Ordinal);
    }

    // A row carrying no id of the instance's own was mapped from the metadata source, which is the
    // instance answering that it holds no site under that identifier.
    [Fact]
    public async Task ASiteTheInstanceHoldsNoRowForReadsAsNotHeld()
    {
        var handler = BodyRecordingHandler.Answering(
            HttpStatusCode.OK, """[{"tvdbId":5999,"title":"Jay Bank Presents"}]""");
        var client = SiteClient(handler, new TestSiteNumbers());

        var read = await ((IWhisparrStudioActing)client).ReadStudioAsync(StoredSiteId, TestCt);

        Assert.Equal(
            MonitoringProjector.EntityReading.NotHeld, MonitoringProjector.Classify(read).Reading);
        Assert.Single(handler.Requests);
    }

    // Held apart from a row the instance holds nothing under: an answer naming no site at all says
    // nothing in this generation's namespace answers to the identifier the library holds.
    [Fact]
    public async Task ALookupNamingNoSiteAtAllIsTheNoIdentityRefusal()
    {
        var handler = BodyRecordingHandler.Answering(HttpStatusCode.OK, "[]");
        var client = SiteClient(handler, new TestSiteNumbers());

        var read = await ((IWhisparrStudioActing)client).ReadStudioAsync(StoredSiteId, TestCt);

        Assert.Equal(
            MonitorRefusalKind.NoIdentityInThisNamespace,
            MonitoringProjector.Classify(read).Refusal);
    }

    [Fact]
    public async Task MonitoringAStudioSendsOneCreateCarryingTheNumberAndTheScope()
    {
        var handler = BodyRecordingHandler.Answering(HttpStatusCode.Created, """{"id":1}""");
        var client = SiteClient(handler, TestSiteNumbers.Numbering(StoredSiteId, SiteId));

        await ((IWhisparrStudioActing)client).AddMonitoredStudioAsync(StoredSiteId,
            MonitorScope.FutureScenes,
            Defaults,
            TestCt);

        var sent = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, sent.Method);
        var body = Sent(sent.Body);
        Assert.Equal(SiteId, body["tvdbId"]!.GetValue<int>());
        Assert.Equal(
            "future", Assert.IsType<JsonObject>(body["addOptions"])["monitor"]!.GetValue<string>());
        Assert.DoesNotContain(StoredSiteId, sent.Body, StringComparison.OrdinalIgnoreCase);
    }

    // The identity the library holds is the thing to fix. Reporting the instance as the party that
    // refused sends a reader to audit one that was never asked.
    [Fact]
    public async Task ASiteTheSourceNamesNoneForRefusesWithNoIdentityAndSendsNothing()
    {
        var handler = BodyRecordingHandler.Answering(HttpStatusCode.Created, """{"id":1}""");
        var client = SiteClient(
            handler, new TestSiteNumbers().Answering(StoredSiteId, WhisparrSiteNumber.NamesNone));

        var answered = await ((IWhisparrSiteRegistrationActing)client).RegisterSiteAsync(StoredSiteId, Defaults, TestCt);

        Assert.Equal(
            MonitorRefusalKind.NoIdentityInThisNamespace, MonitoringProjector.Accepted(answered));
        Assert.Empty(answered.Body);
        Assert.Empty(handler.Requests);
    }

    // A read that was not reached establishes nothing about the site, so reporting it as one the
    // source names none for would send a reader to fix an identity that may be correct.
    [Fact]
    public async Task ASourceThatWasNotReachedRefusesWithADifferentReasonAndSendsNothing()
    {
        var handler = BodyRecordingHandler.Answering(HttpStatusCode.Created, """{"id":1}""");
        var client = SiteClient(
            handler, new TestSiteNumbers().Answering(StoredSiteId, WhisparrSiteNumber.NotReached));

        var answered = await ((IWhisparrSiteRegistrationActing)client).RegisterSiteAsync(StoredSiteId, Defaults, TestCt);

        Assert.NotEqual(
            MonitorRefusalKind.NoIdentityInThisNamespace, MonitoringProjector.Accepted(answered));
        Assert.NotEqual(MonitorRefusalKind.None, MonitoringProjector.Accepted(answered));
        Assert.Empty(answered.Body);
        Assert.Empty(handler.Requests);
    }

    // The port is given no answer for this identifier, so only its own short-circuit can produce a
    // number. A metadata read would answer that the source names no site.
    [Fact]
    public async Task ANumericStoredIdentifierReachesTheInstanceWithNoMetadataRead()
    {
        var handler = BodyRecordingHandler.Answering(HttpStatusCode.Created, """{"id":1}""");
        var client = SiteClient(handler, new TestSiteNumbers());

        await ((IWhisparrSiteRegistrationActing)client).RegisterSiteAsync(SiteId.ToString(CultureInfo.InvariantCulture), Defaults, TestCt);

        Assert.Equal(SiteId, Sent(Assert.Single(handler.Requests).Body)["tvdbId"]!.GetValue<int>());
    }

    // The instance refuses an add carrying no title and discards the value of the one it is given,
    // resolving the site's real title and its slug from the number alone.
    [Fact]
    public async Task NeitherAddBodyCarriesASlugAndEachTitleIsNonEmpty()
    {
        var handler = BodyRecordingHandler.Answering(HttpStatusCode.Created, """{"id":1}""");
        var client = SiteClient(handler, TestSiteNumbers.Numbering(StoredSiteId, SiteId));

        await ((IWhisparrSiteRegistrationActing)client).RegisterSiteAsync(StoredSiteId, Defaults, TestCt);
        await ((IWhisparrStudioActing)client).AddMonitoredStudioAsync(StoredSiteId,
            MonitorScope.AllScenes,
            Defaults,
            TestCt);

        Assert.Equal(2, handler.Requests.Count);
        Assert.All(
            handler.Requests.Select(sent => Sent(sent.Body)),
            body =>
            {
                Assert.Null(body["titleSlug"]);
                Assert.False(string.IsNullOrWhiteSpace(body["title"]!.GetValue<string>()));
            });
    }

    // One request whatever the batch holds. The instance narrows its own list by one number at a
    // time, so a narrowed read would cost one round trip per studio in the library.
    [Fact]
    public async Task TheHeldSiteReadAnswersOnlyTheNumbersItWasAskedAbout()
    {
        var handler = BodyRecordingHandler.Answering(HttpStatusCode.OK, ASiteList());
        var client = SiteClient(handler, new TestSiteNumbers());

        var held = await ((IWhisparrHeldSiteReading)client).ReduceHeldSitesAsync([SiteId, SecondSiteNumber], TestCt);

        Assert.Equal([SecondSiteNumber, SiteId], held.Order());
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ANumberTheInstanceHoldsNoSiteRowForIsAbsentFromTheAnswer()
    {
        var handler = BodyRecordingHandler.Answering(HttpStatusCode.OK, ASiteList());
        var client = SiteClient(handler, new TestSiteNumbers());

        var held = await ((IWhisparrHeldSiteReading)client).ReduceHeldSitesAsync([SiteId, UnheldSiteNumber], TestCt);

        Assert.Equal([SiteId], held);
    }

    [Fact]
    public async Task AnEmptySiteInputSendsNoRequestAtAll()
    {
        var handler = BodyRecordingHandler.Answering(HttpStatusCode.OK, ASiteList());
        var client = SiteClient(handler, new TestSiteNumbers());

        var held = await ((IWhisparrHeldSiteReading)client).ReduceHeldSitesAsync([], TestCt);

        Assert.Empty(held);
        Assert.Empty(handler.Requests);
    }

    // An empty set would report every site it asked about as one the instance holds none of, which
    // is the opposite of the truth and would register the whole library a second time.
    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, "")]
    [InlineData(HttpStatusCode.OK, "{}")]
    public async Task AnAnswerTheHeldSiteReadCouldNotReadRaises(
        HttpStatusCode status, string answered)
    {
        var client = SiteClient(
            BodyRecordingHandler.Answering(status, answered), new TestSiteNumbers());

        await Assert.ThrowsAsync<HttpRequestException>(
            () => ((IWhisparrHeldSiteReading)client).ReduceHeldSitesAsync([SiteId], TestCt));
    }

    // Holds more rows than any case asks about, so a read answering the whole list rather than the
    // asked-about subset fails the count assertions.
    private static string ASiteList()
    {
        var rows = new JsonArray
        {
            Row(11, SiteId),
            Row(12, SecondSiteNumber),
            Row(13, 3372),
            Row(14, 247),
        };

        return rows.ToJsonString();
    }

    private static IWhisparrClient SiteClient(
        BodyRecordingHandler handler, ISiteNumberPort siteNumbers)
        => TestWhisparrClient.Over(
            handler, siteNumbers: siteNumbers, generation: WhisparrGeneration.V2);

    private static JsonObject Sent(string body)
        => Assert.IsType<JsonObject>(JsonNode.Parse(body));

    // The generated resource declares exactly these two members, so the whole member set is
    // asserted rather than the two members alone.
    [Fact]
    public void TheComposedMonitorBodyCarriesTheRowIdAndTheFlagAndNothingElse()
    {
        var body = ComposedV2Body.Of(V2BodyProjector.MonitorScene(FirstRowId, monitored: true));

        Assert.Equal(["episodeIds", "monitored"], body.Select(member => member.Key).Order(StringComparer.Ordinal));
        Assert.Equal([FirstRowId], body["episodeIds"]!.AsArray().Select(id => id!.GetValue<int>()));
        Assert.True(body["monitored"]!.GetValue<bool>());
    }

    [Fact]
    public void TheFlagIsCarriedWhenItIsFalseToo()
    {
        var body = ComposedV2Body.Of(V2BodyProjector.MonitorScene(FirstRowId, monitored: false));

        Assert.NotNull(body["monitored"]);
        Assert.False(body["monitored"]!.GetValue<bool>());
    }

    [Fact]
    public async Task TheRowReadAnswersOnlyTheNumbersItWasAskedAbout()
    {
        var handler = BodyRecordingHandler.Answering(HttpStatusCode.OK, ASiteListing());
        var client = TestWhisparrClient.Over(handler, generation: WhisparrGeneration.V2);

        var rows = await ((IWhisparrSiteSceneReading)client)
            .ReduceSiteSceneRowsAsync(SiteId, [FirstSceneNumber, SecondSceneNumber], TestCt);

        Assert.Equal(2, rows.Count);
        Assert.Equal(FirstRowId, rows[FirstSceneNumber]);
        Assert.Equal(SecondRowId, rows[SecondSceneNumber]);
        Assert.Single(handler.Requests);
    }

    // A zero would be a row id a caller would then set the flag on, which is a row nobody named.
    [Fact]
    public async Task ANumberTheListDoesNotCarryIsAbsentRatherThanZero()
    {
        var client = TestWhisparrClient.Over(
            BodyRecordingHandler.Answering(HttpStatusCode.OK, ASiteListing()),
            generation: WhisparrGeneration.V2);

        var rows = await ((IWhisparrSiteSceneReading)client)
            .ReduceSiteSceneRowsAsync(SiteId, [FirstSceneNumber, UnlistedSceneNumber], TestCt);

        Assert.Single(rows);
        Assert.DoesNotContain(UnlistedSceneNumber, rows.Keys);
    }

    // An empty map would report every scene it asked about as one the instance holds no row for,
    // which is the opposite of the truth.
    [Theory]
    [InlineData(HttpStatusCode.NotFound, "")]
    [InlineData(HttpStatusCode.OK, "{}")]
    public async Task AnAnswerTheReadCouldNotReadRaises(HttpStatusCode status, string answered)
    {
        var client = TestWhisparrClient.Over(
            BodyRecordingHandler.Answering(status, answered), generation: WhisparrGeneration.V2);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => ((IWhisparrSiteSceneReading)client)
                .ReduceSiteSceneRowsAsync(SiteId, [FirstSceneNumber], TestCt));
    }

    [Fact]
    public async Task AnEmptyInputSendsNoRequestAtAll()
    {
        var handler = BodyRecordingHandler.Answering(HttpStatusCode.OK, ASiteListing());
        var client = TestWhisparrClient.Over(handler, generation: WhisparrGeneration.V2);

        var rows = await ((IWhisparrSiteSceneReading)client)
            .ReduceSiteSceneRowsAsync(SiteId, [], TestCt);

        Assert.Empty(rows);
        Assert.Empty(handler.Requests);
    }

    // The monitor verb needs the per-scene status read as well, and this generation registers none,
    // so the surface answers the absent capability before any request.
    [Fact]
    public async Task TheSceneSurfaceStillRefusesTheMonitorVerbOnThisGeneration()
    {
        await using var host = await MonitorHost.CreateAsync(generation: WhisparrGeneration.V2);
        var studioId = await host.SeedStudioAsync(
            MonitorHost.StoredEndpoint, MonitorHost.StudioRemoteIdValue);
        var coveId = await host.SeedStudioSceneAsync(
            studioId, "theporndb.net/graphql", "3c0a6b21-9f7d-4c58-a3e2-71b0d4f5e8a9");

        var result = await host.SceneActionAsync(coveId, "monitor");

        Assert.Equal(SceneRefusalKind.CapabilityAbsentOnThisGeneration, result.Refusal);
        Assert.Empty(host.Client.Acting);
        Assert.Empty(host.Client.SceneStatuses);
    }

    // Holds more rows than any case asks about, so a read answering the whole list rather than the
    // asked-about subset fails the count assertions.
    private static string ASiteListing()
    {
        var rows = new JsonArray
        {
            Row(FirstRowId, FirstSceneNumber),
            Row(SecondRowId, SecondSceneNumber),
            Row(814, 1363740),
            Row(815, 1363741),
            Row(816, 1363742),
        };

        return rows.ToJsonString();
    }

    private static JsonObject Row(int rowId, int sceneNumber)
        => new()
        {
            ["id"] = rowId,
            ["seriesId"] = 1,
            ["tvdbId"] = sceneNumber,
            ["monitored"] = false,
        };
}
