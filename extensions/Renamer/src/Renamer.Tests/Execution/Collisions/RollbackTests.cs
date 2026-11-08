using Cove.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Renamer.Execution;
using Renamer.Options;
using Renamer.Planner;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Execution.Collisions;

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
            // A second row occupies "taken.mkv", so saving a.mkv onto it hits the unique index.
            await ExecutorTestSeed.SeedAdditionalFileAsync(db, folderId, videoId, "taken.mkv");

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
            var executor = new RenamerExecutor(port, bus, journal, "run-test");

            var result = await executor.ExecuteAsync(plan, new RenamerOptions(), default);

            var failedItem = Assert.Single(result.Failed);
            Assert.Equal(RenamerStatus.Failed, failedItem.Status);
            Assert.Contains("rolled back", failedItem.Reason);
            Assert.Empty(result.Renamed);
            Assert.Empty(journal.Rows);
            Assert.Empty(bus.Published);

            Assert.True(File.Exists(oldA), "file must be rolled back to its old path");
            Assert.Equal("A-bytes", File.ReadAllText(oldA));
            Assert.False(File.Exists(newPath), "rolled-back file must not linger at the new path");

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
                new CollisionBlindDataPort(db), new CapturingEventBus(), new FakeRevertJournal(), "run-test");

            var result = await executor.ExecuteAsync(plan, new RenamerOptions(), default);

            Assert.Single(result.Failed);
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
            var executor = new RenamerExecutor(port, bus, journal, "run-test");

            var result = await executor.ExecuteAsync(plan, new RenamerOptions(), default);

            var failedItem = Assert.Single(result.Failed);
            Assert.Equal(RenamerStatus.Failed, failedItem.Status);
            Assert.Contains("recomputed Path", failedItem.Reason);
            Assert.Contains("rolled back", failedItem.Reason);
            Assert.DoesNotContain("NOT confirmed", failedItem.Reason);
            Assert.Empty(result.Renamed);

            Assert.Empty(journal.Rows);
            Assert.Empty(bus.Published);

            Assert.True(File.Exists(oldA), "file must be rolled back to its old path");
            Assert.Equal("A-bytes", File.ReadAllText(oldA));
            Assert.False(File.Exists(newPath), "rolled-back file must not linger at the new path");

            var (basename, _) = await ExecutorTestSeed.ReadFileAsync(db, fileA);
            Assert.Equal("a.mkv", basename);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

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
                new CoveRenamerDataPort(db), bus, journal, "run-test");

            var result = await executor.ExecuteAsync(plan, new RenamerOptions(), default);

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
                "run-test");

            var result = await executor.ExecuteAsync(plan, new RenamerOptions(), default);

            var failedItem = Assert.Single(result.Failed);
            Assert.Contains("did NOT return to its old path", failedItem.Reason);
            Assert.DoesNotContain("; rolled back", failedItem.Reason);
            Assert.Empty(journal.Rows);

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
                new FakeRevertJournal(), "run-test");

            var result = await executor.ExecuteAsync(plan, new RenamerOptions(), default);

            var failedItem = Assert.Single(result.Failed);
            Assert.Contains("rolled back", failedItem.Reason);
            Assert.Contains("rollback warnings", failedItem.Reason);

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
                new CoveRenamerDataPort(db), bus, journal, "run-test");

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
