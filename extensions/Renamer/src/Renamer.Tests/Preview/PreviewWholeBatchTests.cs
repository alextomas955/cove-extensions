using System.Text.Json;
using Cove.Core.Auth;
using Microsoft.AspNetCore.Http.HttpResults;
using Renamer.Options;
using Renamer.Planner;
using Renamer.Tests.TestSupport;
using static Cove.Extensions.Shared.Testing.HttpResultUnwrap;

namespace Renamer.Tests.Preview;

[Collection(SubstDriveScope.CollectionName)]
public sealed class PreviewWholeBatchTests
{
    private static string SrcRoot => OperatingSystem.IsWindows() ? @"C:\library\incoming" : "/srv/library/incoming";
    private static string Fwd(string p) => p.Replace('\\', '/');

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
            var (beforeName, beforePath) = await ExecutorTestSeed.ReadFileAsync(db, fileId);

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
            var principal = FakePrincipalAccessor.WithPermissions(Permissions.VideosRead);

            var result = await ext.PreviewAsync(
                new global::Renamer.Api.RenamerRequest("video", [videoId]), db, principal, default);

            var ok = Assert.IsType<Ok<global::Renamer.Contracts.PreviewResponse>>(Unwrap(result));
            var response = ok.Value!;

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
            var json = JsonSerializer.Serialize(response, global::Renamer.Contracts.PreviewContracts.PreviewResponseJsonOptions);
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
            Assert.DoesNotContain("\"status\":0", json);
            Assert.DoesNotContain("\"Status\":", json);
            Assert.DoesNotContain("\"ConfirmLevel\":", json);

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
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            var (_, videoId, fileId) = await ExecutorTestSeed.SeedVideoAsync(
                db, Fwd(SrcRoot), "raw.mkv", "My Film");
            var (beforeName, beforePath) = await ExecutorTestSeed.ReadFileAsync(db, fileId);

            var options = new RenamerOptions
            {
                FilenameTemplate = "$title",
                ExcludePaths = [new ExcludeRule { Pattern = Fwd(SrcRoot), IsRegex = false }],
            };

            var (ext, _) = await ExtensionHarness.CreateWithSharedContextAsync(db, options);
            var principal = FakePrincipalAccessor.WithPermissions(Permissions.VideosRead);

            var result = await ext.PreviewAsync(
                new global::Renamer.Api.RenamerRequest("video", [videoId]), db, principal, default);

            var ok = Assert.IsType<Ok<global::Renamer.Contracts.PreviewResponse>>(Unwrap(result));
            var response = ok.Value!;

            var item = Assert.Single(response.Items);
            Assert.Equal(fileId, item.FileId);
            Assert.Equal(RenamerStatus.SkipExcluded, item.Status);
            Assert.NotNull(item.Reason);
            Assert.Contains("Exclude:Path:exact", item.Reason);

            // A skip does not act, so the blast radius counts nothing.
            Assert.Equal(0, response.Summary.TotalCount);

            var json = JsonSerializer.Serialize(response, global::Renamer.Contracts.PreviewContracts.PreviewResponseJsonOptions);
            Assert.Contains("\"status\":\"skipExcluded\"", json);

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
