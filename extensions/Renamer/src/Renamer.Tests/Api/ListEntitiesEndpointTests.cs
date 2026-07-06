using System.Text.Json;
using Cove.Core.Auth;
using Cove.Core.Entities;
using Cove.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Api;

/// <summary>
/// The picker's data source: <c>ListStudiosAsync</c>/<c>ListTagsAsync</c>/<c>ListPerformersAsync</c>
/// return a Name-ordered id+name array over the host DB, deny an under-permissioned caller with 403
/// before any read, and never mutate the rows they read. Exercised as plain methods (no HTTP host —
/// <c>MapEndpoints</c> can't be mounted) against a real SQLite <c>CoveContext</c>.
/// </summary>
[Trait("Tier", "Integration")]
public sealed class ListEntitiesEndpointTests
{
    private static global::Renamer.Renamer NewExtension()
    {
        var ext = new global::Renamer.Renamer();
        ((Cove.Plugins.IStatefulExtension)ext).SetStore(new FakeStore());
        return ext;
    }

    private static int StatusOf(IResult result) => Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode ?? 0;

    // Insertion order differs from alphabetical order, so a Name-ordering assertion distinguishes
    // ordered output from incidental insertion order.
    private static readonly string[] SeedNames = ["Charlie", "Alpha", "Bravo"];

    [Fact]
    public async Task ListStudios_WithReadPermission_ReturnsIdNameRows_NameOrdered()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            var seeded = await SeedStudiosAsync(db);

            var ext = NewExtension();
            var principal = FakePrincipalAccessor.WithPermissions(Permissions.VideosRead);

            var result = await ext.ListStudiosAsync(db, principal, default);

            var ok = Assert.IsType<JsonHttpResult<global::Renamer.Renamer.EntityRef[]>>(result);
            var rows = ok.Value!;
            Assert.Equal(3, rows.Length);
            Assert.Equal(["Alpha", "Bravo", "Charlie"], rows.Select(r => r.Name));
            foreach (var row in rows)
            {
                Assert.Equal(seeded[row.Name], row.Id);
            }
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task ListTags_WithReadPermission_ReturnsIdNameRows()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            var seeded = await SeedTagsAsync(db);

            var ext = NewExtension();
            var principal = FakePrincipalAccessor.WithPermissions(Permissions.VideosRead);

            var result = await ext.ListTagsAsync(db, principal, default);

            var ok = Assert.IsType<JsonHttpResult<global::Renamer.Renamer.EntityRef[]>>(result);
            var rows = ok.Value!;
            Assert.Equal(3, rows.Length);
            Assert.Equal(["Alpha", "Bravo", "Charlie"], rows.Select(r => r.Name));
            foreach (var row in rows)
            {
                Assert.Equal(seeded[row.Name], row.Id);
            }
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task ListStudios_Anonymous_Returns403()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            var ext = NewExtension();

            var result = await ext.ListStudiosAsync(db, FakePrincipalAccessor.None(), default);

            Assert.Equal(403, StatusOf(result));
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task ListTags_Anonymous_Returns403()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            var ext = NewExtension();

            var result = await ext.ListTagsAsync(db, FakePrincipalAccessor.None(), default);

            Assert.Equal(403, StatusOf(result));
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task ListStudios_NullPrincipal_Returns403()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            var ext = NewExtension();

            var result = await ext.ListStudiosAsync(db, FakePrincipalAccessor.NullPrincipal(), default);

            Assert.Equal(403, StatusOf(result));
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task ListTags_NullPrincipal_Returns403()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            var ext = NewExtension();

            var result = await ext.ListTagsAsync(db, FakePrincipalAccessor.NullPrincipal(), default);

            Assert.Equal(403, StatusOf(result));
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task ListPerformers_WithReadPermission_ReturnsIdNameRows_NameOrdered()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            var seeded = await SeedPerformersAsync(db);

            var ext = NewExtension();
            var principal = FakePrincipalAccessor.WithPermissions(Permissions.VideosRead);

