using System.Net;
using System.Net.Http.Json;
using Cove.Core.Auth;
using Cove.Plugins;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using WhisparrSync.Connection;
using WhisparrSync.Contracts;
using WhisparrSync.Library;
using WhisparrSync.Options;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;
using static Cove.Extensions.Shared.Testing.HttpResultUnwrap;

namespace WhisparrSync.Tests.Api;

// The host's [RequiresPermission] filter is MVC-only and does nothing for a minimal-API endpoint,
// so each handler enforces its gate through ICurrentPrincipalAccessor. Each deny case is paired
// with a caller who holds the gate: a 403 alone could mean the handler is broken for everyone.
public sealed class EndpointPermissionTests
{
    // The one route that answers a caller holding no Cove permission. A single value rather than a
    // list, so a second anonymous route fails here instead of being added beside this one.
    private const string AnonymousRoute = "POST /api/extensions/com.alextomas955.whisparrsync/callback";

    [Fact]
    public async Task EveryMountedRouteDeclaresItsAccessTier()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddWhisparrSyncBindingServices();
        builder.Services.AddRouting();

        await using var app = builder.Build();
        WhisparrSyncFixture.Create().MapEndpoints(app);

        // Route registrations are not folded into the DI EndpointDataSource until routing
        // middleware is built at start. Without this the data source is empty and every assertion
        // below holds over nothing.
        await app.StartAsync(TestContext.Current.CancellationToken);

        var routes = app.Services
            .GetRequiredService<EndpointDataSource>()
            .Endpoints.OfType<RouteEndpoint>()
            .ToList();

        Assert.NotEmpty(routes);

        // A route declaring neither convention is the failure. The host admits an endpoint that
        // declares nothing anonymously, so the deliberately anonymous route has to say so.
        var undeclared = routes
            .Where(route =>
                route.Metadata.GetMetadata<CovePermissionRequirementMetadata>() is null
                && route.Metadata.GetMetadata<CoveAllowAnonymousMetadata>() is null)
            .Select(Describe)
            .ToList();

        Assert.True(
            undeclared.Count == 0,
            "these mounted routes declare neither a Cove permission requirement nor the explicit "
                + "anonymous convention, so the host admits them anonymously and logs a warning rather "
                + "than refusing: " + string.Join(", ", undeclared));

