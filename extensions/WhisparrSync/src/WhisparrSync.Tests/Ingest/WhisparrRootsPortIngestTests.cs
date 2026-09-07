using System.Net;
using System.Text.Json;
using Cove.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using WhisparrSync.Client;
using WhisparrSync.Ingest;
using WhisparrSync.Options;
using WhisparrSync.Safety;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.Ingest;

/// <summary>
/// The link between the root-folder read and the fail-closed ingest guard, driven through the REAL
/// <see cref="WhisparrRootsPort"/> over a fake transport rather than through an injected root list. The shipped
/// coordinator contract supplies roots directly to the provider delegate, so it proves the guard rejects an empty
/// set but never that a read which could not answer PRODUCES one.
/// </summary>
/// <remarks>
/// The port is wired exactly as the webhook route wires it — the same five-minute cache lifetime — because the
/// no-negative-caching property is only observable with a cache that could hold the failure.
/// </remarks>
[Trait("Tier", "L1")]
public sealed class WhisparrRootsPortIngestTests
{
    private const string BaseUrl = "http://localhost:6969";
    private const string ApiKey = "test-api-key";
    private const string Root = "/data/media";
    private const string VideoPath = "/data/media/Scene/Scene.mkv";

    [Fact]
    public async Task AReadThatCouldNotAnswer_RejectsFailClosed_NoImport_NoScan()
    {
        var (coordinator, scan, _) = New(Unavailable());

        var outcome = await coordinator.IngestAsync(VideoPath, existingId: null, identity: null, default);

        Assert.Equal(IngestResult.Flagged, outcome.Result);
        Assert.Empty(scan.Imports);
        Assert.Empty(scan.Scans);
    }

    [Fact]
    public async Task ARootContainingTheIngestedPath_Imports()
    {
        var (coordinator, scan, _) = New(Roots(Root));

        var outcome = await coordinator.IngestAsync(VideoPath, existingId: null, identity: null, default);

        Assert.Equal(IngestResult.Imported, outcome.Result);
        Assert.Equal(VideoPath, Assert.Single(scan.Imports).Path);
    }

    // Case-sensitive segment-boundary containment is a security property, not a style choice: were
    // /data/media-evil read as living beneath /data/media, an attacker-chosen path on a sibling directory would
    // reach the host ingest. The rule is asserted in isolation elsewhere; this pins that it survives the seam.
    [Fact]
    public async Task ASiblingPrefixRoot_IsRejected_ThroughTheSeam()
    {
        var (coordinator, scan, _) = New(Roots(Root));

        var outcome = await coordinator.IngestAsync(
            "/data/media-evil/Scene.mkv", existingId: null, identity: null, default);

        Assert.Equal(IngestResult.Flagged, outcome.Result);
        Assert.Empty(scan.Imports);
        Assert.Empty(scan.Scans);
    }

    // The highest-risk property of the moved cache: a failed read must leave the cache untouched. A cached empty
    // set would hold the guard closed for the whole lifetime, so every webhook event in that window would be
    // rejected even after Whisparr came back.
    [Fact]
    public async Task AFailedReadIsNotCached_TheNextEventSucceeds()
    {
        var (coordinator, scan, handler) = New(Unavailable(), Roots(Root));

        var rejected = await coordinator.IngestAsync(VideoPath, existingId: null, identity: null, default);
        var imported = await coordinator.IngestAsync(VideoPath, existingId: null, identity: null, default);

        Assert.Equal(IngestResult.Flagged, rejected.Result);
        Assert.Equal(IngestResult.Imported, imported.Result);
        Assert.Equal(2, handler.CallCount);
        Assert.Equal(VideoPath, Assert.Single(scan.Imports).Path);
    }

    private static (IngestCoordinator Coordinator, FakeScanService Scan, FakeHttpMessageHandler Handler) New(
        params Func<HttpResponseMessage>[] rootResponses)
    {
        var handler = FakeHttpMessageHandler.Sequence(rootResponses);
        var client = new WhisparrClient(new HttpClient(handler));
        var options = new WhisparrOptions { BaseUrl = BaseUrl, ApiKey = ApiKey };
        var port = new WhisparrRootsPort(
            _ => Task.FromResult((options, options.BaseUrl, options.ApiKey)), TimeSpan.FromMinutes(5));

        var scan = new FakeScanService();
        var services = new ServiceCollection();
        services.AddScoped<IScanService>(_ => scan);
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        // The webhook route's own wiring: the coordinator's allowed-root provider IS the port read.
        var coordinator = new IngestCoordinator(
            scopeFactory, async ct => (await port.ReadAsync(client, ct)).Paths);
        return (coordinator, scan, handler);
    }

    private static Func<HttpResponseMessage> Roots(params string[] paths)
        => FakeHttpMessageHandler.Respond(
            HttpStatusCode.OK,
            "application/json",
            JsonSerializer.Serialize(
                Array.ConvertAll(paths, p => new { id = 2, path = p, accessible = true, freeSpace = 1L })));

    private static Func<HttpResponseMessage> Unavailable()
        => FakeHttpMessageHandler.Respond(HttpStatusCode.ServiceUnavailable, "application/json", "{}");
}
