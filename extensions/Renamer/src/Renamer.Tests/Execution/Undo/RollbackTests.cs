using Renamer.Execution;
using Renamer.Options;
using Renamer.Planner;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Execution.Undo;

/// <summary>
/// The write-seam rollback proof this phase unlocked. Because <see cref="IRenamerDataPort.ApplyAndSaveAsync"/>
/// is now on the INTERFACE (not just the concrete <c>CoveRenamerDataPort</c>), the executor's
/// disk-first/DB-second rollback spine can be driven by a pure in-memory <see cref="FakeRenamerDataPort"/>
/// with a real on-disk move and NO live database — the L0 test that was impossible while the executor
/// bound the concrete port. The fake makes the save FAIL after a genuine <see cref="DiskMover"/> move;
/// the executor must restore the source and classify the item Failed, never leaving the file abandoned
/// at the new path with the DB unchanged.
/// </summary>
public sealed class RollbackTests
{
    [Fact]
    public async Task SaveThrowsAfterMove_FileRolledBackToOldPath_ClassifiedFailed_NoRevertLogNoEvent()
    {
        using var dir = new TempDir();
        string folderPath = dir.Root.Replace('\\', '/');

        const int fileId = 42;
        const int videoId = 7;
        string oldA = Path.Combine(dir.Root, "a.mkv");
        string newB = Path.Combine(dir.Root, "b.mkv");
        File.WriteAllText(oldA, "A-bytes");

        var fake = SeedSingleFilePort(videoId, fileId, folderPath, "a.mkv");
        // The seam under test: force the save to throw AFTER the disk move has already happened.
        fake.ApplyAndSaveThrow = new InvalidOperationException("forced save failure (unique index)");

        var bus = new CapturingEventBus();
        var journal = new FakeRevertJournal();
        var executor = new RenamerExecutor(fake, bus, journal, "run-test", new DiskMover());

        var plan = new RenamerPlan(videoId, RenamerFileKind.Video,
        [
            new RenamerPlanItem(fileId, folderPath + "/a.mkv", folderPath + "/b.mkv",
                RenamerStatus.Renamer, "b.mkv", folderPath),
        ]);

        var result = await executor.ExecuteAsync(plan, new RenamerOptions(), default);

        // The save was reached (proving the move already happened) and it threw → the item is Failed
        // with a rolled-back reason, not renamed, and produced no undo row / no reindex event.
        Assert.Single(fake.ApplyAndSaveCalls);
        var failedItem = Assert.Single(result.Failed);
        Assert.Equal(RenamerStatus.Failed, failedItem.Status);
        Assert.Contains("rolled back", failedItem.Reason);
        Assert.Empty(result.Renamed);
        Assert.Empty(journal.Rows);
        Assert.Empty(bus.Published);

        // The file is restored to its ORIGINAL path with original content, and not left at the new path.
        Assert.True(File.Exists(oldA), "file must be rolled back to its old path");
        Assert.Equal("A-bytes", File.ReadAllText(oldA));
        Assert.False(File.Exists(newB), "rolled-back file must not linger at the new path");
    }

    [Fact]
    public async Task SaveSucceeds_ReturnsSavedFile_ItemRenamed_RevertLogAndEventWritten()
    {
        using var dir = new TempDir();
        string folderPath = dir.Root.Replace('\\', '/');

        const int fileId = 42;
        const int videoId = 7;
        string oldA = Path.Combine(dir.Root, "a.mkv");
        string newB = Path.Combine(dir.Root, "b.mkv");
        File.WriteAllText(oldA, "A-bytes");

        var fake = SeedSingleFilePort(videoId, fileId, folderPath, "a.mkv");
        // A successful save reports the recomputed Path matching the on-disk destination.
        fake.RecomputedPaths[fileId] = folderPath + "/b.mkv";

        var bus = new CapturingEventBus();
        var journal = new FakeRevertJournal();
        var executor = new RenamerExecutor(fake, bus, journal, "run-test", new DiskMover());

        var plan = new RenamerPlan(videoId, RenamerFileKind.Video,
        [
            new RenamerPlanItem(fileId, folderPath + "/a.mkv", folderPath + "/b.mkv",
                RenamerStatus.Renamer, "b.mkv", folderPath),
        ]);

        var result = await executor.ExecuteAsync(plan, new RenamerOptions(), default);

        // The save-seam received the mutation and reported success → the item is renamed on disk with a
        // revert-log row and a reindex event, and the DB write carried the new basename.
        var savedCall = Assert.Single(fake.ApplyAndSaveCalls);
        Assert.Equal("b.mkv", Assert.Single(savedCall).NewBasename);
        var renamedItem = Assert.Single(result.Renamed);
        Assert.Equal(RenamerStatus.Renamer, renamedItem.Status);
        Assert.Empty(result.Failed);
        Assert.Single(journal.Rows);
        Assert.Single(bus.Published);

        Assert.True(File.Exists(newB), "file must be at the new path after a successful save");
        Assert.Equal("A-bytes", File.ReadAllText(newB));
        Assert.False(File.Exists(oldA), "renamed file must not linger at the old path");
    }

