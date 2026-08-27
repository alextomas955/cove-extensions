using Cove.Core.Auth;
using Cove.Core.Entities;
using Cove.Data;
using Cove.Plugins;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Renamer.Contracts;
using Renamer.Options;
using Renamer.Tests.TestSupport;
using static Cove.Extensions.Shared.Testing.HttpResultUnwrap;

namespace Renamer.Tests.Api;

/// <summary>
/// <c>OrphanedRulesAsync</c> names the rule keys whose entity Cove no longer holds.
/// </summary>
/// <remarks>
/// A rule keys on a stable id so a rename cannot break it; a merge or delete removes that id and the
/// rule then matches nothing. The panel needs to be able to say so, and it cannot infer absence from a
/// failed browser lookup — that reads the same for an entity the viewer may not read, and for a dropped
/// request. So the answer comes from the database, and these tests pin which ids come back.
/// <para>
/// Every expected list is written out by hand from the seeded rows, never derived from the same options
/// the handler reads.
/// </para>
/// </remarks>
public sealed class OrphanedRulesEndpointTests
{
    /// <summary>
    /// Builds the extension over the seeded connection, so the handler's own elevated scope resolves a
    /// context on the SAME database — the wiring <c>ScanLibraryEndpointTests</c> uses.
    /// </summary>
    private static async Task<global::Renamer.Renamer> NewExtensionAsync(
        SqliteConnection conn, RenamerOptions options)
    {
        var ext = RenamerFixture.Create();
        var store = new FakeStore();
        await new OptionsStore(store).SaveAsync(options);
        ((IStatefulExtension)ext).SetStore(store);

        var services = new ServiceCollection();
        services.AddScoped<DbContext>(_ => new CoveContext(
            new DbContextOptionsBuilder<CoveContext>().UseSqlite(conn).Options,
            principalAccessor: null));
        services.AddSingleton<Cove.Core.Events.IEventBus>(new CapturingEventBus());
        await ext.InitializeAsync(services.BuildServiceProvider());
        return ext;
    }

    private static FakePrincipalAccessor Reader() =>
        FakePrincipalAccessor.WithPermissions(Permissions.VideosRead);

    private static Destination Somewhere => Dest.At("/library");

    [Fact]
    public async Task NamesOnlyTheRuleKeysNoEntityAnswersTo()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            // Two studios and one tag exist; the rules also name ids nothing was seeded for.
            db.Add(new Studio { Id = 7, Name = "Kept" });
            db.Add(new Studio { Id = 8, Name = "Also kept" });
            db.Add(new Tag { Id = 30, Name = "Kept tag" });
            await db.SaveChangesAsync();

            var ext = await NewExtensionAsync(conn, new RenamerOptions
            {
                StudioDestinations = { [7] = Somewhere, [99] = Somewhere, [8] = Somewhere },
                TagDestinations = { [30] = Somewhere, [4242] = Somewhere },
            });

            var view = Assert.IsType<Ok<OrphanedRulesView>>(
                Unwrap(await ext.OrphanedRulesAsync(Reader()))).Value!;

            Assert.Equal([99], view.Studios);
            Assert.Equal([4242], view.Tags);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task EveryRuleStillResolves_ReportsNothing()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            db.Add(new Studio { Id = 7, Name = "Kept" });
            await db.SaveChangesAsync();

            var ext = await NewExtensionAsync(conn, new RenamerOptions
            {
                StudioDestinations = { [7] = Somewhere },
            });

            var view = Assert.IsType<Ok<OrphanedRulesView>>(
                Unwrap(await ext.OrphanedRulesAsync(Reader()))).Value!;

            Assert.Empty(view.Studios);
            Assert.Empty(view.Tags);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task NoRulesAtAll_ReportsNothing()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            var ext = await NewExtensionAsync(conn, new RenamerOptions());

            var view = Assert.IsType<Ok<OrphanedRulesView>>(
                Unwrap(await ext.OrphanedRulesAsync(Reader()))).Value!;

            Assert.Empty(view.Studios);
            Assert.Empty(view.Tags);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task NoReadPermission_IsForbidden()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            var ext = await NewExtensionAsync(conn, new RenamerOptions
            {
                StudioDestinations = { [99] = Somewhere },
            });

            // A rule that IS orphaned, so a 403 here can only come from the permission gate.
            Assert.Equal(
                StatusCodes.Status403Forbidden,
                Assert.IsAssignableFrom<IStatusCodeHttpResult>(
                    Unwrap(await ext.OrphanedRulesAsync(FakePrincipalAccessor.None()))).StatusCode);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }
}
