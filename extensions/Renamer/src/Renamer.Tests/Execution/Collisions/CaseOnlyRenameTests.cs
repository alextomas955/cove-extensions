using Renamer.Execution;
using Renamer.Options;
using Renamer.Planner;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Execution.Collisions;

public sealed class CaseOnlyRenameTests
{
    [Fact]
    public async Task CaseOnlyRename_OfFileOntoItself_IsCleanRename_NotSuffixed()
    {
        Assert.SkipUnless(PathOps.PathsIgnoreCase, "asserts case-insensitive path semantics");

        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string folderPath = dir.Root.Replace('\\', '/');
            var (_, videoId, fileId) =
                await ExecutorTestSeed.SeedVideoAsync(db, folderPath, "movie.mkv", "My Film");

            // Disk: only the lower-case source exists. On a case-insensitive volume File.Exists of the
            // case-variant target is True, but it is the source occupying its own slot - not a clobber.
            File.WriteAllText(Path.Combine(dir.Root, "movie.mkv"), "movie-bytes");

            // Hand-built in-place plan: movie.mkv → Movie.mkv (case-only), so the executor's collision
            // seam is exercised directly and deterministically (no planner in the way).
            var plan = new RenamerPlan(videoId, RenamerFileKind.Video,
            [
                new RenamerPlanItem(fileId, folderPath + "/movie.mkv", folderPath + "/Movie.mkv",
                    RenamerStatus.Rename, "Movie.mkv", folderPath),
            ]);

            var port = new CoveRenamerDataPort(db);
            var bus = new CapturingEventBus();
            var executor = new RenamerExecutor(port, bus, new FakeRevertJournal(), "run-test");

            var result = await executor.ExecuteAsync(plan, new RenamerOptions(), default);

            // A clean rename: exactly one renamed, nothing skipped or failed, and the new name is the
            // case-corrected target - not a suffixed Movie (1).mkv.
            var renamedItem = Assert.Single(result.Renamed);
            Assert.Equal(RenamerStatus.Rename, renamedItem.Status);
            Assert.Empty(result.Skipped);
            Assert.Empty(result.Failed);
            Assert.EndsWith("Movie.mkv", renamedItem.NewPath);
            Assert.DoesNotContain("(1)", renamedItem.NewPath);

            // DB read-back confirms the corrected basename (asserted via the row, not a case-blind
            // File.Exists which would be True for both spellings on this volume).
            var (basename, path) = await ExecutorTestSeed.ReadFileAsync(db, fileId);
            Assert.Equal("Movie.mkv", basename);
            Assert.Equal(folderPath + "/Movie.mkv", path);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task DifferentFileAtCaseVariantName_StillCollides_NoClobber()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string folderPath = dir.Root.Replace('\\', '/');
            var (_, videoId, sourceId) =
                await ExecutorTestSeed.SeedVideoAsync(db, folderPath, "other.mkv", "My Film");

            // The occupant is on disk with no file row, so the disk-side check is the only thing that
            // can see it. The source is not a case-variant of the target, so the self-path exclusion
            // must not apply.
            File.WriteAllText(Path.Combine(dir.Root, "other.mkv"), "source-bytes");
            File.WriteAllText(Path.Combine(dir.Root, "Movie.mkv"), "different-file-bytes");
            bool caseInsensitiveVolume = File.Exists(Path.Combine(dir.Root, "MOVIE.mkv"));

            var plan = new RenamerPlan(videoId, RenamerFileKind.Video,
            [
                new RenamerPlanItem(sourceId, folderPath + "/other.mkv", folderPath + "/MOVIE.mkv",
                    RenamerStatus.Rename, "MOVIE.mkv", folderPath),
            ]);

            var executor = new RenamerExecutor(
                new CoveRenamerDataPort(db), new CapturingEventBus(), new FakeRevertJournal(), "run-test");

            var result = await executor.ExecuteAsync(plan, new RenamerOptions(), default);

            // Where MOVIE.mkv and Movie.mkv are one slot the occupant takes it, so the loop suffixes;
            // where they are two, MOVIE.mkv is a free name of its own.
            string expectedBasename = caseInsensitiveVolume ? "MOVIE (1).mkv" : "MOVIE.mkv";
            var renamed = Assert.Single(result.Renamed);
            Assert.Equal(folderPath + "/" + expectedBasename, renamed.NewPath);
            Assert.Empty(result.Skipped);
            Assert.Empty(result.Failed);

            Assert.Equal("source-bytes", File.ReadAllText(Path.Combine(dir.Root, expectedBasename)));
            Assert.Equal("different-file-bytes", File.ReadAllText(Path.Combine(dir.Root, "Movie.mkv")));
            var (basename, _) = await ExecutorTestSeed.ReadFileAsync(db, sourceId);
            Assert.Equal(expectedBasename, basename);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }
}