    /// <summary>
    /// A failure AFTER the save committed degrades the result; it never rolls the rename back.
    /// </summary>
    /// <remarks>
    /// The journal append and the reindex publish used to sit inside the same <c>try</c> as the save,
    /// whose <c>catch</c> rolls the disk back. So a throw from either undid a rename the database had
    /// already committed, and reported "DB save failed" for a save that had succeeded.
    /// </remarks>
    [Fact]
    public async Task JournalAppendThrowsAfterCommit_ItemStaysRenamed_WithAWarning_NoRollback()
    {
        using var dir = new TempDir();
        string folderPath = dir.Root.Replace('\\', '/');

        const int fileId = 42;
        const int videoId = 7;
        string oldA = Path.Combine(dir.Root, "a.mkv");
        string newB = Path.Combine(dir.Root, "b.mkv");
        File.WriteAllText(oldA, "A-bytes");

        var fake = SeedSingleFilePort(videoId, fileId, folderPath, "a.mkv");
        fake.RecomputedPaths[fileId] = folderPath + "/b.mkv";

        var bus = new CapturingEventBus();
        var journal = new FakeRevertJournal { AppendThrow = new InvalidOperationException("journal is full") };
        var executor = new RenamerExecutor(fake, bus, journal, "run-test", new DiskMover());

        var plan = new RenamerPlan(videoId, RenamerFileKind.Video,
        [
            new RenamerPlanItem(fileId, folderPath + "/a.mkv", folderPath + "/b.mkv",
                RenamerStatus.Renamer, "b.mkv", folderPath),
        ]);

        var result = await executor.ExecuteAsync(plan, new RenamerOptions(), default);

        // The rename stands: the database committed it, so unmaking it on disk would be the divergence.
        Assert.Empty(result.Failed);
        var renamedItem = Assert.Single(result.Renamed);
        Assert.Equal(RenamerStatus.Renamer, renamedItem.Status);
        Assert.NotNull(renamedItem.Reason);
        Assert.Contains("undo record", renamedItem.Reason!, StringComparison.OrdinalIgnoreCase);
        // The old wording blamed the save, which had not failed.
        Assert.DoesNotContain("DB save failed", renamedItem.Reason!);

        Assert.True(File.Exists(newB), "the committed rename must stand on disk");
        Assert.False(File.Exists(oldA), "the file must not be rolled back to its old path");
        Assert.Single(fake.ApplyAndSaveCalls);
    }

    /// <summary>
    /// A save that reports NO row for the file is reported as exactly that, not as a path mismatch.
    /// </summary>
    /// <remarks>
    /// <c>saved.FirstOrDefault(…)</c> over a <c>readonly record struct</c> yields <c>default(SavedFile)</c>
    /// with a null <c>RecomputedPath</c>, and <c>PathsEqual</c> coalesces null to <c>""</c> — so a missing
    /// entry takes the mismatch branch and blames a path that was never compared. The rollback is correct
    /// either way (the save's outcome is unknown, and leaving the file at the new path is the
    /// silently-abandoned bug that branch exists to prevent); it is the REASON that was false.
    /// </remarks>
    [Fact]
    public async Task SaveReportsNoRowForTheFile_FailedNamesTheMissingRow_NotAPathMismatch()
    {
        using var dir = new TempDir();
        string folderPath = dir.Root.Replace('\\', '/');

        const int fileId = 42;
        const int videoId = 7;
        string oldA = Path.Combine(dir.Root, "a.mkv");
        string newB = Path.Combine(dir.Root, "b.mkv");
        File.WriteAllText(oldA, "A-bytes");

        var fake = SeedSingleFilePort(videoId, fileId, folderPath, "a.mkv");
        fake.OmitSavedRowsFor.Add(fileId);

        var bus = new CapturingEventBus();
        var journal = new FakeRevertJournal();
        var executor = new RenamerExecutor(fake, bus, journal, "run-test", new DiskMover());

        var plan = new RenamerPlan(videoId, RenamerFileKind.Video,
        [
            new RenamerPlanItem(fileId, folderPath + "/a.mkv", folderPath + "/b.mkv",
                RenamerStatus.Renamer, "b.mkv", folderPath),
        ]);

        var result = await executor.ExecuteAsync(plan, new RenamerOptions(), default);

        var failedItem = Assert.Single(result.Failed);
        Assert.Equal(RenamerStatus.Failed, failedItem.Status);
        Assert.Contains("no row", failedItem.Reason, StringComparison.OrdinalIgnoreCase);
        // The old wording is the defect: it quotes an empty recomputed path as though one were compared.
        Assert.DoesNotContain("recomputed Path ''", failedItem.Reason);

        Assert.Empty(journal.Rows);
        Assert.Empty(bus.Published);
        Assert.True(File.Exists(oldA), "the file must be rolled back to its old path");
        Assert.False(File.Exists(newB), "rolled-back file must not linger at the new path");
    }

    private static FakeRenamerDataPort SeedSingleFilePort(int entityId, int fileId, string folderPath, string basename)
    {
        var fake = new FakeRenamerDataPort();
        var file = new RenamerFile(fileId, RenamerFileKind.Video, basename, ParentFolderId: 1, ParentFolderPath: folderPath);
        fake.SeedEntity(new RenamerEntity(
            entityId, RenamerFileKind.Video, "Film A", Code: null, StudioName: null, Date: null,
            Organized: true, Performers: [], TagRefs: [], Files: [file]));
        return fake;
    }
}
