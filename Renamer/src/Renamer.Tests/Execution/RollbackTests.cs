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
            Assert.Empty(result.Renamerd);
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
}
