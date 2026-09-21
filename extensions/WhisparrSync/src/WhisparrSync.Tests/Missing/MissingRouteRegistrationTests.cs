using Cove.Core.Auth;
using Cove.Plugins;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.Missing;

// The host matches a tab's componentName against the bundle's component map by exact string and
// renders nothing, with no error, when they differ. It substitutes only {entityId} into a count
// endpoint, so a registration whose baked kind disagrees with its page type asks about another kind
// and reports nothing wrong.
public sealed class MissingRouteRegistrationTests
{
    // A tag names no entity either metadata source publishes a catalogue for, so no tag page
    // carries the tab.
    private static readonly string[] PageTypes = ["studio", "performer"];

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

            // The other page type is absent, so a registration cannot carry one page type and ask
            // about another while still ending in the right segment.
            Assert.All(
                PageTypes.Where(other => !string.Equals(other, tab.PageType, StringComparison.Ordinal)),
                other => Assert.DoesNotContain(
                    $"/entity/{other}/", endpoint, StringComparison.Ordinal));
        }
    }

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

        Assert.Equal(8, routes.Count);
        Assert.Equal(8, routes.Select(Describe).Distinct(StringComparer.Ordinal).Count());

        Assert.Equal([Permissions.VideosRead], PermissionsOf(routes, "GET", "/missing"));
        Assert.Equal([Permissions.VideosRead], PermissionsOf(routes, "GET", "/missing/count"));

        // A read of what the source publishes, at the tier the page and the count beside it declare.
        Assert.Equal(
            [Permissions.VideosRead], PermissionsOf(routes, "GET", "/missing/facet/{facetKey}"));
        Assert.Equal(
            [Permissions.ExtensionsConfigure],
            PermissionsOf(routes, "POST", "/missing/bulk-monitor"));
        Assert.Equal(
            [Permissions.ExtensionsConfigure],
            PermissionsOf(routes, "POST", "/missing/monitor-all"));

        // The add that makes an entity's catalogue exist creates an entity in the reader's
        // Whisparr, so it sits at the same tier as the rest of the writes.
        Assert.Equal(
            [Permissions.ExtensionsConfigure], PermissionsOf(routes, "POST", "/missing/track"));
        Assert.Equal(
            [Permissions.ExtensionsConfigure],
            PermissionsOf(routes, "POST", "/missing/{providerSceneId}/monitor"));
        Assert.Equal(
            [Permissions.ExtensionsConfigure],
            PermissionsOf(routes, "POST", "/missing/{providerSceneId}/search"));

        await app.StopAsync(TestContext.Current.CancellationToken);
    }

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