        await app.StopAsync(TestContext.Current.CancellationToken);
    }

    // The tier assertion above accepts an anonymous declaration, so a second anonymous route would
    // pass it. Comparing the anonymous set against one transcribed route is what catches that.
    [Fact]
    public async Task ExactlyOneMountedRouteIsAnonymousAndItIsTheCallback()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddWhisparrSyncBindingServices();
        builder.Services.AddRouting();

        await using var app = builder.Build();
        WhisparrSyncFixture.Create().MapEndpoints(app);
        await app.StartAsync(TestContext.Current.CancellationToken);

        var routes = app.Services
            .GetRequiredService<EndpointDataSource>()
            .Endpoints.OfType<RouteEndpoint>()
            .ToList();

        Assert.NotEmpty(routes);

        var anonymous = routes
            .Where(route => route.Metadata.GetMetadata<CoveAllowAnonymousMetadata>() is not null)
            .Select(Describe)
            .Order()
            .ToList();

        Assert.Equal([AnonymousRoute], anonymous);

        // The conventions are mutually exclusive. An anonymous route also carrying a permission
        // requirement is not stricter, it is a registration the host refuses.
        var callback = routes.Single(route => Describe(route) == AnonymousRoute);
        Assert.Null(callback.Metadata.GetMetadata<CovePermissionRequirementMetadata>());

        await app.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public void TheHostConfigurationProbeRefusesACallerWithoutTheReadTier()
    {
        var extension = WhisparrSyncFixture.Create();

        Assert.Equal(403, StatusOf(extension.HostConfiguration(FakePrincipalAccessor.None())));
        Assert.NotEqual(
            403,
            StatusOf(extension.HostConfiguration(
                FakePrincipalAccessor.WithPermissions(Permissions.VideosRead))));
    }

    [Fact]
    public async Task TheSceneReadRefusesACallerWithoutTheReadTierAndResolvesNoIdentity()
    {
        var (store, options) = NewStore();
        var credentials = new RecordingCredentialPort();
        var identities = new RecordingCardIdentities();
        var client = new RecordingWhisparrV3Client(RecordingWhisparrCore.Json(200, "[]"));

        var refused = await global::WhisparrSync.WhisparrSync.SceneDetailAsync(
            1,
            FakePrincipalAccessor.None(),
            new WhisparrAccess(
                options,
                credentials,
                new FixedInstanceFactory(client),
                NullLogger.Instance),
            identities,
            TestCt);

        Assert.Equal(403, StatusOf(refused));
        Assert.Empty(store.GetKeys);
        Assert.Empty(credentials.Reads);
        Assert.Empty(identities.Resolved);

        var answered = await global::WhisparrSync.WhisparrSync.SceneDetailAsync(
            1,
            FakePrincipalAccessor.WithPermissions(Permissions.VideosRead),
            new WhisparrAccess(
                options,
                credentials,
                new FixedInstanceFactory(client),
                NullLogger.Instance),
            identities,
            TestCt);

        Assert.NotEqual(403, StatusOf(answered));
        Assert.NotEmpty(store.GetKeys);
    }

    // The refused caller holds the read tier, the tier the scene read sits at, so a pass here is
    // about the configure gate and not about holding no permission at all. One case per route, so
    // the failure names the handler whose own re-check was dropped.
    [Theory]
    [InlineData("add")]
    [InlineData("monitor")]
    [InlineData("unmonitor")]
    [InlineData("exclude")]
    [InlineData("remove-exclusion")]
    [InlineData("search")]
    public async Task EachSceneWriteRefusesACallerWithoutTheConfigureTierAndReachesNothing(
        string verb)
    {
        var (store, options) = NewStore();
        var credentials = new RecordingCredentialPort();
        var identities = new RecordingCardIdentities();
        var client = new RecordingWhisparrV3Client(RecordingWhisparrCore.Json(200, "[]"));

        var refused = await SceneWriteAsync(
            verb,
            FakePrincipalAccessor.WithPermissions(Permissions.VideosRead),
            options,
            credentials,
            client,
            identities);

        Assert.Equal(403, StatusOf(refused));
        Assert.Empty(store.GetKeys);
        Assert.Empty(credentials.Reads);
        Assert.Empty(identities.Resolved);
        Assert.Empty(client.Verbs);

        var answered = await SceneWriteAsync(
            verb, Configure(), options, credentials, client, identities);

        Assert.NotEqual(403, StatusOf(answered));
        Assert.NotEmpty(store.GetKeys);
    }

    [Fact]
    public async Task TheConnectionTestRefusesACallerWithoutTheConfigureTierAndRunsNoTest()
    {
        var runner = new RecordingConnectionTestRunner();

        var refused = await global::WhisparrSync.WhisparrSync.ConnectionTestAsync(
            new ConnectionTestRequest("http://whisparr-v3:6969", "a-key"),
            FakePrincipalAccessor.WithPermissions(Permissions.VideosRead),
            runner,
            TestCt);

        Assert.Equal(403, StatusOf(refused));
        Assert.Empty(runner.Transient);
        Assert.Equal(0, runner.Stored);

        var answered = await global::WhisparrSync.WhisparrSync.ConnectionTestAsync(
            new ConnectionTestRequest("http://whisparr-v3:6969", "a-key"), Configure(), runner, TestCt);

        Assert.NotEqual(403, StatusOf(answered));
        Assert.Single(runner.Transient);
    }

    [Fact]
    public async Task TheSettingsReadRefusesACallerWithoutTheConfigureTierAndReadsNothing()
    {
        var (store, options) = NewStore();
        var credentials = new RecordingCredentialPort();

        var refused = await global::WhisparrSync.WhisparrSync.ReadSettingsAsync(
            FakePrincipalAccessor.WithPermissions(Permissions.VideosRead), options, credentials, TestCt);

        Assert.Equal(403, StatusOf(refused));
        Assert.Empty(store.GetKeys);
        Assert.Empty(credentials.Reads);

        var answered = await global::WhisparrSync.WhisparrSync.ReadSettingsAsync(
            Configure(), options, credentials, TestCt);

        Assert.NotEqual(403, StatusOf(answered));
        Assert.NotEmpty(store.GetKeys);
    }

    [Fact]
    public async Task TheSettingsWriteRefusesACallerWithoutTheConfigureTierAndWritesNothing()
    {
        var (store, options) = NewStore();
        var credentials = new RecordingCredentialPort();
        var save = new WhisparrSyncSettingsSaveRequest(
            WhisparrGeneration.V3,
            new WhisparrSyncGenerationSaveRequest("http://whisparr-v3:6969", KeyWriteSignal.Replace, "a-key"),
            null);

        var refused = await WhisparrSyncFixture.Create().SaveSettingsAsync(
            save,
            FakePrincipalAccessor.WithPermissions(Permissions.VideosRead),
            options,
            new OptionsWriteGate(),
            credentials,
            TimeProvider.System,
            TestCt);

        Assert.Equal(403, StatusOf(refused));
        Assert.Empty(credentials.Writes);
        Assert.Equal(0, store.SetCallCount);

        var answered = await WhisparrSyncFixture.Create().SaveSettingsAsync(
            save, Configure(), options, new OptionsWriteGate(), credentials, TimeProvider.System, TestCt);

        Assert.NotEqual(403, StatusOf(answered));
        Assert.Contains(
            credentials.Writes,
            write => write.Generation == WhisparrGeneration.V3 && write.ApiKey == "a-key");
    }

    [Fact]
    public void ACallerWithNoPrincipalAtAllIsRefused()
    {
        var extension = WhisparrSyncFixture.Create();

        Assert.Equal(403, StatusOf(extension.HostConfiguration(FakePrincipalAccessor.NullPrincipal())));
    }

    private static async Task<IResult> SceneWriteAsync(
        string verb,
        ICurrentPrincipalAccessor principal,
        OptionsStore options,
        ICredentialPort credentials,
        IWhisparrClient client,
        ILibraryCardIdentityPort identities)
        => verb switch
        {
            "add" => await global::WhisparrSync.WhisparrSync.AddSceneAsync(
                1,
                principal,
                new WhisparrAccess(
                    options,
                    credentials,
                    new FixedInstanceFactory(client),
                    NullLogger.Instance),
                identities,
                new UnreachableScopes(),
                TestCt),
            "monitor" => await global::WhisparrSync.WhisparrSync.MonitorSceneAsync(
                1,
                principal,
                new WhisparrAccess(
                    options,
                    credentials,
                    new FixedInstanceFactory(client),
                    NullLogger.Instance),
                identities,
                TestCt),
            "unmonitor" => await global::WhisparrSync.WhisparrSync.UnmonitorSceneAsync(
                1,
                principal,
                new WhisparrAccess(
                    options,
                    credentials,
                    new FixedInstanceFactory(client),
                    NullLogger.Instance),
                identities,
                TestCt),
            "exclude" => await global::WhisparrSync.WhisparrSync.ExcludeSceneAsync(
                1,
                principal,
                new WhisparrAccess(
                    options,
                    credentials,
                    new FixedInstanceFactory(client),
                    NullLogger.Instance),
                identities,
                TestCt),
            "remove-exclusion" => await global::WhisparrSync.WhisparrSync.RemoveSceneExclusionAsync(
                1,
                principal,
                new WhisparrAccess(
                    options,
                    credentials,
                    new FixedInstanceFactory(client),
                    NullLogger.Instance),
                identities,
                TestCt),
            "search" => await global::WhisparrSync.WhisparrSync.SearchSceneNowAsync(
                1,
                principal,
                new WhisparrAccess(
                    options,
                    credentials,
                    new FixedInstanceFactory(client),
                    NullLogger.Instance),
                identities,
                TestCt),
            _ => throw new ArgumentOutOfRangeException(nameof(verb)),
        };

    private sealed class UnreachableScopes : IServiceScopeFactory
    {
        public IServiceScope CreateScope()
            => throw new InvalidOperationException(
                "A refused caller must reach no scope, and therefore no library read.");
    }

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    private static FakePrincipalAccessor Configure()
        => FakePrincipalAccessor.WithPermissions(Permissions.ExtensionsConfigure);

    private sealed class RecordingCardIdentities : ILibraryCardIdentityPort
    {
        public List<int> Resolved { get; } = [];

        public Task<IReadOnlyList<LibraryCardIdentity>> ResolveAsync(
            IReadOnlyList<int> coveIds, WhisparrGeneration generation, CancellationToken ct)
        {
            Resolved.AddRange(coveIds);
            return Task.FromResult<IReadOnlyList<LibraryCardIdentity>>([]);
        }

        public Task<SceneCardIdentity> ResolveOneAsync(
            int coveId, WhisparrGeneration generation, CancellationToken ct)
        {
            Resolved.Add(coveId);
            return Task.FromResult(SceneCardIdentity.Unmatched);
        }
    }

    // Every mounted route is driven, so a route added later is refused here rather than waiting for
    // someone to write a case for it. The anonymous one is left out: the case above pins it as the
    // only route authenticated by this extension's own secret. One literal serves every id, which
    // each route answers for whether or not it names anything.
    [Fact]
    public async Task EveryMountedRouteRefusesAnAnonymousCallerAndReachesNothing()
    {
        await using var host = await MonitorHost.CreateAsync(
            principal: FakePrincipalAccessor.None());

        var admitted = new List<string>();
        foreach (var (method, pattern) in host.MountedRoutes)
        {
            if ($"{method} {pattern}" == AnonymousRoute)
            {
                continue;
            }

            using var request = new HttpRequestMessage(new HttpMethod(method), Filled(pattern));
            if (method is "POST" or "PUT")
            {
                request.Content = JsonContent.Create(new { entityType = "studios", entityIds = new[] { 1 } });
            }

            var response = await host.Http.SendAsync(request, TestCt);
            if (response.StatusCode != HttpStatusCode.Forbidden)
            {
                admitted.Add($"{method} {pattern} answered {(int)response.StatusCode}");
            }
        }

        Assert.NotEmpty(host.MountedRoutes);
        Assert.Empty(admitted);
        Assert.Empty(host.Client.Verbs);
        Assert.Empty(host.Jobs.Enqueued);
    }

    private static string Filled(string pattern)
        => string.Join(
            '/',
            pattern.Split('/').Select(segment => segment.StartsWith('{') ? "1" : segment));

    private static (FakeStore Store, OptionsStore Options) NewStore()
    {
        var store = new FakeStore();
        return (store, new OptionsStore(store));
    }

    private static string Describe(RouteEndpoint route)
    {
        var methods = route.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [];
        return $"{string.Join('|', methods)} /{route.RoutePattern.RawText?.TrimStart('/')}";
    }

    private static int StatusOf(IResult result)
        => Assert.IsAssignableFrom<IStatusCodeHttpResult>(Unwrap(result)).StatusCode ?? 0;
}
