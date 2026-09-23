using Cove.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Renamer.Execution;
using Renamer.Options;
using Renamer.Planner;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Execution.Collisions;

/// <summary>
/// The safety-spine rollback test the whole extension hinges on. Seed the SQLite + temp-dir state so the
/// disk move succeeds but the subsequent SaveChangesAsync throws (a forced unique-index clash, with
/// the pre-check bypassed via <see cref="CollisionBlindDataPort"/>). Assert that after execution:
/// (a) the file is back at its original on-disk path, (b) the moved sidecar (if any) is back, and
/// (c) the DB row still carries the old basename - disk and DB consistent. Runs on SQLite-in-memory
/// because EF-InMemory enforces neither the unique index nor transaction rollback.
///
/// The test first proves the disk move really happened (it is observable via the executor having
/// invoked DiskMover.Move - asserted by the file being momentarily at the new path is not possible
/// post-rollback, so instead we assert the negative-control: a DiskMover spy is unnecessary because
/// the only path that reaches SaveChangesAsync is after a successful move; we additionally assert the
/// failure reason names the rollback, proving the catch - not the move - produced the terminal state).
/// </summary>
public sealed class RollbackTests
{
    [Fact]
    public async Task SaveFailsAfterMove_FileRestoredToOldPath_DbRowUnchanged()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string folderPath = dir.Root.Replace('\\', '/');
            var (folderId, videoId, fileA) =
                await ExecutorTestSeed.SeedVideoAsync(db, folderPath, "a.mkv", "Film A");
            // A second row occupies "taken.mkv" so the save of a→taken hits the unique index.
            await ExecutorTestSeed.SeedAdditionalFileAsync(db, folderId, videoId, "taken.mkv");

            // Disk: "a.mkv" exists; "taken.mkv" does not (so the disk move succeeds first).
            string oldA = Path.Combine(dir.Root, "a.mkv");
            File.WriteAllText(oldA, "A-bytes");
            string newPath = Path.Combine(dir.Root, "taken.mkv");
            Assert.False(File.Exists(newPath), "precondition: disk target must be free so the MOVE happens before the save");

            var plan = new RenamerPlan(videoId, RenamerFileKind.Video,
            [
                new RenamerPlanItem(fileA, folderPath + "/a.mkv", folderPath + "/taken.mkv",
                    RenamerStatus.Rename, "taken.mkv", folderPath),
            ]);

            var port = new CollisionBlindDataPort(db);
            var bus = new CapturingEventBus();
            var journal = new FakeRevertJournal();
            var executor = new RenamerExecutor(port, bus, journal, "run-test", new DiskMover());

            var result = await executor.ExecuteAsync(plan, new RenamerOptions(), default);

            // The save threw after the move → item failed with a rollback reason (proving the catch,
            // i.e. the move had already happened before the save error - not a pre-move skip).
            var failedItem = Assert.Single(result.Failed);
            Assert.Equal(RenamerStatus.Failed, failedItem.Status);
            Assert.Contains("rolled back", failedItem.Reason);
            Assert.Empty(result.Renamed);
            Assert.Empty(journal.Rows);   // no success row written
            Assert.Empty(bus.Published);      // no event for a failed item

            // (a) the file is restored to its original path with original content.
            Assert.True(File.Exists(oldA), "file must be rolled back to its old path");
            Assert.Equal("A-bytes", File.ReadAllText(oldA));
            // and is not left at the new path.
            Assert.False(File.Exists(newPath), "rolled-back file must not linger at the new path");

            // (c) the DB row still has the old basename - disk and DB consistent.
            var (basenameA, pathA) = await ExecutorTestSeed.ReadFileAsync(db, fileA);
            Assert.Equal("a.mkv", basenameA);
            Assert.Equal(folderPath + "/a.mkv", pathA);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task SaveFailsAfterMove_SidecarAlsoRestored()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string folderPath = dir.Root.Replace('\\', '/');
            var (folderId, videoId, fileA) =
                await ExecutorTestSeed.SeedVideoAsync(db, folderPath, "a.mkv", "Film A");
            await ExecutorTestSeed.SeedAdditionalFileAsync(db, folderId, videoId, "taken.mkv");

            // Seed a caption sidecar on file A.
            db.Set<VideoCaption>().Add(new VideoCaption { FileId = fileA, Filename = "a.en.vtt", LanguageCode = "en", CaptionType = "vtt" });
            await db.SaveChangesAsync();

            string oldA = Path.Combine(dir.Root, "a.mkv");
            string oldCap = Path.Combine(dir.Root, "a.en.vtt");
            File.WriteAllText(oldA, "A-bytes");
            File.WriteAllText(oldCap, "caption");

