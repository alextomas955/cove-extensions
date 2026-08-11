using System.Net;
using System.Text;
using Cove.Core.Auth;
using Cove.Core.Interfaces;
using Cove.Plugins;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Api;

/// <summary>
/// The two endpoints whose request bodies are deliberately NOT bound by the host, driven over real HTTP
/// so the claim is about what a request does rather than about what a method returns.
/// </summary>
/// <remarks>
/// <c>/preview-sample</c> declares its body with <c>.Accepts&lt;&gt;</c> and parses it itself, because the
/// host's default minimal-API serializer carries no string-enum converter and typed binding would answer
/// 400 before the handler ran. That makes <c>.Accepts&lt;&gt;</c> look like a contradiction a later reader
/// could "fix" by binding the body — so these cases pin the property that would break if anyone did: the
/// same options reach the handler through a PascalCase envelope and through a camelCase one, a malformed
/// body comes back with the handler's OWN error code rather than a host-produced 400, and <c>/undo</c>
/// still reaches its handler on a request that carries no body at all.
/// </remarks>
[Trait("Tier", "L2")]
public sealed class PreviewSampleAcceptsTests
{
    // Transcribed by hand from the server's own sample set, not read back from the engine: the Video
    // sample's title lower-cased through a "$title" template. If the posted `case` value never reached
    // the engine the name would come back "The Example.mp4", which is what makes this a check on the
    // enum VALUE and not merely on the body having parsed.
    private const string LoweredVideoName = "\"newName\":\"the example.mp4\"";

    private const string PascalCaseEnvelope =
        """{ "Options": { "FilenameTemplate": "$title", "Case": "Lower" } }""";

    private const string CamelCaseEnvelope =
        """{ "options": { "filenameTemplate": "$title", "case": "lower" } }""";

    /// <summary>
    /// The extension mounted in an in-memory host, with the routes read back from the host's own
    /// <see cref="EndpointDataSource"/> so no path or extension id is spelled out here.
    /// </summary>
    private sealed class ApiHost : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly IReadOnlyList<string> _routes;

        public HttpClient Client { get; }

        private ApiHost(WebApplication app, IReadOnlyList<string> routes)
        {
            _app = app;
            _routes = routes;
            Client = app.GetTestClient();
        }

        /// <summary>The one mounted route ending in <paramref name="suffix"/>.</summary>
        public string Route(string suffix) =>
            _routes.Single(route => route.EndsWith(suffix, StringComparison.Ordinal));

        public static async Task<ApiHost> BootAsync(ICurrentPrincipalAccessor? principal = null)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseTestServer();

            // A principal holding every renamer permission by default: one host serves both endpoints,
            // and the permission gate is not what most of these cases are about. The /undo case supplies
            // its own, because the handler's permission answer is the one answer it can give without a
            // database.
            builder.Services.AddSingleton(principal ?? FakePrincipalAccessor.WithPermissions(
                    Permissions.VideosRead, Permissions.ImagesRead, Permissions.AudiosRead,
                    Permissions.VideosWrite, Permissions.ImagesWrite, Permissions.AudiosWrite));

            // Registration-time binding only, and never dereferenced: minimal-API binding treats an
            // unregistered complex type as a second body parameter and throws while the route is being
            // mapped. Keeping these null is what keeps this class off a database and therefore on the
            // CI leg that has no cove checkout.
            builder.Services.AddSingleton<DbContext>(_ => null!);
            builder.Services.AddSingleton<IJobService>(_ => null!);
            builder.Services.AddRouting();

            var extension = RenamerFixture.Create();
            ((IStatefulExtension)extension).SetStore(new FakeStore());

            var app = builder.Build();
            extension.MapEndpoints(app);

            // Route registrations are not folded into the DI EndpointDataSource until routing is built
            // at start, so without this the route lookup below finds nothing to fail on.
            await app.StartAsync();

            var routes = app.Services
                .GetRequiredService<EndpointDataSource>()
                .Endpoints.OfType<RouteEndpoint>()
                .Select(endpoint => endpoint.RoutePattern.RawText!)
                .ToList();
            Assert.NotEmpty(routes);

            return new ApiHost(app, routes);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.DisposeAsync();
        }
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    [Theory]
    [InlineData(PascalCaseEnvelope)]
    [InlineData(CamelCaseEnvelope)]
    public async Task PreviewSample_AcceptsEitherCasingOfTheDeclaredBody(string body)
    {
        // RenamerOptions.JsonOptions matches property names case-insensitively AND matches an enum name
        // case-insensitively, so the envelope and the enum value may each arrive in either spelling.
        // Typed binding under the host's converter-less options would reject the enum outright.
        await using var host = await ApiHost.BootAsync();

        var response = await host.Client.PostAsync(host.Route("/preview-sample"), Json(body));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(LoweredVideoName, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PreviewSample_BothCasingsParseToTheSameOptions()
    {
        // The theory above proves each casing works; this proves they agree. Two responses that differ
        // would mean one casing silently lost a member and fell back to a default.
        await using var host = await ApiHost.BootAsync();
        var route = host.Route("/preview-sample");

        var pascal = await host.Client.PostAsync(route, Json(PascalCaseEnvelope));
        var camel = await host.Client.PostAsync(route, Json(CamelCaseEnvelope));

        Assert.Equal(
            await pascal.Content.ReadAsStringAsync(),
            await camel.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task PreviewSample_NoBodyAtAll_IsTheHandlersOwnFallback_NotAHostRejection()
    {
        // No body and no content type — the request `.Accepts<>` describes is absent entirely. A bound
        // body would make this the host's problem; here it reaches the handler, which answers with its
        // documented safe defaults.
        await using var host = await ApiHost.BootAsync();

        var response = await host.Client.PostAsync(host.Route("/preview-sample"), content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"sampleLabel\":", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PreviewSample_MalformedBody_ReturnsTheHandlersOwnErrorCode()
    {
        // The distinction that matters: a host-produced 400 raised during binding carries a
        // ProblemDetails payload and none of the extension's vocabulary. This code can only come from
        // the handler, so its presence proves the request got that far.
        await using var host = await ApiHost.BootAsync();

        var response = await host.Client.PostAsync(host.Route("/preview-sample"), Json("{ not valid json "));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(
            "\"code\":\"INVALID_BODY\"", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Undo_WithNoBody_ReachesTheHandler()
    {
        // The other endpoint the untyped-binding rule covers, and the one where it means only "do not
        // invent a body to type": /undo operates on the last batch and declares no body, so nothing is
        // there to bind. The proof it ran is its OWN 403 — the host's [RequiresPermission] filter is
        // inert on a minimal-API route, so nothing else produces that code, and a body-binding failure
        // would be a host 400 the handler never saw. The permission answer is also the only answer this
        // host can obtain: /undo reads the journal out of the database, and keeping this class off a
        // database is what keeps it on the CI leg that has no cove checkout.
        await using var host = await ApiHost.BootAsync(FakePrincipalAccessor.None());

        var response = await host.Client.PostAsync(host.Route("/undo"), content: null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
