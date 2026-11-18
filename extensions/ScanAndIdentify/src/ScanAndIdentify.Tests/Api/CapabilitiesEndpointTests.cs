using Cove.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using ScanAndIdentify.Tests.TestSupport;

namespace ScanAndIdentify.Tests.Api;

public sealed class CapabilitiesEndpointTests
{
    // Deliberately not the manifest's minCoveVersion: the view reports the host that loaded the
    // extension, which moves independently of the floor it declares.
    private const string RunningHostVersion = "2.4.9-dev.7";

    [Fact]
    public async Task Capabilities_ReportsTheHostVersionSuppliedThroughExtensionContext()
    {
        var extension = await ScanAndIdentifyFixture.CreateInitializedAsync(
            RunningHostVersion, Provider(jobService: true, scanService: true));

        var view = extension.Capabilities();

        Assert.Equal(RunningHostVersion, view.CoveVersion);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task Capabilities_ReportsEachHostServiceSeparately(bool jobService, bool scanService)
    {
        var extension = await ScanAndIdentifyFixture.CreateInitializedAsync(
            RunningHostVersion, Provider(jobService, scanService));

        var view = extension.Capabilities();

        Assert.Equal(jobService, view.JobService);
        Assert.Equal(scanService, view.ScanService);
    }

    private static ServiceProvider Provider(bool jobService, bool scanService)
    {
        var services = new ServiceCollection();
        if (jobService)
        {
            services.AddSingleton<IJobService, StubJobService>();
        }

        // Scoped and scope-validating, the way Cove registers it and builds its container. A singleton
        // stub here would resolve from the root provider and hide that the host's does not.
        if (scanService)
        {
            services.AddScoped<IScanService, StubScanService>();
        }

        return services.BuildServiceProvider(validateScopes: true);
    }
}
