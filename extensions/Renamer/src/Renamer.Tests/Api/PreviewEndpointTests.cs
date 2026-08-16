using System.Data.Common;
using System.Text;
using Cove.Core.Auth;
using Cove.Data;
using Cove.Extensions.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Renamer.Execution;
using Renamer.Options;
using Renamer.Planner;
using Renamer.Tests.Execution;
using Renamer.Tests.TestSupport;
using static Cove.Extensions.Shared.Testing.HttpResultUnwrap;

namespace Renamer.Tests.Api;

/// <summary>
/// Dry-run preview: <c>PreviewAsync</c> runs the planner over the seeded entities and answers
/// <c>{ items, summary }</c> — old→new, status, routing fields and the blast-radius summary — with ZERO
/// mutation, proven by reading back each seeded file's Basename/Path unchanged after the call. The
/// handler is exercised as a plain method (no HTTP host) with a real SQLite <c>CoveContext</c>.
/// </summary>
/// <remarks>
/// The wire-shape assertions read the bytes the result actually WRITES rather than re-serializing its
/// value: the serializer options are the result's own, so a test naming its own instance would agree
/// with itself forever while the response said something else. That is not hypothetical — a numeric or
/// PascalCase <c>status</c> reads as a non-rename in the UI's <c>status === "rename"</c> match, and the
/// rename then silently never fires.
/// </remarks>
[Trait("Tier", "L1")]
public sealed class PreviewEndpointTests
{
    // OS-aware absolute roots so routing to a different root yields a real cross-volume Move. The
    // destination is fictional on purpose: preview probes the SOURCE on disk, never the target.
    private static string SrcRoot => OperatingSystem.IsWindows() ? @"C:\library\incoming" : "/srv/library/incoming";
    private static string PathRoot => OperatingSystem.IsWindows() ? @"F:\by-source" : "/mnt/by-source";

    private static string Fwd(string p) => p.Replace('\\', '/');

    // Most cases here exercise wire shape, fan-out or routing rather than the default template, so they
    // pin a title-only one: the seeded videos carry no height, and the shipped default would append
    // "[$resolution]" and make every expected name depend on a setting these tests are not about.
    private static RenamerOptions TitleOnly => new() { FilenameTemplate = "$title" };

    /// <summary>Executes a result against a real response body and returns what it wrote, as UTF-8 text.</summary>
    private static async Task<string> BodyOfAsync(IResult result)
    {
        var ctx = new DefaultHttpContext();
        var body = new MemoryStream();
        ctx.Response.Body = body;
        await result.ExecuteAsync(ctx);
        return Encoding.UTF8.GetString(body.ToArray());
    }

    [Fact]
    public async Task PreviewAsync_WithVideosRead_ReturnsPlanItems_AndMutatesNothing()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            // Preview probes the source on disk, so the seeded row needs a matching on-disk file for
            // the item to classify as a real rename (a gone source would be SkipMissingSource).
            string folderPath = dir.Root.Replace('\\', '/');
            var (_, videoId, fileId) = await ExecutorTestSeed.SeedVideoAsync(
                db, folderPath, "raw one.mkv", "First Film");
            File.WriteAllText(Path.Combine(dir.Root, "raw one.mkv"), "video-bytes");
            var (beforeName, beforePath) = await ExecutorTestSeed.ReadFileAsync(db, fileId);

            var (ext, _) = await ExtensionHarness.CreateStoreOnlyAsync(TitleOnly);
            var principal = FakePrincipalAccessor.WithPermissions(Permissions.VideosRead);

            var result = await ext.PreviewAsync(
                new global::Renamer.Api.RenamerRequest("video", [videoId]), db, principal, default);

            var ok = Assert.IsType<WireJson<global::Renamer.Contracts.PreviewResponse>>(Unwrap(result));
            var item = Assert.Single(ok.Value!.Items);
            Assert.Equal(fileId, item.FileId);
            Assert.EndsWith("raw one.mkv", item.OldFullPath);
            Assert.Equal("First Film.mkv", item.NewBasename);
            Assert.Equal(RenamerStatus.Rename, item.Status);

            // The UI's confirm summary reads it.status === "rename" and it.fileId.
            var json = await BodyOfAsync(Unwrap(result));
            Assert.Contains("\"status\":\"rename\"", json);
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

            var (ext, _) = await ExtensionHarness.CreateStoreOnlyAsync(TitleOnly);
            var principal = FakePrincipalAccessor.WithPermissions(Permissions.VideosRead);

            var result = await ext.PreviewAsync(
                new global::Renamer.Api.RenamerRequest("video", [videoId]), db, principal, default);

