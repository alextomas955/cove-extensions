using Cove.Core.Auth;
using Cove.Core.Entities;
using Cove.Data;
using Cove.Extensions.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using WhisparrSync.Import;

namespace WhisparrSync.Tests.Import;

/// <summary>
/// That a background read of the host's own library really runs elevated - and the two limits on how
/// far this tier can carry that claim.
/// </summary>
/// <remarks>
/// The host registers its per-principal authorization filters ONLY on the PostgreSQL provider,
/// because the filter's last arm calls a database function that exists only there. So a relational
/// SQLite context carries no filter to bypass, and a read of one succeeds identically whether or not
/// the elevation happened. The first test below pins that, so no later reader mistakes a SQLite green
/// for this proof.
/// <para>
/// What the remaining tests observe is the SQL the real provider generates for the same read: which
/// values the filter binds under a real, non-null, under-privileged principal, and which it binds
/// inside the elevation. That the resulting predicate then excludes every row is READ OFF that SQL
/// rather than observed as a row count, because observing the count needs a live PostgreSQL and this
/// suite has none. An absent principal is never used as the control: it bypasses the filters exactly
/// as System does.
/// </para>
/// </remarks>
public sealed class SystemPrincipalTests
{
    private const string BypassParameter = "@ef_filter__AuthorizationFiltersBypassed2";
    private const string VideoReadParameter = "@ef_filter__CanReadVideos";
    private const string Endpoint = "https://stashdb.org/graphql";
    private const string RemoteId = "e1a5c0d2-0000-4000-8000-000000000002";

    /// <summary>
    /// The control that CANNOT hold on SQLite, asserted as not holding. A relational context there
    /// answers the same read identically for an under-privileged principal and for System.
    /// </summary>
    [Fact]
    public async Task ARelationalSqliteContextCarriesNoAuthorizationFilterAndAnswersEveryPrincipalAlike()
    {
        var principals = new FakePrincipalAccessor();
        principals.Set(CovePrincipal.System());
        var (db, connection) = await CoveContextFactory.CreateSqliteContextAsync(principals);
        await using var _ = db;
        await using var __ = connection;

        await SeedAsync(db);
        var port = new CoveLibraryPort(db, scan: null, metadata: null, config: null);

        Assert.Empty(db.Model.FindEntityType(typeof(VideoRemoteId))!.GetDeclaredQueryFilters());

        principals.Set(UnderPrivileged());
        Assert.NotNull((await port.ResolveByRemoteIdAsync(Endpoint, RemoteId, Ct)).VideoId);
    }

    /// <summary>
    /// On the provider the host actually runs, the same read carries a filter whose bound values
    /// differ by principal: denied for the under-privileged one, bypassed inside the elevation, and
    /// denied again once the body has returned.
    /// </summary>
    [Fact]
    public async Task OnTheHostsOwnProviderTheReadIsDeniedUnderAnUnderPrivilegedPrincipalAndBypassedUnderSystem()
    {
        var principals = new FakePrincipalAccessor();
        principals.Set(UnderPrivileged());
        await using var db = PostgresModel(principals);
        await using var services = new ServiceCollection()
            .AddSingleton<ICurrentPrincipalAccessor>(principals)
            .BuildServiceProvider();

        var denied = IdentityReadSql(db);
        Assert.Contains($"WHEN {BypassParameter} THEN TRUE", denied, StringComparison.Ordinal);
        Assert.Contains($"{BypassParameter}='False'", denied, StringComparison.Ordinal);
        Assert.Contains($"{VideoReadParameter}='False'", denied, StringComparison.Ordinal);

        var elevated = await RunAsSystem.RunAsSystemAsync(services, () => Task.FromResult(IdentityReadSql(db)));
        Assert.Contains($"{BypassParameter}='True'", elevated, StringComparison.Ordinal);

        var afterwards = IdentityReadSql(db);
        Assert.Contains($"{BypassParameter}='False'", afterwards, StringComparison.Ordinal);
    }

    /// <summary>
    /// The elevation is put back even when the body throws, so a failed background read cannot leave a
    /// request path running as System.
    /// </summary>
    [Fact]
    public async Task ThePrincipalIsPutBackWhenTheElevatedBodyThrows()
    {
        var principals = new FakePrincipalAccessor();
        principals.Set(UnderPrivileged());
        await using var services = new ServiceCollection()
            .AddSingleton<ICurrentPrincipalAccessor>(principals)
            .BuildServiceProvider();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunAsSystem.RunAsSystemAsync(services, () => Task.FromException<bool>(new InvalidOperationException())));

        Assert.Equal(PrincipalKind.Anonymous, principals.Current!.Kind);
    }

    /// <summary>
    /// Present, non-null, and carrying no read permission for the entity kind the identity row hangs
    /// off. Nothing here may substitute an absent principal.
    /// </summary>
    private static CovePrincipal UnderPrivileged() => CovePrincipal.Anonymous();

    /// <summary>
    /// The SQL the port's identity read starts from, with the bound filter values in its header. The
    /// model is the host's own, built for PostgreSQL; no connection is opened.
    /// </summary>
    private static string IdentityReadSql(CoveContext db)
        => db.Set<VideoRemoteId>().Where(row => row.RemoteId == RemoteId).ToQueryString();

    private static CoveContext PostgresModel(ICurrentPrincipalAccessor principals)
    {
        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseNpgsql(
                "Host=localhost;Database=model-only;Username=u;Password=p",
                npgsql => npgsql.UseVector())
            // Its own internal provider, so this model is built rather than taken from a cache another
            // test filled: the model's filters are what this file is about.
            .EnableServiceProviderCaching(false)
            .ReplaceService<IModelCacheKeyFactory, CoveModelCacheKeyFactory>()
            .Options;

        return new CoveContext(options, principals);
    }

    private static async Task SeedAsync(CoveContext db)
    {
        var folder = new Folder { Path = "/data" };
        var video = new Video { Title = "seeded" };
        db.Add(folder);
        db.Add(video);
        await db.SaveChangesAsync(Ct);

        db.Add(new VideoFile { Basename = "scene.mp4", ParentFolder = folder, VideoId = video.Id });
        db.Add(new VideoRemoteId { VideoId = video.Id, Endpoint = Endpoint, RemoteId = RemoteId });
        await db.SaveChangesAsync(Ct);

        db.ChangeTracker.Clear();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}
