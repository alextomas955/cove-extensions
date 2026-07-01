using Cove.Data;
using Renamer.Execution;
using Renamer.Planner;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Execution;

/// <summary>
/// The cross-drive reverse-replay proofs — the mirror of
/// <see cref="CrossVolumeVerifyFailTests"/>, driven through <see cref="UndoReplayer"/> (NOT
/// <see cref="RenamerExecutor"/>) so the NEW→OLD direction is exercised. Each test sets up a
/// cross-volume pair via the <see cref="SubstDrive"/> helper (a distinct path root on the same
/// physical disk — no second drive; a live two-drive run is a manual cross-platform check), seeds a file at
/// the NEW (subst) location and a hand-built <see cref="RevertLog.RevertBatch"/> whose entry records
/// OldPath on the temp root and NewPath on the subst root, then reverse-replays it.
///
/// (a) <see cref="CrossDrive_Undo_RestoresByteForByte"/> — after undo the file is back at OLD
/// byte-for-byte and gone from NEW. (b) <see cref="BitFlipOnCopyBack_VerifyFails_FileNotLost"/> — the
/// reverse-direction centerpiece, the mirror of the forward data-loss proof: a bit-flip on the copy-back makes verify FAIL, the
/// reverse move reports !Moved → reported skip, and the file is NOT lost (the NEW copy survives, the
/// OLD slot is not half-written). (c) <see cref="CrossSaveThrows_RollsBackToNEW"/> — when the reverse
/// DB save throws AFTER a successful cross copy-back, the file is rolled back to NEW through
/// <see cref="CrossVolumeMover.RollbackAsync"/> and the entry is Failed.
///
/// SQLite (not EF-InMemory) so the unique index + Path recompute are faithful. Captions are out of
/// undo scope: nothing is asserted about sidecars; the reverse passes sidecars: null.
/// </summary>
[Trait("Tier", "Integration")]
public sealed class CrossVolumeUndoTests
{
    private const string PartialSuffix = ".renamer-partial";

