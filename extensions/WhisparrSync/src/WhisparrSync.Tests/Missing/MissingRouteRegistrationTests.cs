using Cove.Core.Auth;
using Cove.Plugins;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.Missing;

/// <summary>
/// What the catalogue tab is registered as, and which tier each of its routes declares.
/// </summary>
/// <remarks>
/// The host resolves a tab's <c>componentName</c> to the bundle's own component map by exact string
/// and renders nothing, with no error, when they differ. It also substitutes only <c>{entityId}</c>
/// into a count endpoint, so a registration whose baked kind disagrees with its own page type asks
/// one page's badge about another kind and reports nothing wrong.
/// <para>
/// Both sides of each pair are read from the tier that owns them. A literal restated here would
/// agree with whichever side it was copied from and stop reporting the other.
/// </para>
/// </remarks>
public sealed class MissingRouteRegistrationTests
{
    /// <summary>
    /// The entity page types the catalogue tab is registered on, which are the host's own literals.
    /// </summary>
    /// <remarks>
    /// Also what selects the catalogue tab out of the manifest. The video detail page carries a tab
    /// of its own with no catalogue behind it, and it is covered where the manifest's two
    /// generations are compared.
    /// </remarks>
    private static readonly string[] PageTypes = ["studio", "performer", "tag"];

    [Fact]
    public void TheTabIsRegisteredOncePerPageTypeUnderOneComponent()
    {
        var tabs = MissingTabs();

        Assert.Equal(PageTypes.Order(), tabs.Select(tab => tab.PageType).Order());
        Assert.Single(tabs.Select(tab => tab.ComponentName).Distinct(StringComparer.Ordinal));
        Assert.Single(tabs.Select(tab => tab.Key).Distinct(StringComparer.Ordinal));
        Assert.Single(tabs.Select(tab => tab.Label).Distinct(StringComparer.Ordinal));
        Assert.All(tabs, tab => Assert.False(string.IsNullOrWhiteSpace(tab.ComponentName)));
    }

    /// <summary>
    /// Each count endpoint bakes its own registration's kind, and leaves the host its placeholder.
    /// </summary>
    [Fact]
    public void EachCountEndpointBakesItsOwnPageTypeAndKeepsTheHostsPlaceholder()
    {
        foreach (var tab in MissingTabs())
        {
            var endpoint = Assert.IsType<string>(tab.CountEndpoint);

            Assert.Contains("{entityId}", endpoint, StringComparison.Ordinal);
            Assert.EndsWith("/missing/count", endpoint, StringComparison.Ordinal);
            Assert.Contains(
                $"/entity/{tab.PageType}/", endpoint, StringComparison.Ordinal);

            // The other two kinds are absent, so a registration cannot carry one page type and ask
            // about another while still ending in the right segment.
            Assert.All(
                PageTypes.Where(other => !string.Equals(other, tab.PageType, StringComparison.Ordinal)),
                other => Assert.DoesNotContain(
                    $"/entity/{other}/", endpoint, StringComparison.Ordinal));
        }
    }

    /// <summary>Each catalogue route is mounted once, at the tier its reach expresses.</summary>
    [Fact]
    public async Task EachCatalogueRouteIsMountedOnceAtTheTierItsReachExpresses()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddWhisparrSyncBindingServices();
        builder.Services.AddRouting();

        await using var app = builder.Build();
        WhisparrSyncFixture.Create().MapEndpoints(app);

        // A WebApplication's registrations reach the endpoint data source only once routing is built
        // at start, so without this every assertion below holds over nothing.
        await app.StartAsync(TestContext.Current.CancellationToken);

        var routes = app.Services
            .GetRequiredService<EndpointDataSource>()
            .Endpoints.OfType<RouteEndpoint>()
            .Where(route => (route.RoutePattern.RawText ?? string.Empty)
                .Contains("/missing", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(5, routes.Count);
        Assert.Equal(5, routes.Select(Describe).Distinct(StringComparer.Ordinal).Count());

        Assert.Equal([Permissions.VideosRead], PermissionsOf(routes, "GET", "/missing"));
        Assert.Equal([Permissions.VideosRead], PermissionsOf(routes, "GET", "/missing/count"));
        Assert.Equal(
            [Permissions.ExtensionsConfigure],
            PermissionsOf(routes, "POST", "/missing/bulk-monitor"));
        Assert.Equal(
            [Permissions.ExtensionsConfigure],
            PermissionsOf(routes, "POST", "/missing/{providerSceneId}/monitor"));
        Assert.Equal(
            [Permissions.ExtensionsConfigure],
            PermissionsOf(routes, "POST", "/missing/{providerSceneId}/search"));

        await app.StopAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Which scene a per-scene route touches is a path segment, so no body can name one.
    /// </summary>
    [Fact]
    public async Task ThePerSceneRoutesNameTheirSceneInThePath()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddWhisparrSyncBindingServices();
        builder.Services.AddRouting();

        await using var app = builder.Build();
        WhisparrSyncFixture.Create().MapEndpoints(app);
        await app.StartAsync(TestContext.Current.CancellationToken);

        var perScene = app.Services
            .GetRequiredService<EndpointDataSource>()
            .Endpoints.OfType<RouteEndpoint>()
            .Select(route => route.RoutePattern.RawText ?? string.Empty)
            .Where(pattern => pattern.EndsWith("/monitor", StringComparison.Ordinal)
                || pattern.EndsWith("/search", StringComparison.Ordinal))
            .Where(pattern => pattern.Contains("/missing/", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(2, perScene.Count);
        Assert.All(
            perScene,
            pattern => Assert.Contains("{providerSceneId}", pattern, StringComparison.Ordinal));

        await app.StopAsync(TestContext.Current.CancellationToken);
    }

    private static IReadOnlyList<UITabContribution> MissingTabs()
    {
        var extension = WhisparrSyncFixture.Create();
        var manifest = extension.GetUIManifest();

        // Selected by the host page types above, which this file already owns as the host's own
        // literals. Filtering by key would read the registration under test through a literal this
        // file would then own a copy of.
        return [.. manifest.Tabs.Where(tab => PageTypes.Contains(tab.PageType, StringComparer.Ordinal))];
    }

    private static IReadOnlyList<string> PermissionsOf(
        IEnumerable<RouteEndpoint> routes, string method, string suffix)
    {
        var route = routes.Single(route =>
            (route.RoutePattern.RawText ?? string.Empty).EndsWith(suffix, StringComparison.Ordinal)
            && (route.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? []).Contains(method));

        var declared = route.Metadata.GetMetadata<CovePermissionRequirementMetadata>();
        Assert.NotNull(declared);
        Assert.Equal(PermissionMode.Any, declared.Mode);
        return declared.Permissions;
    }

    private static string Describe(RouteEndpoint route)
    {
        var methods = route.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [];
        return $"{string.Join('|', methods)} /{route.RoutePattern.RawText?.TrimStart('/')}";
    }
}
