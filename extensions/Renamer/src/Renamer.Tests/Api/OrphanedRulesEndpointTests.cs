using Cove.Core.Auth;
using Cove.Core.Entities;
using Cove.Data;
using Cove.Plugins;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Renamer.Contracts;
using Renamer.Options;
using Renamer.Tests.TestSupport;
using static Cove.Extensions.Shared.Testing.HttpResultUnwrap;

namespace Renamer.Tests.Api;

public sealed class OrphanedRulesEndpointTests
{
    // Builds the extension over the seeded connection, so the handler's own elevated scope resolves
    // a context on the same database - the wiring ScanLibraryEndpointTests uses.
    private static async Task<global::Renamer.Renamer> NewExtensionAsync(
        SqliteConnection conn, RenamerOptions options, CommandCountingInterceptor? interceptor = null)
    {
        var ext = RenamerFixture.Create();
        var store = new FakeStore();
        await new OptionsStore(store).SaveAsync(options);
        ((IStatefulExtension)ext).SetStore(store);

        var services = new ServiceCollection();
        services.AddScoped<DbContext>(_ =>
        {
            var builder = new DbContextOptionsBuilder<CoveContext>().UseSqlite(conn);
            if (interceptor is not null)
            {
                builder.AddInterceptors(interceptor);
            }

            return new CoveContext(builder.Options, principalAccessor: null);
        });
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
    public async Task NoRulesAtAll_ReportsNothing_WithoutQueryingTheLibrary()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            var interceptor = new CommandCountingInterceptor();
            var ext = await NewExtensionAsync(conn, new RenamerOptions(), interceptor);
            interceptor.ReaderCount = 0;

            var view = Assert.IsType<Ok<OrphanedRulesView>>(
                Unwrap(await ext.OrphanedRulesAsync(Reader()))).Value!;

            Assert.Empty(view.Studios);
            Assert.Empty(view.Tags);
            Assert.Equal(0, interceptor.ReaderCount);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }
}