            var result = await ext.ListPerformersAsync(db, principal, default);

            var ok = Assert.IsType<JsonHttpResult<global::Renamer.Renamer.EntityRef[]>>(result);
            var rows = ok.Value!;
            Assert.Equal(3, rows.Length);
            Assert.Equal(["Alpha", "Bravo", "Charlie"], rows.Select(r => r.Name));
            foreach (var row in rows)
            {
                Assert.Equal(seeded[row.Name], row.Id);
            }
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task ListPerformers_Anonymous_Returns403()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            var ext = NewExtension();

            var result = await ext.ListPerformersAsync(db, FakePrincipalAccessor.None(), default);

            Assert.Equal(403, StatusOf(result));
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task ListPerformers_NullPrincipal_Returns403()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            var ext = NewExtension();

            var result = await ext.ListPerformersAsync(db, FakePrincipalAccessor.NullPrincipal(), default);

            Assert.Equal(403, StatusOf(result));
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task ListPerformers_DoesNotMutateRows()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            await SeedPerformersAsync(db);

            var ext = NewExtension();
            var principal = FakePrincipalAccessor.WithPermissions(Permissions.VideosRead);

            await ext.ListPerformersAsync(db, principal, default);

            Assert.DoesNotContain(
                db.ChangeTracker.Entries(),
                e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted);
            Assert.Equal(0, await db.SaveChangesAsync());
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task ListStudios_SerializesCamelCaseIdName()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            await SeedStudiosAsync(db);

            var ext = NewExtension();
            var principal = FakePrincipalAccessor.WithPermissions(Permissions.VideosRead);

            var result = await ext.ListStudiosAsync(db, principal, default);

            var ok = Assert.IsType<JsonHttpResult<global::Renamer.Renamer.EntityRef[]>>(result);
            var json = JsonSerializer.Serialize(ok.Value!, ok.JsonSerializerOptions);
            Assert.Contains("\"id\":", json);
            Assert.Contains("\"name\":", json);
            Assert.DoesNotContain("\"Id\":", json);
            Assert.DoesNotContain("\"Name\":", json);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task ListStudios_DoesNotMutateRows()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            await SeedStudiosAsync(db);

            var ext = NewExtension();
            var principal = FakePrincipalAccessor.WithPermissions(Permissions.VideosRead);

            await ext.ListStudiosAsync(db, principal, default);

            // The read is AsNoTracking, so it leaves no pending writes: no entry sits in a dirty
            // state and SaveChanges persists nothing. (Seeding leaves the rows tracked as Unchanged
            // in this shared context — a dirty entry or a non-zero SaveChanges is the real signal a
            // read mutated state.)
            Assert.DoesNotContain(
                db.ChangeTracker.Entries(),
                e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted);
            Assert.Equal(0, await db.SaveChangesAsync());
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    private static async Task<Dictionary<string, int>> SeedStudiosAsync(CoveContext db)
    {
        var ids = new Dictionary<string, int>();
        foreach (var name in SeedNames)
        {
            var studio = new Studio { Name = name };
            db.Set<Studio>().Add(studio);
            await db.SaveChangesAsync();
            ids[name] = studio.Id;
        }

        return ids;
    }

    private static async Task<Dictionary<string, int>> SeedTagsAsync(CoveContext db)
    {
        var ids = new Dictionary<string, int>();
        foreach (var name in SeedNames)
        {
            var tag = new Tag { Name = name };
            db.Set<Tag>().Add(tag);
            await db.SaveChangesAsync();
            ids[name] = tag.Id;
        }

        return ids;
    }

    private static async Task<Dictionary<string, int>> SeedPerformersAsync(CoveContext db)
    {
        var ids = new Dictionary<string, int>();
        foreach (var name in SeedNames)
        {
            var performer = new Performer { Name = name };
            db.Set<Performer>().Add(performer);
            await db.SaveChangesAsync();
            ids[name] = performer.Id;
        }

        return ids;
    }
}
