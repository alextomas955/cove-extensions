using Cove.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Renamer.Execution;
using Renamer.Options;
using Renamer.Planner;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Execution;

public sealed class DerivedTitleWriteTests
{
    [Theory]
    [InlineData(RenamerFileKind.Video)]
    [InlineData(RenamerFileKind.Image)]
    [InlineData(RenamerFileKind.Audio)]
    [InlineData(RenamerFileKind.Text)]
    public async Task ATitleWrite_LandsOnlyOnARowThatIsStillTitleless_AndTheRenameLandsEitherWay(RenamerFileKind kind)
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            var (titlelessId, titlelessFileId) = await SeedAsync(db, kind, "library/one", "one.bin", title: null);
            var (titledId, titledFileId) = await SeedAsync(db, kind, "library/two", "two.bin", "Typed In By Hand");

            var port = new CoveRenamerDataPort(db);
            await port.ApplyAndSaveAsync(
                new RenamerFileMutation(
                    titlelessFileId, "one renamed.bin", null, null,
                    new RenamerEntityTitleWrite(kind, titlelessId, "one")));
            await port.ApplyAndSaveAsync(
                new RenamerFileMutation(
                    titledFileId, "two renamed.bin", null, null,
                    new RenamerEntityTitleWrite(kind, titledId, "two")));

            // The rename half of both mutations committed, so the refusal below is the title check and
            // not a save that never happened.
            var (titlelessBasename, _) = await ExecutorTestSeed.ReadFileAsync(db, titlelessFileId);
            var (titledBasename, _) = await ExecutorTestSeed.ReadFileAsync(db, titledFileId);
            Assert.Equal("one renamed.bin", titlelessBasename);
            Assert.Equal("two renamed.bin", titledBasename);

            Assert.Equal("one", await ReadTitleAsync(db, kind, titlelessId));
            Assert.Equal("Typed In By Hand", await ReadTitleAsync(db, kind, titledId));
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task Undo_RestoresTheName_KeepsTheRecordedTitle_AndTheNextRenameRendersTheSameName()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string folderPath = dir.Root.Replace('\\', '/');

            // No stored width, which is what Cove holds for a file it never probed. The
            // $resolution label needs both dimensions, so the rendered name carries none.
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

            await journal.BeginBatchAsync("run-test", "run-test", RenamerFileKind.Video, DateTime.UtcNow);
            var forward = await new RenamerExecutor(
                    port, new CapturingEventBus(), journal, "run-test")
                .ExecuteAsync(
                    await planner.PlanAsync(RenamerFileKind.Video, videoId, options, default),
                    options, default);
            Assert.Single(forward.Renamed);
            Assert.True(
                File.Exists(Path.Combine(dir.Root, "2021-03-14 - raw clip.mkv")),
                "the forward rename did not produce the name the second pass is compared against");

            var batch = await JournalPageReader.ReadWholeUndoTargetAsync(journal);
            Assert.NotNull(batch);
            var undone = await new UndoReplayer(port, new CapturingEventBus())
                .RevertAsync(batch!, default);
            Assert.Equal(1, undone.Undone);

            Assert.True(File.Exists(Path.Combine(dir.Root, "raw clip.mkv")), "undo must restore the name");
            var (restoredBasename, _) = await ExecutorTestSeed.ReadFileAsync(db, fileId);
            Assert.Equal("raw clip.mkv", restoredBasename);
            Assert.Equal("raw clip", await ExecutorTestSeed.ReadVideoTitleAsync(db, videoId));

            // The stored title decides the next name, so it is the same name rather than one derived
            // from the restored filename.
            var again = Assert.Single(
                (await planner.PlanAsync(RenamerFileKind.Video, videoId, options, default)).Items);
            Assert.Equal("2021-03-14 - raw clip.mkv", again.NewBasename);
            Assert.Null(again.DerivedTitle);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    private static async Task<(int EntityId, int FileId)> SeedAsync(
        DbContext db, RenamerFileKind kind, string folderPath, string basename, string? title)
    {
        switch (kind)
        {
            case RenamerFileKind.Video:
                {
                    var (_, id, fileId) = await ExecutorTestSeed.SeedVideoAsync(db, folderPath, basename, title!);
                    return (id, fileId);
                }
            case RenamerFileKind.Image:
                {
                    var (_, id, fileId) = await ExecutorTestSeed.SeedImageAsync(db, folderPath, basename, title!);
                    return (id, fileId);
                }
            case RenamerFileKind.Audio:
                {
                    var (_, id, fileId) = await ExecutorTestSeed.SeedAudioAsync(db, folderPath, basename, title!);
                    return (id, fileId);
                }
            case RenamerFileKind.Text:
                {
                    var (_, id, fileId) = await ExecutorTestSeed.SeedTextAsync(db, folderPath, basename, title!);
                    return (id, fileId);
                }
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    private static async Task<string?> ReadTitleAsync(DbContext db, RenamerFileKind kind, int id)
    {
        db.ChangeTracker.Clear();
        return kind switch
        {
            RenamerFileKind.Video => await db.Set<Video>().AsNoTracking().Where(x => x.Id == id).Select(x => x.Title).SingleAsync(),
            RenamerFileKind.Image => await db.Set<Image>().AsNoTracking().Where(x => x.Id == id).Select(x => x.Title).SingleAsync(),
            RenamerFileKind.Audio => await db.Set<Audio>().AsNoTracking().Where(x => x.Id == id).Select(x => x.Title).SingleAsync(),
            RenamerFileKind.Text => await db.Set<TextDocument>().AsNoTracking().Where(x => x.Id == id).Select(x => x.Title).SingleAsync(),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }
}
