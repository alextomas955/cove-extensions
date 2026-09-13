using System.Net;
using System.Net.Http.Json;
using WhisparrSync.Contracts;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Monitoring;

/// <summary>
/// Which refusal a reader is told when no site number is established for a v2 studio, at the routes
/// a reader reaches rather than against the mapping itself.
/// </summary>
/// <remarks>
/// The shipped client is stood over a byte-level stub, so each case exercises the real request, the
/// real parse and the real classification. A case calling the mapping directly agrees with one the
/// routes never consult.
/// <para>
/// A source that names no site and a source that was not reached are different answers, and a reader
/// can act on only one of them: the identity the library holds is the thing to fix, and a read that
/// arrived at nothing says nothing about that identity at all.
/// </para>
/// </remarks>
public sealed class V2SiteRefusalTests
{
    /// <summary>The spelling this library holds v2's identity rows under.</summary>
    private const string V2Endpoint = "theporndb.net/graphql";

    private const string V2RemoteId = "5f7c1d90-2a3b-4c6d-8e91-0b2f4a6d8c13";

    /// <summary>The number the metadata source names that site by.</summary>
    private const int V2SiteNumber = 3372;

    /// <summary>The list, holding no row for the number the source named.</summary>
    private const string NoHeldSeries = "[]";

    /// <summary>The list, holding the site the source named and monitored.</summary>
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

    /// <summary>
    /// A studio the metadata source names no site for is the no-identity refusal, at the read route.
    /// </summary>
    /// <remarks>
    /// This is the common case rather than a rare one: roughly a third of the library's studios are
    /// unreachable on this generation. Telling the reader the instance refused sends them to audit an
    /// instance that was never asked.
    /// </remarks>
    [Fact]
    public async Task AStudioTheSourceNamesNoSiteForIsTheNoIdentityRefusal()
    {
        var (host, studioId) = await V2StudioAsync(WhisparrSiteNumber.NamesNone);
        await using var owned = host;

        var view = await host.ReadMonitoringAsync(studioId);

        Assert.Equal(MonitorRefusalKind.NoIdentityInThisNamespace, view.Refusal);
    }

    /// <summary>A source that was not reached is a refusal of a different kind.</summary>
    /// <remarks>
    /// It establishes nothing about the studio, so reporting it as one the source names no site for
    /// would send a reader to fix an identity that may be correct.
    /// </remarks>
    [Fact]
    public async Task ASourceThatWasNotReachedIsHeldApartFromOneNamingNoSite()
    {
        var (host, studioId) = await V2StudioAsync(WhisparrSiteNumber.NotReached);
        await using var owned = host;

        var view = await host.ReadMonitoringAsync(studioId);

        Assert.NotEqual(MonitorRefusalKind.NoIdentityInThisNamespace, view.Refusal);
        Assert.NotEqual(MonitorRefusalKind.None, view.Refusal);
    }

    /// <summary>Every gesture a reader can make reports that same fact.</summary>
    /// <remarks>
    /// One fact whichever verb was pressed. A distinction holding on the read alone would still send
    /// a reader who pressed the control to the wrong screen.
    /// </remarks>
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

    /// <summary>The verb that downloads reports it too, on the generation that serves it.</summary>
    /// <remarks>
    /// The one other mounted verb this generation reaches an entity read through. Registering the
    /// scenes a catalogue lacks refuses earlier here, for the generation gap, which is the reason the
    /// precedence puts ahead of the metadata link.
    /// </remarks>
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

    /// <summary>A list the instance itself failed stays the instance refusal.</summary>
    [Fact]
    public async Task AListTheInstanceFailedStaysTheInstanceRefusal()
    {
        var (host, studioId) = await ANumberedSiteAsync(
            (HttpStatusCode.InternalServerError, ""));
        await using var owned = host;

        var view = await host.ReadMonitoringAsync(studioId);

        Assert.Equal(MonitorRefusalKind.InstanceRefused, view.Refusal);
    }

    /// <summary>The site the source numbered still resolves, and the list still answers.</summary>
    [Fact]
    public async Task ANumberedSiteStillResolvesAndTheListStillAnswers()
    {
        var (host, studioId) = await ANumberedSiteAsync((HttpStatusCode.OK, HeldSeries));
        await using var owned = host;

        var view = await host.ReadMonitoringAsync(studioId);

        Assert.Equal(MonitorRefusalKind.None, view.Refusal);
        Assert.True(view.Monitored);
    }

    /// <summary>
    /// A site the instance does not hold resolves, and is read as one it does not hold.
    /// </summary>
    /// <remarks>
    /// The case every registration is made of. A site the instance holds no row for has to read as
    /// absent rather than as a refusal, or a library run refuses every site it was meant to register.
    /// </remarks>
    [Fact]
    public async Task ASiteTheInstanceDoesNotHoldResolvesAndReadsAsAbsent()
    {
        var (host, studioId) = await ANumberedSiteAsync((HttpStatusCode.OK, NoHeldSeries));
        await using var owned = host;

        var view = await host.ReadMonitoringAsync(studioId);

        Assert.Equal(MonitorRefusalKind.None, view.Refusal);
        Assert.False(view.Present);
    }

    /// <summary>No sentence a reader is shown is composed from what the instance answered.</summary>
    /// <remarks>
    /// The refusal is a kind and the words are chosen in the browser from that kind, so a body a
    /// third party controls cannot reach a screen even on the failed path.
    /// </remarks>
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
