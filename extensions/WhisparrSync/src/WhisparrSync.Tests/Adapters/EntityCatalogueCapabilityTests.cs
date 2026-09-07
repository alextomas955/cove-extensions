using System.Net;
using System.Text.Json;
using WhisparrSync.Adapters;
using WhisparrSync.Client;
using WhisparrSync.Contracts;
using WhisparrSync.Tests.TestSupport;
using static WhisparrSync.Tests.TestSupport.EndpointTestSupport;

namespace WhisparrSync.Tests.Adapters;

/// <summary>
/// Deciding the narrow-catalogue capability from the instance's own API description, with the NEGATIVE case
/// as the headline: an instance that does not declare the routes must yield an adapter that structurally
/// cannot answer, a caller that defers, and no movie read of any kind.
/// </summary>
/// <remarks>
/// The positive case is driven from the committed live answer in
/// <c>e2e/fixtures/wire/openapi-capability.json</c> rather than from route keys written here, so the unit
/// decision and the instance's own spelling cannot drift apart.
/// </remarks>
[Trait("Tier", "L2")]
public sealed class EntityCatalogueCapabilityTests
{
    private const string BaseUrl = "http://whisparr.local:6969";
    private const string ApiKey = "KEY";

    // The document shapes the two cases turn on. The negative one is a real path map that simply does not
    // declare the pair — not an empty document, which would also fail for being empty.
    private static string DocumentDeclaring(params string[] paths)
        => JsonSerializer.Serialize(new
        {
            paths = paths.ToDictionary(path => path, _ => new { get = new { } }),
        });

    private static readonly string[] RoutelessPaths =
        ["/api/v3/movie", "/api/v3/studio", "/api/v3/performer", "/api/v3/system/status"];

