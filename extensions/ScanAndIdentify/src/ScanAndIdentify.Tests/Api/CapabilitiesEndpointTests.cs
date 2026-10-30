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
        Assert.NotEqual(ScanAndIdentifyFixture.Manifest.MinCoveVersion, view.CoveVersion);
    }

    [Fact]
    public async Task Capabilities_ReportsEachServiceSeparately_WhenOnlyTheJobServiceIsRegistered()
    {
        var extension = await ScanAndIdentifyFixture.CreateInitializedAsync(
            RunningHostVersion, Provider(jobService: true, scanService: false));

        var view = extension.Capabilities();

        Assert.True(view.JobService);
        Assert.False(view.ScanService);
    }

    [Fact]
    public async Task Capabilities_ReportsBothAvailable_WhenTheHostRegistersBoth()
    {
        var extension = await ScanAndIdentifyFixture.CreateInitializedAsync(
            RunningHostVersion, Provider(jobService: true, scanService: true));

        var view = extension.Capabilities();

        Assert.True(view.JobService);
        Assert.True(view.ScanService);
    }

    private static ServiceProvider Provider(bool jobService, bool scanService)
    {
        var services = new ServiceCollection();
        if (jobService)
        {
            services.AddSingleton<IJobService, StubJobService>();
        }

        if (scanService)
        {
            services.AddSingleton<IScanService, StubScanService>();
        }

        return services.BuildServiceProvider();
    }
}
