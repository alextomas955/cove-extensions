using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cove.Core.Auth;
using Renamer.Contracts;
using Renamer.Planner;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Api;

// Drives Renamer's real minimal-API transport boundary over HTTP: MapEndpoints is mounted in an
// in-process WebApplication/TestServer. Pins that every route the host mounts answers, and that a
// representative response round-trips its DTO.
public sealed class TransportSmokeTests
{
    private const string Base = TransportHost.BaseRoute;
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    // Every route is driven off the mounted table, so a route added to MapEndpoints joins this test on
    // its own. A list written beside the registration would be a copy of it, and the only thing keeping
    // such a copy current is a test comparing the two.
    //
    // Requested anonymously, which is what makes 404 mean "not mounted": every handler gates on a
    // permission before it can answer 404 for a target it did not find.
    [Fact]
    public async Task EveryMountedRoute_Answers()
    {
        await using var host = await TransportHost.BootAsync(FakePrincipalAccessor.None());

        // An empty table reads as every route passing, and the route data source is empty until the
        // host has started.
        Assert.NotEmpty(host.MountedRoutes);

        var unanswered = new List<string>();
        foreach ((string method, string pattern) in host.MountedRoutes)
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), FillRouteValues(pattern));
            if (method is "POST" or "PUT")
            {
                request.Content = JsonContent.Create(new { entityType = "video", entityIds = Array.Empty<int>() });
            }

            var response = await host.Client.SendAsync(request);
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
            {
                unanswered.Add($"{method} {pattern} answered {(int)response.StatusCode}");
            }
        }

        Assert.Empty(unanswered);
    }

    // A route parameter stands for a value the caller supplies, and every one of these routes takes an
    // id it answers for whether or not that id names anything, so one literal serves them all.
    private static string FillRouteValues(string pattern)
        => string.Join(
            '/',
            pattern.Split('/').Select(segment => segment.StartsWith('{') ? "1" : segment));

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
    /// The wire casing as the host actually writes it, read off the raw response body.
    /// </summary>
    /// <remarks>
    /// Every other casing assertion in this suite serializes a DTO with options the test supplies, so it
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

        // RenamerFileKind, RenamerStatus and ConfirmLevel, each as the camelCase string the UI matches.
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
