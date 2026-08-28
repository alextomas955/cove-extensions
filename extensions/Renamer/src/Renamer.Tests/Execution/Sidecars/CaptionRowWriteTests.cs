using Cove.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Renamer.Execution;
using Renamer.Options;
using Renamer.Planner;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Execution.Sidecars;

/// <summary>
/// Drives the real executor over SQLite and a real <see cref="TempDir"/> to prove a renamed caption's
/// DATABASE ROW is written, not only that its file moved on disk.
/// </summary>
/// <remarks>
/// The sibling sidecar tests assert the on-disk move, which the disk mover performs and which a lost
/// row write leaves looking correct, so the row needs an assertion of its own.
/// </remarks>
public sealed class CaptionRowWriteTests
{
    /// <summary>
    /// A caption filename carrying a separator or a parent traversal moves nothing outside the folder.
    /// </summary>
    /// <remarks>
    /// The caption branch joins <c>Filename</c> into both the source and the target path, while the
    /// configured-extension branch rejects separators and <c>..</c> before building either. The
    /// basename-only invariant is stated on <c>IRenamerDataPort</c> and enforced nowhere: the canonical
    /// allowlist re-check resolves the PRIMARY's target, not the sidecars', and the mover applies no
    /// confinement of its own.
    /// </remarks>
    [Fact]
    public async Task CaptionFilenameWithTraversal_IsRejected_NoSidecarEscapesTheFolder()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            // Source and destination sit at DIFFERENT depths on purpose. A traversal resolves against
            // each of them separately, so equal depths would make the escaped source and the escaped
            // target the same path and the move a silent no-op — passing for the wrong reason.
            string srcDir = Path.Combine(dir.Root, "a");
            string destDir = Path.Combine(dir.Root, "b", "c");
            Directory.CreateDirectory(srcDir);
            Directory.CreateDirectory(destDir);
            string srcFolder = srcDir.Replace('\\', '/');
            string destFolder = destDir.Replace('\\', '/');

            var (_, videoId, fileId) =
                await ExecutorTestSeed.SeedVideoAsync(db, srcFolder, "clip.mkv", "Film A");

            db.Set<VideoCaption>().Add(new VideoCaption
            {
                FileId = fileId,
                Filename = "../escape.en.vtt",
                LanguageCode = "en",
                CaptionType = "vtt",
            });
            await db.SaveChangesAsync();

            File.WriteAllText(Path.Combine(srcDir, "clip.mkv"), "video");
            // Sits at root/escape.en.vtt — where srcDir/../escape.en.vtt resolves.
            string outsideSource = Path.Combine(dir.Root, "escape.en.vtt");
            File.WriteAllText(outsideSource, "outside-bytes");
            db.ChangeTracker.Clear();

            var plan = new RenamerPlan(videoId, RenamerFileKind.Video,
            [
                new RenamerPlanItem(fileId, srcFolder + "/clip.mkv", destFolder + "/Film A.mkv",
                    RenamerStatus.Move, "Film A.mkv", destFolder),
            ]);

            var result = await RealExecutor(db).ExecuteAsync(plan, new RenamerOptions(), default);

            // The primary still moves; only the malformed caption is refused.
            Assert.Single(result.Renamed);
            Assert.True(File.Exists(Path.Combine(destDir, "Film A.mkv")), "primary still moves");

            // destDir/../escape.en.vtt is root/b/escape.en.vtt — outside the destination folder.
            Assert.False(File.Exists(Path.Combine(dir.Root, "b", "escape.en.vtt")),
                "no sidecar may be written outside the destination folder");
            Assert.True(File.Exists(outsideSource),
                "a caption naming a traversal must not be read out of its folder either");
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task ARenamedCaption_HasItsRowWritten_WhenNothingHasLoadedTheFilesCaptions()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string folderPath = dir.Root.Replace('\\', '/');
            var (_, videoId, fileId) =
                await ExecutorTestSeed.SeedVideoAsync(db, folderPath, "clip.mkv", "Film A");

            db.Set<VideoCaption>().Add(new VideoCaption
            {
                FileId = fileId,
                Filename = "clip.en.vtt",
                LanguageCode = "en",
                CaptionType = "vtt",
            });
            await db.SaveChangesAsync();
            int captionId = await db.Set<VideoCaption>().Where(c => c.FileId == fileId)
                .Select(c => c.Id).SingleAsync();

            File.WriteAllText(Path.Combine(dir.Root, "clip.mkv"), "video");
            File.WriteAllText(Path.Combine(dir.Root, "clip.en.vtt"), "caption");

            // The line that makes this measure the code rather than the fixture. Seeding through this
            // context leaves the caption tracked, and relationship fix-up then populates the file's
            // Captions navigation — state production never has, because every read the port makes is
            // AsNoTracking and each batch worker saves through a context that has loaded nothing.
            db.ChangeTracker.Clear();

            var result = await RealExecutor(db).ExecuteAsync(
                Plan(videoId, fileId, folderPath, "clip.mkv", "Film A.mkv"), new RenamerOptions(), default);

            Assert.Single(result.Renamed);
            Assert.True(File.Exists(Path.Combine(dir.Root, "Film A.en.vtt")), "the caption file moves");

            db.ChangeTracker.Clear();
            string stored = await db.Set<VideoCaption>().AsNoTracking()
                .Where(c => c.Id == captionId).Select(c => c.Filename).SingleAsync();
            Assert.Equal("Film A.en.vtt", stored);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    private static RenamerPlan Plan(
        int videoId, int fileId, string folderPath, string oldBasename, string newBasename)
        => new(videoId, RenamerFileKind.Video,
        [
            new RenamerPlanItem(fileId, folderPath + "/" + oldBasename, folderPath + "/" + newBasename,
                RenamerStatus.Renamer, newBasename, folderPath),
        ]);

    private static RenamerExecutor RealExecutor(Cove.Data.CoveContext db)
        => new(new CoveRenamerDataPort(db), new CapturingEventBus(), new FakeRevertJournal(), "run-test", new DiskMover());
}
