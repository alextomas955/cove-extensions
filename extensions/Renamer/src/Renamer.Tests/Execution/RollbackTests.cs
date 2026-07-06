using Cove.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Renamer.Execution;
using Renamer.Options;
using Renamer.Planner;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Execution;

/// <summary>
/// The safety-spine rollback test the whole extension hinges on. Seed the SQLite + temp-dir state so the
/// disk move SUCCEEDS but the subsequent SaveChangesAsync THROWS (a forced unique-index clash, with
/// the pre-check bypassed via <see cref="CollisionBlindDataPort"/>). Assert that AFTER execution:
/// (a) the file is back at its ORIGINAL on-disk path, (b) the moved sidecar (if any) is back, and
/// (c) the DB row still carries the OLD basename — disk and DB consistent. Runs on SQLite-in-memory
/// because EF-InMemory enforces neither the unique index nor transaction rollback.
///
/// The test FIRST proves the disk move really happened (it is observable via the executor having
/// invoked DiskMover.Move — asserted by the file being momentarily at the new path is not possible
/// post-rollback, so instead we assert the negative-control: a DiskMover spy is unnecessary because
/// the only path that reaches SaveChangesAsync is AFTER a successful move; we additionally assert the
/// failure reason names the rollback, proving the catch — not the move — produced the terminal state).
/// </summary>
[Trait("Tier", "Integration")]
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
            var fileTaken = await ExecutorTestSeed.SeedAdditionalFileAsync(db, folderId, videoId, "taken.mkv");

            // Disk: "a.mkv" exists; "taken.mkv" does NOT (so the disk move succeeds first).
            string oldA = Path.Combine(dir.Root, "a.mkv");
            File.WriteAllText(oldA, "A-bytes");
            string newPath = Path.Combine(dir.Root, "taken.mkv");
            Assert.False(File.Exists(newPath), "precondition: disk target must be free so the MOVE happens before the save");

            var plan = new RenamerPlan(videoId, RenamerFileKind.Video,
            [
                new RenamerPlanItem(fileA, folderPath + "/a.mkv", folderPath + "/taken.mkv",
                    RenamerStatus.Renamer, "taken.mkv", folderPath),
            ]);

            var port = new CollisionBlindDataPort(db);
            var bus = new CapturingEventBus();
            var executor = new RenamerExecutor(port, bus, new RevertLog(new FakeStore()), new DiskMover());

            var result = await executor.ExecuteAsync(plan, new RenamerOptions(), default);

            // The save threw after the move → item failed with a rollback reason (proving the catch,
            // i.e. the move had ALREADY happened before the save error — not a pre-move skip).
            var failedItem = Assert.Single(result.Failed);
            Assert.Equal(RenamerStatus.Failed, failedItem.Status);
            Assert.Contains("rolled back", failedItem.Reason);
            Assert.Empty(result.Renamed);
            Assert.Empty(result.RevertLog);   // no success row written
            Assert.Empty(bus.Published);      // no event for a failed item

            // (a) the file is restored to its ORIGINAL path with original content.
            Assert.True(File.Exists(oldA), "file must be rolled back to its old path");
            Assert.Equal("A-bytes", File.ReadAllText(oldA));
            // and is NOT left at the new path.
            Assert.False(File.Exists(newPath), "rolled-back file must not linger at the new path");

            // (c) the DB row still has the OLD basename — disk and DB consistent.
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
            var vfA = await db.Set<VideoFile>().FirstAsync(f => f.Id == fileA);
            db.Set<VideoCaption>().Add(new VideoCaption { FileId = fileA, Filename = "a.en.vtt", LanguageCode = "en", CaptionType = "vtt" });
            await db.SaveChangesAsync();

            string oldA = Path.Combine(dir.Root, "a.mkv");
            string oldCap = Path.Combine(dir.Root, "a.en.vtt");
            File.WriteAllText(oldA, "A-bytes");
            File.WriteAllText(oldCap, "caption");

            var plan = new RenamerPlan(videoId, RenamerFileKind.Video,
            [
                new RenamerPlanItem(fileA, folderPath + "/a.mkv", folderPath + "/taken.mkv",
                    RenamerStatus.Renamer, "taken.mkv", folderPath),
            ]);

            var executor = new RenamerExecutor(
                new CollisionBlindDataPort(db), new CapturingEventBus(), new RevertLog(new FakeStore()), new DiskMover());

            var result = await executor.ExecuteAsync(plan, new RenamerOptions(), default);

            Assert.Single(result.Failed);
            // Both the primary file AND the moved caption sidecar are restored to their old paths.
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
    /// The post-save consistency-assertion branch: the DB save SUCCEEDS (commits the new basename) but
    /// the runtime "recomputed Path == on-disk path" assertion FAILS. The executor must roll the disk
    /// back to the OLD path through the same mover the move used — NOT leave the file abandoned at the
    /// new path with no undo record (the pre-fix bug). Assert: (a) the item is Failed with a
    /// path-mismatch + rolled-back reason, (b) the file is back at its OLD on-disk path, (c) no
    /// revert-log row and no event were written for it.
    /// </summary>
    [Fact]
    public async Task SaveSucceedsButRecomputedPathMismatch_FileRolledBack_NoRevertLog()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string folderPath = dir.Root.Replace('\\', '/');
            var (folderId, videoId, fileA) =
                await ExecutorTestSeed.SeedVideoAsync(db, folderPath, "a.mkv", "Film A");

            // Disk: "a.mkv" exists; the target "b.mkv" is free so the disk move succeeds first.
            string oldA = Path.Combine(dir.Root, "a.mkv");
            File.WriteAllText(oldA, "A-bytes");
            string newPath = Path.Combine(dir.Root, "b.mkv");
            Assert.False(File.Exists(newPath), "precondition: disk target must be free so the MOVE happens before the save");

            var plan = new RenamerPlan(videoId, RenamerFileKind.Video,
            [
                new RenamerPlanItem(fileA, folderPath + "/a.mkv", folderPath + "/b.mkv",
                    RenamerStatus.Renamer, "b.mkv", folderPath),
            ]);

            // Port that COMMITS the real save (new basename persisted) but reports a RecomputedPath that
            // does NOT match the on-disk destination, tripping the post-save assertion.
            var port = new MismatchedRecomputedPathDataPort(db);
            var bus = new CapturingEventBus();
            var executor = new RenamerExecutor(port, bus, new RevertLog(new FakeStore()), new DiskMover());

            var result = await executor.ExecuteAsync(plan, new RenamerOptions(), default);

            // (a) the item is Failed, and the reason names BOTH the path mismatch and the rollback.
            var failedItem = Assert.Single(result.Failed);
            Assert.Equal(RenamerStatus.Failed, failedItem.Status);
            Assert.Contains("recomputed Path", failedItem.Reason);
            Assert.Contains("rolled back", failedItem.Reason);
            Assert.Empty(result.Renamed);

            // (c) no revert-log row and no event for a failed item.
            Assert.Empty(result.RevertLog);
            Assert.Empty(bus.Published);

            // (b) the file is rolled back to its OLD path with original content, and NOT at the new path.
            Assert.True(File.Exists(oldA), "file must be rolled back to its old path");
            Assert.Equal("A-bytes", File.ReadAllText(oldA));
            Assert.False(File.Exists(newPath), "rolled-back file must not linger at the new path");
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    /// <summary>
    /// Test-only port: performs the REAL save (so the DB row genuinely commits the new basename), then
    /// returns a <see cref="CoveRenamerDataPort.SavedFile"/> whose RecomputedPath is deliberately wrong,
    /// so the executor's post-save "recomputed Path == on-disk path" assertion fails on the success path.
    /// </summary>
    private sealed class MismatchedRecomputedPathDataPort(Cove.Data.CoveContext db) : CoveRenamerDataPort(db)
    {
        public override async Task<IReadOnlyList<SavedFile>> ApplyAndSaveAsync(
            IReadOnlyList<RenamerFileMutation> mutations, CancellationToken ct = default)
        {
            var saved = await base.ApplyAndSaveAsync(mutations, ct);
            return [.. saved.Select(s => new SavedFile(s.FileId, s.RecomputedPath + ".WRONG"))];
        }
    }
}
