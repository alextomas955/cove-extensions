using Microsoft.EntityFrameworkCore;
using Renamer.Execution;
using Renamer.Planner;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Execution.CrossVolume;

[Collection(SubstDriveScope.CollectionName)]
public sealed class CrossVolumeUndoTests
{
    [Fact]
    public async Task CrossDrive_Undo_RestoresByteForByte()
    {
        Assert.SkipUnless(SecondVolume.IsAvailable, SecondVolume.UnavailableReason);

        using var oldDir = new TempDir();
        using var newDrive = new SecondVolume();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            const string original = "cross-undo bytes that must come back intact";
            // The file currently lives at new (the renamed location on the second volume); undo moves it
            // back to old on the temp root.
            string oldFull = Path.Combine(oldDir.Root, "raw.mkv");
            string newFull = Path.Combine(newDrive.Root, "My Film.mkv");
            File.WriteAllText(newFull, original);
            Assert.False(File.Exists(oldFull));

            var (port, batch, _) = await SeedReverseBatchAsync(db, oldDir.Root, newDrive.Root, oldFull, newFull);

            var minted = new List<string>();
            var undoBus = new CapturingEventBus();
            var replayer = new UndoReplayer(port, undoBus, cross: new CrossVolumeMover(Recorder(minted)));
            var result = await replayer.RevertAsync(batch, default);

            Assert.Equal(1, result.Undone);
            Assert.Empty(result.Failed);
            Assert.Empty(result.Skipped);

            // Disk: file back at old byte-for-byte, gone from new, no in-flight copy anywhere.
            Assert.True(File.Exists(oldFull), "file restored to old (cross) path");
            Assert.Equal(original, File.ReadAllText(oldFull));
            Assert.False(File.Exists(newFull), "new path gone after a verified cross undo");
            AssertMintedPathsGone(minted);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task BitFlipOnCopyBack_VerifyFails_FileNotLost()
    {
        Assert.SkipUnless(SecondVolume.IsAvailable, SecondVolume.UnavailableReason);

        using var oldDir = new TempDir();
        using var newDrive = new SecondVolume();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            const string original = "the real bytes that must survive a corrupted copy-back";
            string oldFull = Path.Combine(oldDir.Root, "raw.mkv");
            string newFull = Path.Combine(newDrive.Root, "My Film.mkv");
            File.WriteAllText(newFull, original);

            var (port, batch, _) = await SeedReverseBatchAsync(db, oldDir.Root, newDrive.Root, oldFull, newFull);

            // Inject the post-copy fault via the CrossVolumeMover test-only fault-seam ctor: flip one
            // byte of the copy-back's in-flight file after copy but before verify. Same length, so caught
            // only by the hash. The seam also records the minted path for the leftover assertion.
            var minted = new List<string>();
            var faultMover = new CrossVolumeMover((path, _) =>
            {
                minted.Add(path);
                var bytes = File.ReadAllBytes(path);
                Assert.NotEmpty(bytes);
                bytes[0] ^= 0xFF;
                File.WriteAllBytes(path, bytes);
                return Task.CompletedTask;
            });

            var undoBus = new CapturingEventBus();
            var replayer = new UndoReplayer(port, undoBus, cross: faultMover);
            var result = await replayer.RevertAsync(batch, default);

            // The reverse move reports VerifyFailed, a reported skip, never Undone.
            Assert.Equal(0, result.Undone);
            Assert.Empty(result.Failed);
            Assert.Equal(UndoStopReason.ReverseMoveVerifyFailed, Assert.Single(result.Skipped).Stop);
            Assert.Empty(undoBus.Published);

            // centerpiece: the file is not lost - the new copy survives byte-for-byte, and the old slot
            // is not half-written (no promoted file, no leftover in-flight copy).
            Assert.True(File.Exists(newFull), "the NEW copy MUST survive a failed copy-back verify");
            Assert.Equal(original, File.ReadAllText(newFull));
            Assert.False(File.Exists(oldFull), "the OLD slot must not be half-written");
            AssertMintedPathsGone(minted);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task CrossSaveThrows_RollsBackToNEW()
    {
        Assert.SkipUnless(SecondVolume.IsAvailable, SecondVolume.UnavailableReason);

        using var oldDir = new TempDir();
        using var newDrive = new SecondVolume();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            const string original = "bytes rolled back across the volume on a reverse save throw";
            string oldFull = Path.Combine(oldDir.Root, "raw.mkv");
            string newFull = Path.Combine(newDrive.Root, "My Film.mkv");
            File.WriteAllText(newFull, original);

            var (_, batch, _) = await SeedReverseBatchAsync(db, oldDir.Root, newDrive.Root, oldFull, newFull);

            // A port whose reverse save throws after the cross copy-back succeeds, so the rollback path runs
            // through CrossVolumeMover.RollbackAsync (cross-drive matching mover).
            var throwingPort = new ThrowOnSaveDataPort(db);
            var minted = new List<string>();
            var undoBus = new CapturingEventBus();
            var replayer = new UndoReplayer(throwingPort, undoBus, cross: new CrossVolumeMover(Recorder(minted)));
            var result = await replayer.RevertAsync(batch, default);

            Assert.Equal(0, result.Undone);
            Assert.Single(result.Failed);
            Assert.Empty(undoBus.Published);

            // The file is rolled back to new across the volume (copy-back) with original bytes; old empty.
            Assert.True(File.Exists(newFull), "reverse save throw must roll the file back to NEW across the volume");
            Assert.Equal(original, File.ReadAllText(newFull));
            Assert.False(File.Exists(oldFull), "old slot must not hold the file after a cross rollback");
            AssertMintedPathsGone(minted);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task AnOldVolumeGoneOffline_IsASkip_AndTheFileStaysAtItsNewPath()
    {
        Assert.SkipUnless(SecondVolume.IsAvailable, SecondVolume.UnavailableReason);

        using var oldDrive = new SecondVolume();
        using var newDir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            const string original = "bytes whose OLD drive goes offline before the restore";
            // The recorded old path lives on the second volume; new lives on the temp root. Disposing
            // the second volume before the replay takes the restore target away: on Windows it unmaps
            // the subst drive, elsewhere it removes the directory.
            string oldFull = Path.Combine(oldDrive.Root, "raw.mkv");
            string newFull = Path.Combine(newDir.Root, "My Film.mkv");
            File.WriteAllText(newFull, original);

            var (port, batch, _) = await SeedReverseBatchAsync(db, oldDrive.Root, newDir.Root, oldFull, newFull);

            oldDrive.Dispose();

            var undoBus = new CapturingEventBus();
            var replayer = new UndoReplayer(port, undoBus, cross: new CrossVolumeMover());
            var result = await replayer.RevertAsync(batch, default);

            // Directory.Exists answers false on an unmapped drive without throwing, so an offline drive
            // stops at the missing-directory check before any move is tried.
            Assert.Equal(0, result.Undone);
            Assert.Empty(result.Failed);
            Assert.Equal(UndoStopReason.OriginalDirectoryUnavailable, Assert.Single(result.Skipped).Stop);
            Assert.Empty(undoBus.Published);

            // The file is not lost - it stays at new byte-for-byte.
            Assert.True(File.Exists(newFull), "an offline OLD drive must leave the file at NEW");
            Assert.Equal(original, File.ReadAllText(newFull));
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    // Seeds the DB so the file currently sits at new ("My Film.mkv" under newRoot) and builds a
    // RevertBatch whose single row records OldPath under oldRoot.
    // The old folder is pre-seeded too so the reverse save's recomputed Path resolves to the old
    // path. Returns the live port, the batch, and (videoId, fileId).
    private static async Task<(CoveRenamerDataPort Port, RevertBatch Batch, (int VideoId, int FileId) Ids)>
        SeedReverseBatchAsync(DbContext db, string oldRoot, string newRoot, string oldFull, string newFull)
    {
        string oldFolder = oldRoot.Replace('\\', '/').TrimEnd('/');
        string newFolder = newRoot.Replace('\\', '/').TrimEnd('/');

        Assert.False(VolumeClassifier.SameVolume(oldFull, newFull),
            "precondition: the two roots must be on different volumes");

        // Pre-seed the old folder (the reverse target) so GetOrCreateFolderId resolves it and the
        // recomputed Path after the reverse save equals the old path.
        var oldFolderRow = new Cove.Core.Entities.Folder { Path = oldFolder, ModTime = DateTime.UtcNow };
        db.Set<Cove.Core.Entities.Folder>().Add(oldFolderRow);
        await db.SaveChangesAsync();

        // Seed the file at its current (new) location: the new folder + the renamed basename.
        var (_, videoId, fileId) =
            await ExecutorTestSeed.SeedVideoAsync(db, newFolder, "My Film.mkv", "My Film");

        var oldPath = oldFull.Replace('\\', '/');
        var entry = new RevertRow("RUN-1", Seq: 1, videoId, fileId, oldPath, SidecarsJson: "");
        var batch = new RevertBatch("RUN-1", RenamerFileKind.Video, [entry]);

        return (new CoveRenamerDataPort(db), batch, (videoId, fileId));
    }

    // A post-copy seam that only records the path production minted, leaving the copy untouched -
    // the mover's real behaviour, plus the observation the test needs.
    private static Func<string, CancellationToken, Task> Recorder(List<string> minted) =>
        (inFlight, _) =>
        {
            minted.Add(inFlight);
            return Task.CompletedTask;
        };

    private static void AssertMintedPathsGone(List<string> minted)
    {
        // The seam must actually have fired, or the loop below asserts nothing at all.
        Assert.NotEmpty(minted);
        foreach (var path in minted)
        {
            Assert.False(File.Exists(path), $"no in-flight copy may be left at {path}");
        }
    }
}
