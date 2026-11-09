using Microsoft.EntityFrameworkCore;
using Renamer.Execution;
using Renamer.Options;
using Renamer.Planner;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Execution;

[Collection(SubstDriveScope.CollectionName)]
public sealed class EmptySourceFolderCleanerTests
{
    private const string RunId = "run-test";

    [Fact]
    public void NonEmptyDir_HoldingAnotherFile_IsLeftIntact()
    {
        using var dir = new TempDir();
        string src = Path.Combine(dir.Root, "sub");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "untracked.txt"), "the batch never moved this");

        var (removed, warning) = EmptySourceFolderCleaner.TryRemoveIfEmpty(src.Replace('\\', '/'));

        Assert.False(removed);
        Assert.Null(warning); // a non-empty dir is the expected common case, not an error
        Assert.True(Directory.Exists(src), "a dir still holding any file must survive");
        Assert.True(File.Exists(Path.Combine(src, "untracked.txt")), "the untracked file must be untouched");
    }

    [Fact]
    public void NonEmptyDir_HoldingASubdirectory_IsLeftIntact()
    {
        using var dir = new TempDir();
        string src = Path.Combine(dir.Root, "sub");
        Directory.CreateDirectory(Path.Combine(src, "nested"));

        var (removed, warning) = EmptySourceFolderCleaner.TryRemoveIfEmpty(src.Replace('\\', '/'));

        Assert.False(removed);
        Assert.Null(warning);
        Assert.True(Directory.Exists(src));
        Assert.True(Directory.Exists(Path.Combine(src, "nested")));
    }

    [Fact]
    public void GenuinelyEmptyDir_IsDeleted()
    {
        using var dir = new TempDir();
        string src = Path.Combine(dir.Root, "sub");
        Directory.CreateDirectory(src);

        var (removed, warning) = EmptySourceFolderCleaner.TryRemoveIfEmpty(src.Replace('\\', '/'));

        Assert.True(removed);
        Assert.Null(warning);
        Assert.False(Directory.Exists(src));
    }

    [Fact]
    public void ALinkedFolder_IsLeftAlone_AndSoIsTheEmptyFolderItPointsAt()
    {
        using var dir = new TempDir();
        string target = Path.Combine(dir.Root, "outside-the-library");
        Directory.CreateDirectory(target);
        string link = Path.Combine(dir.Root, "linked");
        try
        {
            Directory.CreateSymbolicLink(link, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Windows grants symlink creation only to an elevated or developer-mode account.
            Assert.Skip($"cannot create a directory symlink here: {ex.Message}");
        }

        var (removed, warning) = EmptySourceFolderCleaner.TryRemoveIfEmpty(link.Replace('\\', '/'));

        Assert.False(removed);
        Assert.Null(warning);
        Assert.True(Directory.Exists(target));
        Assert.NotNull(new DirectoryInfo(link).LinkTarget);
    }

    [Fact]
    public void AnEmptyDriveRoot_IsNeverDeleted()
    {
        // A real drive root always holds entries, so only an empty one reaches the root guard rather
        // than the non-empty check. A subst drive is the one empty root a test can mint, and only on
        // Windows.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "an empty drive root needs a subst drive, which only Windows has");

        using var drive = new SubstDrive();
        var (removed, warning) = EmptySourceFolderCleaner.TryRemoveIfEmpty(drive.Root.Replace('\\', '/'));

        Assert.False(removed);
        Assert.Null(warning);
        Assert.True(Directory.Exists(drive.Root), "the drive root must survive untouched");
    }

    [Fact]
    public void AlreadyGoneDir_IsNoop_NeverThrows()
    {
        using var dir = new TempDir();
        string gone = Path.Combine(dir.Root, "never-existed").Replace('\\', '/');

        var (removed, warning) = EmptySourceFolderCleaner.TryRemoveIfEmpty(gone);

        Assert.False(removed);
        Assert.Null(warning);
    }

    [Fact]
    public async Task CrossFolderMove_WithOptionOn_DeletesEmptiedSourceDir_FileAtDestination()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            var (_, plan, _) = await SeedMoveOutOfSrcAsync(db, dir);
            var options = new RenamerOptions { RemoveEmptyFolder = true };

            var result = await NewExecutor(db).ExecuteAsync(plan, options, default);

            var moved = Assert.Single(result.Renamed);
            Assert.Equal(RenamerStatus.Move, moved.Status);
            Assert.Null(moved.Reason); // no cleanup warning
            Assert.Empty(result.Failed);

            Assert.True(File.Exists(Path.Combine(dir.Root, "dst", "My Film.mkv")), "file must be at the destination");
            Assert.False(Directory.Exists(Path.Combine(dir.Root, "src")), "the emptied source dir must be deleted");
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task OptionOff_LeavesEmptiedSourceDirIntact()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            var (_, plan, _) = await SeedMoveOutOfSrcAsync(db, dir);
            var options = new RenamerOptions(); // RemoveEmptyFolder defaults to false

            var result = await NewExecutor(db).ExecuteAsync(plan, options, default);

            Assert.Single(result.Renamed);
            Assert.True(File.Exists(Path.Combine(dir.Root, "dst", "My Film.mkv")));
            Assert.True(Directory.Exists(Path.Combine(dir.Root, "src")),
                "with the option off, the emptied source dir must be left as-is (byte-identical to today)");
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task UndoOfMoveAfterCleanup_Skips_BecauseOriginalDirectoryGone_FileStaysAtDestination()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            var (dstFolder, plan, fileId) = await SeedMoveOutOfSrcAsync(db, dir);

            var port = new CoveRenamerDataPort(db);
            var journal = new FakeRevertJournal();
            var options = new RenamerOptions { RemoveEmptyFolder = true };

            await journal.BeginBatchAsync(RunId, RunId, RenamerFileKind.Video, DateTime.UtcNow);
            var fwd = await new RenamerExecutor(port, new CapturingEventBus(), journal, RunId)
                .ExecuteAsync(plan, options, default);
            Assert.Single(fwd.Renamed);
            Assert.False(Directory.Exists(Path.Combine(dir.Root, "src")), "the move + cleanup deleted the source dir");

            var batch = await JournalPageReader.ReadWholeUndoTargetAsync(journal);
            Assert.NotNull(batch);
            var replayer = new UndoReplayer(port, new CapturingEventBus());
            var undo = await replayer.RevertAsync(batch!, default);

            Assert.Equal(0, undo.Undone);
            var skip = Assert.Single(undo.Skipped);
            Assert.Contains("original directory no longer exists", skip.Reason);

            Assert.True(File.Exists(Path.Combine(dir.Root, "dst", "My Film.mkv")),
                "the file remains at the destination - undo did not move it back, but it is not lost");
            var (basename, path) = await ExecutorTestSeed.ReadFileAsync(db, fileId);
            Assert.Equal("My Film.mkv", basename);
            Assert.Equal(dstFolder + "/My Film.mkv", path);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    // A video seeded at src/clip.mkv, its bytes on disk and an empty dst beside it, with the plan
    // that moves and renames it. Every cleanup case starts here: the move is what empties src.
    private static async Task<(string DstFolder, RenamerPlan Plan, int FileId)> SeedMoveOutOfSrcAsync(
        DbContext db, TempDir dir)
    {
        string srcFolder = Path.Combine(dir.Root, "src").Replace('\\', '/');
        string dstFolder = Path.Combine(dir.Root, "dst").Replace('\\', '/');
        Directory.CreateDirectory(dstFolder.Replace('/', Path.DirectorySeparatorChar));
        var (_, videoId, fileId) =
            await ExecutorTestSeed.SeedVideoAsync(db, srcFolder, "clip.mkv", "My Film");

        string oldFull = Path.Combine(dir.Root, "src", "clip.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(oldFull)!);
        File.WriteAllText(oldFull, "video-bytes");

        var plan = new RenamerPlan(videoId, RenamerFileKind.Video,
        [
            new RenamerPlanItem(fileId, srcFolder + "/clip.mkv", dstFolder + "/My Film.mkv",
                RenamerStatus.Move, "My Film.mkv", dstFolder),
        ]);
        return (dstFolder, plan, fileId);
    }

    private static RenamerExecutor NewExecutor(DbContext db) =>
        new(new CoveRenamerDataPort(db), new CapturingEventBus(), new FakeRevertJournal(), RunId);
}
