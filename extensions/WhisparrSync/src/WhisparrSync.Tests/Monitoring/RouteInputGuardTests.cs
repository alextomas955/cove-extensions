using System.Globalization;
using System.Net;
using System.Text;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Monitoring;

// The route set is enumerated from the emitted wire document rather than transcribed, so a route
// mounted later is covered without an edit. Every assertion is on the status of a raw response:
// reading the answer as its declared contract would throw, and a throw is not the same evidence as
// a bad request.
public sealed class RouteInputGuardTests
{
    // An integer inside no member of the kind enum. It parses, which is the defect: a parse that
    // succeeds is not the same as a value the arms downstream can act on.
    private const string UndefinedKind = "7";

    // Two of the routes bind a body and the rest take none, so every route is sent this.
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

    [Theory]
    [MemberData(nameof(KindTakingRoutes))]
    public async Task AKindNamingNoMemberIsARefusedRequestOnEveryRoute(string method, string template)
    {
        await using var host = await MonitorHost.CreateAsync();
        await host.SeedStudioAsync(MonitorHost.StoredEndpoint, MonitorHost.StudioRemoteIdValue);

        var answered = await SendAsync(host, method, Address(template, UndefinedKind));

        Assert.Equal(HttpStatusCode.BadRequest, answered.StatusCode);
    }

    // A document that answered an empty set would otherwise read as a pass.
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
                "bulk-monitor",
                "count",
                "missing",

                // Twice: the entity's own monitor route and the catalogue's per-scene one, which are
                // different routes ending in the same segment.
                "monitor",
                "monitor",
                "monitor-all",
                "monitoring",
                "reflect-owned",
                "scope",
                "search",
                "search-all-monitored",
                "status",
                "unmonitor",

                // The facet-value lookup, which names the facet in its last segment rather than a
                // verb.
                "{facetKey}",
            ],
            templates);
    }

    // A kind the enum declares still reaches its own arm, so the guard refuses the undefined value
    // rather than everything.
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

    // Regression: the read route echoed the numeric kind straight back out.
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
