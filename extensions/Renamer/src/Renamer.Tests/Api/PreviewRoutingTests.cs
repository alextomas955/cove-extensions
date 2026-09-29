using Cove.Core.Auth;
using Microsoft.AspNetCore.Http.HttpResults;
using Renamer.Options;
using Renamer.Planner;
using Renamer.Tests.TestSupport;
using static Cove.Extensions.Shared.Testing.HttpResultUnwrap;

namespace Renamer.Tests.Api;

public sealed class PreviewRoutingTests
{
    // A fictional destination root on a different drive than the temp source, so routing anchors on a
    // distinct root; only the source needs to exist on disk (preview probes the source, not the dest).
    private static string PathRoot => OperatingSystem.IsWindows() ? @"F:\by-source" : "/mnt/by-source";

    private static string Fwd(string p) => p.Replace('\\', '/');

    [Fact]
    public async Task PreviewAsync_RoutedItem_ReportsTheRoutedDestination_AndMutatesNothing()
    {
        // The source lives in a real temp dir so preview's on-disk source probe finds it (a gone
        // source would be SkipMissingSource, not the routed Move this test asserts). The routed
        // destination (PathRoot, a fictional different drive) stays a distinct root for routing.
        using var srcDir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string srcFolder = srcDir.Root.Replace('\\', '/');
            var (_, videoId, fileId) = await ExecutorTestSeed.SeedVideoAsync(
                db, srcFolder, "raw.mkv", "My Film");
            File.WriteAllText(Path.Combine(srcDir.Root, "raw.mkv"), "video-bytes");
            var (beforeName, beforePath) = await ExecutorTestSeed.ReadFileAsync(db, fileId);

            // An exact source-path rule and an allowed destination root, so a preview that reads the
            // saved routes anchors the move on PathRoot.
            var options = new RenamerOptions
            {
                FilenameTemplate = "$title",
                FolderTemplate = "Sorted",
                PathDestinations =
                [
                    new PathDestinationRule
                    {
                        Pattern = srcFolder, Dest = Dest.At(PathRoot, "Sorted"), IsRegex = false,
                    },
                ],
            };

            var (ext, _) = await ExtensionHarness.CreateWithSharedContextAsync(
                db, options, srcFolder, PathRoot);

            var principal = FakePrincipalAccessor.WithPermissions(Permissions.VideosRead);

            var result = await ext.PreviewAsync(
                new global::Renamer.Api.RenamerRequest("video", [videoId]), db, principal, default);

            var ok = Assert.IsType<Ok<global::Renamer.Contracts.PreviewResponse>>(Unwrap(result));
            var item = Assert.Single(ok.Value!.Items);

            // The preview reports the route the saved rule selects.
            Assert.Equal(RenamerStatus.Move, item.Status);
            Assert.Equal(Fwd(PathRoot), item.ResolvedDestinationRoot);
            Assert.Equal("SourcePath:exact", item.MatchedRule);

            // Still zero mutation.
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
}
