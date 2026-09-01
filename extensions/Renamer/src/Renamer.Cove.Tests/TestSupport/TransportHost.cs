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

/// <summary>
/// Mounts the extension's own <c>MapEndpoints</c> in an in-process
/// <see cref="WebApplication"/>/TestServer over a real <see cref="CoveContext"/>, and hands back an
/// <see cref="HttpClient"/> that speaks to it.
/// </summary>
/// <remarks>
/// A handler called directly receives whatever arguments the test constructs, so a test written that
/// way proves nothing about what the host's model binding actually produces for a given request body.
/// Anything asserting on the wire — the bound request shape, the status code, the response bytes —
/// belongs on this seam.
/// </remarks>
public sealed class TransportHost : IAsyncDisposable
{
    /// <summary>The route prefix the host mounts an extension's endpoints under.</summary>
    public const string BaseRoute = "/api/extensions/com.alextomas955.renamer";

    private readonly WebApplication _app;
    private readonly SqliteConnection _conn;
    private readonly CoveContext _db;

    /// <summary>A client bound to the in-process server; request paths start at <see cref="BaseRoute"/>.</summary>
    public HttpClient Client { get; }

    /// <summary>
    /// Every route the extension mounted, as an HTTP method paired with the route pattern as written
    /// (so a parameterised route reads <c>/job-status/{jobId}</c>, not a request path).
    /// </summary>
    public IReadOnlyList<(string Method, string Pattern)> MountedRoutes { get; }

    private TransportHost(
        WebApplication app,
        HttpClient client,
        SqliteConnection conn,
        CoveContext db,
        IReadOnlyList<(string Method, string Pattern)> mountedRoutes)
    {
        _app = app;
        Client = client;
        _conn = conn;
        _db = db;
        MountedRoutes = mountedRoutes;
    }

    /// <summary>Boots a server serving the extension's routes as the given principal.</summary>
    /// <param name="principal">The principal every in-handler permission check reads.</param>
    /// <param name="store">The extension store, or null for an empty <c>FakeStore</c>.</param>
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
        builder.Services.AddSingleton<IJobService>(new StubJobService());
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

        var mountedRoutes = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .SelectMany(endpoint =>
                (endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? Array.Empty<string>())
                    .Select(method => (Method: method, Pattern: endpoint.RoutePattern.RawText ?? string.Empty)))
            .ToArray();

        return new TransportHost(app, app.GetTestClient(), conn, db, mountedRoutes);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        await _db.DisposeAsync();
        await _conn.DisposeAsync();
    }

    /// <summary>Accepts any enqueue and never runs it; all other members are unused and throw.</summary>
    private sealed class StubJobService : IJobService
    {
        public string Enqueue(string type, string description, Func<Cove.Core.Interfaces.IJobProgress, CancellationToken, Task> work, bool exclusive = true)
            => "job-1";

        public bool Cancel(string jobId) => throw new NotSupportedException();
        public bool ReorderQueued(string jobId, string? beforeJobId) => throw new NotSupportedException();
        public JobInfo? GetJob(string jobId) => throw new NotSupportedException();
        public IReadOnlyList<JobInfo> GetAllJobs() => throw new NotSupportedException();
        public IReadOnlyList<JobInfo> GetJobHistory() => throw new NotSupportedException();
    }
}
