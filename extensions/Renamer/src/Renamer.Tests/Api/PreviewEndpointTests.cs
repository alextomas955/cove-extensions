using System.Text.Json;
using Cove.Core.Auth;
using Cove.Plugins;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Renamer.Api;
using Renamer.Contracts;
using Renamer.Options;
using Renamer.Planner;
using Renamer.Tests.TestSupport;
using static Cove.Extensions.Shared.Testing.HttpResultUnwrap;

namespace Renamer.Tests.Api;

[Collection(SubstDriveScope.CollectionName)]
public sealed class PreviewEndpointTests
{
    private static async Task<Renamer> BuildExtensionAsync()
    {
        var ext = RenamerFixture.Create();
        var store = new FakeStore();
        // "$title" keeps the seeded video, which has no height, from gaining a resolution suffix.
        await new OptionsStore(store).SaveAsync(new RenamerOptions { FilenameTemplate = "$title" });
        ((IStatefulExtension)ext).SetStore(store);
        return ext;
    }

    // A fictional destination root on a different drive than the temp source, so routing anchors on a
    // distinct root; only the source needs to exist on disk, because preview probes the source.
    private static string PathRoot => OperatingSystem.IsWindows() ? @"F:\by-source" : "/mnt/by-source";

    private static string SrcRoot =>
        OperatingSystem.IsWindows() ? @"C:\library\incoming" : "/srv/library/incoming";

    private static string Fwd(string p) => p.Replace('\\', '/');

    // Drives /preview for one video as a caller who may read videos, and unwraps the one success
    // shape the route answers with. Asserted as the Ok variant, because the refusal this endpoint
    // also returns is an IValueHttpResult too.
    private static async Task<PreviewResponse> PreviewVideoAsync(
        Renamer ext, DbContext db, int videoId)
    {
        var principal = FakePrincipalAccessor.WithPermissions(Permissions.VideosRead);
        var result = await ext.PreviewAsync(
            new RenamerRequest("video", [videoId]), db, principal, default);

        return Assert.IsType<Ok<PreviewResponse>>(Unwrap(result)).Value!;
    }

