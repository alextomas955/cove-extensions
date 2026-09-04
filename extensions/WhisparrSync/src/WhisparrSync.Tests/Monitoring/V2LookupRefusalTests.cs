using System.Net;
using System.Net.Http.Json;
using WhisparrSync.Contracts;
using WhisparrSync.Monitoring;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.Monitoring;

/// <summary>
/// Which refusal a reader is told when the older generation's lookup names no entity, at the routes
/// a reader reaches rather than against the helper that maps it.
/// </summary>
/// <remarks>
/// The shipped client is stood over a byte-level stub, so each case exercises the real lookup, the
/// real parse and the real classification. A case calling the projector directly agrees with a
/// mapping the routes never consult, which is how a helper with no production caller kept four
/// passing tests.
/// <para>
/// This generation publishes no contract, so a field's type cannot be assumed. An answer carrying a
/// field of the wrong type is unreadable, and unreadable is a refusal rather than an exception out
/// of the route.
/// </para>
/// </remarks>
public sealed class V2LookupRefusalTests
{
    /// <summary>The spelling this library holds the older generation's identity rows under.</summary>
    private const string V2Endpoint = "theporndb.net/graphql";

    private const string V2RemoteId = "5f7c1d90-2a3b-4c6d-8e91-0b2f4a6d8c13";

    /// <summary>What the instance answers an identifier its own source does not know.</summary>
    private const string NoMatch = "[]";

    /// <summary>One entity, readable, as this generation names one.</summary>
    private const string OneSite = """[{"tvdbId":3372,"title":"Vixen","titleSlug":"vixen"}]""";

    /// <summary>Two entities, with nothing in the answer saying which was meant.</summary>
    private const string TwoSites =
        """[{"tvdbId":3372,"title":"Vixen","titleSlug":"vixen"},{"tvdbId":3373,"title":"Vixen 2","titleSlug":"vixen-2"}]""";

    /// <summary>The listing, holding the entity the lookup named and monitored.</summary>
    private const string HeldSeries =
        """[{"id":11,"tvdbId":3372,"title":"Vixen","monitored":true}]""";

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    private static async Task<(MonitorHost Host, int StudioId)> V2StudioAsync(
        params (HttpStatusCode Status, string Answer)[] answers)
    {
        var host = await MonitorHost.CreateAsync(
            generation: WhisparrGeneration.V2,
            bytes: BodyRecordingHandler.AnsweringInTurn(answers));
        var studioId = await host.SeedStudioAsync(V2Endpoint, V2RemoteId);
        return (host, studioId);
    }

    /// <summary>
    /// An identifier the instance's own source does not know is the no-identity refusal, at the read
    /// route.
    /// </summary>
    /// <remarks>
    /// This is the common case rather than a rare one: roughly a third of the library's studios are
    /// unreachable on this generation. Telling the reader the instance refused sends them to audit an
    /// instance that did exactly what it was asked.
    /// </remarks>
    [Fact]
    public async Task AnIdentifierTheInstanceDoesNotKnowIsTheNoIdentityRefusal()
    {
        var (host, studioId) = await V2StudioAsync((HttpStatusCode.OK, NoMatch));
        await using var owned = host;

        var view = await host.ReadMonitoringAsync(studioId);

        Assert.Equal(MonitorRefusalKind.NoIdentityInThisNamespace, view.Refusal);
    }

    /// <summary>The route answers the refusal the projector maps, not one of its own.</summary>
    /// <remarks>
    /// Tied to the projector rather than to a literal, so the mapping cannot pass here while the
    /// route composes its own answer beside it.
    /// </remarks>
    [Fact]
    public async Task TheRouteAnswersTheRefusalTheProjectorMapsForThatReading()
    {
        var (host, studioId) = await V2StudioAsync((HttpStatusCode.OK, NoMatch));
        await using var owned = host;

        var view = await host.ReadMonitoringAsync(studioId);

        Assert.Equal(V2LookupProjector.RefusalFor(V2LookupReading.NoMatch), view.Refusal);
        Assert.NotEqual(MonitorRefusalKind.InstanceRefused, view.Refusal);
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
        var (host, studioId) = await V2StudioAsync((HttpStatusCode.OK, NoMatch));
        await using var owned = host;

        var view = await host.ActRawAsync(
            "studio", studioId, verb, """{"scope":"futureScenes"}""");

        Assert.Equal(MonitorRefusalKind.NoIdentityInThisNamespace, view.Refusal);
    }

    /// <summary>The verbs this build mounted beside the three report it too.</summary>
    [Fact]
    public async Task TheRegistrationVerbReportsThatSameFact()
    {
        var (host, studioId) = await V2StudioAsync((HttpStatusCode.OK, NoMatch));
        await using var owned = host;

        var enqueued = await host.AddAllMissingViewAsync("studio", studioId);

        Assert.Null(enqueued.JobId);
        Assert.Equal(MonitorRefusalKind.NoIdentityInThisNamespace, enqueued.Refusal);
    }