            var plan = new RenamerPlan(videoId, RenamerFileKind.Video,
            [
                new RenamerPlanItem(fileA, folderPath + "/a.mkv", folderPath + "/taken.mkv",
                    RenamerStatus.Rename, "taken.mkv", folderPath),
            ]);

            var executor = new RenamerExecutor(
                new CollisionBlindDataPort(db), new CapturingEventBus(), new FakeRevertJournal(), "run-test", new DiskMover());

            var result = await executor.ExecuteAsync(plan, new RenamerOptions(), default);

            Assert.Single(result.Failed);
            // Both the primary file and the moved caption sidecar are restored to their old paths.
            Assert.True(File.Exists(oldA), "primary file restored");
            Assert.True(File.Exists(oldCap), "sidecar caption restored");
            Assert.Equal("caption", File.ReadAllText(oldCap));
            Assert.False(File.Exists(Path.Combine(dir.Root, "taken.mkv")));
            Assert.False(File.Exists(Path.Combine(dir.Root, "taken.en.vtt")));
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    /// <summary>
    /// The post-save consistency-assertion branch: the DB save succeeds (commits the new basename) but
    /// the runtime "recomputed Path == on-disk path" assertion fails. The executor must roll the disk
    /// back to the old path through the same mover the move used and write the committed row back to
    /// the old location, so the branch ends with disk and database agreeing. Assert: (a) the item is
    /// Failed with a path-mismatch + rolled-back reason, (b) the file is back at its old on-disk path,
    /// (c) the row names the old basename again, (d) no revert-log row and no event were written.
    /// </summary>
    [Fact]
    public async Task SaveSucceedsButRecomputedPathMismatch_FileAndRowBothRolledBack()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string folderPath = dir.Root.Replace('\\', '/');
            var (_, videoId, fileA) =
                await ExecutorTestSeed.SeedVideoAsync(db, folderPath, "a.mkv", "Film A");

            // Disk: "a.mkv" exists; the target "b.mkv" is free so the disk move succeeds first.
            string oldA = Path.Combine(dir.Root, "a.mkv");
            File.WriteAllText(oldA, "A-bytes");
            string newPath = Path.Combine(dir.Root, "b.mkv");
            Assert.False(File.Exists(newPath), "precondition: disk target must be free so the MOVE happens before the save");

            var plan = new RenamerPlan(videoId, RenamerFileKind.Video,
            [
                new RenamerPlanItem(fileA, folderPath + "/a.mkv", folderPath + "/b.mkv",
                    RenamerStatus.Rename, "b.mkv", folderPath),
            ]);

            // Port that commits the real save (new basename persisted) but reports a RecomputedPath that
            // does not match the on-disk destination, tripping the post-save assertion.
            var port = new MismatchedRecomputedPathDataPort(db);
            var bus = new CapturingEventBus();
            var journal = new FakeRevertJournal();
            var executor = new RenamerExecutor(port, bus, journal, "run-test", new DiskMover());

            var result = await executor.ExecuteAsync(plan, new RenamerOptions(), default);

            // (a) the item is Failed, and the reason names both the path mismatch and the rollback.
            var failedItem = Assert.Single(result.Failed);
            Assert.Equal(RenamerStatus.Failed, failedItem.Status);
            Assert.Contains("recomputed Path", failedItem.Reason);
            Assert.Contains("rolled back", failedItem.Reason);
            Assert.DoesNotContain("NOT confirmed", failedItem.Reason);
            Assert.Empty(result.Renamed);

            // (c) no revert-log row and no event for a failed item.
            Assert.Empty(journal.Rows);
            Assert.Empty(bus.Published);

            // (b) the file is rolled back to its old path with original content, and not at the new path.
            Assert.True(File.Exists(oldA), "file must be rolled back to its old path");
            Assert.Equal("A-bytes", File.ReadAllText(oldA));
            Assert.False(File.Exists(newPath), "rolled-back file must not linger at the new path");

            // (c) the committed row is back to the old basename, so it names the file's real location.
            var (basename, _) = await ExecutorTestSeed.ReadFileAsync(db, fileA);
            Assert.Equal("a.mkv", basename);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    /// <summary>
    /// The journal append runs after the save committed and the on-disk path was asserted, so a throw
    /// there is not a save failure: rolling the disk back would revert a move the database already
    /// agrees with, and would report the item failed for something the save did not do. The move stands
    /// and the failure is a warning on it.
    /// </summary>
    [Fact]
    public async Task JournalAppendThrowsAfterTheSaveCommitted_MoveStands_WarnedNotRolledBack()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string folderPath = dir.Root.Replace('\\', '/');
            var (_, videoId, fileA) =
                await ExecutorTestSeed.SeedVideoAsync(db, folderPath, "a.mkv", "Film A");

