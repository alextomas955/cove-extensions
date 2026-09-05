using Cove.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Renamer.Execution;
using Renamer.Options;
using Renamer.Planner;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Execution.Sidecars;

/// <summary>
/// A caption's stored filename is a basename only, and the executor enforces that rather than
/// trusting it: the value is joined into both the sidecar's source and its target, so a separator or
/// a parent traversal in it builds a move that reaches outside the folders the rename is confined to.
/// Nothing downstream catches it — the canonical re-check resolves the primary, not the sidecar
/// targets, and the disk mover applies no confinement of its own.
/// </summary>
/// <remarks>
/// The escape is only observable across a folder MOVE. In a same-folder rename the source and the
/// target directory are one, so a traversal prefix cancels against itself and names the same file
/// twice; with the destination at a different depth the two resolve apart and the sidecar lands
/// outside both folders.
/// </remarks>
public sealed class CaptionFilenameGuardTests
{
    [Fact]
    public async Task CaptionFilenameWithAParentTraversal_IsRejectedWithAWarning_AndMovesNothingOutsideTheFolder()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string srcFolder = Path.Combine(dir.Root, "src").Replace('\\', '/');
            string dstFolder = Path.Combine(dir.Root, "dst", "nested").Replace('\\', '/');
            Directory.CreateDirectory(ToNative(srcFolder));
            Directory.CreateDirectory(ToNative(dstFolder));

            var (_, videoId, fileId) =
                await ExecutorTestSeed.SeedVideoAsync(db, srcFolder, "clip.mkv", "My Film");
            db.Set<VideoCaption>().Add(new VideoCaption
            {
                FileId = fileId,
                Filename = "../escape.srt",
                LanguageCode = "en",
                CaptionType = "srt",
            });
            await db.SaveChangesAsync();
            int captionId = await db.Set<VideoCaption>().Where(c => c.FileId == fileId)
                .Select(c => c.Id).SingleAsync();

            File.WriteAllText(Path.Combine(dir.Root, "src", "clip.mkv"), "video");
            // The file "src/../escape.srt" names. An unguarded sidecar move takes it to
            // "dst/nested/../escape.srt" — a third directory, outside both the source and the target.
            string outside = Path.Combine(dir.Root, "escape.srt");
            File.WriteAllText(outside, "not mine to move");
            string wouldEscapeTo = Path.Combine(dir.Root, "dst", "escape.srt");

            var result = await RealExecutor(db).ExecuteAsync(
                MovePlan(videoId, fileId, srcFolder, "clip.mkv", dstFolder, "My Film.mkv"),
                new RenamerOptions(), default);

            // The primary is never failed by a rejected caption.
            var moved = Assert.Single(result.Renamed);
            Assert.Empty(result.Failed);
            Assert.True(File.Exists(Path.Combine(dir.Root, "dst", "nested", "My Film.mkv")), "primary still moves");

            Assert.True(File.Exists(outside), "a file outside the folders must be left where it is");
            Assert.Equal("not mine to move", File.ReadAllText(outside));
            Assert.False(File.Exists(wouldEscapeTo), "no sidecar may be written outside the source and target folders");

            // The rejection is reported rather than silent.
            Assert.NotNull(moved.Reason);
            Assert.Contains("../escape.srt", moved.Reason);

            db.ChangeTracker.Clear();
            string stored = await db.Set<VideoCaption>().AsNoTracking()
                .Where(c => c.Id == captionId).Select(c => c.Filename).SingleAsync();
            Assert.Equal("../escape.srt", stored);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task ACaptionFilenameThatIsAPlainBasename_StillMovesWithItsFile()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string srcFolder = Path.Combine(dir.Root, "src").Replace('\\', '/');
            string dstFolder = Path.Combine(dir.Root, "dst", "nested").Replace('\\', '/');
            Directory.CreateDirectory(ToNative(srcFolder));
            Directory.CreateDirectory(ToNative(dstFolder));

            var (_, videoId, fileId) =
                await ExecutorTestSeed.SeedVideoAsync(db, srcFolder, "clip.mkv", "My Film");
            db.Set<VideoCaption>().Add(new VideoCaption
            {
                FileId = fileId,
                Filename = "clip.en.vtt",
                LanguageCode = "en",
                CaptionType = "vtt",
            });
            await db.SaveChangesAsync();

            File.WriteAllText(Path.Combine(dir.Root, "src", "clip.mkv"), "video");
            File.WriteAllText(Path.Combine(dir.Root, "src", "clip.en.vtt"), "caption");

            var result = await RealExecutor(db).ExecuteAsync(
                MovePlan(videoId, fileId, srcFolder, "clip.mkv", dstFolder, "My Film.mkv"),
                new RenamerOptions(), default);

            var moved = Assert.Single(result.Renamed);
            Assert.Null(moved.Reason);
            string newCaption = Path.Combine(dir.Root, "dst", "nested", "My Film.en.vtt");
            Assert.True(File.Exists(newCaption), "a well-formed caption still rides with its file");
            Assert.Equal("caption", File.ReadAllText(newCaption));
            Assert.False(File.Exists(Path.Combine(dir.Root, "src", "clip.en.vtt")));
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    private static string ToNative(string forwardSlash) => forwardSlash.Replace('/', Path.DirectorySeparatorChar);

    private static RenamerPlan MovePlan(
        int videoId, int fileId, string srcFolder, string oldBasename, string dstFolder, string newBasename)
        => new(videoId, RenamerFileKind.Video,
        [
            new RenamerPlanItem(fileId, srcFolder + "/" + oldBasename, dstFolder + "/" + newBasename,
                RenamerStatus.Move, newBasename, dstFolder),
        ]);

    private static RenamerExecutor RealExecutor(DbContext db)
        => new(new CoveRenamerDataPort(db), new CapturingEventBus(), new FakeRevertJournal(), "run-test", new DiskMover());
}
