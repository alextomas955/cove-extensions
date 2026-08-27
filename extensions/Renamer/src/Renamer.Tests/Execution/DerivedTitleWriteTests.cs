using Renamer.Execution;
using Renamer.Options;
using Renamer.Planner;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Execution;

/// <summary>
/// The title write at the port, against the row rather than against the plan that asked for it.
/// </summary>
/// <remarks>
/// The planner derives a title only for a title-less item, so the port's own emptiness re-check looks
/// redundant from the plan's side - and it is exactly the window it exists for: a person can type a
/// title between the preview and the run, and this is the only place the extension writes metadata
/// rather than location, so a rename must never overwrite what they wrote. The same check is what makes
/// the write idempotent across the files of a multi-file item, which save one at a time.
/// </remarks>
public sealed class DerivedTitleWriteTests
{
    [Fact]
    public async Task ATitleWrite_LandsOnlyOnARowThatIsStillTitleless_AndTheRenamerLandsEitherWay()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string folderPath = dir.Root.Replace('\\', '/');
            string siblingDir = Path.Combine(dir.Root, "sibling").Replace('\\', '/');

            var (_, titlelessId, titlelessFileId) =
                await ExecutorTestSeed.SeedVideoAsync(db, folderPath, "one.mkv", title: null!);
            var (_, titledId, titledFileId) =
                await ExecutorTestSeed.SeedVideoAsync(db, siblingDir, "two.mkv", "Typed In By Hand");

            var port = new CoveRenamerDataPort(db);
            await port.ApplyAndSaveAsync(
            [
                new RenamerFileMutation(
                    titlelessFileId, "one renamed.mkv", null, null,
                    new RenamerEntityTitleWrite(RenamerFileKind.Video, titlelessId, "one")),
                new RenamerFileMutation(
                    titledFileId, "two renamed.mkv", null, null,
                    new RenamerEntityTitleWrite(RenamerFileKind.Video, titledId, "two")),
            ]);

            // The rename half of both mutations committed, so the refusal below is the title check and
            // not a save that never happened.
            var (titlelessBasename, _) = await ExecutorTestSeed.ReadFileAsync(db, titlelessFileId);
            var (titledBasename, _) = await ExecutorTestSeed.ReadFileAsync(db, titledFileId);
            Assert.Equal("one renamed.mkv", titlelessBasename);
            Assert.Equal("two renamed.mkv", titledBasename);

            Assert.Equal("one", await ExecutorTestSeed.ReadVideoTitleAsync(db, titlelessId));
            Assert.Equal("Typed In By Hand", await ExecutorTestSeed.ReadVideoTitleAsync(db, titledId));
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    /// <summary>
    /// Undo puts the file back under its old name and leaves the recorded title standing, so a later
    /// rename of that item renders the same name again.
    /// </summary>
    /// <remarks>
    /// The undo path restores names and folders, not metadata, so this is what the documented behaviour
    /// actually is rather than an oversight to correct here. Pinned because the settings reference states
    /// it: without the recorded title the second plan would derive one from whatever the file is called
    /// at the time, which after an undo is the old name again.
    /// </remarks>
    [Fact]
    public async Task Undo_RestoresTheName_KeepsTheRecordedTitle_AndTheNextRenamerRendersTheSameName()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string folderPath = dir.Root.Replace('\\', '/');
            var (_, videoId, fileId) = await ExecutorTestSeed.SeedVideoAsync(
                db, folderPath, "raw clip.mkv", title: null!,
                date: new DateOnly(2021, 3, 14), height: 2160);
            File.WriteAllText(Path.Combine(dir.Root, "raw clip.mkv"), "video-bytes");

            var options = new RenamerOptions
            {
                FilenameTemplate = "{$date - }$title{ [$resolution]}",
                FilenameAsTitle = true,
            };
            var port = new CoveRenamerDataPort(db);
            var planner = new RenamerPlanner(port);
            var journal = new FakeRevertJournal();

            await journal.BeginBatchAsync("run-test", RenamerFileKind.Video, DateTime.UtcNow);
            var forward = await new RenamerExecutor(
                    port, new CapturingEventBus(), journal, "run-test", new DiskMover())
                .ExecuteAsync(
                    await planner.PlanAsync(RenamerFileKind.Video, videoId, options, default),
                    options, default);
            Assert.Single(forward.Renamed);

            var batch = await JournalPageReader.ReadWholeUndoTargetAsync(journal);
            Assert.NotNull(batch);
            var undone = await new UndoReplayer(port, new CapturingEventBus(), new DiskMover())
                .RevertAsync(batch!, default);
            Assert.Equal(1, undone.Undone);

            Assert.True(File.Exists(Path.Combine(dir.Root, "raw clip.mkv")), "undo must restore the name");
            var (restoredBasename, _) = await ExecutorTestSeed.ReadFileAsync(db, fileId);
            Assert.Equal("raw clip.mkv", restoredBasename);
            Assert.Equal("raw clip", await ExecutorTestSeed.ReadVideoTitleAsync(db, videoId));

            // The stored title decides the next name, so it is the SAME name rather than one derived
            // from the restored filename.
            var again = Assert.Single(
                (await planner.PlanAsync(RenamerFileKind.Video, videoId, options, default)).Items);
            Assert.Equal("2021-03-14 - raw clip [4k].mkv", again.NewBasename);
            Assert.Null(again.DerivedTitle);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }
}