            string oldA = Path.Combine(dir.Root, "a.mkv");
            File.WriteAllText(oldA, "A-bytes");
            string newPath = Path.Combine(dir.Root, "b.mkv");

            var plan = new RenamerPlan(videoId, RenamerFileKind.Video,
            [
                new RenamerPlanItem(fileA, folderPath + "/a.mkv", folderPath + "/b.mkv",
                    RenamerStatus.Rename, "b.mkv", folderPath),
            ]);

            var journal = new FakeRevertJournal
            {
                AppendThrow = new InvalidOperationException("journal write failed"),
            };
            var bus = new CapturingEventBus();
            var executor = new RenamerExecutor(
                new CoveRenamerDataPort(db), bus, journal, "run-test", new DiskMover());

            var result = await executor.ExecuteAsync(plan, new RenamerOptions(), default);

            // The file is renamed, not failed and not rolled back.
            Assert.Empty(result.Failed);
            var renamedItem = Assert.Single(result.Renamed);
            Assert.Equal(RenamerStatus.Rename, renamedItem.Status);
            Assert.NotNull(renamedItem.Reason);
            Assert.Contains("revert-log entry not written", renamedItem.Reason);
            Assert.Contains("journal write failed", renamedItem.Reason);

            // The rename committed, so the host is still told to reindex it.
            Assert.Single(bus.Published);

            Assert.True(File.Exists(newPath), "a committed move must survive a post-save failure");
            Assert.Equal("A-bytes", File.ReadAllText(newPath));
            Assert.False(File.Exists(oldA), "the old slot must stay empty");

            // The database agrees with the disk - the whole point of not rolling back here.
            var (basename, path) = await ExecutorTestSeed.ReadFileAsync(db, fileA);
            Assert.Equal("b.mkv", basename);
            Assert.Equal(folderPath + "/b.mkv", path);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    /// <summary>
    /// The mismatch branch when the file does not come back: the save commits, the recomputed path
    /// disagrees, and the old slot is occupied by the time the rollback runs, so the media file stays
    /// at the new path. The committed row must then be left naming the new path - writing it back would
    /// point the database at a location the bytes are not at - and the reason must say so.
    /// </summary>
    [Fact]
    public async Task RecomputedPathMismatchAndTheFileCannotComeBack_RowKeepsTheNewName()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string folderPath = dir.Root.Replace('\\', '/');
            var (_, videoId, fileA) =
                await ExecutorTestSeed.SeedVideoAsync(db, folderPath, "a.mkv", "Film A");

            string oldA = Path.Combine(dir.Root, "a.mkv");
            File.WriteAllText(oldA, "A-bytes");
            string newPath = Path.Combine(dir.Root, "b.mkv");

            var plan = new RenamerPlan(videoId, RenamerFileKind.Video,
            [
                new RenamerPlanItem(fileA, folderPath + "/a.mkv", folderPath + "/b.mkv",
                    RenamerStatus.Rename, "b.mkv", folderPath),
            ]);

            var journal = new FakeRevertJournal();
            var executor = new RenamerExecutor(
                new ReoccupyOldSlotThenMisreportDataPort(db, oldA), new CapturingEventBus(), journal,
                "run-test", new DiskMover());

            var result = await executor.ExecuteAsync(plan, new RenamerOptions(), default);

            var failedItem = Assert.Single(result.Failed);
            Assert.Contains("did NOT return to its old path", failedItem.Reason);
            Assert.DoesNotContain("; rolled back", failedItem.Reason);
            Assert.Empty(journal.Rows);

            // The file is still at the new path, and the row still names it, so the two agree.
            Assert.True(File.Exists(newPath), "the rollback could not reclaim the old slot");
            var (basename, _) = await ExecutorTestSeed.ReadFileAsync(db, fileA);
            Assert.Equal("b.mkv", basename);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    /// <summary>
    /// The mismatch branch when the primary comes back but a sidecar does not: a rollback reports both
    /// in one warning list, so reading the warnings would leave the committed row naming a location the
    /// media file has left. The row is put back, and the stuck sidecar is reported alongside it.
    /// </summary>
    [Fact]
    public async Task RecomputedPathMismatchAndASidecarCannotComeBack_RowStillGoesBack()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string folderPath = dir.Root.Replace('\\', '/');
            var (_, videoId, fileA) =
                await ExecutorTestSeed.SeedVideoAsync(db, folderPath, "a.mkv", "Film A");
            db.Set<VideoCaption>().Add(new VideoCaption
            {
                FileId = fileA,
                Filename = "a.en.vtt",
                LanguageCode = "en",
                CaptionType = "vtt",
            });
            await db.SaveChangesAsync();

            string oldA = Path.Combine(dir.Root, "a.mkv");
            string oldCaption = Path.Combine(dir.Root, "a.en.vtt");
            File.WriteAllText(oldA, "A-bytes");
            File.WriteAllText(oldCaption, "caption");

