using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cove.Core.Auth;
using Cove.Core.Interfaces;
using Cove.Data;
using Cove.Plugins;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Renamer.Contracts;
using Renamer.Planner;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Api;

/// <summary>
/// The only C# coverage of Renamer's REAL minimal-API transport boundary: MapEndpoints is mounted in an
/// in-process WebApplication/TestServer and driven over HTTP. Pins the route table the MF-30/MF-31
/// executor/planner extraction must preserve (every mounted route resolves — never 404/405) and that a
/// representative response round-trips its DTO. Thin (route-exists + shape only) — the handler-level
/// permission/logic tests already exist. Cove-present tier (a CovePrincipal + a real CoveContext), so it
/// is Compile-Removed on the bare CI leg alongside the other endpoint tests.
/// <para>
/// This retires the standing "WebApplicationFactory can't mount extension routes" claim in Renamer.Api.cs:
/// a minimal WebApplication driving the extension's own MapEndpoints does mount and serve them.
/// </para>
/// </summary>
[Trait("Tier", "L2")]
public sealed class TransportSmokeTests
{
    private const string Base = "/api/extensions/com.alextomas955.renamer";
    private const string DocumentFileName = "openapi.json";
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <summary>Every operation the committed wire document declares, as (method, absolute path).</summary>
    /// <remarks>
    /// Derived from the document rather than listed here. The hand-written table this replaces sat at
    /// eight entries while the document declared nine, so <c>/scan-rows</c> shipped with no transport
    /// coverage and nothing said so. Derivation removes that failure at its source instead of scanning
    /// for it afterwards: CI already byte-diffs this document against the one registration emits, so a
    /// route added without regenerating it fails there, and a regenerated document reaches this theory
    /// with no edit here.
    /// </remarks>
    public static TheoryData<string, string> Routes()
    {
        var data = new TheoryData<string, string>();
        foreach (var (method, path) in DeclaredOperations())
        {
            data.Add(method, path);
        }

        return data;
    }

    private static readonly string[] HttpMethods =
        ["get", "put", "post", "delete", "options", "head", "patch", "trace"];

    private static List<(string Method, string Path)> DeclaredOperations()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(DocumentPath()));

        var operations = new List<(string, string)>();
        foreach (var path in document.RootElement.GetProperty("paths").EnumerateObject())
        {
            foreach (var operation in path.Value.EnumerateObject())
            {
                if (HttpMethods.Contains(operation.Name, StringComparer.Ordinal))
                {
                    operations.Add((operation.Name.ToUpperInvariant(), path.Name));
                }
            }
        }

        return operations;
    }

    private static string DocumentPath()
    {
        var path = Path.Combine(AppContext.BaseDirectory, DocumentFileName);
        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                $"{DocumentFileName} is not next to the test assembly ({path}). The Content item in "
                    + "Renamer.Tests.csproj that copies the committed wire document has been removed or "
                    + "renamed. Failing here is deliberate: without the document this suite would derive "
                    + "an empty route table and report a pass having exercised no route at all.");
        }

        return path;
    }

    /// <summary>The derivation is non-vacuous, and covers the route the hand-written table missed.</summary>
    /// <remarks>
    /// A theory whose data provider yields nothing passes while exercising nothing, which is the failure
    /// this repository's gate doctrine names outright. The floor is transcribed by hand from the
    /// document's own spelling — a count read back out of the same parse would agree with itself however
    /// little it found — and the scan-rows POST is named because it is the operation the hand-maintained
    /// list never reached.
    /// </remarks>
    [Fact]
    public void TheRouteTheoryDerivesEveryDeclaredOperation_AndIsNeverEmpty()
    {
        var operations = DeclaredOperations();

        Assert.True(
            operations.Count >= 10,
            $"the wire document yielded {operations.Count} operations, but declared 10 when this floor "
                + "was written — a smaller number means the parse found less than the document holds.");
        Assert.Equal(operations.Count, Routes().Count);
        Assert.Contains(("POST", Base + "/scan-rows"), operations);
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

    private sealed class RecordingJobService : IJobService
    {
        public string Enqueue(string type, string description, Func<Cove.Core.Interfaces.IJobProgress, CancellationToken, Task> work, bool exclusive = true)
            => "job-1";

        public bool Cancel(string jobId) => throw new NotSupportedException();
        public bool ReorderQueued(string jobId, string? beforeJobId) => throw new NotSupportedException();
        public JobInfo? GetJob(string jobId) => throw new NotSupportedException();
        public IReadOnlyList<JobInfo> GetAllJobs() => throw new NotSupportedException();
        public IReadOnlyList<JobInfo> GetJobHistory() => throw new NotSupportedException();
    }

    private sealed class TransportHost : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly SqliteConnection _conn;
        private readonly CoveContext _db;
        public HttpClient Client { get; }

        private TransportHost(WebApplication app, HttpClient client, SqliteConnection conn, CoveContext db)
        {
            _app = app;
            Client = client;
            _conn = conn;
            _db = db;
        }

        public static async Task<TransportHost> BootAsync(
            ICurrentPrincipalAccessor principal, IExtensionStore? store = null)
        {
            var conn = new SqliteConnection("Data Source=:memory:");
            await conn.OpenAsync();
            var db = new CoveContext(new DbContextOptionsBuilder<CoveContext>().UseSqlite(conn).Options, principalAccessor: null);
            await db.Database.EnsureCreatedAsync();

            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseTestServer();
            builder.Services.AddSingleton(principal);
            builder.Services.AddSingleton<DbContext>(db);
            builder.Services.AddSingleton<IJobService>(new RecordingJobService());
            builder.Services.AddSingleton<Cove.Core.Events.IEventBus>(new CapturingEventBus());
            builder.Services.AddRouting();

            var ext = RenamerFixture.Create();
            ((IStatefulExtension)ext).SetStore(store ?? new FakeStore());

            var app = builder.Build();
            // Initialized, not just mapped: the batch-summary route reads the journal out of the
            // database, so it needs the scope factory a real host hands the extension at load.
            await ext.InitializeAsync(app.Services);
            ext.MapEndpoints(app);
            await app.StartAsync();

            return new TransportHost(app, app.GetTestClient(), conn, db);
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
            await _db.DisposeAsync();
            await _conn.DisposeAsync();
        }
    }
}
