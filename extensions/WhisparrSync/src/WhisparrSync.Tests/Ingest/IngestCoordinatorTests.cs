using Cove.Core.Auth;
using Cove.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using WhisparrSync.Ingest;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.Ingest;

/// <summary>
/// The import + path-containment contract for <see cref="IngestCoordinator"/>: it fail-closes on a path
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

        var outcome = await coordinator.IngestAsync(VideoPath, existingId: null, identity: null, default);

        Assert.Equal(IngestResult.Imported, outcome.Result);
        Assert.Equal("Video", outcome.Kind);
        var call = Assert.Single(scan.Imports);
        Assert.Equal(VideoPath, call.Path);
        Assert.Null(call.EntityId);
        Assert.Empty(scan.Scans);
    }

    [Fact]
    public async Task InRoot_Upgrade_PassesExistingId_UpgradesInPlace()
    {
        var (coordinator, scan) = New();

        var outcome = await coordinator.IngestAsync(VideoPath, existingId: 55, identity: null, default);

        Assert.Equal(IngestResult.Imported, outcome.Result);
        Assert.Equal(55, outcome.CoveEntityId);
        Assert.Equal(55, Assert.Single(scan.Imports).EntityId);
    }

    [Fact]
    public async Task OutOfRoot_Rejected_FailClosed_NoIngest_NoScan_Flagged()
    {
        var (coordinator, scan) = New(Root);

        var outcome = await coordinator.IngestAsync("/somewhere/else/Scene.mkv", existingId: null, identity: null, default);

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

        var outcome = await coordinator.IngestAsync("/data/media-evil/Scene.mkv", existingId: null, identity: null, default);

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

        var outcome = await coordinator.IngestAsync(VideoPath, existingId: null, identity: null, default);

        Assert.Equal(IngestResult.Flagged, outcome.Result);
        Assert.Empty(scan.Imports);
        Assert.Empty(scan.Scans);
    }

    [Fact]
    public async Task InRoot_ImportThrows_FileNotFound_FallsBackToScopedScan_TaggedPathNotVisible()
    {
        // A FileNotFound on an IN-ROOT path is the sync-broken signal (Whisparr and Cove see different paths),
        // so it is tagged distinctly for the settings banner — not the generic ingest-failed reason.
        var (coordinator, scan) = New();
        scan.ThrowOnNextImport(new FileNotFoundException("gone"));

        var outcome = await coordinator.IngestAsync(VideoPath, existingId: null, identity: null, default);

        Assert.Equal(IngestResult.Flagged, outcome.Result);
        Assert.Equal(IngestCoordinator.PathNotVisibleReason, outcome.Reason);
        Assert.Empty(scan.Imports); // the throwing import recorded nothing
        var scan1 = Assert.Single(scan.Scans);
        Assert.Contains(Path.GetDirectoryName(VideoPath)!, scan1.Paths!);
    }

    [Fact]
    public async Task InRoot_ImportThrows_DirectoryNotFound_TaggedPathNotVisible()
    {
        // The other half of the same `catch when` — a missing directory is the same sync-broken signal as a
        // missing file (Cove can't see the Whisparr mount), so it gets the same distinct tag.
        var (coordinator, scan) = New();
        scan.ThrowOnNextImport(new DirectoryNotFoundException("no dir"));

        var outcome = await coordinator.IngestAsync(VideoPath, existingId: null, identity: null, default);

        Assert.Equal(IngestResult.Flagged, outcome.Result);
        Assert.Equal(IngestCoordinator.PathNotVisibleReason, outcome.Reason);
    }

    [Fact]
    public async Task InRoot_ImportThrows_GenericIO_FallsBackToScopedScan_NotTaggedPathNotVisible()
    {
        // A non-missing IO failure is a different problem than a path mismatch, so it keeps the generic reason
        // and does NOT trip the sync-broken banner.
        var (coordinator, scan) = New();
        scan.ThrowOnNextImport(new IOException("device busy"));

        var outcome = await coordinator.IngestAsync(VideoPath, existingId: null, identity: null, default);

        Assert.Equal(IngestResult.Flagged, outcome.Result);
        Assert.NotEqual(IngestCoordinator.PathNotVisibleReason, outcome.Reason);
        Assert.Contains("IOException", outcome.Reason);
        Assert.Single(scan.Scans);
    }

    [Fact]
    public async Task InRoot_UnresolvedKind_FallsBackToScopedScan_Flagged()
    {
        var (coordinator, scan) = New();

        var outcome = await coordinator.IngestAsync("/data/media/Scene/notes.xyz", existingId: null, identity: null, default);

        Assert.Equal(IngestResult.Flagged, outcome.Result);
        Assert.Empty(scan.Imports);
        Assert.Single(scan.Scans);
    }

    [Fact]
    public async Task Ingest_RunsAsSystemPrincipal_ThenRestoresPrevious()
    {
        // A Whisparr On-Import carries no Cove principal, so the ambient one is a non-System user (here a
        // read-less Anonymous). CoveContext's per-principal query filters would return zero rows under it, so
        // the ingest must elevate to System for its whole span (see IngestCoordinator) and restore afterwards.
        var scan = new FakeScanService();
        var accessor = new CurrentPrincipalAccessor();
        var before = CovePrincipal.Anonymous();
        accessor.Set(before);

        var services = new ServiceCollection();
        services.AddScoped<IScanService>(_ => scan);
        services.AddSingleton<ICurrentPrincipalAccessor>(accessor);
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var coordinator = new IngestCoordinator(
            scopeFactory, _ => ValueTask.FromResult<IReadOnlyList<string>>([Root]));

        PrincipalKind? kindAtImport = null;
        scan.OnImport = () => kindAtImport = accessor.Current?.Kind;

        await coordinator.IngestAsync(VideoPath, existingId: null, identity: null, default);

        Assert.Equal(PrincipalKind.System, kindAtImport); // elevated for the ingest
        Assert.Same(before, accessor.Current); // restored afterwards, request principal untouched
    }

    [Theory]
    [InlineData("/data/media/a.mkv")]
    [InlineData("/data/media/a.mp4")]
    [InlineData("/data/media/a.webm")]
    public async Task VideoExtension_RoutesToImportDownloadedVideo(string path)
    {
        var (coordinator, scan) = New();

        var outcome = await coordinator.IngestAsync(path, existingId: null, identity: null, default);

        Assert.Equal(IngestResult.Imported, outcome.Result);
        Assert.Equal("Video", Assert.Single(scan.Imports).Kind);
        Assert.Empty(scan.Scans);
    }

    // Whisparr's own webhook/history payloads only ever carry the scene's main video file. A non-video
    // extension can only arrive from schema drift or a user's non-default extension list; both fall back
    // to the scoped scan, never a mis-dispatched import.
    [Theory]
    [InlineData("/data/media/a.jpg")]
    [InlineData("/data/media/a.cbz")]
    [InlineData("/data/media/a.mp3")]
    [InlineData("/data/media/a.txt")]
    public async Task NonVideoExtension_FallsBackToScopedScan(string path)
    {
        var (coordinator, scan) = New();

        var outcome = await coordinator.IngestAsync(path, existingId: null, identity: null, default);

        Assert.Equal(IngestResult.Flagged, outcome.Result);
        Assert.Empty(scan.Imports);
        Assert.Single(scan.Scans);
    }
}