    // Preview plans and answers; it writes nothing. The row is read before the call and compared
    // after, so a write is caught at the row rather than inferred from the response.
    private static async Task AssertFileUnchangedAsync(
        DbContext db, int fileId, (string Basename, string Path) before)
    {
        Assert.Equal(before, await ExecutorTestSeed.ReadFileAsync(db, fileId));
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
            var before = await ExecutorTestSeed.ReadFileAsync(db, fileId);

            var ext = await BuildExtensionAsync();
            var response = await PreviewVideoAsync(ext, db, videoId);
            var item = Assert.Single(response.Items);
            Assert.Equal(fileId, item.FileId);
            Assert.EndsWith("raw one.mkv", item.OldFullPath);
            Assert.Equal("First Film.mkv", item.NewBasename);
            Assert.Equal(RenamerStatus.Rename, item.Status);

            // The UI's confirm summary reads it.status === "rename" and it.fileId, so a numeric enum or a
            // PascalCase key reads as nothing to rename and the rename never fires.
            var json = JsonSerializer.Serialize(response, PreviewContracts.PreviewResponseJsonOptions);
            Assert.Contains("\"status\":\"rename\"", json);
            Assert.Contains("\"fileId\":", json);
            Assert.DoesNotContain("\"status\":0", json);
            Assert.DoesNotContain("\"Status\":", json);

            // The in-flight overflow signals reach the wire, and an ordinary same-volume rename carries
            // neither of them: DiskMover mints no temporary name, so a warning here would fire on a
            // correct plan, which is the failure that teaches a user to ignore the badge. The positive
            // arm needs two volumes, so it lives in the pager suite, which injects a mount table.
            Assert.Contains("\"inFlightPathOverflow\":false", json);
            Assert.Contains("\"inFlightPathOverflowCount\":0", json);
            Assert.False(item.InFlightPathOverflow);
            Assert.Equal(0, response.Summary.InFlightPathOverflowCount);

            // A same-volume rename is the lightest blast radius the confirm step shows.
            var summary = response.Summary;
            Assert.Equal(1, summary.TotalCount);
            Assert.Equal(1, summary.SameVolumeCount);
            Assert.Equal(0, summary.CrossVolumeCount);
            Assert.Empty(summary.VolumePairs);
            Assert.Equal(ConfirmLevel.Light, summary.ConfirmLevel);

            await AssertFileUnchangedAsync(db, fileId, before);
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
            var (folderId, videoId, firstFileId) = await ExecutorTestSeed.SeedVideoAsync(
                db, "/library/films", "part1.mkv", "Two Part Film");
            int secondFileId = await ExecutorTestSeed.SeedAdditionalFileAsync(db, folderId, videoId, "part2.mkv");

            var ext = await BuildExtensionAsync();
            var response = await PreviewVideoAsync(ext, db, videoId);
            // One plan item per physical file of the entity, never just the first file. Neither file is
            // on disk, so both are missing-source skips: the item list still names each one.
            Assert.Equal([firstFileId, secondFileId], response.Items.Select(i => i.FileId).Order());
            Assert.All(response.Items, i => Assert.Equal(RenamerStatus.SkipMissingSource, i.Status));
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task PreviewAsync_RoutedItem_ReportsTheRoutedDestination_AndMutatesNothing()
    {
        // The source lives in a real temp dir so preview's on-disk source probe finds it: a gone
        // source would be SkipMissingSource, not the routed Move asserted below.
        using var srcDir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string srcFolder = srcDir.Root.Replace('\\', '/');
            var (_, videoId, fileId) = await ExecutorTestSeed.SeedVideoAsync(
                db, srcFolder, "raw.mkv", "My Film");
            File.WriteAllText(Path.Combine(srcDir.Root, "raw.mkv"), "video-bytes");
            var before = await ExecutorTestSeed.ReadFileAsync(db, fileId);

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

            var response = await PreviewVideoAsync(ext, db, videoId);
            var item = Assert.Single(response.Items);

            Assert.Equal(RenamerStatus.Move, item.Status);
            Assert.Equal(Fwd(PathRoot), item.ResolvedDestinationRoot);
            Assert.Equal("SourcePath:exact", item.MatchedRule);

            await AssertFileUnchangedAsync(db, fileId, before);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task PreviewAsync_ACrossVolumeMove_SummarizesItsBytes_AndSerializesCamelCaseStringEnums()
    {
        Assert.SkipUnless(SecondVolume.IsAvailable, SecondVolume.UnavailableReason);

        // Preview probes the source on disk, so it lives in a real temp dir. The summary classifies
        // volumes against the real mount table, so the destination must be a second filesystem.
        using var srcDir = new TempDir();
        using var destVolume = new SecondVolume();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            const long SizeBytes = 123_456;
            string srcFolder = srcDir.Root.Replace('\\', '/');
            var (_, videoId, fileId) = await ExecutorTestSeed.SeedVideoAsync(
                db, srcFolder, "raw.mkv", "My Film", size: SizeBytes);
            File.WriteAllText(Path.Combine(srcDir.Root, "raw.mkv"), "video-bytes");
            var before = await ExecutorTestSeed.ReadFileAsync(db, fileId);

            var options = new RenamerOptions
            {
                FilenameTemplate = "$title",
                PathDestinations =
                [
                    new PathDestinationRule
                    {
                        Pattern = srcFolder, Dest = Dest.At(destVolume.Root, "Sorted"), IsRegex = false,
                    },
                ],
            };

            var (ext, _) = await ExtensionHarness.CreateWithSharedContextAsync(
                db, options, srcFolder, destVolume.Root);
            var response = await PreviewVideoAsync(ext, db, videoId);

            var item = Assert.Single(response.Items);
            Assert.Equal(fileId, item.FileId);
            Assert.Equal(RenamerStatus.Move, item.Status);

            Assert.Equal(1, response.Summary.TotalCount);
            Assert.Equal(1, response.Summary.CrossVolumeCount);
            Assert.Equal(SizeBytes, response.Summary.CrossVolumeBytes);
            var pair = Assert.Single(response.Summary.VolumePairs);
            Assert.Equal(1, pair.Count);
            Assert.Equal(SizeBytes, pair.Bytes);
            Assert.Equal(ConfirmLevel.Standard, response.Summary.ConfirmLevel);

            // The UI matches on camelCase keys and camelCase enum strings, so a PascalCase key or a
            // numeric enum reads as nothing to do.
            var json = JsonSerializer.Serialize(response, PreviewContracts.PreviewResponseJsonOptions);
            Assert.Contains("\"items\":", json);
            Assert.Contains("\"summary\":", json);
            Assert.Contains("\"status\":\"move\"", json);
            Assert.Contains("\"resolvedDestinationRoot\":", json);
            Assert.Contains("\"matchedRule\":", json);
            Assert.Contains("\"targetVolume\":", json);
            Assert.Contains("\"confirmLevel\":\"standard\"", json);
            Assert.Contains("\"volumePairs\":", json);
            Assert.Contains("\"from\":", json);
            Assert.Contains("\"to\":", json);
            Assert.Contains("\"count\":", json);
            Assert.Contains("\"bytes\":", json);

            await AssertFileUnchangedAsync(db, fileId, before);
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
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            var (_, videoId, fileId) = await ExecutorTestSeed.SeedVideoAsync(
                db, Fwd(SrcRoot), "raw.mkv", "My Film");
            var before = await ExecutorTestSeed.ReadFileAsync(db, fileId);

            var options = new RenamerOptions
            {
                FilenameTemplate = "$title",
                ExcludePaths = [new ExcludeRule { Pattern = Fwd(SrcRoot), IsRegex = false }],
            };

            var (ext, _) = await ExtensionHarness.CreateWithSharedContextAsync(db, options);
            var response = await PreviewVideoAsync(ext, db, videoId);

            var item = Assert.Single(response.Items);
            Assert.Equal(fileId, item.FileId);
            Assert.Equal(RenamerStatus.SkipExcluded, item.Status);
            Assert.NotNull(item.Reason);
            Assert.Contains("Exclude:Path:exact", item.Reason);

            // A skip does not act, so the blast radius counts nothing.
            Assert.Equal(0, response.Summary.TotalCount);

            var json = JsonSerializer.Serialize(response, PreviewContracts.PreviewResponseJsonOptions);
            Assert.Contains("\"status\":\"skipExcluded\"", json);

            await AssertFileUnchangedAsync(db, fileId, before);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }
}
