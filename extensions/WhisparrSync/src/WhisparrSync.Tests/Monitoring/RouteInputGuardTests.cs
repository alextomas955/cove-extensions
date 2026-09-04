using System.Globalization;
using System.Net;
using System.Text;
using WhisparrSync.Monitoring;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.Monitoring;

/// <summary>
/// What every one-entity route answers a kind segment that names no entity kind.
/// </summary>
/// <remarks>
/// The route set is enumerated from the emitted wire document rather than transcribed, so a route
/// mounted later is covered here without an edit. A hand-written array would keep passing while the
/// new route answered whatever it liked.
/// <para>
/// Every assertion is on the STATUS of a raw response. Reading the answer as the contract it
/// declares would throw on an unhandled failure, and a throwing test is not the same evidence as a
/// bad request.
/// </para>
/// </remarks>
public sealed class RouteInputGuardTests
{
    /// <summary>
    /// An integer inside no member of the kind enum. It parses, which is the whole defect: a parse
    /// that succeeds is not the same as a value the arms downstream can act on.
    /// </summary>
    private const string UndefinedKind = "7";

    /// <summary>What each route is sent, since two of them bind a body and the rest take none.</summary>
    private const string ScopeBody = """{"scope":"futureScenes"}""";

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    public static TheoryData<string, string> KindTakingRoutes
    {
        get
        {
            TheoryData<string, string> routes = [];
            foreach (var route in WireDocument.KindTakingRoutes())
            {
                routes.Add(route.Method, route.Template);
            }

            return routes;
        }
    }

    /// <summary>
    /// Every route the shipped document declares a kind segment on answers a bad request for a kind
    /// that names nothing, rather than raising into a handler whose declared results hold no failure.
    /// </summary>
    [Theory]
    [MemberData(nameof(KindTakingRoutes))]
    public async Task AKindNamingNoMemberIsARefusedRequestOnEveryRoute(string method, string template)
    {
        await using var host = await MonitorHost.CreateAsync();
        await host.SeedStudioAsync(MonitorHost.StoredEndpoint, MonitorHost.StudioRemoteIdValue);

        var answered = await SendAsync(host, method, Address(template, UndefinedKind));

        Assert.Equal(HttpStatusCode.BadRequest, answered.StatusCode);
    }

    /// <summary>
    /// The enumeration covers the routes this build actually mounts, so a document that answered an
    /// empty set could not read as a pass.
    /// </summary>
    [Fact]
    public void EveryMountedEntityRouteIsEnumerated()
    {
        var templates = WireDocument.KindTakingRoutes()
            .Select(route => route.Template[(route.Template.LastIndexOf('/') + 1)..])
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            [
                "add-all-missing",
                "monitor",
                "monitoring",
                "reflect-owned",
                "scope",
                "search-all-monitored",
                "unmonitor",
            ],
            templates);
    }

    /// <summary>
    /// A kind the enum does declare still reaches its own arm, so the guard is proven to be refusing
    /// the undefined value rather than everything.
    /// </summary>
    [Fact]
    public async Task AKindTheEnumDeclaresStillReachesItsOwnArm()
    {
        await using var host = await MonitorHost.CreateAsync();
        host.Client.Answering(
            nameof(IWhisparrStudioActing.ReadStudioAsync),
            MonitorHost.Json(200, MonitorHost.AddedStudio));
        var studioId = await host.SeedStudioAsync(
            MonitorHost.StoredEndpoint, MonitorHost.StudioRemoteIdValue);

        var answered = await host.PostRawAsync("studio", studioId, "monitor", ScopeBody);

        Assert.Equal(HttpStatusCode.OK, answered.StatusCode);
    }

    /// <summary>
    /// The read route answers the refusal rather than a body naming the kind it was given. What the
    /// review recorded it doing was echoing the numeric kind straight back out.
    /// </summary>
    [Fact]
    public async Task TheReadRouteAnswersNoBodyCarryingTheKindItWasGiven()
    {
        await using var host = await MonitorHost.CreateAsync(apiKey: null);

        var answered = await host.Http.GetAsync(
            Address(
                "/api/extensions/" + host.ExtensionId + "/entity/{kind}/{coveId}/monitoring",
                UndefinedKind),
            TestCt);

        Assert.Equal(HttpStatusCode.BadRequest, answered.StatusCode);
        Assert.DoesNotContain(
            UndefinedKind,
            await answered.Content.ReadAsStringAsync(TestCt),
            StringComparison.Ordinal);
    }

    private static string Address(string template, string kind)
        => template.Replace("{kind}", kind, StringComparison.Ordinal)
            .Replace("{coveId}", 1.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

    private static async Task<HttpResponseMessage> SendAsync(
        MonitorHost host, string method, string address)
    {
        if (method == "get")
        {
            return await host.Http.GetAsync(address, TestCt);
        }

        using var content = new StringContent(ScopeBody, Encoding.UTF8, "application/json");
        return await host.Http.PostAsync(address, content, TestCt);
    }
}
