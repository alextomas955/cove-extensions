using System.Net;
using System.Text.Json.Nodes;
using WhisparrSync.Contracts;
using WhisparrSync.Monitoring;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.Whisparr;

/// <summary>
/// The older generation's per-scene seams: the row read, the composed monitor body, and the
/// per-scene surface that is still refused there.
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

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

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
