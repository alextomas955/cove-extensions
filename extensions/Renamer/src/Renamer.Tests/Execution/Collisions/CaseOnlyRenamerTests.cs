using Renamer.Execution;
using Renamer.Options;
using Renamer.Planner;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Execution.Collisions;

/// <summary>
/// Case-only renamer behaviour (integration, SQLite + a real temp dir). Proves two things the
/// executor's collision loop must get right:
/// <list type="bullet">
/// <item>A pure case-fix renamer (<c>movie.mkv</c> → <c>Movie.mkv</c>) — where the only thing occupying
/// the target name is the SOURCE file itself — completes as a clean <see cref="RenamerStatus.Rename"/>
/// to <c>Movie.mkv</c>, NOT a needlessly suffixed <c>Movie (1).mkv</c> and NOT a collision skip.</item>
/// <item>A DIFFERENT file already at the case-variant target name still collides: a third source
/// renamed onto <c>Movie.mkv</c> is suffixed or skipped, never clobbering the existing file. The
/// cross-file no-clobber guarantee is preserved.</item>
/// </list>
/// <para>
/// The first outcome is platform-UNIVERSAL, which is why neither test is gated, but it is reached by
/// two different mechanisms and the distinction is the whole point: where the volume folds case
/// (Windows, macOS) <c>File.Exists</c> of the case-variant target is TRUE and the fix is that
/// <c>PathOps.PathsEqual</c> recognizes it as the source's own slot; where the volume is
/// case-sensitive (Linux ext4) <c>File.Exists</c> is simply false, so no collision path is entered at
/// all. Gating this on <c>IsWindows()</c> hid the macOS defect it was written to catch.
/// </para>
/// Uses the real <see cref="CoveRenamerDataPort"/> (not the collision-blind port) so the disk-side
/// <c>File.Exists</c> check is the one under test.
/// </summary>
[Trait("Tier", "L1")]
public sealed class CaseOnlyRenamerTests
{
    [Fact]
    public async Task CaseOnlyRenamer_OfFileOntoItself_IsCleanRenamer_NotSuffixed()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string folderPath = dir.Root.Replace('\\', '/');
            var (_, videoId, fileId) =
                await ExecutorTestSeed.SeedVideoAsync(db, folderPath, "movie.mkv", "My Film");

            // Disk: only the lower-case source exists. Where the volume folds case, File.Exists of the
            // case-variant target is True — but it is the SOURCE occupying its own slot, not a clobber.
            // Where it does not, File.Exists is false and the collision loop never runs. Same outcome.
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
            var executor = new RenamerExecutor(port, bus, new FakeRevertJournal(), "run-test", new DiskMover());

            var result = await executor.ExecuteAsync(plan, new RenamerOptions(), default);

            // Clean Renamer: exactly one renamed, nothing skipped or failed, and the new name is the
            // case-corrected target — NOT a suffixed Movie (1).mkv.
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

            // Seed three distinct files in one folder: the lower-case "movie.mkv", a DIFFERENT
            // "Movie.mkv" already occupying the case-variant name, and the source we will renamer.
            // This is the guard on widening the case rule (stated at PathOps.PathsEqual, with the
            // APFS/CIFS caveats): ignoring case for SELF-path equality must not let a different
            // file's name be treated as free.
            var (folderId, videoId, _) =
                await ExecutorTestSeed.SeedVideoAsync(db, folderPath, "movie.mkv", "My Film");
            await ExecutorTestSeed.SeedAdditionalFileAsync(db, folderId, videoId, "Movie.mkv");
            var sourceId = await ExecutorTestSeed.SeedAdditionalFileAsync(db, folderId, videoId, "other.mkv");

            File.WriteAllText(Path.Combine(dir.Root, "movie.mkv"), "lower-bytes");
            File.WriteAllText(Path.Combine(dir.Root, "Movie.mkv"), "different-file-bytes");
            File.WriteAllText(Path.Combine(dir.Root, "other.mkv"), "source-bytes");

            // Renamer the THIRD source onto the case-variant name a DIFFERENT file already holds.
            var plan = new RenamerPlan(videoId, RenamerFileKind.Video,
            [
                new RenamerPlanItem(sourceId, folderPath + "/other.mkv", folderPath + "/Movie.mkv",
                    RenamerStatus.Rename, "Movie.mkv", folderPath),
            ]);

            var port = new CoveRenamerDataPort(db);
            var bus = new CapturingEventBus();
            var executor = new RenamerExecutor(port, bus, new FakeRevertJournal(), "run-test", new DiskMover());

            var result = await executor.ExecuteAsync(plan, new RenamerOptions(), default);

            // No clobber: the source did NOT land on the existing Movie.mkv. It was either suffixed to a
            // free name (Renamed, not "Movie.mkv") or skip-collisioned.
            if (result.Renamed.Count == 1)
            {
                Assert.NotEqual(folderPath + "/Movie.mkv", result.Renamed[0].NewPath.Replace('\\', '/'));
                Assert.Empty(result.Failed);
            }
            else
            {
                var skipped = Assert.Single(result.Skipped);
                Assert.Equal(RenamerStatus.SkipCollision, skipped.Status);
                Assert.Empty(result.Renamed);
            }

            // The pre-existing DIFFERENT file at Movie.mkv is untouched — its bytes survive intact.
            Assert.Equal("different-file-bytes", File.ReadAllText(Path.Combine(dir.Root, "Movie.mkv")));
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }
}
