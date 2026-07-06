using System.Text.Json;
using Cove.Core.Auth;
using Microsoft.AspNetCore.Http.HttpResults;
using Renamer.Options;
using Renamer.Planner;
using Renamer.Tests.Execution;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Preview;

/// <summary>
/// Whole-batch wire shape: <c>/preview</c> now answers an object
/// <c>{ items, summary }</c> (was a bare array). This pins the load-bearing serialization contract:
/// each per-item object stays camelCase with <c>status</c> the STRING (so the UI's
/// <c>status === "Renamer"</c> match survives) AND carries its routing fields; the additive summary
/// serializes camelCase with <c>confirmLevel</c> the STRING and <c>volumePairs</c> as
/// <c>{ from, to, count, bytes }</c>. The handler is exercised as a plain method (no HTTP host) over a
/// real SQLite <c>CoveContext</c>, and zero mutation is re-asserted.
/// </summary>
[Trait("Tier", "Integration")]
public sealed class PreviewWholeBatchTests
{
    // OS-aware absolute roots so routing to a different root yields a real cross-volume Move.
    private static string SrcRoot => OperatingSystem.IsWindows() ? @"C:\library\incoming" : "/srv/library/incoming";
    private static string PathRoot => OperatingSystem.IsWindows() ? @"F:\by-source" : "/mnt/by-source";
    private static string Fwd(string p) => p.Replace('\\', '/');

    private static async Task<global::Renamer.Renamer> BuildExtensionAsync(RenamerOptions options)
    {
        var ext = new global::Renamer.Renamer();
        var store = new FakeStore();
        await new OptionsStore(store).SaveAsync(options);
        ((Cove.Plugins.IStatefulExtension)ext).SetStore(store);
        return ext;
    }

