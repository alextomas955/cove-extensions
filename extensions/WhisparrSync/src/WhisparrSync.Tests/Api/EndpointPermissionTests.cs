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
        "GET /api/extensions/com.alextomas955.whisparrsync/settings",
        "POST /api/extensions/com.alextomas955.whisparrsync/connection/test",
        "PUT /api/extensions/com.alextomas955.whisparrsync/settings",
    ];

    [Fact]
    public async Task EveryMountedRouteDeclaresAPermissionGate()
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

        var ungated = routes
            .Where(route => route.Metadata.GetMetadata<CovePermissionRequirementMetadata>() is null)
            .Select(Describe)
            .ToList();

        Assert.True(
            ungated.Count == 0,
            "these mounted routes declare no Cove permission requirement, so the host admits them "
                + "anonymously and logs a warning rather than refusing: " + string.Join(", ", ungated));

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
            credentials,
            TimeProvider.System,
            TestCt);

        Assert.Equal(403, StatusOf(refused));
        Assert.Empty(credentials.Writes);
        Assert.Equal(0, store.SetCallCount);

        var answered = await global::WhisparrSync.WhisparrSync.SaveSettingsAsync(
            save, Configure(), options, credentials, TimeProvider.System, TestCt);

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
