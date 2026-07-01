using Cove.Core.Events;
using Renamer.Execution;
using Renamer.Options;
using Renamer.Planner;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Execution;

/// <summary>
/// Full renamer path: seed a Folder + VideoFile on a SQLite-in-memory <see cref="Cove.Data.CoveContext"/>
/// AND a matching file in a real <see cref="TempDir"/>, plan + execute an in-place renamer, and assert:
/// (a) the file is at the new on-disk path and absent at the old; (b) the DB VideoFile.Basename is the
/// new name and its RECOMPUTED Path == folder.Path + "/" + newBasename (Cove recomputed it on save — the
/// executor never set .Path); (c) the IEventBus received exactly one VideoUpdated for the entity id
/// (asserting the call ARGS, not merely that Publish was called).
///
/// Uses SQLite (relational) so the unique index + ComputeFilePaths are faithful; the real temp dir is
/// the disk tier. Both disposables are released in a finally.
/// </summary>
[Trait("Tier", "Integration")]
public sealed class RenamerExecutorIntegrationTests
{
    [Fact]
    public async Task MovesDiskAndUpdatesRecord_RecomputedPathMatches_PublishesEvent()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            // The Folder.Path is the real temp-dir root so disk + DB align on one absolute location.
            string folderPath = dir.Root.Replace('\\', '/');
            var (folderId, videoId, fileId) =
                await ExecutorTestSeed.SeedVideoAsync(db, folderPath, "raw clip.mkv", "My Film");

            // Real on-disk source matching the seeded row.
            string oldFull = Path.Combine(dir.Root, "raw clip.mkv");
            File.WriteAllText(oldFull, "video-bytes");

            var port = new CoveRenamerDataPort(db);
            var bus = new CapturingEventBus();
            var revertLog = new RevertLog(new FakeStore());
            var executor = new RenamerExecutor(port, bus, revertLog, new DiskMover());

            var options = new RenamerOptions { FilenameTemplate = "$title" }; // → "My Film.mkv"

            // Plan via the live port (read-only), then execute.
            var plan = await new RenamerPlanner(port).PlanAsync(RenamerFileKind.Video, videoId, options, default);
            var result = await executor.ExecuteAsync(plan, options, default);

            // (a) disk: new exists, old gone, content intact.
            string newFull = Path.Combine(dir.Root, "My Film.mkv");
            Assert.True(File.Exists(newFull), "renamerd file must exist on disk");
            Assert.False(File.Exists(oldFull), "old file must be gone");
            Assert.Equal("video-bytes", File.ReadAllText(newFull));

            // (b) DB: basename updated; Path RECOMPUTED (not set) to folder + new basename.
            var (basename, path) = await ExecutorTestSeed.ReadFileAsync(db, fileId);
            Assert.Equal("My Film.mkv", basename);
            Assert.Equal(folderPath + "/My Film.mkv", path);

            // Result buckets: one renamerd, none skipped/failed; revert-log row written.
            var renamerdItem = Assert.Single(result.Renamerd);
            Assert.Equal(RenamerStatus.Renamer, renamerdItem.Status);
            Assert.Empty(result.Failed);
            Assert.Empty(result.Skipped);
            var revert = Assert.Single(result.RevertLog);
            Assert.Equal(fileId, revert.FileId);
            Assert.EndsWith("My Film.mkv", revert.NewPath);

            // (c) event ARGS: exactly one VideoUpdated for this video id.
            var evt = Assert.IsType<EntityEvent>(Assert.Single(bus.Published));
            Assert.Equal(EventType.VideoUpdated, evt.Type);
            Assert.Equal("Video", evt.EntityType);
            Assert.Equal(videoId, evt.EntityId);