    [Fact]
    public async Task CrossDrive_Undo_RestoresByteForByte()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // subst is Windows-only; a live cross-volume run is a manual cross-platform check.
        }

        using var oldDir = new TempDir();
        using var newDrive = new SubstDrive();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            const string original = "cross-undo bytes that must come back intact";
            // The file CURRENTLY lives at NEW (the renamerd location on the subst root); undo moves it
            // back to OLD on the temp root.
            string oldFull = Path.Combine(oldDir.Root, "raw.mkv");
            string newFull = Path.Combine(newDrive.Root, "My Film.mkv");
            File.WriteAllText(newFull, original);
            Assert.False(File.Exists(oldFull));

            var (port, batch, _) = await SeedReverseBatchAsync(db, oldDir.Root, newDrive.Root, oldFull, newFull);

            var undoBus = new CapturingEventBus();
            var replayer = new UndoReplayer(port, undoBus, new DiskMover(), cross: new CrossVolumeMover());
            var result = await replayer.RevertAsync(batch, default);

            Assert.Equal(1, result.Undone);
            Assert.Empty(result.Failed);
            Assert.Empty(result.Skipped);

            // Disk: file back at OLD byte-for-byte, gone from NEW, no leftover .partial.
            Assert.True(File.Exists(oldFull), "file restored to old (cross) path");
            Assert.Equal(original, File.ReadAllText(oldFull));
            Assert.False(File.Exists(newFull), "new path gone after a verified cross undo");
            Assert.False(File.Exists(newFull + PartialSuffix), "no .partial left behind");
            Assert.False(File.Exists(oldFull + PartialSuffix), "no .partial left behind");
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
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var oldDir = new TempDir();
        using var newDrive = new SubstDrive();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            const string original = "the real bytes that must survive a corrupted copy-back";
            string oldFull = Path.Combine(oldDir.Root, "raw.mkv");
            string newFull = Path.Combine(newDrive.Root, "My Film.mkv");
            File.WriteAllText(newFull, original);

            var (port, batch, _) = await SeedReverseBatchAsync(db, oldDir.Root, newDrive.Root, oldFull, newFull);

            // Inject the post-copy fault via the CrossVolumeMover test-only fault-seam ctor: flip one byte
            // of the copy-back .partial AFTER copy but BEFORE verify. Same length → caught only by the hash.
            var faultMover = new CrossVolumeMover((partial, _) =>
            {
                var bytes = File.ReadAllBytes(partial);
                Assert.NotEmpty(bytes);
                bytes[0] ^= 0xFF;
                File.WriteAllBytes(partial, bytes);
                return Task.CompletedTask;
            });

            var undoBus = new CapturingEventBus();
            var replayer = new UndoReplayer(port, undoBus, new DiskMover(), cross: faultMover);
            var result = await replayer.RevertAsync(batch, default);

            // The reverse move reports !Moved (VerifyFailed) → reported SKIP, never Undone.
            Assert.Equal(0, result.Undone);
            Assert.Empty(result.Failed);
            Assert.Single(result.Skipped);
            Assert.Empty(undoBus.Published);

            // CENTERPIECE: the file is NOT lost — the NEW copy survives byte-for-byte, and the OLD slot
            // is not half-written (no promoted file, no leftover .partial).
            Assert.True(File.Exists(newFull), "the NEW copy MUST survive a failed copy-back verify");
            Assert.Equal(original, File.ReadAllText(newFull));
            Assert.False(File.Exists(oldFull), "the OLD slot must not be half-written");
            Assert.False(File.Exists(oldFull + PartialSuffix), "no .partial promoted at OLD");
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
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var oldDir = new TempDir();
        using var newDrive = new SubstDrive();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            const string original = "bytes rolled back across the volume on a reverse save throw";
            string oldFull = Path.Combine(oldDir.Root, "raw.mkv");
            string newFull = Path.Combine(newDrive.Root, "My Film.mkv");
            File.WriteAllText(newFull, original);

            var (_, batch, _) = await SeedReverseBatchAsync(db, oldDir.Root, newDrive.Root, oldFull, newFull);

            // A port whose reverse save throws AFTER the cross copy-back succeeds → the rollback path runs
            // through CrossVolumeMover.RollbackAsync (cross-drive matching mover, D-02).
            var throwingPort = new ThrowOnSaveDataPort(db);
            var undoBus = new CapturingEventBus();
            var replayer = new UndoReplayer(throwingPort, undoBus, new DiskMover(), cross: new CrossVolumeMover());
            var result = await replayer.RevertAsync(batch, default);

            Assert.Equal(0, result.Undone);
            Assert.Single(result.Failed);
            Assert.Empty(undoBus.Published);

            // The file is rolled back to NEW across the volume (copy-back) with original bytes; OLD empty.
            Assert.True(File.Exists(newFull), "reverse save throw must roll the file back to NEW across the volume");
            Assert.Equal(original, File.ReadAllText(newFull));
            Assert.False(File.Exists(oldFull), "old slot must not hold the file after a cross rollback");
            Assert.False(File.Exists(newFull + PartialSuffix), "no leftover .partial after rollback");
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task DirMissing_Skip_NotRecreated()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var oldDir = new TempDir();
        using var newDrive = new SubstDrive();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            const string original = "bytes whose OLD directory has since vanished";
            // The recorded OLD dir is a subdirectory that does NOT exist on disk (deleted since the move).
            string missingOldDir = Path.Combine(oldDir.Root, "gone");
            string oldFull = Path.Combine(missingOldDir, "raw.mkv");
            string newFull = Path.Combine(newDrive.Root, "My Film.mkv");
            File.WriteAllText(newFull, original);
            Assert.False(Directory.Exists(missingOldDir), "precondition: the OLD dir must be absent");

            var (port, batch, _) = await SeedReverseBatchAsync(db, missingOldDir, newDrive.Root, oldFull, newFull);

            var undoBus = new CapturingEventBus();
            var replayer = new UndoReplayer(port, undoBus, new DiskMover(), cross: new CrossVolumeMover());
            var result = await replayer.RevertAsync(batch, default);

            // The missing OLD dir is a reported SKIP citing "original directory no longer exists".
            Assert.Equal(0, result.Undone);
            Assert.Empty(result.Failed);
            var skip = Assert.Single(result.Skipped);
            Assert.NotNull(skip.Reason);
            Assert.Contains("original directory no longer exists", skip.Reason, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(undoBus.Published);

            // The OLD dir is NOT recreated (we never restore into a possibly-relocated location), and the
            // file stays at NEW byte-for-byte.
            Assert.False(Directory.Exists(missingOldDir), "the missing OLD dir must NOT be recreated");
            Assert.False(File.Exists(oldFull), "no file may be restored into the missing OLD dir");
            Assert.True(File.Exists(newFull), "the file must stay at NEW");
            Assert.Equal(original, File.ReadAllText(newFull));
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task DestinationFull_or_Offline_ReportedSkip()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var oldDrive = new SubstDrive();
        using var newDir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            const string original = "bytes whose OLD drive goes offline before the restore";
            // The recorded OLD path lives on a SUBST drive; NEW lives on the temp root. We unmap the OLD
            // subst mapping BEFORE the replay so the reverse write target's drive is gone ("offline").
            string oldFull = Path.Combine(oldDrive.Root, "raw.mkv");
            string newFull = Path.Combine(newDir.Root, "My Film.mkv");
            File.WriteAllText(newFull, original);

            // Build the reverse batch (OLD on the subst root, NEW on the temp root) while OLD is still mapped.
            var (port, batch, _) = await SeedReverseBatchAsync(db, oldDrive.Root, newDir.Root, oldFull, newFull);

            // Take the OLD drive "offline": unmap the subst mapping so its root no longer resolves.
            oldDrive.Dispose();

            var undoBus = new CapturingEventBus();
            var replayer = new UndoReplayer(port, undoBus, new DiskMover(), cross: new CrossVolumeMover());
            var result = await replayer.RevertAsync(batch, default);

            // A gone OLD drive is a reported SKIP (the dir-missing Directory.Exists check returns false on
            // an unmapped drive — never throws — or the cross mover's IOException classify catches it).
            // NO catch(DriveNotFoundException): an offline drive surfaces as DirectoryNotFoundException : IOException.
            Assert.Equal(0, result.Undone);
            Assert.Empty(result.Failed);
            Assert.Single(result.Skipped);
            Assert.Empty(undoBus.Published);

            // The file is NOT lost — it stays at NEW byte-for-byte.
            Assert.True(File.Exists(newFull), "an offline OLD drive must leave the file at NEW");
            Assert.Equal(original, File.ReadAllText(newFull));
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task CrossReoccupiedOldSlot_SkippedNotClobbered()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var oldDir = new TempDir();
        using var newDrive = new SubstDrive();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            const string original = "the renamerd bytes that must NOT clobber a re-occupied OLD slot";
            const string squatter = "a different file already sitting in the OLD slot";
            string oldFull = Path.Combine(oldDir.Root, "raw.mkv");
            string newFull = Path.Combine(newDrive.Root, "My Film.mkv");
            File.WriteAllText(newFull, original);
            // The OLD slot is RE-OCCUPIED on disk (a different file now sits there).
            File.WriteAllText(oldFull, squatter);

            var (port, batch, _) = await SeedReverseBatchAsync(db, oldDir.Root, newDrive.Root, oldFull, newFull);

            var undoBus = new CapturingEventBus();
            var replayer = new UndoReplayer(port, undoBus, new DiskMover(), cross: new CrossVolumeMover());
            var result = await replayer.RevertAsync(batch, default);

            // The re-occupied OLD slot is a reported SKIP — never clobbered.
            Assert.Equal(0, result.Undone);
            Assert.Empty(result.Failed);
            Assert.Single(result.Skipped);
            Assert.Empty(undoBus.Published);

            // The squatter is intact, and the renamerd bytes survive at NEW.
            Assert.Equal(squatter, File.ReadAllText(oldFull));
            Assert.True(File.Exists(newFull), "the renamerd file must stay at NEW, not clobber the OLD slot");
            Assert.Equal(original, File.ReadAllText(newFull));
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    /// <summary>
    /// Seeds the DB so the file CURRENTLY sits at NEW (subst root, "My Film.mkv") and builds a
    /// <see cref="RevertLog.RevertBatch"/> whose single entry records OldPath on the temp root and
    /// NewPath on the subst root. The OLD folder is pre-seeded too so the reverse save's recomputed
    /// Path resolves to the OLD path. Returns the live port, the batch, and (videoId, fileId).
    /// </summary>
    private static async Task<(CoveRenamerDataPort Port, RevertLog.RevertBatch Batch, (int VideoId, int FileId) Ids)>
        SeedReverseBatchAsync(CoveContext db, string oldRoot, string newRoot, string oldFull, string newFull)
    {
        string oldFolder = oldRoot.Replace('\\', '/').TrimEnd('/');
        string newFolder = newRoot.Replace('\\', '/').TrimEnd('/');

        // Sanity: the two roots are DIFFERENT path roots → cross-volume.
        Assert.False(VolumeClassifier.SameVolume(oldFull, newFull),
            "precondition: the subst destination must be a different path root than the temp source");

        // Pre-seed the OLD folder (the reverse target) so GetOrCreateFolderId resolves it and the
        // recomputed Path after the reverse save equals the OLD path.
        var oldFolderRow = new Cove.Core.Entities.Folder { Path = oldFolder, ModTime = DateTime.UtcNow };
        db.Set<Cove.Core.Entities.Folder>().Add(oldFolderRow);
        await db.SaveChangesAsync();

        // Seed the file at its CURRENT (NEW) location: the NEW folder + the renamerd basename.
        var (_, videoId, fileId) =
            await ExecutorTestSeed.SeedVideoAsync(db, newFolder, "My Film.mkv", "My Film");

        var oldPath = oldFull.Replace('\\', '/');
        var newPath = newFull.Replace('\\', '/');
        var entry = new RevertLog.RevertEntry(videoId, fileId, oldPath, newPath);
        var batch = new RevertLog.RevertBatch(RenamerFileKind.Video, [entry]);

        return (new CoveRenamerDataPort(db), batch, (videoId, fileId));
    }

    /// <summary>A port whose reverse save always throws, forcing the UndoReplayer rollback path.</summary>
    private sealed class ThrowOnSaveDataPort(CoveContext db) : CoveRenamerDataPort(db)
    {
        public override Task<IReadOnlyList<SavedFile>> ApplyAndSaveAsync(
            IReadOnlyList<RenamerFileMutation> mutations, CancellationToken ct = default)
            => throw new InvalidOperationException("forced save failure");
    }
}
