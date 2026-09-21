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

// The host's [RequiresPermission] filter is MVC-only and does nothing for a minimal-API extension
// endpoint, so each handler enforces its gate itself through ICurrentPrincipalAccessor. Each deny
// case is paired with a caller who holds the gate, because a 403 alone could mean the handler is
// broken for everyone.
public sealed class EndpointPermissionTests
{
    // Transcribed by hand. A set computed from the registration would agree with it whatever it says.
    private static readonly string[] MountedRoutes =
    [
        "GET /api/extensions/com.alextomas955.whisparrsync/addressing/folder-mappings",
        "GET /api/extensions/com.alextomas955.whisparrsync/host-configuration",
        "GET /api/extensions/com.alextomas955.whisparrsync/callback/status",
        "GET /api/extensions/com.alextomas955.whisparrsync/entity/{kind}/{coveId}/monitoring",
        "GET /api/extensions/com.alextomas955.whisparrsync/entity/{kind}/{coveId}/missing",
        "GET /api/extensions/com.alextomas955.whisparrsync/entity/{kind}/{coveId}/missing/count",
        "GET /api/extensions/com.alextomas955.whisparrsync/entity/{kind}/{coveId}/missing/facet/{facetKey}",
        "GET /api/extensions/com.alextomas955.whisparrsync/import/banner",
        "GET /api/extensions/com.alextomas955.whisparrsync/job-status/{jobId}",
        "GET /api/extensions/com.alextomas955.whisparrsync/scene/{coveId}",
        "GET /api/extensions/com.alextomas955.whisparrsync/settings",
        "GET /api/extensions/com.alextomas955.whisparrsync/sync/preview",
        "POST /api/extensions/com.alextomas955.whisparrsync/callback",
        "POST /api/extensions/com.alextomas955.whisparrsync/callback/register",
        "POST /api/extensions/com.alextomas955.whisparrsync/connection/test",
        "POST /api/extensions/com.alextomas955.whisparrsync/entities/bulk-monitor",
        "POST /api/extensions/com.alextomas955.whisparrsync/entity/{kind}/{coveId}/add-all-missing",
        "POST /api/extensions/com.alextomas955.whisparrsync/entity/{kind}/{coveId}/missing/bulk-monitor",
        "POST /api/extensions/com.alextomas955.whisparrsync/entity/{kind}/{coveId}/missing/monitor-all",
        "POST /api/extensions/com.alextomas955.whisparrsync/entity/{kind}/{coveId}/missing/{providerSceneId}/monitor",
        "POST /api/extensions/com.alextomas955.whisparrsync/entity/{kind}/{coveId}/missing/{providerSceneId}/search",
        "POST /api/extensions/com.alextomas955.whisparrsync/entity/{kind}/{coveId}/monitor",
        "POST /api/extensions/com.alextomas955.whisparrsync/entity/{kind}/{coveId}/reflect-owned",
        "POST /api/extensions/com.alextomas955.whisparrsync/entity/{kind}/{coveId}/search-all-monitored",
        "POST /api/extensions/com.alextomas955.whisparrsync/entity/{kind}/{coveId}/scope",
        "POST /api/extensions/com.alextomas955.whisparrsync/entity/{kind}/{coveId}/unmonitor",
        "POST /api/extensions/com.alextomas955.whisparrsync/library/{kind}/status",
        "POST /api/extensions/com.alextomas955.whisparrsync/scene/{coveId}/add",
        "POST /api/extensions/com.alextomas955.whisparrsync/scene/{coveId}/exclude",
        "POST /api/extensions/com.alextomas955.whisparrsync/scene/{coveId}/monitor",
        "POST /api/extensions/com.alextomas955.whisparrsync/scene/{coveId}/remove-exclusion",
        "POST /api/extensions/com.alextomas955.whisparrsync/scene/{coveId}/search",
        "POST /api/extensions/com.alextomas955.whisparrsync/scene/{coveId}/unmonitor",
        "POST /api/extensions/com.alextomas955.whisparrsync/scenes/batch",
        "POST /api/extensions/com.alextomas955.whisparrsync/sync/preview",
        "POST /api/extensions/com.alextomas955.whisparrsync/sync/run",
        "PUT /api/extensions/com.alextomas955.whisparrsync/addressing/folder-mappings",
        "PUT /api/extensions/com.alextomas955.whisparrsync/settings",
    ];

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
        Assert.Equal(MountedRoutes.Order(), routes.Select(Describe).Order());

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
        var client = new RecordingWhisparrClient(RecordingWhisparrClient.Json(200, "[]"));

        var refused = await global::WhisparrSync.WhisparrSync.SceneDetailAsync(
            1, FakePrincipalAccessor.None(), options, credentials, client, identities,
            NullLogger.Instance, TestCt);

        Assert.Equal(403, StatusOf(refused));
        Assert.Empty(store.GetKeys);
        Assert.Empty(credentials.Reads);
        Assert.Empty(identities.Resolved);

        var answered = await global::WhisparrSync.WhisparrSync.SceneDetailAsync(
            1,
            FakePrincipalAccessor.WithPermissions(Permissions.VideosRead),
            options,
            credentials,
            client,
            identities,
            NullLogger.Instance,
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
        var client = new RecordingWhisparrClient(RecordingWhisparrClient.Json(200, "[]"));

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
                options,
                credentials,
                client,
                identities,
                new UnreachableScopes(),
                NullLogger.Instance,
                TestCt),
            "monitor" => await global::WhisparrSync.WhisparrSync.MonitorSceneAsync(
                1, principal, options, credentials, client, identities, NullLogger.Instance, TestCt),
            "unmonitor" => await global::WhisparrSync.WhisparrSync.UnmonitorSceneAsync(
                1, principal, options, credentials, client, identities, NullLogger.Instance, TestCt),
            "exclude" => await global::WhisparrSync.WhisparrSync.ExcludeSceneAsync(
                1, principal, options, credentials, client, identities, NullLogger.Instance, TestCt),
            "remove-exclusion" => await global::WhisparrSync.WhisparrSync.RemoveSceneExclusionAsync(
                1, principal, options, credentials, client, identities, NullLogger.Instance, TestCt),
            "search" => await global::WhisparrSync.WhisparrSync.SearchSceneNowAsync(
                1, principal, options, credentials, client, identities, NullLogger.Instance, TestCt),
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