            var ok = Assert.IsType<WireJson<global::Renamer.Contracts.PreviewResponse>>(Unwrap(result));
            // one plan item per physical file of the entity, never just the first file.
            Assert.Equal(2, ok.Value!.Items.Count);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    /// <summary>
    /// Several selected entities each get their own plan item — the fan-out, not just the first id.
    /// </summary>
    /// <remarks>
    /// The other single-entity cases here pass a one-element id array, and so does every e2e that drives
    /// "Rename selected" (one `selectCard` call, no multi-select helper existed). So the N&gt;1 fan-out
    /// of the user's actual selection was unexercised at every tier — the same shape as issue #108,
    /// where a bulk edit renamed nothing because only the one-item path had ever been walked. A preview
    /// that answered for the first id alone would have satisfied every prior assertion, and it is what
    /// the confirm dialog counts before anything touches disk.
    /// </remarks>
    [Fact]
    public async Task PreviewAsync_WithSeveralEntityIds_ReturnsAnItemForEveryOne()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            var ids = await SeedVideosAsync(db, dir, count: 4);

            var (ext, _) = await ExtensionHarness.CreateStoreOnlyAsync(TitleOnly);
            var principal = FakePrincipalAccessor.WithPermissions(Permissions.VideosRead);

            var result = await ext.PreviewAsync(
                new global::Renamer.Api.RenamerRequest("video", [.. ids]), db, principal, default);

            var ok = Assert.IsType<WireJson<global::Renamer.Contracts.PreviewResponse>>(Unwrap(result));

            // Every seeded entity is represented, and by its OWN computed name — asserting only the
            // count would pass on four copies of the first id's answer.
            Assert.Equal(ids.Count, ok.Value!.Items.Count);
            for (int i = 0; i < ids.Count; i++)
            {
                Assert.Contains(ok.Value.Items, item => item.NewBasename == $"Film {i}.mkv");
                Assert.Contains(
                    ok.Value.Items,
                    item => item.OldFullPath.EndsWith($"raw {i}.mkv", StringComparison.Ordinal));
            }
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    /// <summary>
    /// The routed destination preview reports is the SAME one the batch will execute.
    /// </summary>
    /// <remarks>
    /// Regression: <c>/preview</c> must route through the same <c>RouteLookups</c> the manual batch
    /// builds. Before the fix <c>PreviewAsync</c> called the empty-lookups overload and reported every
    /// item as an in-place source-confine rename even when a destination rule was configured — preview
    /// lied about where files would move. The cross-check against a planner run is what makes this a pin
    /// on the two paths AGREEING rather than a second assertion of one path's answer, and it is why the
    /// case survives beside the cross-volume one below, which asserts the same three routing fields but
    /// never compares them to the batch.
    /// </remarks>
    [Fact]
    public async Task PreviewAsync_RoutedItem_ReportsRoutedDestination_MatchingBatch_AndMutatesNothing()
    {
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
                AllowedRoots = [srcFolder, PathRoot],
                PathDestinations =
                [
                    new PathDestinationRule
                    {
                        Pattern = srcFolder, Dest = Dests.At(PathRoot, "Sorted"), IsRegex = false,
                    },
                ],
            };

            // The INITIALIZING harness, unlike the rest of this suite: a destination root is chosen
            // from Cove's library paths and re-checked against them on every plan, and that list
            // reaches the extension through Initialize. Without it this item would preview as
            // SkipRootMissing — correctly, but the routed destination is what the case is about.
            var (ext, _) = await ExtensionHarness.CreateWithSharedContextAsync(
                db, options: options, libraryRoots: PathRoot);
            var principal = FakePrincipalAccessor.WithPermissions(Permissions.VideosRead);

            var result = await ext.PreviewAsync(
                new global::Renamer.Api.RenamerRequest("video", [videoId]), db, principal, default);

            var ok = Assert.IsType<WireJson<global::Renamer.Contracts.PreviewResponse>>(Unwrap(result));
            var item = Assert.Single(ok.Value!.Items);

            // The preview reflects the routed destination — the SAME route the batch resolves.
            Assert.Equal(RenamerStatus.Move, item.Status);
            Assert.Equal(PathRoot.Replace('\\', '/'), item.ResolvedDestinationRoot);
            Assert.Equal("SourcePath:exact", item.MatchedRule);

            // Cross-check: the planner (the batch's own path) resolves the identical destination for
            // the same options + lookups — preview and batch agree. The port is handed the same
            // library paths the extension was, or the two would disagree for that reason alone.
            var port = new CoveRenamerDataPort(db, LibraryConfig(PathRoot));
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

