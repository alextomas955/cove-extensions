using Cove.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using WhisparrSync.Ingest;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests;

/// <summary>
/// IMPT-01 / IMPT-03 for <see cref="IngestCoordinator"/>: it resolves the scoped <see cref="IScanService"/>
/// from a fresh scope and routes each <see cref="IngestKind"/> to the matching <c>ImportDownloaded*</c>
/// method, threading the <c>int?</c> existing id (null = fresh create, non-null = upgrade-in-place).
/// </summary>
public sealed class IngestCoordinatorTests
{
    private const string VideoPath = "/data/media/Scene/Scene.mkv";

    private static (IngestCoordinator Coordinator, FakeScanService Scan) New()
    {
        var scan = new FakeScanService();
        var services = new ServiceCollection();
        services.AddScoped<IScanService>(_ => scan);
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        return (new IngestCoordinator(scopeFactory), scan);
    }

    [Fact]
    public async Task FreshImport_PassesNullId_Creates()
    {
        var (coordinator, scan) = New();

        var coveId = await coordinator.IngestAsync(IngestKind.Video, VideoPath, existingId: null, default);

        var call = Assert.Single(scan.Imports);
        Assert.Equal("Video", call.Kind);
        Assert.Equal(VideoPath, call.Path);
        Assert.Null(call.EntityId);
        Assert.True(coveId > 0);
    }

    [Fact]
    public async Task Upgrade_PassesExistingId_UpgradesInPlace()
    {
        var (coordinator, scan) = New();

        var coveId = await coordinator.IngestAsync(IngestKind.Video, VideoPath, existingId: 55, default);

        var call = Assert.Single(scan.Imports);
        Assert.Equal(55, call.EntityId);
        Assert.Equal(55, coveId);
    }

    // The enum is internal, so a public [Theory] cannot take it directly (CS0051); pass the int and cast.
    [Theory]
    [InlineData((int)IngestKind.Video, "Video")]
    [InlineData((int)IngestKind.Image, "Image")]
    [InlineData((int)IngestKind.Gallery, "Gallery")]
    [InlineData((int)IngestKind.Audio, "Audio")]
    [InlineData((int)IngestKind.Text, "Text")]
    public async Task EachKind_RoutesToTheMatchingMethod(int kind, string expectedMethod)
    {
        var (coordinator, scan) = New();

        await coordinator.IngestAsync((IngestKind)kind, VideoPath, existingId: null, default);

        Assert.Equal(expectedMethod, Assert.Single(scan.Imports).Kind);
    }
}