            // MOVE-01 explicit: the classifier verdict for the executed in-place pair is same-volume,
            // so the atomic DiskMover fast path (above) is the one that ran.
            Assert.True(VolumeClassifier.SameVolume(oldFull, newFull),
                "an in-place renamer under one root must classify as same-volume (DiskMover path)");
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    /// <summary>
    /// MOVE-01 cross-branch: force the executor's volume branch to take the verified
    /// <see cref="CrossVolumeMover"/> path by moving a file from a real <see cref="TempDir"/> to a
    /// SUBST-mapped second root (a distinct <see cref="Path.GetPathRoot(string)"/> on the same physical
    /// volume — no second drive). Assert the cross move executed end-to-end: the source is gone, the
    /// destination exists with the original content, the DB Basename + ParentFolderId + recomputed Path
    /// are updated, a revert-log row is written, and one VideoUpdated event fired.
    /// </summary>
    [Fact]
    public async Task CrossVolumeBranch_HappyMove_UsesCrossMover_DiskAndDbUpdated()
    {
        using var src = new TempDir();
        using var dst = new SubstDrive();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string srcFolder = src.Root.Replace('\\', '/');
            string dstFolder = dst.Root.Replace('\\', '/').TrimEnd('/'); // "P:" (root, distinct from src)
            var (folderId, videoId, fileId) =
                await ExecutorTestSeed.SeedVideoAsync(db, srcFolder, "clip.mkv", "My Film");

            string oldFull = Path.Combine(src.Root, "clip.mkv");
            File.WriteAllText(oldFull, "cross-bytes");

            // Sanity: the source and the subst destination are on DIFFERENT path roots → cross-volume.
            string newFull = dstFolder + "/My Film.mkv";
            Assert.False(VolumeClassifier.SameVolume(srcFolder + "/clip.mkv", newFull),
                "precondition: subst destination must be a different path root than the temp source");

            var port = new CoveRenamerDataPort(db);
            var bus = new CapturingEventBus();
            var revertLog = new RevertLog(new FakeStore());
            // Inject a real CrossVolumeMover (the production mover) so the cross branch runs end-to-end.
            var executor = new RenamerExecutor(port, bus, revertLog, new DiskMover(), new CrossVolumeMover());

            // Explicit MOVE plan: source on the temp drive, target folder on the subst drive.
            var plan = new RenamerPlan(videoId, RenamerFileKind.Video,
            [
                new RenamerPlanItem(fileId, srcFolder + "/clip.mkv", newFull,
                    RenamerStatus.Move, "My Film.mkv", dstFolder),
            ]);

            var result = await executor.ExecuteAsync(plan, new RenamerOptions(), default);

            // Disk: dest present with original content, source gone, no .partial left behind.
            string newOnDisk = Path.Combine(dst.Root, "My Film.mkv");
            Assert.True(File.Exists(newOnDisk), "cross-moved file must exist at the dest root");
            Assert.Equal("cross-bytes", File.ReadAllText(newOnDisk));
            Assert.False(File.Exists(oldFull), "source must be deleted (delete-source-last) after a verified cross move");
            Assert.False(File.Exists(newOnDisk + ".renamer-partial"), "no leftover .partial");

            // Result buckets: one moved, none skipped/failed; revert-log row written.
            var movedItem = Assert.Single(result.Renamerd);
            Assert.Equal(RenamerStatus.Move, movedItem.Status);
            Assert.Empty(result.Failed);
            Assert.Empty(result.Skipped);
            Assert.Single(result.RevertLog);

            // DB: Basename updated, ParentFolderId moved to the (new) dest folder, recomputed Path matches.
            var (basename, path) = await ExecutorTestSeed.ReadFileAsync(db, fileId);
            Assert.Equal("My Film.mkv", basename);
            Assert.Equal(dstFolder + "/My Film.mkv", path);

            // Event ARGS: exactly one VideoUpdated for this video id.
            var evt = Assert.IsType<EntityEvent>(Assert.Single(bus.Published));
            Assert.Equal(EventType.VideoUpdated, evt.Type);
            Assert.Equal(videoId, evt.EntityId);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    /// <summary>
    /// D-05 / MOVE-05 cross-path rollback: a VERIFIED cross-volume move whose subsequent DB save throws
    /// (a forced <c>(ParentFolderId, Basename)</c> unique-index clash, pre-check bypassed via
    /// <see cref="CollisionBlindDataPort"/>) must roll back through <see cref="CrossVolumeMover.RollbackAsync"/>
    /// — copy the bytes back across the volume and restore the source — leaving disk and DB consistent.
    /// Re-proves disk-first/DB-second for the cross path.
    /// </summary>
    [Fact]
    public async Task CrossVolumeSaveFailure_RollsBackThroughCrossMover_SourceRestored()
    {
        using var src = new TempDir();
        using var dst = new SubstDrive();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string srcFolder = src.Root.Replace('\\', '/');
            string dstFolder = dst.Root.Replace('\\', '/').TrimEnd('/');

            var (srcFolderId, videoId, fileA) =
                await ExecutorTestSeed.SeedVideoAsync(db, srcFolder, "a.mkv", "Film A");

            // Pre-seed the DEST folder (same Path the executor will GetOrCreate) holding a row that
            // already occupies "taken.mkv", so the cross-move's save of (destFolderId, "taken.mkv")
            // hits the unique index and throws — AFTER the verified cross move has happened.
            var destFolder = new Cove.Core.Entities.Folder { Path = dstFolder, ModTime = DateTime.UtcNow };
            db.Set<Cove.Core.Entities.Folder>().Add(destFolder);
            await db.SaveChangesAsync();
            await ExecutorTestSeed.SeedAdditionalFileAsync(db, destFolder.Id, videoId, "taken.mkv");

            string oldA = Path.Combine(src.Root, "a.mkv");
            File.WriteAllText(oldA, "A-bytes");
            string newOnDisk = Path.Combine(dst.Root, "taken.mkv");
            Assert.False(File.Exists(newOnDisk), "precondition: dest free so the CROSS move happens before the save");

            string newFull = dstFolder + "/taken.mkv";
            Assert.False(VolumeClassifier.SameVolume(srcFolder + "/a.mkv", newFull),
                "precondition: cross-volume pair");

            var plan = new RenamerPlan(videoId, RenamerFileKind.Video,
            [
                new RenamerPlanItem(fileA, srcFolder + "/a.mkv", newFull,
                    RenamerStatus.Move, "taken.mkv", dstFolder),
            ]);

            var executor = new RenamerExecutor(
                new CollisionBlindDataPort(db), new CapturingEventBus(), new RevertLog(new FakeStore()),
                new DiskMover(), new CrossVolumeMover());

            var result = await executor.ExecuteAsync(plan, new RenamerOptions(), default);

            // The save threw after the verified cross move → item failed with a rollback reason.
            var failedItem = Assert.Single(result.Failed);
            Assert.Equal(RenamerStatus.Failed, failedItem.Status);
            Assert.Contains("rolled back", failedItem.Reason);
            Assert.Empty(result.Renamerd);
            Assert.Empty(result.RevertLog);

            // (a) the source is RESTORED across the volume (copy-back) with its original content.
            Assert.True(File.Exists(oldA), "cross rollback must copy the file back to its old path");
            Assert.Equal("A-bytes", File.ReadAllText(oldA));
            // and is NOT left on the dest volume.
            Assert.False(File.Exists(newOnDisk), "rolled-back file must not linger at the dest");
            Assert.False(File.Exists(newOnDisk + ".renamer-partial"), "no leftover .partial after rollback");

            // (c) the DB row still carries the OLD basename + source folder — disk and DB consistent.
            var (basenameA, pathA) = await ExecutorTestSeed.ReadFileAsync(db, fileA);
            Assert.Equal("a.mkv", basenameA);
            Assert.Equal(srcFolder + "/a.mkv", pathA);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    /// <summary>
    /// WR-03 regression: when the cross-volume rollback FAILS to fully restore (here the old source slot
    /// is re-occupied before the copy-back runs, so <see cref="CrossVolumeMover.RollbackAsync"/> records a
    /// "rollback target re-occupied" warning rather than restoring), the executor must NOT report a clean
    /// "file rolled back". It must surface the rollback warnings so the disk/DB divergence is visible —
    /// the previous code discarded the warnings list and falsely claimed a rollback that did not happen.
    /// </summary>
    [Fact]
    public async Task CrossVolumeSaveFailure_RollbackWarnings_Surfaced_NotSilentlyRolledBack()
    {
        using var src = new TempDir();
        using var dst = new SubstDrive();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string srcFolder = src.Root.Replace('\\', '/');
            string dstFolder = dst.Root.Replace('\\', '/').TrimEnd('/');

            var (srcFolderId, videoId, fileA) =
                await ExecutorTestSeed.SeedVideoAsync(db, srcFolder, "a.mkv", "Film A");

            string oldA = Path.Combine(src.Root, "a.mkv");
            File.WriteAllText(oldA, "A-bytes");
            string newOnDisk = Path.Combine(dst.Root, "My Film.mkv");

            string newFull = dstFolder + "/My Film.mkv";
            Assert.False(VolumeClassifier.SameVolume(srcFolder + "/a.mkv", newFull),
                "precondition: cross-volume pair");

            var plan = new RenamerPlan(videoId, RenamerFileKind.Video,
            [
                new RenamerPlanItem(fileA, srcFolder + "/a.mkv", newFull,
                    RenamerStatus.Move, "My Film.mkv", dstFolder),
            ]);

            // A data port whose save re-occupies the OLD source slot (so the rollback copy-back finds the
            // target taken → "rollback target re-occupied" warning) and then throws.
            var port = new ReoccupyOldSlotThenThrowDataPort(db, oldA);
            var executor = new RenamerExecutor(
                port, new CapturingEventBus(), new RevertLog(new FakeStore()),
                new DiskMover(), new CrossVolumeMover());

            var result = await executor.ExecuteAsync(plan, new RenamerOptions(), default);

            var failedItem = Assert.Single(result.Failed);
            Assert.Equal(RenamerStatus.Failed, failedItem.Status);
            // The failed reason must report the INCOMPLETE rollback + the warning, NOT a clean "rolled back".
            Assert.Contains("rollback INCOMPLETE", failedItem.Reason);
            Assert.Contains("rollback target re-occupied", failedItem.Reason);
            Assert.DoesNotContain("file rolled back", failedItem.Reason);
            Assert.Empty(result.Renamerd);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    /// <summary>
    /// Test-only port: on save, re-creates a file at <c>oldSlot</c> (simulating the source slot getting
    /// re-occupied between the move and the rollback) and then throws, so the subsequent rollback's
    /// copy-back finds its target taken and records a warning instead of restoring.
    /// </summary>
    private sealed class ReoccupyOldSlotThenThrowDataPort(Cove.Data.CoveContext db, string oldSlot)
        : CoveRenamerDataPort(db)
    {
        public override Task<IReadOnlyList<SavedFile>> ApplyAndSaveAsync(
            IReadOnlyList<RenamerFileMutation> mutations, CancellationToken ct = default)
        {
            File.WriteAllText(oldSlot, "intruder bytes re-occupying the old slot");
            throw new InvalidOperationException("forced save failure");
        }
    }
}
