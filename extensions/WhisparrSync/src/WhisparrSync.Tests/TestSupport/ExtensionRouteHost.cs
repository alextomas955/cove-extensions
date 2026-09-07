using Cove.Core.Auth;
using Cove.Plugins;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using WhisparrSync.Client;
using WhisparrSync.Discovery;
using Ext = global::WhisparrSync.WhisparrSync;

namespace WhisparrSync.Tests.TestSupport;

/// <summary>
/// Mounts the extension's <c>MapEndpoints</c> into an in-process web application, exposing both an HTTP
/// client and the materialized route table.
/// </summary>
/// <remarks>
/// The route table is the interesting half: the access tier each route declares is endpoint METADATA, so a
/// test asserting coverage has to read the endpoints the extension actually registered rather than a list
/// someone maintained by hand. Cove's own authorization middleware is not mounted here (it lives in
/// Cove.Api, which extensions do not reference), so this harness proves what a route DECLARES, never what
/// the host then enforces — that is the containerized e2e leg's job.
/// </remarks>
internal sealed class ExtensionRouteHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    public HttpClient Client { get; }

    /// <summary>Every route the extension registered, with its metadata.</summary>
    public IReadOnlyList<RouteEndpoint> Routes { get; }

    private ExtensionRouteHost(WebApplication app, HttpClient client, IReadOnlyList<RouteEndpoint> routes)
    {
        _app = app;
        Client = client;
        Routes = routes;
    }

    public static async Task<ExtensionRouteHost> BootAsync(CovePrincipal? principal)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();

        var accessor = new FakePrincipalAccessor();
        accessor.Set(principal);
        builder.Services.AddSingleton<ICurrentPrincipalAccessor>(accessor);
        builder.Services.AddSingleton(new WhisparrClient(new HttpClient(FakeHttpMessageHandler.Json("{}"))));
        // The discovery routes take the direct-metadata clients as endpoint args, so the route table cannot
        // register without them in the container (they are real DI services in the host).
        builder.Services.AddSingleton(new StashDbGraphQlClient(new HttpClient(FakeHttpMessageHandler.Json("{}"))));
        builder.Services.AddSingleton(new TpdbClient(new HttpClient(FakeHttpMessageHandler.Json("{}"))));
        builder.Services.AddRouting();

        var ext = new Ext();
        ((IStatefulExtension)ext).SetStore(new FakeStore());

        var app = builder.Build();
        // Capture the host scope factory the /webhook route reads (its coordinator opens a scope); the
        // guarded reconcile loop won't fault the boot even without an IJobService registered.
        await ext.InitializeAsync(app.Services);
        ext.MapEndpoints(app);
        await app.StartAsync();

        return new ExtensionRouteHost(app, app.GetTestClient(), Materialize(app));
    }

    /// <summary>Reads the endpoints the extension's data source produced, conventions already applied.</summary>
    private static List<RouteEndpoint> Materialize(WebApplication app)
        => ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .ToList();

    /// <summary>The route's method + pattern, e.g. <c>GET /api/extensions/…/status</c>.</summary>
    public static string Describe(RouteEndpoint route)
    {
        var methods = route.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods;
        var method = methods is { Count: > 0 } ? string.Join("|", methods) : "ANY";
        return $"{method} {route.RoutePattern.RawText}";
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
