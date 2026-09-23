using Cove.Core.Auth;
using Cove.Core.Interfaces;
using Cove.Data;
using Cove.Plugins;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Renamer.Tests.TestSupport;

// Mounts the extension's own MapEndpoints in an in-process WebApplication/TestServer over a real
// CoveContext, and hands back an HttpClient that speaks to it. A handler called directly receives
// whatever arguments the test constructs, so a test written that way proves nothing about what the
// host's model binding actually produces for a given request body. Anything asserting on the wire -
// the bound request shape, the status code, the response bytes - belongs on this seam.
public sealed class TransportHost : IAsyncDisposable
{
    // The route prefix the host mounts an extension's endpoints under.
    public const string BaseRoute = "/api/extensions/com.alextomas955.renamer";

    private readonly WebApplication _app;
    private readonly SqliteConnection _conn;
    private readonly DbContext _db;
    private readonly StubJobService _jobs;

    // A client bound to the in-process server; request paths start at BaseRoute.
    public HttpClient Client { get; }

    // Every route the extension mounted, as an HTTP method paired with the route pattern as written
    // (so a parameterised route reads /job-status/{jobId}, not a request path).
    public IReadOnlyList<(string Method, string Pattern)> MountedRoutes { get; }

    // Every endpoint the extension mounted, carrying the metadata its registration attached.
    public IReadOnlyList<RouteEndpoint> Endpoints { get; }

    // How many jobs the handlers enqueued.
    public int EnqueuedJobs => _jobs.EnqueuedCount;

    private TransportHost(
        WebApplication app,
        HttpClient client,
        SqliteConnection conn,
        DbContext db,
        StubJobService jobs,
        IReadOnlyList<RouteEndpoint> endpoints)
    {
        _app = app;
        Client = client;
        _conn = conn;
        _db = db;
        _jobs = jobs;
        Endpoints = endpoints;
        MountedRoutes = [.. endpoints.SelectMany(endpoint =>
            (endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? Array.Empty<string>())
                .Select(method => (Method: method, Pattern: endpoint.RoutePattern.RawText ?? string.Empty)))];
    }

    // Boots a server serving the extension's routes as the given principal. principal: The
    // principal every in-handler permission check reads. store: The extension store, or null for an
    // empty FakeStore.
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
        var jobs = new StubJobService();
        builder.Services.AddSingleton<IJobService>(jobs);
        builder.Services.AddSingleton<IAuthorizationService>(new RecordingAuthorizationService());
        builder.Services.AddSingleton<Cove.Core.Events.IEventBus>(new CapturingEventBus());
        builder.Services.AddRouting();

        var ext = RenamerFixture.Create();
        ((IStatefulExtension)ext).SetStore(store ?? new FakeStore());

        var app = builder.Build();
        // Initialized the way the host does: the undo endpoints resolve a scope of their own now,
        // so a handler reached without this throws before it can answer.
        await ext.InitializeAsync(app.Services);
        ext.MapEndpoints(app);
        await app.StartAsync();

        var endpoints = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .ToArray();

        return new TransportHost(app, app.GetTestClient(), conn, db, jobs, endpoints);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        await _db.DisposeAsync();
        await _conn.DisposeAsync();
    }

    // Counts every enqueue and never runs it; all other members are unused and throw.
    private sealed class StubJobService : IJobService
    {
        public int EnqueuedCount { get; private set; }

        public string Enqueue(string type, string description, Func<Cove.Core.Interfaces.IJobProgress, CancellationToken, Task> work, bool exclusive = true)
        {
            EnqueuedCount++;
            return "job-1";
        }

        public bool Cancel(string jobId) => throw new NotSupportedException();
        public bool ReorderQueued(string jobId, string? beforeJobId) => throw new NotSupportedException();
        public JobInfo? GetJob(string jobId) => throw new NotSupportedException();
        public IReadOnlyList<JobInfo> GetAllJobs() => throw new NotSupportedException();
        public IReadOnlyList<JobInfo> GetJobHistory() => throw new NotSupportedException();
    }
}
