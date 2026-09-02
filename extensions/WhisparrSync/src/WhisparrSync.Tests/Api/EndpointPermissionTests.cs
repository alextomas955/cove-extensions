using Cove.Core.Auth;
using Cove.Plugins;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using WhisparrSync.Contracts;
using WhisparrSync.Options;
using WhisparrSync.Tests.TestSupport;
using static Cove.Extensions.Shared.Testing.HttpResultUnwrap;

namespace WhisparrSync.Tests.Api;

/// <summary>
/// Security-critical: the host's <c>[RequiresPermission]</c> filter is MVC-only and does NOTHING for a
/// minimal-API extension endpoint, so each handler enforces its gate itself through
/// <see cref="ICurrentPrincipalAccessor"/>.
/// </summary>
/// <remarks>
/// Two halves that answer different questions. The first walks the mounted route table and asserts
/// every endpoint DECLARES a gate, which is what a route added later without one fails. The rest drive
/// each handler with a principal that lacks the gate and assert both the 403 and — the part that
/// matters — that the recording collaborators were never called, so the deny path did nothing rather
/// than merely answered.
/// <para>
/// Each deny path is paired with a caller who does hold the gate. Without that control a 403 could
/// equally mean the handler is broken for everyone.
/// </para>
/// </remarks>
public sealed class EndpointPermissionTests
{
    /// <summary>
    /// Every route this extension mounts, transcribed by hand from its own registration.
    /// </summary>
    /// <remarks>
    /// Written out rather than derived, so a route added or removed has to be stated here too. A set
    /// computed from the registration would agree with it whatever it says.
    /// </remarks>
    private static readonly string[] MountedRoutes =
    [
        "GET /api/extensions/com.alextomas955.whisparrsync/host-configuration",
        "GET /api/extensions/com.alextomas955.whisparrsync/callback/status",
        "GET /api/extensions/com.alextomas955.whisparrsync/entity/{kind}/{coveId}/monitoring",
        "GET /api/extensions/com.alextomas955.whisparrsync/import/banner",
        "GET /api/extensions/com.alextomas955.whisparrsync/settings",
        "POST /api/extensions/com.alextomas955.whisparrsync/callback",
        "POST /api/extensions/com.alextomas955.whisparrsync/callback/register",
        "POST /api/extensions/com.alextomas955.whisparrsync/connection/test",
        "POST /api/extensions/com.alextomas955.whisparrsync/entity/{kind}/{coveId}/monitor",
        "PUT /api/extensions/com.alextomas955.whisparrsync/settings",
    ];

    /// <summary>
    /// The one route this extension mounts that answers a caller holding no Cove permission.
    /// </summary>
    /// <remarks>
    /// Written out as a single value rather than a list, so a SECOND anonymous route is a failure
    /// here rather than an entry someone adds beside this one.
    /// </remarks>
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

        // MANDATORY. A WebApplication's route registrations are not folded into the DI
        // EndpointDataSource until routing middleware is built at start, so without this the data
        // source is empty and every assertion below holds over nothing.
        await app.StartAsync(TestContext.Current.CancellationToken);

        var routes = app.Services
            .GetRequiredService<EndpointDataSource>()
            .Endpoints.OfType<RouteEndpoint>()
            .ToList();

        Assert.NotEmpty(routes);
        Assert.Equal(MountedRoutes.Order(), routes.Select(Describe).Order());

        // A route declaring NEITHER convention is the failure. An endpoint that simply declares
        // nothing is admitted anonymously too, so "no permission metadata" alone cannot be the rule
        // once one route is deliberately anonymous — the anonymous one has to SAY so.
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

    /// <summary>
    /// Exactly one mounted route is anonymous, and it is the callback.
    /// </summary>
    /// <remarks>
    /// The previous assertion accepts the anonymous declaration as a tier, which on its own would let
    /// a second anonymous route in unremarked. This is what stops that: the anonymous set is compared
    /// against one transcribed route, so an addition fails here and has to be argued for.
    /// </remarks>
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

        // The conventions are mutually exclusive and the host rejects conflicting metadata, so the
        // anonymous route carrying a permission requirement too would not be a stricter route — it
        // would be a registration the host refuses.
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

        var refused = await global::WhisparrSync.WhisparrSync.SaveSettingsAsync(
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

        var answered = await global::WhisparrSync.WhisparrSync.SaveSettingsAsync(
            save, Configure(), options, new OptionsWriteGate(), credentials, TimeProvider.System, TestCt);

        Assert.NotEqual(403, StatusOf(answered));
        Assert.Contains(
            credentials.Writes,
            write => write.Generation == WhisparrGeneration.V3 && write.ApiKey == "a-key");
    }

    /// <summary>
    /// A principal that is null rather than anonymous is a different arm of the same gate.
    /// </summary>
    [Fact]
    public void ACallerWithNoPrincipalAtAllIsRefused()
    {
        var extension = WhisparrSyncFixture.Create();

        Assert.Equal(403, StatusOf(extension.HostConfiguration(FakePrincipalAccessor.NullPrincipal())));
    }

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    private static FakePrincipalAccessor Configure()
        => FakePrincipalAccessor.WithPermissions(Permissions.ExtensionsConfigure);

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
