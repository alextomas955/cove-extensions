using Cove.Core.Auth;
using Microsoft.AspNetCore.Http.HttpResults;
using Renamer.Execution;
using Renamer.Options;
using Renamer.Planner;
using Renamer.Tests.Execution;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Api;

/// <summary>
/// Regression: <c>/preview</c> must route through the SAME <c>RouteLookups</c> the manual batch
/// builds, so the dry-run reflects the routed destination the batch will execute. Before the fix
/// <c>PreviewAsync</c> called the empty-lookups overload and reported every item as an in-place
/// source-confine renamer even when a destination rule was configured — preview lied about where files
/// would move. This pins that preview now carries the routed <see cref="RenamerPlanItem.ResolvedDestinationRoot"/>
/// and <see cref="RenamerPlanItem.MatchedRule"/>, and still mutates nothing.
/// </summary>
[Trait("Tier", "Integration")]
public sealed class PreviewRoutingTests
{
    // A fictional destination root on a DIFFERENT drive than the temp source, so routing anchors on a
    // distinct root; only the source needs to exist on disk (preview probes the source, not the dest).
    private static string PathRoot => OperatingSystem.IsWindows() ? @"F:\by-source" : "/mnt/by-source";

    [Fact]
    public async Task PreviewAsync_RoutedItem_ReportsRoutedDestination_MatchingBatch_AndMutatesNothing()
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

            // An exact source-path rule + an allowed dest root: BuildLookups turns this into a
            // source-path route, so a correctly-wired preview anchors the move on PathRoot.
            var options = new RenamerOptions
            {
                FilenameTemplate = "$title",
                FolderTemplate = "Sorted",
                AllowedRoots = [srcFolder, PathRoot],
                PathDestinations = [new PathDestinationRule { Pattern = srcFolder, Dest = PathRoot, IsRegex = false }],
            };

            var ext = new global::Renamer.Renamer();
            var store = new FakeStore();
            await new OptionsStore(store).SaveAsync(options);
            ((Cove.Plugins.IStatefulExtension)ext).SetStore(store);

            var principal = FakePrincipalAccessor.WithPermissions(Permissions.VideosRead);

            var result = await ext.PreviewAsync(
                new global::Renamer.Api.RenamerRequest("video", [videoId]), db, principal, default);

            var ok = Assert.IsType<JsonHttpResult<global::Renamer.Api.PreviewResponse>>(result);
            var item = Assert.Single(ok.Value!.Items);

            // The preview now reflects the routed destination — the SAME route the batch resolves.
            Assert.Equal(RenamerStatus.Move, item.Status);
            Assert.Equal(PathRoot, item.ResolvedDestinationRoot);
            Assert.Equal("SourcePath:exact", item.MatchedRule);

            // Cross-check: the planner (the batch's own path) resolves the identical destination for
            // the same options + lookups — preview and batch agree.
            var port = new CoveRenamerDataPort(db);
            var plan = await new RenamerPlanner(port).PlanAsync(
                RenamerFileKind.Video, videoId, options, BuildLookupsViaBatch(options), default);
            var batchItem = Assert.Single(plan.Items);
            Assert.Equal(batchItem.ResolvedDestinationRoot, item.ResolvedDestinationRoot);
            Assert.Equal(batchItem.MatchedRule, item.MatchedRule);

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

    // Rebuild the same lookups the batch builds (exact source-path rule → PathExactToDest). Mirrors
    // Renamer.BuildLookups for the non-regex case without reaching into the private method.
    private static RouteLookups BuildLookupsViaBatch(RenamerOptions o)
    {
        var exact = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rule in o.PathDestinations)
        {
            if (!rule.IsRegex)
            {
                exact.TryAdd(rule.Pattern, rule.Dest);
            }
        }

        return new RouteLookups(
            o.StudioDestinations,
            new Dictionary<string, string>(o.TagDestinations, StringComparer.OrdinalIgnoreCase),
            exact,
            System.Array.Empty<(System.Text.RegularExpressions.Regex, string)>());
    }
}
