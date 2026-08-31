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

/// <summary>
/// What the probe reports about the host services this extension's container may or may not be able
/// to produce, and that neither of them is required for the extension to load.
/// </summary>
/// <remarks>
/// The container an extension really runs in is built by the host and cannot be reproduced here, so
/// what this suite fixes is what the reading MEANS and that a container offering neither service
/// still loads. What the live container answers is the containerized spec's subject.
/// </remarks>
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

    /// <summary>A container that does offer both reports both, from the same reading.</summary>
    /// <remarks>
    /// The discriminating control for the case above: without it both members could equally be fixed
    /// at false and every assertion on them would agree with that forever. The doubles stand in for
    /// the host's registrations, so what is fixed is the reading rather than whether Cove's own
    /// services resolve.
    /// </remarks>
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

    /// <summary>A registration present but unproducible reads as unobtainable rather than throwing.</summary>
    /// <remarks>
    /// The shape the host's own container can produce: it copies a descriptor across without
    /// necessarily copying everything the type needs, so the entry exists and resolving it throws. An
    /// extension that let that escape its load-time reading would be disabled by the host instead of
    /// reporting it.
    /// </remarks>
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

    /// <summary>
    /// A container built the way the host builds an extension's, so a scoped resolve taken off its
    /// root throws here exactly as it would there.
    /// </summary>
    private static ServiceProvider Container(IServiceCollection services)
        => services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

    private static HostConfigurationView ProbeOf(global::WhisparrSync.WhisparrSync extension)
        => Assert.IsType<HostConfigurationView>(
            Assert.IsAssignableFrom<IValueHttpResult>(
                Unwrap(extension.HostConfiguration(
                    FakePrincipalAccessor.WithPermissions(Permissions.VideosRead)))).Value);

    /// <summary>Resolvable, and nothing here calls a member of it.</summary>
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

    /// <inheritdoc cref="UnusedScanService"/>
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
