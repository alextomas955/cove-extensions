using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using WhisparrSync.Contracts;
using WhisparrSync.Monitoring;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Whisparr;

/// <summary>
/// Whisparr v2's site and per-scene seams: the three site paths and what each sends, the row read,
/// the composed monitor body, and the per-scene surface that is still refused there.
/// </summary>
/// <remarks>
/// Asserted on parsed bodies and served answers rather than on source text. A text assertion passes
/// on a body carrying a member the instance discards, and this generation discards the other one's
/// spellings without saying so.
/// <para>
/// The identifiers are the ones the measurement recorded against
/// <c>whisparr:v2-2.2.0-release.231</c>: the site's own number is 5999 and a scene there is named by
/// the number the metadata provider issued.
/// </para>
/// </remarks>
public sealed class V2SceneRowTests
{
    private const int SiteId = 5999;

    private const int FirstSceneNumber = 1363738;

    private const int SecondSceneNumber = 1363739;

    private const int UnlistedSceneNumber = 11025224;

    private const int FirstRowId = 812;

    private const int SecondRowId = 813;

    private static readonly Uri Address = new("http://whisparr:6969");

    private const string ApiKey = "0e2e0e2e0e2e0e2e0e2e0e2e0e2e0e2e";

    /// <summary>A stored studio identifier of the shape the metadata source mints.</summary>
    private const string StoredSiteId = "3c0a6b21-9f7d-4c58-a3e2-71b0d4f5e8a9";

    private static readonly AddDefaults Defaults = new(1, "/config/library");

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    /// <summary>Registering a site is one create carrying the number, and no search.</summary>
    /// <remarks>
    /// The instance's own search proxy answers a server failure after a hundred seconds for studios
    /// the metadata source knows, so a site path that took it could never register them.
    /// </remarks>
    [Fact]
    public async Task RegisteringASiteSendsOneCreateCarryingTheNumberAndNothingElse()
    {
        var handler = BodyRecordingHandler.Answering(HttpStatusCode.Created, """{"id":1}""");
        var client = SiteClient(handler, TestSiteNumbers.Numbering(StoredSiteId, SiteId));

        var answered = await ((IWhisparrSiteRegistrationActing)client).RegisterSiteAsync(
            Address, ApiKey, StoredSiteId, Defaults, TestCt);

        Assert.Equal(MonitorRefusalKind.None, MonitoringProjector.Accepted(answered));
        var sent = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal("/api/v3/series", sent.Path);
        Assert.Equal(SiteId, Sent(sent.Body)["tvdbId"]!.GetValue<int>());
    }

    /// <summary>The held read is one list narrowed to the number, and carries the matched row.</summary>
    [Fact]
    public async Task TheHeldReadNarrowsToTheNumberAndCarriesTheMatchedRow()
    {
        const string listed = """
            [{"id":11,"tvdbId":5999,"title":"Jay Bank Presents","monitored":true},
             {"id":12,"tvdbId":247,"title":"Tushy Raw","monitored":false}]
            """;

        var handler = BodyRecordingHandler.Answering(HttpStatusCode.OK, listed);
        var client = SiteClient(handler, TestSiteNumbers.Numbering(StoredSiteId, SiteId));

        var read = await ((IWhisparrStudioActing)client).ReadStudioAsync(
            Address, ApiKey, WhisparrGeneration.V2, StoredSiteId, TestCt);

        // The recorded query, never the recorded path: AbsolutePath alone cannot tell a read narrowed
        // to one site from one that asked the instance for its whole catalogue.
        Assert.Equal("/api/v3/series?tvdbId=5999", Assert.Single(handler.Targets));
        Assert.Equal(
            MonitoringProjector.EntityReading.Held, MonitoringProjector.Classify(read).Reading);
        Assert.Equal(11, MonitoringProjector.EntityIdIn(read.Body));
        Assert.DoesNotContain("Tushy Raw", read.Body, StringComparison.Ordinal);
    }

    /// <summary>A site the list holds no row for reads as not held, which is not a refusal.</summary>
    [Fact]
    public async Task ASiteTheListHoldsNoRowForReadsAsNotHeld()
    {
        var handler = BodyRecordingHandler.Answering(HttpStatusCode.OK, "[]");
        var client = SiteClient(handler, TestSiteNumbers.Numbering(StoredSiteId, SiteId));

        var read = await ((IWhisparrStudioActing)client).ReadStudioAsync(
            Address, ApiKey, WhisparrGeneration.V2, StoredSiteId, TestCt);

        Assert.Equal(
            MonitoringProjector.EntityReading.NotHeld, MonitoringProjector.Classify(read).Reading);
        Assert.Single(handler.Requests);
    }

