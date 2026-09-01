using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cove.Core.Auth;
using Renamer.Contracts;
using Renamer.Planner;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Api;

// Drives Renamer's real minimal-API transport boundary over HTTP: MapEndpoints is mounted in an
// in-process WebApplication/TestServer. Pins that every route the host mounts answers rather than
// returning 404 or 405, and that a representative response round-trips its DTO. Lives in the
// Cove-dependent test project because it needs a CovePrincipal and a real CoveContext.
public sealed class TransportSmokeTests
{
    private const string Base = TransportHost.BaseRoute;
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    // Pattern is what the registration reads and RequestPath is what the theory sends, so a
    // parameterised route can be compared against the mounted table and still be requested.
    private static readonly (string Method, string Pattern, string RequestPath)[] PinnedRoutes =
    [
        ("GET", Base + "/last-batch", Base + "/last-batch"),
        ("GET", Base + "/last-scan", Base + "/last-scan"),
        ("GET", Base + "/library-paths", Base + "/library-paths"),
        ("GET", Base + "/orphaned-rules", Base + "/orphaned-rules"),
        ("GET", Base + "/job-status/{jobId}", Base + "/job-status/job-1"),
        ("POST", Base + "/preview", Base + "/preview"),
        ("POST", Base + "/renamer", Base + "/renamer"),
        ("POST", Base + "/preview-sample", Base + "/preview-sample"),
        ("POST", Base + "/undo", Base + "/undo"),
        ("POST", Base + "/scan-library", Base + "/scan-library"),
        ("POST", Base + "/scan-rows", Base + "/scan-rows"),
        ("POST", Base + "/renamer-library", Base + "/renamer-library"),
    ];

    public static TheoryData<string, string> Routes()
    {
        var data = new TheoryData<string, string>();
        foreach (var (method, _, requestPath) in PinnedRoutes)
        {
            data.Add(method, requestPath);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task Route_IsRegistered(string method, string path)
    {
        await using var host = await TransportHost.BootAsync(FakePrincipalAccessor.None());

        using var req = new HttpRequestMessage(new HttpMethod(method), path);
        if (method == "POST")
        {
            req.Content = JsonContent.Create(new { entityType = "video", entityIds = Array.Empty<int>() });
        }

        var resp = await host.Client.SendAsync(req);

        Assert.NotEqual(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.NotEqual(HttpStatusCode.MethodNotAllowed, resp.StatusCode);
    }

    // The list above is hand-transcribed: a route added to MapEndpoints does not join it on its own.
    [Fact]
    public async Task EveryMountedRoute_IsDrivenByTheRouteTheory()
    {
        await using var host = await TransportHost.BootAsync(FakePrincipalAccessor.None());

        var mounted = host.MountedRoutes
            .Select(route => $"{route.Method} {route.Pattern}")
            .Order(StringComparer.Ordinal)
            .ToArray();
        var pinned = PinnedRoutes
            .Select(route => $"{route.Method} {route.Pattern}")
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(pinned, mounted);
    }

    [Fact]
    public async Task GatedRoute_Anonymous_Returns403_NotInert()
    {
        // The host's [RequiresPermission] filter is inert on minimal-API routes, so the handler enforces
        // the read permission itself. Prove the gate fires at the real transport boundary.
        await using var host = await TransportHost.BootAsync(FakePrincipalAccessor.None());
        var resp = await host.Client.GetAsync(Base + "/last-batch");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task LastBatch_Authorized_RoundTripsToSummary()
    {
        await using var host = await TransportHost.BootAsync(FakePrincipalAccessor.WithPermissions(Permissions.VideosRead));

        var resp = await host.Client.GetAsync(Base + "/last-batch");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var json = await resp.Content.ReadAsStringAsync();
        var summary = JsonSerializer.Deserialize<LastBatchSummary>(json, Web);
        Assert.NotNull(summary);
        Assert.False(summary.HasBatch); // fresh store: no batch to undo
    }

    /// <summary>
    /// The wire casing as the HOST actually writes it, read off the raw response body.
    /// </summary>
    /// <remarks>
    /// Every other casing assertion in this suite serializes a DTO with options the TEST supplies, so it
    /// proves only that the test's serializer works. This one names nothing: the bytes come from the
    /// host's own pipeline over real HTTP, which is the only place the contract is actually settled.
    /// Both halves are covered here because they have different sources — property casing is the host's
    /// <c>JsonSerializerDefaults.Web</c> default, while the enum strings come from
    /// <c>CamelCaseStringEnumConverter</c> on the enum types.
    /// </remarks>
    [Fact]
    public async Task LastScan_WritesCamelCaseProperties_AndCamelCaseStringEnums()
    {
        var store = new FakeStore();
        await store.SetAsync(
            global::Renamer.Renamer.LastScanSummaryKey,
            JsonSerializer.Serialize(SeededScanSummary(), PreviewContracts.PreviewResponseJsonOptions));

        await using var host = await TransportHost.BootAsync(
            FakePrincipalAccessor.WithPermissions(Permissions.VideosRead), store);

        var resp = await host.Client.GetAsync(Base + "/last-scan");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadAsStringAsync();

        Assert.Contains("\"totalFiles\":", body, StringComparison.Ordinal);
        Assert.Contains("\"completedAtUtcTicks\":", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"TotalFiles\":", body, StringComparison.Ordinal);

        // RenamerFileKind, RenamerStatus and ConfirmLevel, each as the camelCase STRING the UI matches.
        // A numeric enum here is the defect the converter exists to prevent: the panel compares against
        // "renamer"/"noOp", so a 0 reads as a non-rename and the renamer silently never fires.
        Assert.Contains("\"kinds\":[\"video\"]", body, StringComparison.Ordinal);
        Assert.Contains("\"status\":\"noOp\"", body, StringComparison.Ordinal);
        Assert.Contains("\"confirmLevel\":\"light\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"status\":0", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Status\":", body, StringComparison.Ordinal);
    }

    // Built from the records directly rather than through the planner: this test is about the bytes on
    // the way out, so the cheapest input that carries one of each wire enum is the honest one.
    private static ScanSummary SeededScanSummary()
    {
        var blastRadius = new PreviewSummary(
            TotalCount: 1,
            SameVolumeCount: 1,
            CrossVolumeCount: 0,
            CrossVolumeBytes: 0,
            VolumePairs: [],
            ConfirmLevel: ConfirmLevel.Light,
            Undoable: true,
            InFlightPathOverflowCount: 0);

        return new ScanSummary(
            ScanSummary.CurrentSchemaVersion,
            CompletedAtUtcTicks: 1,
            Kinds:
            [
                new ScanKindSummary(
                    RenamerFileKind.Video,
                    Entities: 1,
                    Files: 1,
                    StatusCounts: [new ScanStatusCount(RenamerStatus.NoOp, 1)],
                    BlastRadius: blastRadius,
                    VolumePairsTruncated: false),
            ]);
    }
}