    /// <summary>
    /// The whole-batch answer: items AND the summary that quantifies a cross-volume blast radius, both
    /// camelCase with string enums on the wire.
    /// </summary>
    [SkippableFact]
    public async Task PreviewAsync_ReturnsItemsAndSummary_WithRoutingFields_AndCamelCaseStringEnums()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "needs a Windows drive letter to stand in for a second volume");

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
                AllowedRoots = [srcFolder, PathRoot],
                PathDestinations =
                [
                    new PathDestinationRule
                    {
                        Pattern = srcFolder, Dest = Dests.At(PathRoot, "Sorted"), IsRegex = false,
                    },
                ],
            };

            // Initializing harness, for the reason stated on the routed case above.
            var (ext, _) = await ExtensionHarness.CreateWithSharedContextAsync(
                db, options: options, libraryRoots: PathRoot);
            var principal = FakePrincipalAccessor.WithPermissions(Permissions.VideosRead);

            var result = await ext.PreviewAsync(
                new global::Renamer.Api.RenamerRequest("video", [videoId]), db, principal, default);

            var ok = Assert.IsType<WireJson<global::Renamer.Contracts.PreviewResponse>>(Unwrap(result));
            var response = ok.Value!;

            // Per-item contract preserved + routing fields present.
            var item = Assert.Single(response.Items);
            Assert.Equal(fileId, item.FileId);
            Assert.Equal(RenamerStatus.Move, item.Status);
            Assert.Equal(PathRoot.Replace('\\', '/'), item.ResolvedDestinationRoot);
            Assert.Equal("SourcePath:exact", item.MatchedRule);

            // Summary quantifies the (cross-volume) blast radius.
            Assert.Equal(1, response.Summary.TotalCount);
            Assert.Equal(1, response.Summary.CrossVolumeCount);
            var pair = Assert.Single(response.Summary.VolumePairs);
            Assert.Equal(1, pair.Count);

            var json = await BodyOfAsync(Unwrap(result));
            Assert.Contains("\"items\":", json);
            Assert.Contains("\"summary\":", json);
            Assert.Contains("\"status\":\"move\"", json);
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
        // non-acting skip (BatchPreview.Summarize counts only Rename|Move), so the summary shows
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

            var (ext, _) = await ExtensionHarness.CreateStoreOnlyAsync(options);
            var principal = FakePrincipalAccessor.WithPermissions(Permissions.VideosRead);

            var result = await ext.PreviewAsync(
                new global::Renamer.Api.RenamerRequest("video", [videoId]), db, principal, default);

            var ok = Assert.IsType<WireJson<global::Renamer.Contracts.PreviewResponse>>(Unwrap(result));
            var response = ok.Value!;

            // The excluded item APPEARS in the preview (not dropped), with SkipExcluded + its reason.
            var item = Assert.Single(response.Items);
            Assert.Equal(fileId, item.FileId);
            Assert.Equal(RenamerStatus.SkipExcluded, item.Status);
            Assert.NotNull(item.Reason);
            Assert.Contains("Exclude:Path:exact", item.Reason);

            // Non-acting skip: zero Rename/Move counted in the blast-radius summary.
            Assert.Equal(0, response.Summary.TotalCount);

            var json = await BodyOfAsync(Unwrap(result));
            Assert.Contains("\"status\":\"skipExcluded\"", json);

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
            // source would be SkipMissingSource instead of the same-volume rename this test asserts.
            string folderPath = dir.Root.Replace('\\', '/');
            var (_, videoId, _) = await ExecutorTestSeed.SeedVideoAsync(
                db, folderPath, "raw one.mkv", "First Film");
            File.WriteAllText(Path.Combine(dir.Root, "raw one.mkv"), "video-bytes");

            var (ext, _) = await ExtensionHarness.CreateStoreOnlyAsync(TitleOnly);
            var principal = FakePrincipalAccessor.WithPermissions(Permissions.VideosRead);

            var result = await ext.PreviewAsync(
                new global::Renamer.Api.RenamerRequest("video", [videoId]), db, principal, default);

            var ok = Assert.IsType<WireJson<global::Renamer.Contracts.PreviewResponse>>(Unwrap(result));
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

    /// <summary>
    /// A preview of N entities loads each of them ONCE — the blast-radius file sizes come off the entity
    /// the planner already loaded, never off a second identical multi-<c>Include</c> read per id.
    /// </summary>
    /// <remarks>
    /// The handler builds its own <c>CoveRenamerDataPort</c> from the <c>DbContext</c> it is handed, so
    /// there is no port seam to count calls on (unlike the batch path's load-once cases in
    /// <c>RenamerPlannerTests</c>). The
    /// count is taken at the database instead, and the per-load command count is CALIBRATED from a real
    /// <c>LoadEntityAsync</c> in this same test rather than assumed to be one: how many reader commands
    /// EF renders an Include chain into is EF's decision, and hard-coding today's answer would make this
    /// pin fail on a provider change that is not the regression it exists to catch.
    /// </remarks>
    [Fact]
    public async Task PreviewAsync_LoadsEachSelectedEntityExactlyOnce()
    {
        using var dir = new TempDir();
        var recorder = new ReaderTextRecorder();
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        try
        {
            var options = new DbContextOptionsBuilder<CoveContext>()
                .UseSqlite(connection)
                .AddInterceptors(recorder)
                .Options;
            await using var db = new CoveContext(options, principalAccessor: null);
            await db.Database.EnsureCreatedAsync();

            var ids = await SeedVideosAsync(db, dir, count: 3);

            // Calibrate against the real load: whatever SQL texts ONE LoadEntityAsync issues are the
            // texts an entity load is recognised by below, and how many it issues is the per-load cost.
            var port = new CoveRenamerDataPort(db);
            recorder.Texts.Clear();
            await port.LoadEntityAsync(RenamerFileKind.Video, ids[0]);
            var loadTexts = recorder.Texts.ToHashSet(StringComparer.Ordinal);
            int perLoad = recorder.Texts.Count;
            Assert.True(perLoad > 0, "calibration issued no reader command — the count below would be vacuous");

            var (ext, _) = await ExtensionHarness.CreateStoreOnlyAsync(TitleOnly);
            var principal = FakePrincipalAccessor.WithPermissions(Permissions.VideosRead);

            recorder.Texts.Clear();
            var result = await ext.PreviewAsync(
                new global::Renamer.Api.RenamerRequest("video", [.. ids]), db, principal, default);

            // Name the outcome: a 403 or a 400 would issue no load at all and satisfy a bare count.
            var ok = Assert.IsType<WireJson<global::Renamer.Contracts.PreviewResponse>>(Unwrap(result));
            Assert.Equal(ids.Count, ok.Value!.Items.Count);

            int loadCommands = recorder.Texts.Count(text => loadTexts.Contains(text));
            Assert.Equal(perLoad * ids.Count, loadCommands);
        }
        finally
        {
            await connection.DisposeAsync();
        }
    }

    /// <summary>
    /// Seeds <paramref name="count"/> single-file videos ("Film {i}" / "raw {i}.mkv") and returns their
    /// entity ids in seeded order.
    /// </summary>
    /// <remarks>
    /// One folder for all of them: <c>folders.Path</c> is unique, so a second <c>SeedVideoAsync</c>
    /// against the same path violates it. Same shape ParallelBatchTests uses to build a multi-entity
    /// batch. Each file is also written to disk because preview probes the source, and a gone source
    /// classifies as SkipMissingSource instead of a real rename.
    /// </remarks>
    private static async Task<List<int>> SeedVideosAsync(CoveContext db, TempDir dir, int count)
    {
        string folderPath = dir.Root.Replace('\\', '/');
        var (folderId, firstId, _) = await ExecutorTestSeed.SeedVideoAsync(
            db, folderPath, "raw 0.mkv", "Film 0");
        File.WriteAllText(Path.Combine(dir.Root, "raw 0.mkv"), "bytes-0");

        List<int> ids = [firstId];
        for (int i = 1; i < count; i++)
        {
            var video = new Cove.Core.Entities.Video { Title = $"Film {i}", Organized = true };
            db.Set<Cove.Core.Entities.Video>().Add(video);
            await db.SaveChangesAsync();
            await ExecutorTestSeed.SeedAdditionalFileAsync(db, folderId, video.Id, $"raw {i}.mkv");
            File.WriteAllText(Path.Combine(dir.Root, $"raw {i}.mkv"), $"bytes-{i}");
            ids.Add(video.Id);
        }

        return ids;
    }

    // Rebuild the same lookups the batch builds (exact source-path rule → PathExactToDest). Mirrors
    // Renamer.BuildLookups for the non-regex case without reaching into the private method.
    private static RouteLookups BuildLookupsViaBatch(RenamerOptions o)
    {
        var exact = new Dictionary<string, Destination>(StringComparer.Ordinal);
        foreach (var rule in o.PathDestinations)
        {
            if (!rule.IsRegex)
            {
                exact.TryAdd(rule.Pattern, rule.Dest);
            }
        }

        return new RouteLookups(
            o.StudioDestinations,
            o.TagDestinations,
            exact,
            System.Array.Empty<(System.Text.RegularExpressions.Regex, Destination)>());
    }

    /// <summary>Cove configuration declaring <paramref name="roots"/> as its library paths.</summary>
    private static Cove.Core.Interfaces.CoveConfiguration LibraryConfig(params string[] roots) => new()
    {
        CovePaths = [.. roots.Select(r => new Cove.Core.Interfaces.CovePath { Path = r })],
    };

    /// <summary>Records the SQL of every executed reader command, so a test can recognise one query by its own text.</summary>
    private sealed class ReaderTextRecorder : DbCommandInterceptor
    {
        public List<string> Texts { get; } = [];

        public override ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken ct = default)
        {
            Texts.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }

        public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
        {
            Texts.Add(command.CommandText);
            return result;
        }
    }
}