    /// <summary>Monitoring a studio is one create carrying the number and the scope.</summary>
    [Fact]
    public async Task MonitoringAStudioSendsOneCreateCarryingTheNumberAndTheScope()
    {
        var handler = BodyRecordingHandler.Answering(HttpStatusCode.Created, """{"id":1}""");
        var client = SiteClient(handler, TestSiteNumbers.Numbering(StoredSiteId, SiteId));

        await ((IWhisparrStudioActing)client).AddMonitoredStudioAsync(
            Address,
            ApiKey,
            WhisparrGeneration.V2,
            StoredSiteId,
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

    /// <summary>A site the source names none for is the no-identity refusal, and sends nothing.</summary>
    /// <remarks>
    /// The reader can act on that: the identity the library holds is the thing to fix. Reporting the
    /// instance as the party that refused sends them to audit one that was never asked.
    /// </remarks>
    [Fact]
    public async Task ASiteTheSourceNamesNoneForRefusesWithNoIdentityAndSendsNothing()
    {
        var handler = BodyRecordingHandler.Answering(HttpStatusCode.Created, """{"id":1}""");
        var client = SiteClient(
            handler, new TestSiteNumbers().Answering(StoredSiteId, WhisparrSiteNumber.NamesNone));

        var answered = await ((IWhisparrSiteRegistrationActing)client).RegisterSiteAsync(
            Address, ApiKey, StoredSiteId, Defaults, TestCt);

        Assert.Equal(
            MonitorRefusalKind.NoIdentityInThisNamespace, MonitoringProjector.Accepted(answered));
        Assert.Empty(answered.Body);
        Assert.Empty(handler.Requests);
    }

    /// <summary>A source that was not reached is a different refusal, and sends nothing.</summary>
    /// <remarks>
    /// It establishes nothing about the site, so reporting it as one the source names none for would
    /// send a reader to fix an identity that may be correct.
    /// </remarks>
    [Fact]
    public async Task ASourceThatWasNotReachedRefusesWithADifferentReasonAndSendsNothing()
    {
        var handler = BodyRecordingHandler.Answering(HttpStatusCode.Created, """{"id":1}""");
        var client = SiteClient(
            handler, new TestSiteNumbers().Answering(StoredSiteId, WhisparrSiteNumber.NotReached));

        var answered = await ((IWhisparrSiteRegistrationActing)client).RegisterSiteAsync(
            Address, ApiKey, StoredSiteId, Defaults, TestCt);

        Assert.NotEqual(
            MonitorRefusalKind.NoIdentityInThisNamespace, MonitoringProjector.Accepted(answered));
        Assert.NotEqual(MonitorRefusalKind.None, MonitoringProjector.Accepted(answered));
        Assert.Empty(answered.Body);
        Assert.Empty(handler.Requests);
    }

    /// <summary>A stored identifier that is already a number reaches the instance unaided.</summary>
    /// <remarks>
    /// The port is given no answer for it, so only its own short-circuit can produce a number and a
    /// metadata read would answer that the source names no site.
    /// </remarks>
    [Fact]
    public async Task ANumericStoredIdentifierReachesTheInstanceWithNoMetadataRead()
    {
        var handler = BodyRecordingHandler.Answering(HttpStatusCode.Created, """{"id":1}""");
        var client = SiteClient(handler, new TestSiteNumbers());

        await ((IWhisparrSiteRegistrationActing)client).RegisterSiteAsync(
            Address, ApiKey, SiteId.ToString(CultureInfo.InvariantCulture), Defaults, TestCt);

        Assert.Equal(SiteId, Sent(Assert.Single(handler.Requests).Body)["tvdbId"]!.GetValue<int>());
    }

    /// <summary>Neither add body names the site, and neither carries a slug.</summary>
    /// <remarks>
    /// The instance refuses an add carrying no title and discards the value of the one it is given,
    /// resolving the site's real title and its slug from the number alone. The shipped document is
    /// what is asserted, because what the instance receives is what this is about.
    /// </remarks>
    [Fact]
    public async Task NeitherAddBodyCarriesASlugAndEachTitleIsNonEmpty()
    {
        var handler = BodyRecordingHandler.Answering(HttpStatusCode.Created, """{"id":1}""");
        var client = SiteClient(handler, TestSiteNumbers.Numbering(StoredSiteId, SiteId));

        await ((IWhisparrSiteRegistrationActing)client).RegisterSiteAsync(
            Address, ApiKey, StoredSiteId, Defaults, TestCt);
        await ((IWhisparrStudioActing)client).AddMonitoredStudioAsync(
            Address,
            ApiKey,
            WhisparrGeneration.V2,
            StoredSiteId,
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

    private static WhisparrClient SiteClient(
        BodyRecordingHandler handler, ISiteNumberPort siteNumbers)
        => TestWhisparrClient.Over(handler, siteNumbers: siteNumbers);

    private static JsonObject Sent(string body)
        => Assert.IsType<JsonObject>(JsonNode.Parse(body));

    /// <summary>The composed body carries the instance's own row id and the flag, and nothing else.</summary>
    /// <remarks>
    /// The generated resource declares exactly these two members, so the whole member set is what is
    /// asserted: a body gaining one, or losing the flag, is what reddens this.
    /// </remarks>
    [Fact]
    public void TheComposedMonitorBodyCarriesTheRowIdAndTheFlagAndNothingElse()
    {
        var body = ComposedV2Body.Of(V2BodyProjector.MonitorScene(FirstRowId, monitored: true));

        Assert.Equal(["episodeIds", "monitored"], body.Select(member => member.Key).Order(StringComparer.Ordinal));
        Assert.Equal([FirstRowId], body["episodeIds"]!.AsArray().Select(id => id!.GetValue<int>()));
        Assert.True(body["monitored"]!.GetValue<bool>());
    }

    /// <summary>The flag travels false as itself rather than as the member's absence.</summary>
    [Fact]
    public void TheFlagIsCarriedWhenItIsFalseToo()
    {
        var body = ComposedV2Body.Of(V2BodyProjector.MonitorScene(FirstRowId, monitored: false));

        Assert.NotNull(body["monitored"]);
        Assert.False(body["monitored"]!.GetValue<bool>());
    }

    /// <summary>
    /// The row read answers only the numbers it was asked about, whatever the site's list holds.
    /// </summary>
    [Fact]
    public async Task TheRowReadAnswersOnlyTheNumbersItWasAskedAbout()
    {
        var handler = BodyRecordingHandler.Answering(HttpStatusCode.OK, ASiteListing());
        var client = TestWhisparrClient.Over(handler);

        var rows = await client.ReduceSiteSceneRowsAsync(
            Address, ApiKey, SiteId, [FirstSceneNumber, SecondSceneNumber], TestCt);

        Assert.Equal(2, rows.Count);
        Assert.Equal(FirstRowId, rows[FirstSceneNumber]);
        Assert.Equal(SecondRowId, rows[SecondSceneNumber]);
        Assert.Single(handler.Requests);
    }

    /// <summary>
    /// A number the site's list does not carry is absent from the answer rather than present with a
    /// zero.
    /// </summary>
    /// <remarks>
    /// A zero would be a row id a caller would then set the flag on, which is a row nobody named.
    /// </remarks>
    [Fact]
    public async Task ANumberTheListDoesNotCarryIsAbsentRatherThanZero()
    {
        var client = TestWhisparrClient.Over(
            BodyRecordingHandler.Answering(HttpStatusCode.OK, ASiteListing()));

        var rows = await client.ReduceSiteSceneRowsAsync(
            Address, ApiKey, SiteId, [FirstSceneNumber, UnlistedSceneNumber], TestCt);

        Assert.Single(rows);
        Assert.DoesNotContain(UnlistedSceneNumber, rows.Keys);
    }

    /// <summary>
    /// An answer the read could not read raises, and answers no empty map.
    /// </summary>
    /// <remarks>
    /// An empty map would report every scene it asked about as one the instance holds no row for,
    /// which is the opposite of the truth.
    /// </remarks>
    [Theory]
    [InlineData(HttpStatusCode.NotFound, "")]
    [InlineData(HttpStatusCode.OK, "{}")]
    public async Task AnAnswerTheReadCouldNotReadRaises(HttpStatusCode status, string answered)
    {
        var client = TestWhisparrClient.Over(BodyRecordingHandler.Answering(status, answered));

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.ReduceSiteSceneRowsAsync(
                Address, ApiKey, SiteId, [FirstSceneNumber], TestCt));
    }

    /// <summary>An empty input asks nothing.</summary>
    [Fact]
    public async Task AnEmptyInputSendsNoRequestAtAll()
    {
        var handler = BodyRecordingHandler.Answering(HttpStatusCode.OK, ASiteListing());
        var client = TestWhisparrClient.Over(handler);

        var rows = await client.ReduceSiteSceneRowsAsync(Address, ApiKey, SiteId, [], TestCt);

        Assert.Empty(rows);
        Assert.Empty(handler.Requests);
    }

    /// <summary>
    /// The per-scene surface still refuses on this generation, and sends nothing.
    /// </summary>
    /// <remarks>
    /// The widening gave this generation a per-scene monitor role. It did not thereby give a reader
    /// a control that cannot work: the verb needs the per-scene status read as well, and this
    /// generation registers none, so the surface answers the absent capability before any request.
    /// </remarks>
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

    /// <summary>
    /// One site's own row list, holding more rows than any case here asks about.
    /// </summary>
    /// <remarks>
    /// Larger than the questions asked of it, so a read that answered the whole list rather than the
    /// asked-about subset is what the count assertions catch.
    /// </remarks>
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
