using Cove.Core.Entities;
using Cove.Core.Events;
using Microsoft.EntityFrameworkCore;
using Renamer.Execution;
using Renamer.Jobs;
using Renamer.Options;
using Renamer.Planner;
using Renamer.Tests.Execution.Collisions;
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
[Trait("Tier", "L1")]
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
            var (_, videoId, fileId) =
                await ExecutorTestSeed.SeedVideoAsync(db, folderPath, "raw clip.mkv", "My Film");

            // Real on-disk source matching the seeded row.
            string oldFull = Path.Combine(dir.Root, "raw clip.mkv");
            File.WriteAllText(oldFull, "video-bytes");

            var port = new CoveRenamerDataPort(db);
            var bus = new CapturingEventBus();
            var journal = new FakeRevertJournal();
            var executor = new RenamerExecutor(port, bus, journal, "run-test", new DiskMover());

            var options = new RenamerOptions { FilenameTemplate = "$title" }; // → "My Film.mkv"

            // Plan via the live port (read-only), then execute.
            var plan = await new RenamerPlanner(port).PlanAsync(RenamerFileKind.Video, videoId, options, RouteLookupsFixtures.RoutingNeutral, default);
            var result = await executor.ExecuteAsync(plan, options, default);

            // (a) disk: new exists, old gone, content intact.
            string newFull = Path.Combine(dir.Root, "My Film.mkv");
            Assert.True(File.Exists(newFull), "renamed file must exist on disk");
            Assert.False(File.Exists(oldFull), "old file must be gone");
            Assert.Equal("video-bytes", File.ReadAllText(newFull));

            // (b) DB: basename updated; Path RECOMPUTED (not set) to folder + new basename.
            var (basename, path) = await ExecutorTestSeed.ReadFileAsync(db, fileId);
            Assert.Equal("My Film.mkv", basename);
            Assert.Equal(folderPath + "/My Film.mkv", path);

            // Result buckets: one renamed, none skipped/failed; one journal row written.
            var renamedItem = Assert.Single(result.Renamed);
            Assert.Equal(RenamerStatus.Rename, renamedItem.Status);
            Assert.Empty(result.Failed);
            Assert.Empty(result.Skipped);
            var revert = Assert.Single(journal.Rows);
            Assert.Equal(fileId, revert.FileId);
            Assert.EndsWith("raw clip.mkv", revert.OldPath);

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
    /// T3: a host shutdown mid-save (an <see cref="OperationCanceledException"/> from the DB save) is
    /// cancellation, not a data failure. The post-move rollback still restores the disk, then the OCE
    /// propagates out of the batch — it must NOT land as a <see cref="RenamerStatus.Failed"/> item row.
    /// Same-volume so it runs on every platform.
    /// </summary>
    [Fact]
    public async Task SaveCancelled_RollsBackAndPropagates_NeverFailed()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string folderPath = dir.Root.Replace('\\', '/');
            var (_, videoId, _) =
                await ExecutorTestSeed.SeedVideoAsync(db, folderPath, "raw clip.mkv", "My Film");

            string oldFull = Path.Combine(dir.Root, "raw clip.mkv");
            File.WriteAllText(oldFull, "video-bytes");

            var options = new RenamerOptions { FilenameTemplate = "$title" }; // → "My Film.mkv"
            var plan = await new RenamerPlanner(new CoveRenamerDataPort(db))
                .PlanAsync(RenamerFileKind.Video, videoId, options, RouteLookupsFixtures.RoutingNeutral, default);

            var executor = new RenamerExecutor(
                new CancelOnSaveDataPort(db), new CapturingEventBus(), new FakeRevertJournal(), "run-test", new DiskMover());

            // The cancel flows out as cancellation (the batch ends), never a Failed row.
            await Assert.ThrowsAsync<OperationCanceledException>(() => executor.ExecuteAsync(plan, options, default));

            // The post-move rollback still ran on the cancel path: the file is back at OLD, none at NEW.
            string newFull = Path.Combine(dir.Root, "My Film.mkv");
            Assert.True(File.Exists(oldFull), "cancel rollback must restore the source");
            Assert.False(File.Exists(newFull), "no file may linger at the new path after a cancelled save");
            Assert.Equal("video-bytes", File.ReadAllText(oldFull));
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    /// <summary>
    /// A source row present in the DB but ABSENT on disk must be classified SkipMissingSource by the
    /// executor's source pre-check — NOT SkipLocked (the swallowed-IOException bucket it would fall
    /// into if it reached the mover). It is a safe no-op skip: nothing renamed/failed, no revert-log
    /// row, no event published.
    /// </summary>
    [Fact]
    public async Task MissingSource_ClassifiedSkipMissingSource_NotSkipLocked()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            // Seed the Folder + VideoFile on the real temp-dir root, but write NO on-disk source file.
            string folderPath = dir.Root.Replace('\\', '/');
            var (_, videoId, _) =
                await ExecutorTestSeed.SeedVideoAsync(db, folderPath, "gone.mkv", "My Film");

            var port = new CoveRenamerDataPort(db);
            var bus = new CapturingEventBus();
            var journal = new FakeRevertJournal();
            var executor = new RenamerExecutor(port, bus, journal, "run-test", new DiskMover());

            var options = new RenamerOptions { FilenameTemplate = "$title" };

            var plan = await new RenamerPlanner(port).PlanAsync(RenamerFileKind.Video, videoId, options, RouteLookupsFixtures.RoutingNeutral, default);
            var result = await executor.ExecuteAsync(plan, options, default);

            // Classified as SkipMissingSource — not SkipLocked — with a missing-source reason.
            var skippedItem = Assert.Single(result.Skipped);
            Assert.Equal(RenamerStatus.SkipMissingSource, skippedItem.Status);
            Assert.Contains("missing", skippedItem.Reason);

            // A missing source is a safe no-op skip: nothing moved/failed, no journal row, no event.
            Assert.Empty(result.Renamed);
            Assert.Empty(result.Failed);
            Assert.Empty(journal.Rows);
            Assert.Empty(bus.Published);
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
    /// are updated, a journal row is written, and one VideoUpdated event fired.
    /// </summary>
    [SkippableFact]
    public async Task CrossVolumeBranch_HappyMove_UsesCrossMover_DiskAndDbUpdated()
    {
        Skip.IfNot(SecondVolume.IsAvailable, SecondVolume.UnavailableReason);

        using var src = new TempDir();
        using var dst = new SecondVolume();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string srcFolder = src.Root.Replace('\\', '/');
            string dstFolder = dst.Root.Replace('\\', '/').TrimEnd('/'); // "P:" (root, distinct from src)
            var (_, videoId, fileId) =
                await ExecutorTestSeed.SeedVideoAsync(db, srcFolder, "clip.mkv", "My Film");

            string oldFull = Path.Combine(src.Root, "clip.mkv");
            File.WriteAllText(oldFull, "cross-bytes");

            // Sanity: the source and the subst destination are on DIFFERENT path roots → cross-volume.
            string newFull = dstFolder + "/My Film.mkv";
            Assert.False(VolumeClassifier.SameVolume(srcFolder + "/clip.mkv", newFull),
                "precondition: subst destination must be a different path root than the temp source");

            var port = new CoveRenamerDataPort(db);
            var bus = new CapturingEventBus();
            var journal = new FakeRevertJournal();
            // Inject a real CrossVolumeMover (the production mover) so the cross branch runs end-to-end,
            // with the post-copy seam recording the in-flight name it mints. That name is unguessable, so
            // recording it is the only way the leftover assertion below can name the right path instead
            // of a path this test made up.
            var minted = new List<string>();
            var executor = new RenamerExecutor(
                port, bus, journal, "run-test", new DiskMover(), new CrossVolumeMover(Recorder(minted)));

            // Explicit MOVE plan: source on the temp drive, target folder on the subst drive.
            var plan = new RenamerPlan(videoId, RenamerFileKind.Video,
            [
                new RenamerPlanItem(fileId, srcFolder + "/clip.mkv", newFull,
                    RenamerStatus.Move, "My Film.mkv", dstFolder),
            ]);

            var result = await executor.ExecuteAsync(plan, new RenamerOptions(), default);

            // Disk: dest present with original content, source gone, no in-flight copy left behind.
            string newOnDisk = Path.Combine(dst.Root, "My Film.mkv");
            Assert.True(File.Exists(newOnDisk), "cross-moved file must exist at the dest root");
            Assert.Equal("cross-bytes", File.ReadAllText(newOnDisk));
            Assert.False(File.Exists(oldFull), "source must be deleted (delete-source-last) after a verified cross move");
            AssertMintedPathsGone(minted);

            // Result buckets: one moved, none skipped/failed; one journal row written.
            var movedItem = Assert.Single(result.Renamed);
            Assert.Equal(RenamerStatus.Move, movedItem.Status);
            Assert.Empty(result.Failed);
            Assert.Empty(result.Skipped);
            Assert.Single(journal.Rows);

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
    /// A cross-volume move the mover classified <see cref="MoveOutcome.PermissionDenied"/> reaches the
    /// run result as <see cref="RenamerStatus.SkipPermissionDenied"/> — NOT as
    /// <see cref="RenamerStatus.SkipLocked"/>, which every non-moved outcome once collapsed into. A
    /// denial and a lock ask for opposite responses, so an operator can only act on the difference if
    /// the status carries it.
    /// </summary>
    [SkippableFact]
    public async Task CrossVolumeMove_PermissionDenied_ClassifiedSkipPermissionDenied_NotSkipLocked()
    {
        Skip.IfNot(SecondVolume.IsAvailable, SecondVolume.UnavailableReason);

        using var src = new TempDir();
        using var dst = new SecondVolume();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string srcFolder = src.Root.Replace('\\', '/');
            string dstFolder = dst.Root.Replace('\\', '/').TrimEnd('/');
            var (_, videoId, fileId) =
                await ExecutorTestSeed.SeedVideoAsync(db, srcFolder, "clip.mkv", "My Film");

            string oldFull = Path.Combine(src.Root, "clip.mkv");
            File.WriteAllText(oldFull, "cross-bytes");

            // Sanity: the source and the subst destination are on DIFFERENT path roots → cross-volume.
            string newFull = dstFolder + "/My Film.mkv";
            Assert.False(VolumeClassifier.SameVolume(srcFolder + "/clip.mkv", newFull),
                "precondition: subst destination must be a different path root than the temp source");

            var port = new CoveRenamerDataPort(db);
            var bus = new CapturingEventBus();
            var journal = new FakeRevertJournal();
            // The seam fires INSIDE CopyVerifyPromoteDeleteAsync's try, so the throw meets that method's
            // own classifying catches. UnauthorizedAccessException does not derive from IOException, so it
            // passes the locked-or-exists catch and lands in the permission one — the outcome asserted
            // below is the mover's real classification of a real throw, not a value this test handed it.
            var executor = new RenamerExecutor(
                port, bus, journal, "run-test", new DiskMover(),
                new CrossVolumeMover((_, _) => throw new UnauthorizedAccessException("denied at the post-copy seam")));

            var plan = new RenamerPlan(videoId, RenamerFileKind.Video,
            [
                new RenamerPlanItem(fileId, srcFolder + "/clip.mkv", newFull,
                    RenamerStatus.Move, "My Film.mkv", dstFolder),
            ]);

            var result = await executor.ExecuteAsync(plan, new RenamerOptions(), default);

            var skippedItem = Assert.Single(result.Skipped);
            Assert.Equal(RenamerStatus.SkipPermissionDenied, skippedItem.Status);
            Assert.StartsWith("permission denied:", skippedItem.Reason);
            Assert.Empty(result.Renamed);
            Assert.Empty(result.Failed);

            // Disk: the source survives at its original path and nothing was left at the destination.
            Assert.True(File.Exists(oldFull), "a denied move must leave the source where it was");
            Assert.Equal("cross-bytes", File.ReadAllText(oldFull));
            Assert.False(File.Exists(Path.Combine(dst.Root, "My Film.mkv")));

            // DB: disk-first means no save ran, so the row still names the original basename and folder.
            var (basename, path) = await ExecutorTestSeed.ReadFileAsync(db, fileId);
            Assert.Equal("clip.mkv", basename);
            Assert.Equal(srcFolder + "/clip.mkv", path);
            Assert.Empty(journal.Rows);
            Assert.Empty(bus.Published);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    /// <summary>
    /// A cross-volume move the mover classified <see cref="MoveOutcome.Cancelled"/> reaches the run
    /// result as <see cref="RenamerStatus.SkipCancelled"/>, and no exception leaves
    /// <c>ExecuteAsync</c>. The mover classifies a cancel rather than throwing it, and the executor
    /// must not convert that back into a failure: on shutdown, work is cancelled, never defective.
    /// </summary>
    [SkippableFact]
    public async Task CrossVolumeMove_Cancelled_ClassifiedSkipCancelled_NotSkipLocked()
    {
        Skip.IfNot(SecondVolume.IsAvailable, SecondVolume.UnavailableReason);

        using var src = new TempDir();
        using var dst = new SecondVolume();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string srcFolder = src.Root.Replace('\\', '/');
            string dstFolder = dst.Root.Replace('\\', '/').TrimEnd('/');
            var (_, videoId, fileId) =
                await ExecutorTestSeed.SeedVideoAsync(db, srcFolder, "clip.mkv", "My Film");

            string oldFull = Path.Combine(src.Root, "clip.mkv");
            File.WriteAllText(oldFull, "cross-bytes");

            // Sanity: the source and the subst destination are on DIFFERENT path roots → cross-volume.
            string newFull = dstFolder + "/My Film.mkv";
            Assert.False(VolumeClassifier.SameVolume(srcFolder + "/clip.mkv", newFull),
                "precondition: subst destination must be a different path root than the temp source");

            var port = new CoveRenamerDataPort(db);
            var bus = new CapturingEventBus();
            var journal = new FakeRevertJournal();
            // Same seam, and OperationCanceledException lands in the FIRST of the three catches — the one
            // that removes the in-flight copy and returns a classified Cancelled rather than rethrowing.
            // That catch is what makes this a real outcome instead of a cancellation this test forced past
            // the mover, and it is why nothing below has to tolerate a throw escaping ExecuteAsync.
            var executor = new RenamerExecutor(
                port, bus, journal, "run-test", new DiskMover(),
                new CrossVolumeMover((_, _) => throw new OperationCanceledException("host shutting down mid-copy")));

            var plan = new RenamerPlan(videoId, RenamerFileKind.Video,
            [
                new RenamerPlanItem(fileId, srcFolder + "/clip.mkv", newFull,
                    RenamerStatus.Move, "My Film.mkv", dstFolder),
            ]);

            var result = await executor.ExecuteAsync(plan, new RenamerOptions(), default);

            var skippedItem = Assert.Single(result.Skipped);
            Assert.Equal(RenamerStatus.SkipCancelled, skippedItem.Status);
            Assert.Empty(result.Renamed);
            Assert.Empty(result.Failed);

            // Disk: nothing renamed, and the source is exactly where and what it was.
            Assert.True(File.Exists(oldFull), "a cancelled move must leave the source where it was");
            Assert.Equal("cross-bytes", File.ReadAllText(oldFull));
            Assert.False(File.Exists(Path.Combine(dst.Root, "My Film.mkv")));

            // DB untouched, and nothing journalled — there is no move to put back.
            var (basename, path) = await ExecutorTestSeed.ReadFileAsync(db, fileId);
            Assert.Equal("clip.mkv", basename);
            Assert.Equal(srcFolder + "/clip.mkv", path);
            Assert.Empty(journal.Rows);
            Assert.Empty(bus.Published);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    /// <summary>
    /// MOVE-05 cross-path rollback: a VERIFIED cross-volume move whose subsequent DB save throws
    /// (a forced <c>(ParentFolderId, Basename)</c> unique-index clash, pre-check bypassed via
    /// <see cref="CollisionBlindDataPort"/>) must roll back through <see cref="CrossVolumeMover.RollbackAsync"/>
    /// — copy the bytes back across the volume and restore the source — leaving disk and DB consistent.
    /// Re-proves disk-first/DB-second for the cross path.
    /// </summary>
    [SkippableFact]
    public async Task CrossVolumeSaveFailure_RollsBackThroughCrossMover_SourceRestored()
    {
        Skip.IfNot(SecondVolume.IsAvailable, SecondVolume.UnavailableReason);

        using var src = new TempDir();
        using var dst = new SecondVolume();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string srcFolder = src.Root.Replace('\\', '/');
            string dstFolder = dst.Root.Replace('\\', '/').TrimEnd('/');

            var (_, videoId, fileA) =
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

            var journal = new FakeRevertJournal();
            var minted = new List<string>();
            var executor = new RenamerExecutor(
                new CollisionBlindDataPort(db), new CapturingEventBus(), journal, "run-test",
                new DiskMover(), new CrossVolumeMover(Recorder(minted)));

            var result = await executor.ExecuteAsync(plan, new RenamerOptions(), default);

            // The save threw after the verified cross move → item failed with a rollback reason.
            var failedItem = Assert.Single(result.Failed);
            Assert.Equal(RenamerStatus.Failed, failedItem.Status);
            Assert.Contains("rolled back", failedItem.Reason);
            Assert.Empty(result.Renamed);
            Assert.Empty(journal.Rows);

            // (a) the source is RESTORED across the volume (copy-back) with its original content.
            Assert.True(File.Exists(oldA), "cross rollback must copy the file back to its old path");
            Assert.Equal("A-bytes", File.ReadAllText(oldA));
            // and is NOT left on the dest volume.
            Assert.False(File.Exists(newOnDisk), "rolled-back file must not linger at the dest");
            // Both directions of the cross move minted their own in-flight name; neither may survive.
            AssertMintedPathsGone(minted);

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
    /// Regression: when the cross-volume rollback FAILS to fully restore (here the old source slot
    /// is re-occupied before the copy-back runs, so <see cref="CrossVolumeMover.RollbackAsync"/> records a
    /// "rollback target re-occupied" warning rather than restoring), the executor must NOT report a clean
    /// "file rolled back". It must surface the rollback warnings so the disk/DB divergence is visible —
    /// silently discarding the warnings would falsely claim a rollback that did not happen.
    /// </summary>
    [SkippableFact]
    public async Task CrossVolumeSaveFailure_RollbackWarnings_Surfaced_NotSilentlyRolledBack()
    {
        Skip.IfNot(SecondVolume.IsAvailable, SecondVolume.UnavailableReason);

        using var src = new TempDir();
        using var dst = new SecondVolume();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string srcFolder = src.Root.Replace('\\', '/');
            string dstFolder = dst.Root.Replace('\\', '/').TrimEnd('/');

            var (_, videoId, fileA) =
                await ExecutorTestSeed.SeedVideoAsync(db, srcFolder, "a.mkv", "Film A");

            string oldA = Path.Combine(src.Root, "a.mkv");
            File.WriteAllText(oldA, "A-bytes");

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
                port, new CapturingEventBus(), new FakeRevertJournal(), "run-test",
                new DiskMover(), new CrossVolumeMover());

            var result = await executor.ExecuteAsync(plan, new RenamerOptions(), default);

            var failedItem = Assert.Single(result.Failed);
            Assert.Equal(RenamerStatus.Failed, failedItem.Status);
            // The failed reason must report the INCOMPLETE rollback + the warning, NOT a clean "rolled back".
            Assert.Contains("rollback INCOMPLETE", failedItem.Reason);
            Assert.Contains("rollback target re-occupied", failedItem.Reason);
            Assert.DoesNotContain("file rolled back", failedItem.Reason);
            Assert.Empty(result.Renamed);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    // ── destination-allowlist guard, WIRED INTO the executor ─────────────────────────────────────────
    //
    // Every canonical-guard case elsewhere calls CanonicalPathGuard.Check directly with a hand-built
    // folder string; these drive the real executor write path with the guard wired in, which is the seam
    // where the two load-bearing invariants live: the guard must cover the FULL final path (not just the
    // folder), and it must run BEFORE any disk/DB mutation.
    //
    // Their junction setup is deliberately NOT deduplicated against CanonicalPathGuardTests. The setup is
    // identical and the asserted LAYER is not: those assert the guard class rejects a path, these assert
    // no DB row leaked through the executor's own wiring. Merging on the shared setup would delete the
    // only test that observes the row leak while leaving a green suite.

    /// <summary>Creates an NTFS junction <paramref name="link"/> → <paramref name="target"/> via <c>cmd /c mklink /J</c> (no privilege required).</summary>
    private static void MakeJunction(string link, string target)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = System.Diagnostics.Process.Start(psi)!;
        p.WaitForExit(5000);
        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException("mklink /J failed: " + p.StandardError.ReadToEnd());
        }
    }

    /// <summary>
    /// A <c>Move</c> whose target folder is a JUNCTION inside the allowed root pointing OUTSIDE it is
    /// blocked, the file does not move, and — proving the guard runs before the row-creation seam — NO
    /// <see cref="Folder"/> row is persisted for the rejected destination.
    /// </summary>
    /// <remarks>
    /// This is the suite's ONLY observation of the escape-path folder-row leak: neutralizing the
    /// row-creation guard turns this test red and nothing else in the canonical-guard family. Its name
    /// and its folder-count assert are both load-bearing and must survive verbatim.
    /// </remarks>
    [SkippableFact] // On Windows this always runs — junctions need no privilege; it IS the guard-runs-first proof.
    [Trait("Adversarial", "Junction")]
    public async Task MoveToJunctionEscapingAllowedRoot_IsBlocked_NoFolderRowLeaked()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "needs an NTFS junction (cmd /c mklink /J)");

        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            // Source folder + file live in a "source" subtree; the allowed root is a SEPARATE subtree.
            string srcDir = Directory.CreateDirectory(Path.Combine(dir.Root, "source")).FullName;
            string allowed = Directory.CreateDirectory(Path.Combine(dir.Root, "allowed")).FullName;
            string outside = Directory.CreateDirectory(Path.Combine(dir.Root, "outside")).FullName;

            // A junction physically INSIDE the allowed root but resolving OUTSIDE it.
            string escape = Path.Combine(allowed, "escape");
            MakeJunction(escape, outside);

            string srcFolderPath = srcDir.Replace('\\', '/');
            var (_, videoId, fileId) =
                await ExecutorTestSeed.SeedVideoAsync(db, srcFolderPath, "clip.mkv", "My Film");

            string oldFull = Path.Combine(srcDir, "clip.mkv");
            File.WriteAllText(oldFull, "video-bytes");

            // Hand-build a MOVE whose target folder is the junction-escape path.
            string escapeFwd = escape.Replace('\\', '/');
            var plan = new RenamerPlan(videoId, RenamerFileKind.Video,
            [
                new RenamerPlanItem(fileId, srcFolderPath + "/clip.mkv", escapeFwd + "/My Film.mkv",
                    RenamerStatus.Move, "My Film.mkv", escapeFwd),
            ]);

            var port = new CoveRenamerDataPort(db);
            var bus = new CapturingEventBus();
            var executor = new RenamerExecutor(port, bus, new FakeRevertJournal(), "run-test", new DiskMover());
            var options = new RenamerOptions { AllowedRoots = [allowed.Replace('\\', '/')] };

            int foldersBefore = await db.Set<Folder>().CountAsync();

            var result = await executor.ExecuteAsync(plan, options, default);

            // (a) The item is BLOCKED — SkipBlocked, with the guard's "outside every allowed root" reason.
            var skipped = Assert.Single(result.Skipped);
            Assert.Equal(RenamerStatus.SkipBlocked, skipped.Status);
            Assert.NotNull(skipped.Reason);
            Assert.Contains("outside every allowed root", skipped.Reason);
            Assert.Empty(result.Renamed);
            Assert.Empty(result.Failed);
            Assert.Empty(bus.Published);

            // (b) The physical file did NOT move — it stays at the source.
            Assert.True(File.Exists(oldFull), "blocked move must leave the source file in place");
            Assert.Equal("video-bytes", File.ReadAllText(oldFull));
            Assert.False(File.Exists(Path.Combine(escape, "My Film.mkv")), "no file may land at the escape destination");

            // (c) NO Folder DB row was created for the rejected destination (the guard ran BEFORE the
            //     row-creation seam). The folder count is unchanged and no row points at the escape.
            Assert.Equal(foldersBefore, await db.Set<Folder>().CountAsync());
            Assert.False(
                await db.Set<Folder>().AnyAsync(f => f.Path == escapeFwd),
                "no Folder row may be persisted for the out-of-allowlist escape path");
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    /// <summary>
    /// A leaf swapped into a junction-to-elsewhere BETWEEN the two guards is refused by the final-path
    /// re-check, and nothing lands outside the allowed roots.
    /// </summary>
    [SkippableFact] // On Windows this always runs — junctions need no privilege; it IS the (4b) re-check proof.
    [Trait("Adversarial", "Junction")]
    public async Task LeafSwappedToJunctionBetweenGuards_IsBlocked_NothingEscapes()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "needs an NTFS junction (cmd /c mklink /J)");

        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string srcDir = Directory.CreateDirectory(Path.Combine(dir.Root, "source")).FullName;
            string allowed = Directory.CreateDirectory(Path.Combine(dir.Root, "allowed")).FullName;
            string outside = Directory.CreateDirectory(Path.Combine(dir.Root, "outside")).FullName;

            // The destination is a REAL, empty directory at check time, so the row-creation guard accepts
            // it. It only becomes an escape AFTER that check has passed.
            string dest = Directory.CreateDirectory(Path.Combine(allowed, "dest")).FullName;

            string srcFolderPath = srcDir.Replace('\\', '/');
            var (_, videoId, fileId) =
                await ExecutorTestSeed.SeedVideoAsync(db, srcFolderPath, "clip.mkv", "My Film");

            string oldFull = Path.Combine(srcDir, "clip.mkv");
            File.WriteAllText(oldFull, "video-bytes");

            string destFwd = dest.Replace('\\', '/');
            var plan = new RenamerPlan(videoId, RenamerFileKind.Video,
            [
                new RenamerPlanItem(fileId, srcFolderPath + "/clip.mkv", destFwd + "/My Film.mkv",
                    RenamerStatus.Move, "My Film.mkv", destFwd),
            ]);

            // The swap runs inside GetOrCreateFolderIdAsync — the FIRST await after the row-creation
            // guard and the only window that lands between the two guards, so the leaf is real when the
            // first guard reads it and a junction-to-elsewhere when the final-path guard re-resolves it.
            // Passing NO pre-resolved folder map is what routes the executor through that call at all.
            var port = new SwapLeafToJunctionOnFolderResolve(new CoveRenamerDataPort(db), dest, outside);
            var bus = new CapturingEventBus();
            var executor = new RenamerExecutor(port, bus, new FakeRevertJournal(), "run-test", new DiskMover());
            var options = new RenamerOptions { AllowedRoots = [allowed.Replace('\\', '/')] };

            var result = await executor.ExecuteAsync(plan, options, default);

            // (a) The premise held. A swap that silently failed to happen would make every assert below
            //     pass for the wrong reason — this test would then prove nothing at all.
            Assert.True(port.Swapped, "the check/use swap never fired: GetOrCreateFolderIdAsync was not reached");
            Assert.NotNull(new DirectoryInfo(dest).LinkTarget);

            // (b) The item is BLOCKED by the final-path re-check, with the guard's reason.
            var skipped = Assert.Single(result.Skipped);
            Assert.Equal(RenamerStatus.SkipBlocked, skipped.Status);
            Assert.NotNull(skipped.Reason);
            Assert.Contains("outside every allowed root", skipped.Reason);
            Assert.Empty(result.Renamed);
            Assert.Empty(result.Failed);
            Assert.Empty(bus.Published);

            // (c) Nothing escaped: the source stayed put and no file landed through the junction.
            Assert.True(File.Exists(oldFull), "a blocked move must leave the source file in place");
            Assert.Equal("video-bytes", File.ReadAllText(oldFull));
            Assert.False(File.Exists(Path.Combine(outside, "My Film.mkv")),
                "no file may land outside every allowed root through the swapped leaf");

            // Deliberately NOT asserted: Folder-row absence. The delegated call legitimately persists a
            // row for the destination path, which was still real when the row-creation guard approved it.
            // Row creation is the other pin's subject; asserting it here would make this test fail for the
            // wrong reason.
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    /// <summary>
    /// The positive control for the two rejections above: a benign destination genuinely under the
    /// allowed root still moves and still persists its folder row.
    /// </summary>
    /// <remarks>
    /// Not optional and not obvious. Without it, a guard that refused EVERY destination would satisfy
    /// both negative cases, and the pair would read as proof of containment while proving only that
    /// nothing ever moves.
    /// </remarks>
    [Fact]
    public async Task MoveToRealSubdirUnderAllowedRoot_Succeeds()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string srcDir = Directory.CreateDirectory(Path.Combine(dir.Root, "source")).FullName;
            string allowed = Directory.CreateDirectory(Path.Combine(dir.Root, "allowed")).FullName;
            // A real subdirectory physically under the allowed root (no reparse point).
            string target = Directory.CreateDirectory(Path.Combine(allowed, "season-01")).FullName;

            string srcFolderPath = srcDir.Replace('\\', '/');
            var (_, videoId, fileId) =
                await ExecutorTestSeed.SeedVideoAsync(db, srcFolderPath, "clip.mkv", "My Film");

            string oldFull = Path.Combine(srcDir, "clip.mkv");
            File.WriteAllText(oldFull, "video-bytes");

            string targetFwd = target.Replace('\\', '/');
            var plan = new RenamerPlan(videoId, RenamerFileKind.Video,
            [
                new RenamerPlanItem(fileId, srcFolderPath + "/clip.mkv", targetFwd + "/My Film.mkv",
                    RenamerStatus.Move, "My Film.mkv", targetFwd),
            ]);

            var port = new CoveRenamerDataPort(db);
            var bus = new CapturingEventBus();
            var executor = new RenamerExecutor(port, bus, new FakeRevertJournal(), "run-test", new DiskMover());
            var options = new RenamerOptions { AllowedRoots = [allowed.Replace('\\', '/')] };

            var result = await executor.ExecuteAsync(plan, options, default);

            // A benign in-allowlist destination moves: item renamed/moved, file on disk at the new path.
            var moved = Assert.Single(result.Renamed);
            Assert.Equal(RenamerStatus.Move, moved.Status);
            Assert.Empty(result.Skipped);
            Assert.Empty(result.Failed);

            string newFull = Path.Combine(target, "My Film.mkv");
            Assert.True(File.Exists(newFull), "the benign in-allowlist move must land on disk");
            Assert.False(File.Exists(oldFull), "the source must be gone after a successful move");
            Assert.Equal("video-bytes", File.ReadAllText(newFull));

            // The destination folder row is now persisted (the guard accepted it before the create).
            Assert.True(await db.Set<Folder>().AnyAsync(f => f.Path == targetFwd));
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    // ── batch core, driven through the extension ──────────────────────────────────────────────────────
    //
    // The shared RunRenamerBatchAsync opens a scope via the captured IServiceScopeFactory, builds the
    // port+executor over the real CoveContext, renames every id on disk + in the DB, and reports
    // per-item progress plus a final 1.0. Bad/empty input is a clean no-op that still reports 1.0.

    /// <summary>
    /// Builds the extension for a batch case over <paramref name="conn"/>, pinning the two options the
    /// batch cases depend on.
    /// </summary>
    /// <remarks>
    /// <c>SameVolumeConcurrency = 1</c> is a TEST-HARNESS requirement, not a product behavior under test.
    /// The batch opens one DI scope per worker and
    /// <see cref="ExtensionHarness.CreateWithScopedContextAsync"/> registers <c>DbContext</c> SCOPED over
    /// a single shared in-memory SQLite connection (the connection is what keeps the <c>:memory:</c>
    /// database alive). In production each scope draws its OWN pooled connection, so parallel workers
    /// never share one; here they would, and two <c>DbContext</c>s racing on one SQLite connection
    /// intermittently throw inside EF's dependency resolution. Serializing same-volume workers removes
    /// that harness-only race while still exercising the full per-item batch path. The default (8) is
    /// covered by production and the E2E suite, which use real per-scope connections.
    /// <para>
    /// The title-only template is pinned because these cases assert on output names over height-less
    /// seed videos; the shipped default appends <c>"[$resolution]"</c> and would perturb them.
    /// </para>
    /// </remarks>
    private static async Task<global::Renamer.Renamer> BuildBatchExtensionAsync(
        Microsoft.Data.Sqlite.SqliteConnection conn, IEventBus bus)
    {
        var (extension, _) = await ExtensionHarness.CreateWithScopedContextAsync(
            conn, bus, new RenamerOptions { FilenameTemplate = "$title", SameVolumeConcurrency = 1 });
        return extension;
    }

    [Fact]
    public async Task Batch_RenamesEveryId_OnDiskAndInDb_ReportsPerItemPlusFinalOne()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string folderPath = dir.Root.Replace('\\', '/');
            // Two distinct videos sharing ONE folder (a second SeedVideoAsync would re-insert the
            // folder and trip the folders.Path unique index). Seed the folder+video once, then add
            // a second video + file in the same folder.
            var (folderId, v1, file1) = await ExecutorTestSeed.SeedVideoAsync(db, folderPath, "raw one.mkv", "First Film");

            var video2 = new Video { Title = "Second Film", Organized = true };
            db.Set<Video>().Add(video2);
            await db.SaveChangesAsync();
            var file2 = await ExecutorTestSeed.SeedAdditionalFileAsync(db, folderId, video2.Id, "raw two.mkv");
            var v2 = video2.Id;

            // Real on-disk sources matching the seeded rows.
            File.WriteAllText(Path.Combine(dir.Root, "raw one.mkv"), "bytes-1");
            File.WriteAllText(Path.Combine(dir.Root, "raw two.mkv"), "bytes-2");

            var bus = new CapturingEventBus();
            var ext = await BuildBatchExtensionAsync(conn, bus);
            var progress = new FakeJobProgress();

            await ext.RunRenamerBatchAsync(RenamerJob.Encode("video", [v1, v2]), progress, default);

            // Disk: both renamed to "$title.mkv", old gone, content intact.
            Assert.True(File.Exists(Path.Combine(dir.Root, "First Film.mkv")));
            Assert.True(File.Exists(Path.Combine(dir.Root, "Second Film.mkv")));
            Assert.False(File.Exists(Path.Combine(dir.Root, "raw one.mkv")));
            Assert.False(File.Exists(Path.Combine(dir.Root, "raw two.mkv")));
            Assert.Equal("bytes-1", File.ReadAllText(Path.Combine(dir.Root, "First Film.mkv")));

            // DB: basenames updated.
            var (b1, _) = await ExecutorTestSeed.ReadFileAsync(db, file1);
            var (b2, _) = await ExecutorTestSeed.ReadFileAsync(db, file2);
            Assert.Equal("First Film.mkv", b1);
            Assert.Equal("Second Film.mkv", b2);

            // Progress: PHASE B reports per COMPLETED unit (done/total), so a 2-item batch emits a
            // sub-1.0 progress tick before the final 1.0. Under parallelism the exact fraction order is
            // nondeterministic; assert that per-item progress is emitted and the run ends at 1.0.
            Assert.Contains(progress.Reports, r => r.Percent is > 0d and < 1d);
            Assert.Equal(1d, progress.LastPercent);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task Batch_EmptyIds_ReportsFinalOne_PerformsZeroRenames()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string folderPath = dir.Root.Replace('\\', '/');
            await ExecutorTestSeed.SeedVideoAsync(db, folderPath, "keep me.mkv", "Untouched");
            File.WriteAllText(Path.Combine(dir.Root, "keep me.mkv"), "stay");

            var bus = new CapturingEventBus();
            var ext = await BuildBatchExtensionAsync(conn, bus);
            var progress = new FakeJobProgress();

            await ext.RunRenamerBatchAsync(RenamerJob.Encode("video", []), progress, default);

            // Untouched on disk; no renamer event published; only a final 1.0 reported.
            Assert.True(File.Exists(Path.Combine(dir.Root, "keep me.mkv")));
            Assert.Empty(bus.Published);
            Assert.Equal(1d, progress.LastPercent);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task Batch_UnsupportedEntityType_IsCleanNoOp_ReportsFinalOne()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            var bus = new CapturingEventBus();
            var ext = await BuildBatchExtensionAsync(conn, bus);
            var progress = new FakeJobProgress();

            await ext.RunRenamerBatchAsync(RenamerJob.Encode("gallery", [1, 2]), progress, default);

            Assert.Empty(bus.Published);
            Assert.Equal(1d, progress.LastPercent);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    /// <summary>
    /// A post-copy seam that only records the in-flight path the cross mover minted, leaving the copy
    /// untouched — the mover's real behaviour, plus the observation the cross cases need.
    /// </summary>
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

    /// <summary>Test-only port: the save throws a cancellation (a host shutdown mid-save), forcing the
    /// executor's post-move OCE path — rollback, then propagate — rather than the data-failure path.</summary>
    private sealed class CancelOnSaveDataPort(Cove.Data.CoveContext db) : CoveRenamerDataPort(db)
    {
        public override Task<IReadOnlyList<SavedFile>> ApplyAndSaveAsync(
            IReadOnlyList<RenamerFileMutation> mutations, CancellationToken ct = default)
            => throw new OperationCanceledException("host shutting down mid-save");
    }

    /// <summary>
    /// A real <see cref="CoveRenamerDataPort"/> with ONE behavior added: the first
    /// <see cref="GetOrCreateFolderIdAsync"/> call replaces an empty destination directory with a
    /// junction pointing elsewhere, then delegates unchanged.
    /// </summary>
    /// <remarks>
    /// The seam matters more than the mechanism. That call is the first await the executor makes after
    /// the row-creation guard, so a swap performed here is the check/use race the final-path re-check
    /// exists to close, reproduced deterministically rather than by timing. Every other member forwards
    /// verbatim, so the executor is exercised against real data throughout. If the executor is ever
    /// reordered so this call no longer sits between the two guards, <see cref="Swapped"/> stays false
    /// and the test fails loudly instead of passing for a reason it no longer proves.
    /// </remarks>
    private sealed class SwapLeafToJunctionOnFolderResolve(
        IRenamerDataPort inner, string leafToSwap, string junctionTarget) : IRenamerDataPort
    {
        /// <summary>True once the swap has actually been performed.</summary>
        public bool Swapped { get; private set; }

        public Task<int> GetOrCreateFolderIdAsync(string folderPath, CancellationToken ct = default)
        {
            if (!Swapped)
            {
                Directory.Delete(leafToSwap); // empty by construction — no recursive delete
                MakeJunction(leafToSwap, junctionTarget);
                Swapped = true;
            }

            return inner.GetOrCreateFolderIdAsync(folderPath, ct);
        }

        public Task<RenamerEntity?> LoadEntityAsync(RenamerFileKind kind, int entityId, CancellationToken ct = default)
            => inner.LoadEntityAsync(kind, entityId, ct);

        public Task<IReadOnlyList<int>> LoadAllEntityIdsAsync(RenamerFileKind kind, CancellationToken ct = default)
            => inner.LoadAllEntityIdsAsync(kind, ct);

        public Task<IReadOnlyList<int>> LoadEntityIdPageAsync(
            RenamerFileKind kind, int afterEntityId, int take, CancellationToken ct = default)
            => inner.LoadEntityIdPageAsync(kind, afterEntityId, take, ct);

        public Task<IReadOnlyList<RenamerEntity>> LoadEntitiesAsync(
            RenamerFileKind kind, IReadOnlyList<int> ids, CancellationToken ct = default)
            => inner.LoadEntitiesAsync(kind, ids, ct);

        public Task<bool> CollisionExistsAsync(int folderId, string basename, int selfFileId, CancellationToken ct = default)
            => inner.CollisionExistsAsync(folderId, basename, selfFileId, ct);

        public Task<int?> TryGetFolderIdAsync(string folderPath, CancellationToken ct = default)
            => inner.TryGetFolderIdAsync(folderPath, ct);

        public Task<bool> SourceExistsAsync(string fullPath, CancellationToken ct = default)
            => inner.SourceExistsAsync(fullPath, ct);

        public Task<IReadOnlyList<SavedFile>> ApplyAndSaveAsync(
            IReadOnlyList<RenamerFileMutation> mutations, CancellationToken ct = default)
            => inner.ApplyAndSaveAsync(mutations, ct);
    }
}
