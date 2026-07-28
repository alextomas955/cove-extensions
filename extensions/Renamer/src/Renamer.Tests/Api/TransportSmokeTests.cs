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
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public static TheoryData<string, string> Routes()
    {
        var data = new TheoryData<string, string>();
        foreach (var g in new[] { "/last-batch", "/last-scan", "/list-studios", "/list-tags", "/list-performers" })
        {
            data.Add("GET", g);
        }

        foreach (var p in new[] { "/preview", "/renamer", "/preview-sample", "/undo", "/scan-library", "/renamer-library" })
        {
            data.Add("POST", p);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task Route_IsRegistered(string method, string path)
    {
        await using var host = await TransportHost.BootAsync(FakePrincipalAccessor.None());

        using var req = new HttpRequestMessage(new HttpMethod(method), Base + path);
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

        public static async Task<TransportHost> BootAsync(ICurrentPrincipalAccessor principal)
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
            builder.Services.AddRouting();

            var ext = new global::Renamer.Renamer();
            ((IStatefulExtension)ext).SetStore(new FakeStore());

            var app = builder.Build();
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