    // The two keys exactly as the live instance's document spells them, read from the committed artifact.
    private static string[] DeclaredCatalogueRoutes()
    {
        using var document = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "openapi-capability.json")));
        return [.. document.RootElement.GetProperty("routes").EnumerateArray()
            .Select(route => route.GetProperty("key").GetString()!)];
    }

    [Fact]
    public void A_document_declaring_neither_route_yields_the_capability_absent()
        => Assert.False(WhisparrCapabilities.From(RoutelessPaths).NarrowEntityCatalogue);

    [Fact]
    public void A_document_declaring_only_one_of_the_pair_yields_the_capability_absent()
    {
        var routes = DeclaredCatalogueRoutes();
        Assert.Equal(2, routes.Length);
        Assert.False(WhisparrCapabilities.From([.. RoutelessPaths, routes[0]]).NarrowEntityCatalogue);
        Assert.False(WhisparrCapabilities.From([.. RoutelessPaths, routes[1]]).NarrowEntityCatalogue);
    }

    [Fact]
    public void The_live_documents_own_route_keys_yield_the_capability_present()
        => Assert.True(WhisparrCapabilities.From([.. RoutelessPaths, .. DeclaredCatalogueRoutes()]).NarrowEntityCatalogue);

    [Fact]
    public void The_route_keys_are_matched_case_insensitively()
        => Assert.True(WhisparrCapabilities
            .From(DeclaredCatalogueRoutes().Select(route => route.ToUpperInvariant()))
            .NarrowEntityCatalogue);

    [Fact]
    public void An_adapter_selected_without_the_capability_does_not_carry_the_role()
    {
        var client = new WhisparrClient(new HttpClient(FakeHttpMessageHandler.Json("[]")));

        var adapter = AdapterSelector.SelectForVersion("v3", client, WhisparrCapabilities.None);

        Assert.IsType<V3Adapter>(adapter);
        Assert.IsNotType<V3EntityCatalogueAdapter>(adapter);
        Assert.False(adapter is IWhisparrEntityCatalogue);
    }

    [Fact]
    public void An_adapter_selected_with_the_capability_carries_the_role()
    {
        var client = new WhisparrClient(new HttpClient(FakeHttpMessageHandler.Json("[]")));

        var adapter = AdapterSelector.SelectForVersion(
            "v3", client, WhisparrCapabilities.From(DeclaredCatalogueRoutes()));

        Assert.IsType<V3EntityCatalogueAdapter>(adapter);
        Assert.True(adapter is IWhisparrEntityCatalogue);
    }

    [Fact]
    public void The_capability_free_overload_can_never_reach_the_role()
    {
        var client = new WhisparrClient(new HttpClient(FakeHttpMessageHandler.Json("[]")));

        Assert.False(AdapterSelector.SelectForVersion("v3", client) is IWhisparrEntityCatalogue);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task A_document_read_that_does_not_answer_reports_its_classification_and_reads_as_absent(
        HttpStatusCode status)
    {
        var port = new WhisparrCapabilityPort(
            new WhisparrClient(new HttpClient(FakeHttpMessageHandler.Html(status))));

        var read = await port.ResolveAsync(BaseUrl, "v3", CancellationToken.None);

        // The classification survives, so a caller can tell "could not ask" from "asked, no such route" —
        // and the fail-closed reading still yields absent for a caller that needs the role.
        Assert.False(read.IsOk);
        Assert.False(WhisparrCapabilityPort.OrAbsent(read).NarrowEntityCatalogue);
    }

    [Fact]
    public async Task A_document_that_answers_but_carries_no_paths_member_is_an_ok_verdict_of_absent()
    {
        var port = new WhisparrCapabilityPort(
            new WhisparrClient(new HttpClient(FakeHttpMessageHandler.Json("{\"openapi\":\"3.0.1\"}"))));

        var read = await port.ResolveAsync(BaseUrl, "v3", CancellationToken.None);

        // Ok, unlike the case above: the instance answered, and what it said was "no such route".
        Assert.True(read.IsOk);
        Assert.False(read.Value!.NarrowEntityCatalogue);
    }

    [Fact]
    public async Task An_unreachable_instance_is_not_reported_as_a_build_missing_a_route()
    {
        // The read-only discovery surface never invokes the role, so an outage must reach its own outage
        // handling rather than be answered with a capability refusal a user could do nothing about.
        var handler = FakeHttpMessageHandler.Html(HttpStatusCode.BadGateway);
        var ext = NewExtension(await StoreWith(BaseUrl, ApiKey));

        var result = await ext.DiscoveryEntityAsync(
            new DiscoveryEntityRequest(1, "studio"),
            new WhisparrClient(new HttpClient(handler)),
            StashDbClientReturning(),
            TpdbClientReturning(),
            CancellationToken.None);

        Assert.DoesNotContain("CAPABILITY_UNAVAILABLE", ResponseJson(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_v3_instance_declaring_neither_route_defers_the_discovery_read_and_costs_no_movie_read()
    {
        var handler = FakeHttpMessageHandler.Json(DocumentDeclaring(RoutelessPaths));
        var ext = NewExtension(await StoreWith(BaseUrl, ApiKey));

        var result = await ext.DiscoveryEntityAsync(
            new DiscoveryEntityRequest(1, "studio"),
            new WhisparrClient(new HttpClient(handler)),
            StashDbClientReturning(),
            TpdbClientReturning(),
            CancellationToken.None);

        Assert.Equal(400, StatusOf(result));
        Assert.Contains("CAPABILITY_UNAVAILABLE", ResponseJson(result), StringComparison.Ordinal);

        // The zero is only worth something if the instrument saw the run at all — so the document read is
        // accounted for explicitly rather than left as an unasserted remainder.
        var breakdown = WhisparrRequestCounter.Classify(handler);
        Assert.Equal(0, breakdown.WholeSetMovieReads);
        Assert.Equal(0, breakdown.NarrowMovieReads);
        Assert.Equal(0, breakdown.Writes);
        Assert.Equal(1, breakdown.OtherReads);
        Assert.Equal(1, breakdown.Total);
        Assert.EndsWith("/docs/v3/openapi.json", handler.Requests[0].Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_v3_instance_declaring_both_routes_gets_past_the_capability_gate()
    {
        var handler = FakeHttpMessageHandler.Json(
            DocumentDeclaring([.. RoutelessPaths, .. DeclaredCatalogueRoutes()]));
        var ext = NewExtension(await StoreWith(BaseUrl, ApiKey));

        var result = await ext.DiscoveryEntityAsync(
            new DiscoveryEntityRequest(1, "studio"),
            new WhisparrClient(new HttpClient(handler)),
            StashDbClientReturning(),
            TpdbClientReturning(),
            CancellationToken.None);

        Assert.DoesNotContain("CAPABILITY_UNAVAILABLE", ResponseJson(result), StringComparison.Ordinal);
    }
}
