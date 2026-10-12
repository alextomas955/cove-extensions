using Cove.Core.Auth;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using WhisparrSync.Contracts;
using WhisparrSync.Tests.TestSupport;
using static Cove.Extensions.Shared.Testing.HttpResultUnwrap;

namespace WhisparrSync.Tests.Api;

// The container an extension runs in is built by the host and cannot be reproduced here, so these
// cases fix what the reading means, not what the live container answers.
public sealed class HostConfigurationProbeTests
{
    [Fact]
    public async Task AContainerOfferingNeitherHostServiceStillLoadsAndReportsBothUnobtainable()
    {
        var extension = WhisparrSyncFixture.Create();
        await using var services = Container(new ServiceCollection());

        await extension.InitializeAsync(services, TestContext.Current.CancellationToken);

        var probe = ProbeOf(extension);
        Assert.False(probe.ScanServiceResolved);
        Assert.False(probe.MetadataServerServiceResolved);
    }

    // The control for the case above. Without it both members could be fixed at false and every
    // assertion on them would still pass.
    [Fact]
    public async Task AContainerOfferingBothHostServicesReportsBoth()
    {
        var extension = WhisparrSyncFixture.Create();
        await using var services = Container(new ServiceCollection()
            .AddScoped<IScanService, UnusedScanService>()
            .AddTransient<IMetadataServerService, UnusedMetadataServerService>());

        await extension.InitializeAsync(services, TestContext.Current.CancellationToken);

        var probe = ProbeOf(extension);
        Assert.True(probe.ScanServiceResolved);
        Assert.True(probe.MetadataServerServiceResolved);
    }

    // The host's container copies a descriptor across without everything the type needs, so an
    // entry can exist and throw on resolve. An extension that let that escape its load-time
    // reading would be disabled by the host instead of reporting it.
    [Fact]
    public async Task AHostServiceThatCannotBeProducedReadsAsUnobtainable()
    {
        var extension = WhisparrSyncFixture.Create();
        await using var services = Container(new ServiceCollection()
            .AddScoped<IScanService>(
                _ => throw new InvalidOperationException("this registration cannot be produced")));

        await extension.InitializeAsync(services, TestContext.Current.CancellationToken);

        Assert.False(ProbeOf(extension).ScanServiceResolved);
    }

    // Built the way the host builds an extension's container, so a scoped resolve taken off the
    // root throws here as it would there.
    private static ServiceProvider Container(IServiceCollection services)
        => services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

    private static HostConfigurationView ProbeOf(global::WhisparrSync.WhisparrSync extension)
        => Assert.IsType<HostConfigurationView>(
            Assert.IsAssignableFrom<IValueHttpResult>(
                Unwrap(extension.HostConfiguration(
                    FakePrincipalAccessor.WithPermissions(Permissions.VideosRead)))).Value);

    private sealed class UnusedScanService : IScanService
    {
        public string StartScan(ScanOperationOptions? options = null) => throw new NotSupportedException();

        public Task<int> ImportDownloadedVideoAsync(string path, int? videoId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<int> ImportDownloadedImageAsync(string path, int? imageId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<int> ImportDownloadedGalleryAsync(string path, int? galleryId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<int> ImportDownloadedAudioAsync(string path, int? audioId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<int> ImportDownloadedTextAsync(string path, int? textDocumentId, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private sealed class UnusedMetadataServerService : IMetadataServerService
    {
        public Task<bool> MergeVideoAsync(
            Video video,
            string endpoint,
            string videoId,
            MetadataServerVideoImportRequestDto? importConfig,
            CancellationToken ct)
            => throw new NotSupportedException();
    }
}