    /// <summary>
    /// Two matching entities is a fact about the instance's answer, so it stays the instance refusal.
    /// </summary>
    /// <remarks>
    /// The answer never echoes the term, so nothing says which of the two was meant and acting on
    /// either would act on an entity nobody named.
    /// </remarks>
    [Fact]
    public async Task TwoMatchingEntitiesStayTheInstanceRefusal()
    {
        var (host, studioId) = await V2StudioAsync((HttpStatusCode.OK, TwoSites));
        await using var owned = host;

        var view = await host.ReadMonitoringAsync(studioId);

        Assert.Equal(MonitorRefusalKind.InstanceRefused, view.Refusal);
    }

    /// <summary>A body that is not a list of entities at all stays the instance refusal.</summary>
    [Fact]
    public async Task ABodyThatIsNotAListOfEntitiesStaysTheInstanceRefusal()
    {
        var (host, studioId) = await V2StudioAsync(
            (HttpStatusCode.OK, """{"message":"not a list"}"""));
        await using var owned = host;

        var view = await host.ReadMonitoringAsync(studioId);

        Assert.Equal(MonitorRefusalKind.InstanceRefused, view.Refusal);
    }

    /// <summary>A lookup the instance itself failed stays the instance refusal.</summary>
    [Fact]
    public async Task ALookupTheInstanceFailedStaysTheInstanceRefusal()
    {
        var (host, studioId) = await V2StudioAsync((HttpStatusCode.InternalServerError, ""));
        await using var owned = host;

        var view = await host.ReadMonitoringAsync(studioId);

        Assert.Equal(MonitorRefusalKind.InstanceRefused, view.Refusal);
    }

    /// <summary>One readable entity still resolves, and the listing still answers what is held.</summary>
    [Fact]
    public async Task OneReadableEntityStillResolvesAndTheListingStillAnswers()
    {
        var (host, studioId) = await V2StudioAsync(
            (HttpStatusCode.OK, OneSite), (HttpStatusCode.OK, HeldSeries));
        await using var owned = host;

        var view = await host.ReadMonitoringAsync(studioId);

        Assert.Equal(MonitorRefusalKind.None, view.Refusal);
        Assert.True(view.Monitored);
    }

    /// <summary>
    /// An entity field of a type this generation never declared reads as unreadable, and the route
    /// answers rather than failing.
    /// </summary>
    /// <remarks>
    /// The raw status is asserted, because an exception escaping the route answers 500 and a view
    /// read off a failed response would report nothing at all. This generation publishes no contract,
    /// so a proxy or a build answering a number where a string was assumed is expressible.
    /// </remarks>
    [Theory]
    [InlineData("""[{"tvdbId":3372,"title":3372,"titleSlug":"vixen"}]""")]
    [InlineData("""[{"tvdbId":3372,"title":"Vixen","titleSlug":17}]""")]
    [InlineData("""[{"tvdbId":3372,"title":{"en":"Vixen"},"titleSlug":"vixen"}]""")]
    [InlineData("""[{"tvdbId":3372,"title":null,"titleSlug":"vixen"}]""")]
    public async Task AFieldOfTheWrongTypeIsARefusalAndNotAServerFailure(string answer)
    {
        var (host, studioId) = await V2StudioAsync((HttpStatusCode.OK, answer));
        await using var owned = host;

        var answered = await host.Http.GetAsync(
            host.RouteFor("studio", studioId, "monitoring"), TestCt);

        Assert.Equal(HttpStatusCode.OK, answered.StatusCode);
        var view = await answered.Content.ReadFromJsonAsync<EntityMonitoringView>(TestCt);
        Assert.Equal(MonitorRefusalKind.InstanceRefused, view!.Refusal);
    }

    /// <summary>No sentence a reader is shown is composed from what the lookup answered.</summary>
    /// <remarks>
    /// The refusal is a kind and the words are chosen in the browser from that kind, so a body a
    /// third party controls cannot reach a screen even on the failed path.
    /// </remarks>
    [Fact]
    public async Task NothingTheLookupAnsweredReachesTheViewOnAFailedPath()
    {
        var (host, studioId) = await V2StudioAsync(
            (HttpStatusCode.OK, """{"message":"contact your administrator, code 44e8"}"""));
        await using var owned = host;

        var answered = await host.Http.GetAsync(
            host.RouteFor("studio", studioId, "monitoring"), TestCt);
        var carried = await answered.Content.ReadAsStringAsync(TestCt);

        Assert.DoesNotContain("administrator", carried, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("44e8", carried, StringComparison.Ordinal);
    }
}
