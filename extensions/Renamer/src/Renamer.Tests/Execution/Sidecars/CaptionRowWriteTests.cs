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
[Trait("Tier", "L1")]
public sealed class CaptionRowWriteTests
{
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
                RenamerStatus.Rename, newBasename, folderPath),
        ]);

    private static RenamerExecutor RealExecutor(Cove.Data.CoveContext db)
        => new(new CoveRenamerDataPort(db), new CapturingEventBus(), new FakeRevertJournal(), "run-test", new DiskMover());
}
