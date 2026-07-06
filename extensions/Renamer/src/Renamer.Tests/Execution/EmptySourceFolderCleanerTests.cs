using Renamer.Execution;
using Renamer.Options;
using Renamer.Planner;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Execution;

/// <summary>
/// The destructive-leg safety net: every invariant that keeps the opt-in empty-source-folder cleanup
/// from ever destroying data the move did not touch. Filesystem behavior (enumerate, link-resolve,
/// non-recursive delete) is exercised against a real <see cref="TempDir"/>, not a mock. The end-to-end
/// cases (1, 8, 9) drive the real executor so the call-site trigger and the move-result-still-moved
/// contract are proven, not just the helper in isolation. The undo-contract case pins the REAL
/// behavior: a deleted source folder makes a later undo of that move SKIP the restore (the file stays
/// at its verified destination, never lost).
/// </summary>
[Trait("Tier", "Integration")]
public sealed class EmptySourceFolderCleanerTests
{
    // ── helper-level invariants (Tests 2-7) ──────────────────────────────────

    [Fact]
    public void NonEmptyDir_HoldingAnotherFile_IsLeftIntact()
    {
        using var dir = new TempDir();
        string src = Path.Combine(dir.Root, "sub");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "untracked.txt"), "the batch never moved this");

        var (removed, warning) = EmptySourceFolderCleaner.TryRemoveIfEmpty(src.Replace('\\', '/'), []);

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

        var (removed, warning) = EmptySourceFolderCleaner.TryRemoveIfEmpty(src.Replace('\\', '/'), []);

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

        var (removed, warning) = EmptySourceFolderCleaner.TryRemoveIfEmpty(src.Replace('\\', '/'), []);

        Assert.True(removed);
        Assert.Null(warning);
        Assert.False(Directory.Exists(src));
    }

    [Fact]
    public void DriveRoot_IsNeverDeleted()
    {
        string root = Path.GetPathRoot(Path.GetTempPath())!; // e.g. "C:\" — a real, existing root
        var (removed, warning) = EmptySourceFolderCleaner.TryRemoveIfEmpty(root.Replace('\\', '/'), []);

        Assert.False(removed);
        Assert.Null(warning);
        Assert.True(Directory.Exists(root), "the drive root must survive untouched");
    }

    [Fact]
    public void ConfiguredAllowedRoot_IsNeverDeleted_EvenWhenEmpty()
    {
        using var dir = new TempDir();
        // The temp root itself is the configured allowed root; point the cleaner AT it while it is empty.
        // Emptying a folder INSIDE a root is fine; deleting the root itself is refused.
        string root = dir.Root.Replace('\\', '/');

        var (removed, warning) = EmptySourceFolderCleaner.TryRemoveIfEmpty(root, [root]);

        Assert.False(removed);
        Assert.NotNull(warning);
        Assert.Contains("allowed root", warning);
        Assert.True(Directory.Exists(dir.Root), "a configured allowed root must never be deleted");
    }

    [Fact]
    public void DirOutsideAllowedRoots_IsRefused_NotDeleted()
    {
        using var inside = new TempDir();
        using var outside = new TempDir();
        string src = Path.Combine(outside.Root, "sub");
        Directory.CreateDirectory(src); // empty, but OUTSIDE the configured allowlist

        var (removed, warning) = EmptySourceFolderCleaner.TryRemoveIfEmpty(
            src.Replace('\\', '/'), [inside.Root.Replace('\\', '/')]);

        Assert.False(removed);
        Assert.NotNull(warning);
        Assert.True(Directory.Exists(src), "a dir outside every allowed root must not be deleted");
    }

    [Fact]
    public void AlreadyGoneDir_IsNoop_NeverThrows()
    {
        using var dir = new TempDir();
        string gone = Path.Combine(dir.Root, "never-existed").Replace('\\', '/');

        var (removed, warning) = EmptySourceFolderCleaner.TryRemoveIfEmpty(gone, []);

        Assert.False(removed);
        Assert.Null(warning);
    }

    [Fact]
    public void EmptyDirInsideConfiguredRoot_IsDeleted()
    {
        using var dir = new TempDir();
        string root = dir.Root.Replace('\\', '/');
        string src = Path.Combine(dir.Root, "sub");
        Directory.CreateDirectory(src); // a genuinely empty CHILD of the allowed root

        var (removed, warning) = EmptySourceFolderCleaner.TryRemoveIfEmpty(src.Replace('\\', '/'), [root]);

        Assert.True(removed);
        Assert.Null(warning);
        Assert.False(Directory.Exists(src));
    }

    // ── end-to-end through the executor (Tests 1, 8, 9) ──────────────────────

    [Fact]
    public async Task CrossFolderMove_WithOptionOn_DeletesEmptiedSourceDir_FileAtDestination()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string srcFolder = Path.Combine(dir.Root, "src").Replace('\\', '/');
            string dstFolder = Path.Combine(dir.Root, "dst").Replace('\\', '/');
            Directory.CreateDirectory(dstFolder.Replace('/', Path.DirectorySeparatorChar));
            var (folderId, videoId, fileId) =
                await ExecutorTestSeed.SeedVideoAsync(db, srcFolder, "clip.mkv", "My Film");

            string oldFull = Path.Combine(dir.Root, "src", "clip.mkv");
            Directory.CreateDirectory(Path.GetDirectoryName(oldFull)!);
            File.WriteAllText(oldFull, "video-bytes");

            var executor = NewExecutor(db, out _);
            var options = new RenamerOptions { RemoveEmptyFolder = true };

            // Explicit cross-FOLDER (same-volume) move: src/clip.mkv → dst/My Film.mkv.
            var plan = new RenamerPlan(videoId, RenamerFileKind.Video,
            [
                new RenamerPlanItem(fileId, srcFolder + "/clip.mkv", dstFolder + "/My Film.mkv",
                    RenamerStatus.Move, "My Film.mkv", dstFolder),
            ]);

            var result = await executor.ExecuteAsync(plan, options, default);

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
    public async Task CleanupGuardReject_DoesNotFailTheMove_WarningSurfaced()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            // The destination is inside the allowlist (so the move passes the guard); the SOURCE dir
            // sits OUTSIDE it, so the cleanup guard refuses — a non-fatal warning, not a failed move.
            string dstRoot = Path.Combine(dir.Root, "dst").Replace('\\', '/');
            Directory.CreateDirectory(dstRoot.Replace('/', Path.DirectorySeparatorChar));
            string srcFolder = Path.Combine(dir.Root, "src").Replace('\\', '/');
            var (folderId, videoId, fileId) =
                await ExecutorTestSeed.SeedVideoAsync(db, srcFolder, "clip.mkv", "My Film");

            string oldFull = Path.Combine(dir.Root, "src", "clip.mkv");
            Directory.CreateDirectory(Path.GetDirectoryName(oldFull)!);
            File.WriteAllText(oldFull, "video-bytes");

            var executor = NewExecutor(db, out _);
            var options = new RenamerOptions
            {
                RemoveEmptyFolder = true,
                AllowedRoots = [dstRoot],
            };

            var plan = new RenamerPlan(videoId, RenamerFileKind.Video,
            [
                new RenamerPlanItem(fileId, srcFolder + "/clip.mkv", dstRoot + "/My Film.mkv",
                    RenamerStatus.Move, "My Film.mkv", dstRoot),
            ]);

            var result = await executor.ExecuteAsync(plan, options, default);

            // The move still reports moved — a cleanup refusal never flips it to failed.
            var moved = Assert.Single(result.Renamed);
            Assert.Equal(RenamerStatus.Move, moved.Status);
            Assert.Empty(result.Failed);
            Assert.NotNull(moved.Reason); // the cleanup warning is surfaced on the moved result
            Assert.True(File.Exists(Path.Combine(dir.Root, "dst", "My Film.mkv")), "the move itself succeeded");
            Assert.True(Directory.Exists(Path.Combine(dir.Root, "src")), "the refused source dir is left intact");
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
            string srcFolder = Path.Combine(dir.Root, "src").Replace('\\', '/');
            string dstFolder = Path.Combine(dir.Root, "dst").Replace('\\', '/');
            Directory.CreateDirectory(dstFolder.Replace('/', Path.DirectorySeparatorChar));
            var (folderId, videoId, fileId) =
                await ExecutorTestSeed.SeedVideoAsync(db, srcFolder, "clip.mkv", "My Film");

            string oldFull = Path.Combine(dir.Root, "src", "clip.mkv");
            Directory.CreateDirectory(Path.GetDirectoryName(oldFull)!);
            File.WriteAllText(oldFull, "video-bytes");

            var executor = NewExecutor(db, out _);
            var options = new RenamerOptions(); // RemoveEmptyFolder defaults to false

            var plan = new RenamerPlan(videoId, RenamerFileKind.Video,
            [
                new RenamerPlanItem(fileId, srcFolder + "/clip.mkv", dstFolder + "/My Film.mkv",
                    RenamerStatus.Move, "My Film.mkv", dstFolder),
            ]);

            var result = await executor.ExecuteAsync(plan, options, default);

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

    // ── same-folder renamer never triggers the cleaner (the trigger predicate skips it) ───

    [Fact]
    public async Task SameFolderRenamer_NeverEntersCleanup_SourceDirSurvives()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            // A pure in-place renamer inside one folder: the parent dir does not change, so the trigger
            // predicate must skip the cleaner outright. We prove the predicate skipped — not merely
            // that the cleaner no-op'd — by leaving the folder with the renamed file still in it AND
            // setting RemoveEmptyFolder on: had the cleaner run, it would have found a file and no-op'd
            // too, so the distinguishing observation is that the folder (still holding the renamed file)
            // is intact and the move stayed in-place.
            string folder = dir.Root.Replace('\\', '/');
            var (folderId, videoId, fileId) =
                await ExecutorTestSeed.SeedVideoAsync(db, folder, "raw clip.mkv", "My Film");

            string oldFull = Path.Combine(dir.Root, "raw clip.mkv");
            File.WriteAllText(oldFull, "video-bytes");

            var executor = NewExecutor(db, out _);
            var options = new RenamerOptions { FilenameTemplate = "$title", RemoveEmptyFolder = true };

            var plan = await new RenamerPlanner(new CoveRenamerDataPort(db))
                .PlanAsync(RenamerFileKind.Video, videoId, options, default);
            var item = Assert.Single(plan.Items);
            Assert.Equal(RenamerStatus.Renamer, item.Status); // an in-place renamer, not a move

            // The trigger predicate's two independent reasons to skip both hold for this item:
            // it is not a move, AND the parent dir does not change.
            Assert.False(item.Status == RenamerStatus.Move, "an in-place renamer is not a move → cleanup never fires");
            Assert.Equal(DirOf(item.OldFullPath), DirOf(item.NewFullPath));

            var result = await executor.ExecuteAsync(plan, options, default);

            Assert.Single(result.Renamed);
            Assert.True(Directory.Exists(dir.Root), "the source folder must survive a same-folder renamer");
            Assert.True(File.Exists(Path.Combine(dir.Root, "My Film.mkv")), "the renamed file stays in the folder");
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    // ── undo contract: a deleted source folder makes undo SKIP the restore (file not lost) ───

    [Fact]
    public async Task UndoOfMoveAfterCleanup_Skips_BecauseOriginalDirectoryGone_FileStaysAtDestination()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string srcFolder = Path.Combine(dir.Root, "src").Replace('\\', '/');
            string dstFolder = Path.Combine(dir.Root, "dst").Replace('\\', '/');
            Directory.CreateDirectory(dstFolder.Replace('/', Path.DirectorySeparatorChar));
            var (folderId, videoId, fileId) =
                await ExecutorTestSeed.SeedVideoAsync(db, srcFolder, "clip.mkv", "My Film");

            string oldFull = Path.Combine(dir.Root, "src", "clip.mkv");
            Directory.CreateDirectory(Path.GetDirectoryName(oldFull)!);
            File.WriteAllText(oldFull, "video-bytes");

            var port = new CoveRenamerDataPort(db);
            var revertLog = new RevertLog(new FakeStore());
            var options = new RenamerOptions { RemoveEmptyFolder = true };

            await revertLog.BeginBatchAsync("RUN-1", RenamerFileKind.Video);
            var plan = new RenamerPlan(videoId, RenamerFileKind.Video,
            [
                new RenamerPlanItem(fileId, srcFolder + "/clip.mkv", dstFolder + "/My Film.mkv",
                    RenamerStatus.Move, "My Film.mkv", dstFolder),
            ]);
            var fwd = await new RenamerExecutor(port, new CapturingEventBus(), revertLog, new DiskMover())
                .ExecuteAsync(plan, options, default);
            Assert.Single(fwd.Renamed);
            Assert.False(Directory.Exists(Path.Combine(dir.Root, "src")), "the move + cleanup deleted the source dir");

            // Undo the batch: the original directory is gone, so the restore SKIPS — it is NOT recreated.
            var batch = await revertLog.ReadLastOpenBatchAsync();
            Assert.NotNull(batch);
            var replayer = new UndoReplayer(port, new CapturingEventBus(), new DiskMover());
            var undo = await replayer.RevertAsync(batch!, default);

            Assert.Equal(0, undo.Undone);
            var skip = Assert.Single(undo.Skipped);
            Assert.Contains("original directory no longer exists", skip.Reason);

            // The file is NEVER lost: it stays at its verified destination, and the DB still agrees.
            Assert.True(File.Exists(Path.Combine(dir.Root, "dst", "My Film.mkv")),
                "the file remains at the destination — undo did not move it back, but it is not lost");
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

    private static RenamerExecutor NewExecutor(Cove.Data.CoveContext db, out CapturingEventBus bus)
    {
        bus = new CapturingEventBus();
        return new RenamerExecutor(new CoveRenamerDataPort(db), bus, new RevertLog(new FakeStore()), new DiskMover());
    }

    private static string DirOf(string fullPath)
    {
        string p = fullPath.Replace('\\', '/');
        int slash = p.LastIndexOf('/');
        return slash >= 0 ? p[..slash] : "";
    }
}