            var plan = new RenamerPlan(videoId, RenamerFileKind.Video,
            [
                new RenamerPlanItem(fileA, folderPath + "/a.mkv", folderPath + "/b.mkv",
                    RenamerStatus.Rename, "b.mkv", folderPath),
            ]);

            // The port runs after the disk move and before the rollback, so occupying the caption's old
            // slot there makes the sidecar rollback warn while the primary's own slot stays free.
            var executor = new RenamerExecutor(
                new ReoccupyOldSlotThenMisreportDataPort(db, oldCaption), new CapturingEventBus(),
                new FakeRevertJournal(), "run-test", new DiskMover());

            var result = await executor.ExecuteAsync(plan, new RenamerOptions(), default);

            var failedItem = Assert.Single(result.Failed);
            Assert.Contains("rolled back", failedItem.Reason);
            Assert.Contains("rollback warnings", failedItem.Reason);

            // The primary is back and the row names it again, whatever the sidecar did.
            Assert.True(File.Exists(oldA), "the primary file must be back at its old path");
            Assert.False(File.Exists(Path.Combine(dir.Root, "b.mkv")));
            var (basename, _) = await ExecutorTestSeed.ReadFileAsync(db, fileA);
            Assert.Equal("a.mkv", basename);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    /// <summary>
    /// A throwing event bus runs after the journal append, so its failure must name the event and leave
    /// the revert-log row standing: reported as an unwritten revert-log entry it would tell a user their
    /// rename cannot be undone when it can.
    /// </summary>
    [Fact]
    public async Task EventPublishThrowsAfterTheSaveCommitted_RevertRowStands_WarnsAboutTheEvent()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string folderPath = dir.Root.Replace('\\', '/');
            var (_, videoId, fileA) =
                await ExecutorTestSeed.SeedVideoAsync(db, folderPath, "a.mkv", "Film A");
            File.WriteAllText(Path.Combine(dir.Root, "a.mkv"), "A-bytes");

            var plan = new RenamerPlan(videoId, RenamerFileKind.Video,
            [
                new RenamerPlanItem(fileA, folderPath + "/a.mkv", folderPath + "/b.mkv",
                    RenamerStatus.Rename, "b.mkv", folderPath),
            ]);

            var bus = new CapturingEventBus { PublishThrow = new InvalidOperationException("bus is down") };
            var journal = new FakeRevertJournal();
            var executor = new RenamerExecutor(
                new CoveRenamerDataPort(db), bus, journal, "run-test", new DiskMover());

            var result = await executor.ExecuteAsync(plan, new RenamerOptions(), default);

            var renamedItem = Assert.Single(result.Renamed);
            Assert.NotNull(renamedItem.Reason);
            Assert.Contains("rename event not published", renamedItem.Reason);
            Assert.DoesNotContain("revert-log entry not written", renamedItem.Reason);

            // The undo record is there, which is what the warning must not deny.
            var row = Assert.Single(journal.Rows);
            Assert.Equal(fileA, row.FileId);
            Assert.Equal(folderPath + "/a.mkv", row.OldPath);

            Assert.True(File.Exists(Path.Combine(dir.Root, "b.mkv")));
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    /// <summary>
    /// Test-only port: performs the real save (so the DB row genuinely commits the new basename), then
    /// returns a recomputed path that is deliberately wrong, so the executor's
    /// post-save "recomputed Path == on-disk path" assertion fails on the success path. Only the first
    /// save is misreported; the executor's restore of the row is left to report itself truthfully.
    /// </summary>
    private sealed class MismatchedRecomputedPathDataPort(DbContext db) : CoveRenamerDataPort(db)
    {
        private int _saves;

        public override async Task<string> ApplyAndSaveAsync(
            RenamerFileMutation mutation, CancellationToken ct = default)
        {
            string recomputed = await base.ApplyAndSaveAsync(mutation, ct);
            return ++_saves == 1 ? recomputed + ".WRONG" : recomputed;
        }
    }

    /// <summary>
    /// Test-only port: commits the real save, occupies the old slot so the rollback cannot reclaim it,
    /// and misreports the recomputed path so the post-save assertion fails.
    /// </summary>
    private sealed class ReoccupyOldSlotThenMisreportDataPort(DbContext db, string oldSlot)
        : CoveRenamerDataPort(db)
    {
        public override async Task<string> ApplyAndSaveAsync(
            RenamerFileMutation mutation, CancellationToken ct = default)
        {
            string recomputed = await base.ApplyAndSaveAsync(mutation, ct);
            File.WriteAllText(oldSlot, "intruder bytes re-occupying the old slot");
            return recomputed + ".WRONG";
        }
    }

}
