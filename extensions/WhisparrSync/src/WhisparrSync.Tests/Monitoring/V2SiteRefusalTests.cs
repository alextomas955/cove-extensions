using System.Net;
using System.Net.Http.Json;
using WhisparrSync.Contracts;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Monitoring;

// The shipped client is stood over a byte-level stub, so each case exercises the real request, the
// real parse and the real classification. A case calling the mapping directly would assert a path
// the routes never take.
public sealed class V2SiteRefusalTests
{
    // The spelling this library holds v2's identity rows under.
    private const string V2Endpoint = "theporndb.net/graphql";

    private const string V2RemoteId = "5f7c1d90-2a3b-4c6d-8e91-0b2f4a6d8c13";

    // The number the metadata source names that site by.
    private const int V2SiteNumber = 3372;

    private const string NoHeldSeries = "[]";

    // The list, holding the site the source named, monitored.
    private const string HeldSeries =
        """[{"id":11,"tvdbId":3372,"title":"Vixen","monitored":true}]""";

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    private static async Task<(MonitorHost Host, int StudioId)> V2StudioAsync(
        WhisparrSiteNumber answer,
        params (HttpStatusCode Status, string Answer)[] answers)
    {
        var host = await MonitorHost.CreateAsync(
            generation: WhisparrGeneration.V2,
            bytes: BodyRecordingHandler.AnsweringInTurn(answers),
            siteNumbers: new TestSiteNumbers().Answering(V2RemoteId, answer));
        var studioId = await host.SeedStudioAsync(V2Endpoint, V2RemoteId);
        return (host, studioId);
    }

    private static Task<(MonitorHost Host, int StudioId)> ANumberedSiteAsync(
        params (HttpStatusCode Status, string Answer)[] answers)
        => V2StudioAsync(WhisparrSiteNumber.Numbered(V2SiteNumber), answers);

    // Roughly a third of the library's studios reach no site number on v2, so reporting this as an
    // instance refusal would send readers to audit an instance that was never asked.
    [Fact]
    public async Task AStudioTheSourceNamesNoSiteForIsTheNoIdentityRefusal()
    {
        var (host, studioId) = await V2StudioAsync(WhisparrSiteNumber.NamesNone);
        await using var owned = host;

        var view = await host.ReadMonitoringAsync(studioId);

        Assert.Equal(MonitorRefusalKind.NoIdentityInThisNamespace, view.Refusal);
    }

    // An unreached source establishes nothing about the studio, so reporting it as one the source
    // names no site for would send a reader to fix an identity that may be correct.
    [Fact]
    public async Task ASourceThatWasNotReachedIsHeldApartFromOneNamingNoSite()
    {
        var (host, studioId) = await V2StudioAsync(WhisparrSiteNumber.NotReached);
        await using var owned = host;

        var view = await host.ReadMonitoringAsync(studioId);

        Assert.NotEqual(MonitorRefusalKind.NoIdentityInThisNamespace, view.Refusal);
        Assert.NotEqual(MonitorRefusalKind.None, view.Refusal);
    }

    // The classification holding on the read alone would still send a reader who pressed a control
    // to the wrong screen, so every acting route is checked.
    [Theory]
    [InlineData("monitor")]
    [InlineData("unmonitor")]
    [InlineData("scope")]
    public async Task EveryActingRouteReportsThatSameFact(string verb)
    {
        var (host, studioId) = await V2StudioAsync(WhisparrSiteNumber.NamesNone);
        await using var owned = host;

        var view = await host.ActRawAsync(
            "studio", studioId, verb, """{"scope":"futureScenes"}""");

        Assert.Equal(MonitorRefusalKind.NoIdentityInThisNamespace, view.Refusal);
    }

    // The one other mounted verb that reaches an entity read on v2. The scene-registering verb
    // refuses earlier, on the generation, so it cannot report this refusal.
    [Fact]
    public async Task TheVerbThatDownloadsReportsThatSameFact()
    {
        var (host, studioId) = await V2StudioAsync(WhisparrSiteNumber.NamesNone);
        await using var owned = host;

        var answered = await host.PostRawAsync("studio", studioId, "search-all-monitored", "{}");
        answered.EnsureSuccessStatusCode();
        var view = await answered.Content.ReadFromJsonAsync<EntityMonitoringView>(TestCt);

        Assert.Equal(MonitorRefusalKind.NoIdentityInThisNamespace, view!.Refusal);
    }

    [Fact]
    public async Task AListTheInstanceFailedStaysTheInstanceRefusal()
    {
        var (host, studioId) = await ANumberedSiteAsync(
            (HttpStatusCode.InternalServerError, ""));
        await using var owned = host;

        var view = await host.ReadMonitoringAsync(studioId);

        Assert.Equal(MonitorRefusalKind.InstanceRefused, view.Refusal);
    }

    [Fact]
    public async Task ANumberedSiteStillResolvesAndTheListStillAnswers()
    {
        var (host, studioId) = await ANumberedSiteAsync((HttpStatusCode.OK, HeldSeries));
        await using var owned = host;

        var view = await host.ReadMonitoringAsync(studioId);

        Assert.Equal(MonitorRefusalKind.None, view.Refusal);
        Assert.True(view.Monitored);
    }

    // A site the instance holds no row for reads as absent, not as a refusal. Classifying it as a
    // refusal would make a library run refuse every site it was meant to register.
    [Fact]
    public async Task ASiteTheInstanceDoesNotHoldResolvesAndReadsAsAbsent()
    {
        var (host, studioId) = await ANumberedSiteAsync((HttpStatusCode.OK, NoHeldSeries));
        await using var owned = host;

        var view = await host.ReadMonitoringAsync(studioId);

        Assert.Equal(MonitorRefusalKind.None, view.Refusal);
        Assert.False(view.Present);
    }

    // The refusal travels as a kind and the browser chooses the words, so a body a third party
    // controls cannot reach a screen even on the failed path.
    [Fact]
    public async Task NothingTheInstanceAnsweredReachesTheViewOnAFailedPath()
    {
        var (host, studioId) = await ANumberedSiteAsync(
            (HttpStatusCode.OK, """{"message":"contact your administrator, code 44e8"}"""));
        await using var owned = host;

        var answered = await host.Http.GetAsync(
            host.RouteFor("studio", studioId, "monitoring"), TestCt);
        var carried = await answered.Content.ReadAsStringAsync(TestCt);

        Assert.Equal(HttpStatusCode.OK, answered.StatusCode);
        Assert.DoesNotContain("administrator", carried, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("44e8", carried, StringComparison.Ordinal);
    }
}
