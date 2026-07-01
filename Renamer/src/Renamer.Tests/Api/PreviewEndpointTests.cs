using System.Text.Json;
using Cove.Core.Auth;
using Microsoft.AspNetCore.Http.HttpResults;
using Renamer.Options;
using Renamer.Planner;
using Renamer.Tests.Execution;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Api;

/// <summary>
/// Dry-run preview: <c>PreviewAsync</c> runs the planner over the seeded entity and returns
/// <see cref="RenamerPlanItem"/>[] (old→new + status) with ZERO mutation — proven by reading back
/// each seeded file's Basename/Path unchanged after the call. The handler is exercised as a plain
/// method (no HTTP host) with a real SQLite <c>CoveContext</c>.
/// </summary>
[Trait("Tier", "Integration")]
public sealed class PreviewEndpointTests
{
    private static async Task<global::Renamer.Renamer> BuildExtensionAsync()
    {
        var ext = new global::Renamer.Renamer();
        var store = new FakeStore();
        // This test exercises preview wire-shape + zero mutation, not the default template; pin the
        // title-only template in the store so the seeded (height-less) video renders a stable
        // "Title.ext" name independent of the shipped default (which would append "[$resolution]").
        await new OptionsStore(store).SaveAsync(new RenamerOptions { FilenameTemplate = "$title" });
        ((Cove.Plugins.IStatefulExtension)ext).SetStore(store);
        // PreviewAsync uses Store (OptionsStore) but not _scopeFactory/_eventBus; no Initialize needed.
        return ext;
    }

    [Fact]
    public async Task PreviewAsync_WithVideosRead_ReturnsPlanItems_AndMutatesNothing()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            var (_, videoId, fileId) = await ExecutorTestSeed.SeedVideoAsync(
                db, "/library/films", "raw one.mkv", "First Film");
            var (beforeName, beforePath) = await ExecutorTestSeed.ReadFileAsync(db, fileId);

            var ext = await BuildExtensionAsync();
            var principal = FakePrincipalAccessor.WithPermissions(Permissions.VideosRead);

            var result = await ext.PreviewAsync(
                new global::Renamer.Api.RenamerRequest("video", [videoId]), db, principal, default);

            var ok = Assert.IsType<JsonHttpResult<global::Renamer.Api.PreviewResponse>>(result);
            var item = Assert.Single(ok.Value!.Items);
            Assert.Equal(fileId, item.FileId);
            Assert.EndsWith("raw one.mkv", item.OldFullPath);
            Assert.Equal("First Film.mkv", item.NewBasename);
            Assert.Equal(RenamerStatus.Renamer, item.Status);

            // WIRE-SHAPE regression (the bug live-browser verification caught): the response MUST
            // serialize as camelCase with `status` the STRING "Renamer" — NOT PascalCase, NOT the
            // numeric 0. The UI's confirm summary reads it.status === "Renamer" and it.fileId; a
            // numeric enum or PascalCase key reads as a non-renamer and the renamer silently never
            // fires. Assert the actual bytes, using the options the handler attached.
            var json = JsonSerializer.Serialize(ok.Value!, ok.JsonSerializerOptions);
            Assert.Contains("\"status\":\"Renamer\"", json);
            Assert.Contains("\"fileId\":", json);
            Assert.DoesNotContain("\"status\":0", json);
            Assert.DoesNotContain("\"Status\":", json);

            // Zero mutation: the seeded row is byte-for-byte unchanged after the preview.
            var (afterName, afterPath) = await ExecutorTestSeed.ReadFileAsync(db, fileId);
            Assert.Equal(beforeName, afterName);
            Assert.Equal(beforePath, afterPath);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task PreviewAsync_CoversEveryFileOfAMultiFileEntity()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            var (folderId, videoId, _) = await ExecutorTestSeed.SeedVideoAsync(
                db, "/library/films", "part1.mkv", "Two Part Film");
            await ExecutorTestSeed.SeedAdditionalFileAsync(db, folderId, videoId, "part2.mkv");

            var ext = await BuildExtensionAsync();
            var principal = FakePrincipalAccessor.WithPermissions(Permissions.VideosRead);

            var result = await ext.PreviewAsync(
                new global::Renamer.Api.RenamerRequest("video", [videoId]), db, principal, default);

            var ok = Assert.IsType<JsonHttpResult<global::Renamer.Api.PreviewResponse>>(result);
            // one plan item per physical file of the entity, never just the first file.
            Assert.Equal(2, ok.Value!.Items.Count);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }
}