    [Fact]
    public async Task PreviewAsync_ReturnsItemsAndSummary_WithRoutingFields_AndCamelCaseStringEnums()
    {
        // The source lives in a real temp dir so preview's on-disk source probe finds it (a gone
        // source would be SkipMissingSource, not the routed Move this test asserts). The routed
        // destination (PathRoot, a fictional different drive) stays cross-volume vs the temp source.
        using var srcDir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string srcFolder = srcDir.Root.Replace('\\', '/');
            var (_, videoId, fileId) = await ExecutorTestSeed.SeedVideoAsync(
                db, srcFolder, "raw.mkv", "My Film");
            File.WriteAllText(Path.Combine(srcDir.Root, "raw.mkv"), "video-bytes");
            var (beforeName, beforePath) = await ExecutorTestSeed.ReadFileAsync(db, fileId);

            // An exact source-path rule + an allowed dest root on a DIFFERENT volume → a routed Move
            // that the aggregate classifies as cross-volume.
            var options = new RenamerOptions
            {
                FilenameTemplate = "$title",
                FolderTemplate = "Sorted",
                AllowedRoots = [srcFolder, PathRoot],
                PathDestinations = [new PathDestinationRule { Pattern = srcFolder, Dest = PathRoot, IsRegex = false }],
            };

            var ext = await BuildExtensionAsync(options);
            var principal = FakePrincipalAccessor.WithPermissions(Permissions.VideosRead);

            var result = await ext.PreviewAsync(
                new global::Renamer.Api.RenamerRequest("video", [videoId]), db, principal, default);

            var ok = Assert.IsType<JsonHttpResult<global::Renamer.Api.PreviewResponse>>(result);
            var response = ok.Value!;

            // Per-item contract preserved + routing fields present.
            var item = Assert.Single(response.Items);
            Assert.Equal(fileId, item.FileId);
            Assert.Equal(RenamerStatus.Move, item.Status);
            Assert.Equal(PathRoot, item.ResolvedDestinationRoot);
            Assert.Equal("SourcePath:exact", item.MatchedRule);

            // Summary quantifies the (cross-volume) blast radius.
            Assert.Equal(1, response.Summary.TotalCount);
            Assert.Equal(1, response.Summary.CrossVolumeCount);
            var pair = Assert.Single(response.Summary.VolumePairs);
            Assert.Equal(1, pair.Count);

            // WIRE-SHAPE regression: the bytes the UI reads MUST be camelCase with `status` and
            // `confirmLevel` the STRING — NOT PascalCase, NOT a numeric enum. Serialize with the
            // handler's own options.
            var json = JsonSerializer.Serialize(response, ok.JsonSerializerOptions);
            Assert.Contains("\"items\":", json);
            Assert.Contains("\"summary\":", json);
            Assert.Contains("\"status\":\"Move\"", json);
            Assert.Contains("\"resolvedDestinationRoot\":", json);
            Assert.Contains("\"matchedRule\":", json);
            Assert.Contains("\"targetVolume\":", json);
            Assert.Contains("\"confirmLevel\":", json);
            Assert.Contains("\"volumePairs\":", json);
            Assert.Contains("\"from\":", json);
            Assert.Contains("\"to\":", json);
            Assert.Contains("\"count\":", json);
            Assert.Contains("\"bytes\":", json);
            Assert.DoesNotContain("\"status\":0", json);
            Assert.DoesNotContain("\"Status\":", json);
            Assert.DoesNotContain("\"ConfirmLevel\":", json);

            // Zero mutation.
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
    public async Task PreviewAsync_ExcludedItem_AppearsAsSkipExcluded_WithReason_NotSilentlyDropped()
    {
        // EXCL-03: an item matched by a source-path exclude is a VISIBLE SkipExcluded
        // skip-with-reason in the whole-batch preview item list — NOT silently dropped. It is a
        // non-acting skip (BatchPreview.Summarize counts only Renamer|Move), so the summary shows
        // zero acting items while the item itself still appears with its exclude reason.
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            var (_, videoId, fileId) = await ExecutorTestSeed.SeedVideoAsync(
                db, Fwd(SrcRoot), "raw.mkv", "My Film");
            var (beforeName, beforePath) = await ExecutorTestSeed.ReadFileAsync(db, fileId);

            // An EXACT source-path exclude on the seeded folder → the item is excluded FIRST.
            var options = new RenamerOptions
            {
                FilenameTemplate = "$title",
                ExcludePaths = [new ExcludeRule { Pattern = Fwd(SrcRoot), IsRegex = false }],
            };

            var ext = await BuildExtensionAsync(options);
            var principal = FakePrincipalAccessor.WithPermissions(Permissions.VideosRead);

            var result = await ext.PreviewAsync(
                new global::Renamer.Api.RenamerRequest("video", [videoId]), db, principal, default);

            var ok = Assert.IsType<JsonHttpResult<global::Renamer.Api.PreviewResponse>>(result);
            var response = ok.Value!;

            // The excluded item APPEARS in the preview (not dropped), with SkipExcluded + its reason.
            var item = Assert.Single(response.Items);
            Assert.Equal(fileId, item.FileId);
            Assert.Equal(RenamerStatus.SkipExcluded, item.Status);
            Assert.NotNull(item.Reason);
            Assert.Contains("Exclude:Path:exact", item.Reason);

            // Non-acting skip: zero Renamer/Move counted in the blast-radius summary.
            Assert.Equal(0, response.Summary.TotalCount);

            // The status survives serialization as the camelCase STRING the UI matches on.
            var json = JsonSerializer.Serialize(response, ok.JsonSerializerOptions);
            Assert.Contains("\"status\":\"SkipExcluded\"", json);

            // Zero mutation.
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
    public async Task PreviewAsync_SameVolumeRenamer_SummaryIsLight()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            // Preview probes the source on disk, so give the seeded row a real on-disk file — a gone
            // source would be SkipMissingSource instead of the same-volume Renamer this test asserts.
            string folderPath = dir.Root.Replace('\\', '/');
            var (_, videoId, _) = await ExecutorTestSeed.SeedVideoAsync(
                db, folderPath, "raw one.mkv", "First Film");
            File.WriteAllText(Path.Combine(dir.Root, "raw one.mkv"), "video-bytes");

            var ext = await BuildExtensionAsync(new RenamerOptions { FilenameTemplate = "$title" });
            var principal = FakePrincipalAccessor.WithPermissions(Permissions.VideosRead);

            var result = await ext.PreviewAsync(
                new global::Renamer.Api.RenamerRequest("video", [videoId]), db, principal, default);

            var ok = Assert.IsType<JsonHttpResult<global::Renamer.Api.PreviewResponse>>(result);
            var response = ok.Value!;

            Assert.Equal(1, response.Summary.TotalCount);
            Assert.Equal(1, response.Summary.SameVolumeCount);
            Assert.Equal(0, response.Summary.CrossVolumeCount);
            Assert.Empty(response.Summary.VolumePairs);
            Assert.Equal(ConfirmLevel.Light, response.Summary.ConfirmLevel);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }
}
