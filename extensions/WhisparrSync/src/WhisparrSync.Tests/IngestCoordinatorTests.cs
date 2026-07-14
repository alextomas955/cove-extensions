using System.IO;
using Cove.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using WhisparrSync.Ingest;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests;

/// <summary>
/// IMPT-01 / IMPT-05 / path-containment for <see cref="IngestCoordinator"/>: it fail-closes on a path
/// outside every known Whisparr root (no ingest, no scan), falls back to a scoped <c>StartScan</c> on a
/// failed or kind-unresolvable in-root ingest (never silent, never thrown), and otherwise resolves the
/// scoped <see cref="IScanService"/> and routes each kind to its <c>ImportDownloaded*</c> method.
/// </summary>
public sealed class IngestCoordinatorTests
{
    private const string Root = "/data/media";
    private const string VideoPath = "/data/media/Scene/Scene.mkv";

    private static (IngestCoordinator Coordinator, FakeScanService Scan) New(params string[] roots)
    {
        roots = roots.Length == 0 ? [Root] : roots;
        var scan = new FakeScanService();
        var services = new ServiceCollection();
        services.AddScoped<IScanService>(_ => scan);
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var coordinator = new IngestCoordinator(
            scopeFactory, _ => ValueTask.FromResult<IReadOnlyList<string>>(roots));
        return (coordinator, scan);
    }

    [Fact]
    public async Task InRoot_FreshVideo_Imported_WithNullId()
    {
        var (coordinator, scan) = New();

        var outcome = await coordinator.IngestAsync(VideoPath, existingId: null, default);

        Assert.Equal(IngestResult.Imported, outcome.Result);
        Assert.Equal(IngestKind.Video, outcome.Kind);
        var call = Assert.Single(scan.Imports);
        Assert.Equal(VideoPath, call.Path);
        Assert.Null(call.EntityId);
        Assert.Empty(scan.Scans);
    }

    [Fact]
    public async Task InRoot_Upgrade_PassesExistingId_UpgradesInPlace()
    {
        var (coordinator, scan) = New();

        var outcome = await coordinator.IngestAsync(VideoPath, existingId: 55, default);

        Assert.Equal(IngestResult.Imported, outcome.Result);
        Assert.Equal(55, outcome.CoveEntityId);
        Assert.Equal(55, Assert.Single(scan.Imports).EntityId);
    }

    [Fact]
    public async Task OutOfRoot_Rejected_FailClosed_NoIngest_NoScan_Flagged()
    {
        var (coordinator, scan) = New(Root);

        var outcome = await coordinator.IngestAsync("/somewhere/else/Scene.mkv", existingId: null, default);

        Assert.Equal(IngestResult.Flagged, outcome.Result);
        Assert.Contains("outside", outcome.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(scan.Imports);
        Assert.Empty(scan.Scans);
    }

    [Fact]
    public async Task SiblingRootPrefix_IsNotContained_Rejected()
    {
        // Segment-boundary containment: /data/media-evil must NOT be treated as inside /data/media.
        var (coordinator, scan) = New(Root);

        var outcome = await coordinator.IngestAsync("/data/media-evil/Scene.mkv", existingId: null, default);

        Assert.Equal(IngestResult.Flagged, outcome.Result);
        Assert.Empty(scan.Imports);
        Assert.Empty(scan.Scans);
    }

    [Fact]
    public async Task EmptyRoots_Rejected_FailClosed_NoIngest_NoScan_Flagged()
    {
        // The root set is unavailable (e.g. Whisparr unreachable) → the guard never ingests an unvalidated path.
        var scan = new FakeScanService();
        var services = new ServiceCollection();
        services.AddScoped<IScanService>(_ => scan);
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var coordinator = new IngestCoordinator(
            scopeFactory, _ => ValueTask.FromResult<IReadOnlyList<string>>([]));

        var outcome = await coordinator.IngestAsync(VideoPath, existingId: null, default);

        Assert.Equal(IngestResult.Flagged, outcome.Result);
        Assert.Empty(scan.Imports);
        Assert.Empty(scan.Scans);
    }

    [Fact]
    public async Task InRoot_ImportThrows_FallsBackToScopedScan_Flagged()
    {
        var (coordinator, scan) = New();
        scan.ThrowOnNextImport(new FileNotFoundException("gone"));

        var outcome = await coordinator.IngestAsync(VideoPath, existingId: null, default);

        Assert.Equal(IngestResult.Flagged, outcome.Result);
        Assert.Empty(scan.Imports); // the throwing import recorded nothing
        var scan1 = Assert.Single(scan.Scans);
        Assert.Contains(Path.GetDirectoryName(VideoPath)!, scan1.Paths!);
    }

    [Fact]
    public async Task InRoot_UnresolvedKind_FallsBackToScopedScan_Flagged()
    {
        var (coordinator, scan) = New();

        var outcome = await coordinator.IngestAsync("/data/media/Scene/notes.xyz", existingId: null, default);

        Assert.Equal(IngestResult.Flagged, outcome.Result);
        Assert.Empty(scan.Imports);
        Assert.Single(scan.Scans);
    }

    // The enum is internal, so a public [Theory] cannot take it directly (CS0051); use a path per kind.
    [Theory]
    [InlineData("/data/media/a.mkv", "Video")]
    [InlineData("/data/media/a.jpg", "Image")]
    [InlineData("/data/media/a.cbz", "Gallery")]
    [InlineData("/data/media/a.mp3", "Audio")]
    [InlineData("/data/media/a.txt", "Text")]
    public async Task EachExtension_RoutesToTheMatchingMethod(string path, string expectedMethod)
    {
        var (coordinator, scan) = New();

        await coordinator.IngestAsync(path, existingId: null, default);

        Assert.Equal(expectedMethod, Assert.Single(scan.Imports).Kind);
    }
}
