using Renamer.Execution;
using Renamer.Options;
using Renamer.Planner;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Execution;

/// <summary>
/// Collision BACKSTOP (integration, SQLite): the <c>(ParentFolderId, Basename)</c> UNIQUE index is the
/// final safety net behind the proactive suffix loop. Seed two VideoFiles "a.mkv"/"b.mkv" in one
/// folder on a real SQLite <see cref="Cove.Data.CoveContext"/>; force a renamer of "a"→"b" with the DB
/// collision pre-check BYPASSED (<see cref="CollisionBlindDataPort"/>) so the proactive suffixing
/// never fires and the save itself hits the unique index → DbUpdateException. Assert the executor's
/// catch fired (item Failed) and the disk file was rolled back to its old path. This proves the
/// backstop an EF-InMemory test would FALSE-GREEN (InMemory enforces no unique index).
/// </summary>
[Trait("Tier", "Integration")]
public sealed class CollisionTests
{
    [Fact]
    public async Task DuplicateBasenameSave_ThrowsOnUniqueIndex_CaughtAndRolledBack()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string folderPath = dir.Root.Replace('\\', '/');
            var (folderId, videoId, fileA) =
                await ExecutorTestSeed.SeedVideoAsync(db, folderPath, "a.mkv", "Film A");
            var fileB = await ExecutorTestSeed.SeedAdditionalFileAsync(db, folderId, videoId, "b.mkv");

            // Disk: only "a.mkv" exists ("b.mkv" the DB-occupied target is NOT on disk, so the disk
            // move SUCCEEDS and the save is what hits the unique index).
            string oldA = Path.Combine(dir.Root, "a.mkv");
            File.WriteAllText(oldA, "A-bytes");

            // Hand-build a plan that renamers a → b (the DB-taken name). The blind port stops the
            // executor's exec-time re-check from suffixing it away.
            var plan = new RenamerPlan(videoId, RenamerFileKind.Video,
            [
                new RenamerPlanItem(fileA, folderPath + "/a.mkv", folderPath + "/b.mkv",
                    RenamerStatus.Renamer, "b.mkv", folderPath),
            ]);

            var port = new CollisionBlindDataPort(db);
            var bus = new CapturingEventBus();
            var executor = new RenamerExecutor(port, bus, new RevertLog(new FakeStore()), new DiskMover());

            var result = await executor.ExecuteAsync(plan, new RenamerOptions(), default);

            // The save threw on the unique index → item failed, caught (not propagated).
            var failedItem = Assert.Single(result.Failed);
            Assert.Equal(RenamerStatus.Failed, failedItem.Status);
            Assert.Empty(result.Renamerd);

            // Disk rolled back: "a.mkv" restored, "b.mkv" not left on disk; no event published.
            Assert.True(File.Exists(oldA), "the disk file must be rolled back to its old path");
            Assert.Equal("A-bytes", File.ReadAllText(oldA));
            Assert.False(File.Exists(Path.Combine(dir.Root, "b.mkv")), "the moved file must not linger at the new path");
            Assert.Empty(bus.Published);

            // DB rows unchanged.
            var (basenameA, _) = await ExecutorTestSeed.ReadFileAsync(db, fileA);
            var (basenameB, _) = await ExecutorTestSeed.ReadFileAsync(db, fileB);
            Assert.Equal("a.mkv", basenameA);
            Assert.Equal("b.mkv", basenameB);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }
}
