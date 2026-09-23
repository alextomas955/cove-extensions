using Cove.Core.Auth;
using Cove.Core.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Renamer.Contracts;
using Renamer.Execution;
using Renamer.Options;
using Renamer.Planner;
using Renamer.Tests.TestSupport;
using static Cove.Extensions.Shared.Testing.HttpResultUnwrap;

namespace Renamer.Tests.Execution.Journal;

public sealed class UndoRetryTests
{
    private const string RunId = "retry-run";
    // Relative, never a calendar date. The undo path refuses a batch outside the retention window, and
    // these cases are about retry accounting rather than expiry - so a fixed date would hold while it
    // was written and then start failing these tests on its own, for a reason none of them names.
    private static readonly DateTime Opened = DateTime.UtcNow;

    private sealed record Seeded(int VideoId, int FileId, string OldFull, string NewFull);

    [Fact]
    public async Task APartialUndo_RetiresOnlyTheRowsThatCameBack_AndLeavesTheRestInTheTable()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            var (ext, comes, stays) = await RenameTwoAsync(db, dir);

            // Occupy the restore slot of the second file, which is a cause the world can clear: the
            // reverse move refuses to clobber, so that entry stops and its row must survive.
            File.WriteAllText(stays.OldFull, "someone else's file");

            var undo = UndoValue(await ext.UndoAsync(Write, new RecordingAuthorizationService(), default));

            Assert.Equal(1, undo.Undone);
            Assert.Single(undo.SkippedSample);
            Assert.Equal(1, undo.SkippedCount);
            Assert.Empty(undo.FailedSample);
            Assert.Equal(0, undo.FailedCount);
            Assert.True(File.Exists(comes.OldFull), "the restorable file is back");
            Assert.True(File.Exists(stays.NewFull), "the blocked file never moved");

            // The table is the record of what is left - exactly the row that did not come back. The
            // previous defect spent the whole batch on the first partial success, which is what made
            // the remaining work unreachable; row presence is the state, so the read that feeds the
            // button must still return this batch.
            await using var journal = new CoveRevertJournal(db);
            var open = await JournalPageReader.ReadWholeUndoTargetAsync(journal);
            Assert.NotNull(open);
            var remaining = Assert.Single(open.Rows);
            Assert.Equal(stays.FileId, remaining.FileId);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task ARowWhoseFileLeftTheLibrary_IsRetiredAsUnrestorable_AndNeverOfferedAgain()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            var (ext, comes, gone) = await RenameTwoAsync(db, dir);

            // The row outlives its file: nothing carries that id any more, so no later attempt can find
            // a current path to move back from. This is the one stop no retry can improve on.
            db.Set<VideoFile>().Remove(await db.Set<VideoFile>().SingleAsync(f => f.Id == gone.FileId));
            await db.SaveChangesAsync();

            var undo = UndoValue(await ext.UndoAsync(Write, new RecordingAuthorizationService(), default));

            Assert.Equal(1, undo.Undone);
            // The count is what the response states and what a caller reads; the sample is only where
            var stopped = Assert.Single(undo.SkippedSample);
            Assert.Equal(gone.FileId, stopped.FileId);

            await using var journal = new CoveRevertJournal(db);

            // Both rows are gone - the terminal one too, so the batch can reach spent instead of
            // offering an undo that could never complete.
            Assert.Null(await JournalPageReader.ReadWholeUndoTargetAsync(journal));

            // …and the aggregate still says how it ended, on the counter that keeps the two apart.
            var summary = await journal.ReadUndoTargetAsync();
            Assert.NotNull(summary);
            Assert.Equal(2, summary.Value.OriginalCount);
            Assert.Equal(1, summary.Value.RestoredCount);
            Assert.Equal(1, summary.Value.UnrestorableCount);
            Assert.Equal(0, summary.Value.Remaining);
            Assert.True(File.Exists(comes.OldFull));
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task TheOriginalCount_NeverMoves_SoRestoredPlusUnrestorablePlusRemainingAlwaysSumsToIt()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            // Three files, one per outcome: one comes back, one can never come back, one is blocked for
            // now. Together they exercise every counter the aggregate has.
            var (ext, seeded) = await RenameManyAsync(db, dir, ["comes", "gone", "blocked"]);
            var (comes, gone, blocked) = (seeded[0], seeded[1], seeded[2]);

            db.Set<VideoFile>().Remove(await db.Set<VideoFile>().SingleAsync(f => f.Id == gone.FileId));
            await db.SaveChangesAsync();
            File.WriteAllText(blocked.OldFull, "someone else's file");

            await ext.UndoAsync(Write, new RecordingAuthorizationService(), default);

            await using var journal = new CoveRevertJournal(db);
            var afterFirst = await journal.ReadUndoTargetAsync();
            Assert.NotNull(afterFirst);
            Assert.Equal(3, afterFirst.Value.OriginalCount);
            Assert.Equal(1, afterFirst.Value.RestoredCount);
            Assert.Equal(1, afterFirst.Value.UnrestorableCount);
            Assert.Equal(1, afterFirst.Value.Remaining);
            AssertReconciles(afterFirst.Value);

            File.Delete(blocked.OldFull);
            await ext.UndoAsync(Write, new RecordingAuthorizationService(), default);

            var afterRetry = await journal.ReadUndoTargetAsync();
            Assert.NotNull(afterRetry);
            // The count of what the run journalled never moves; only how it was settled does.
            Assert.Equal(3, afterRetry.Value.OriginalCount);
            Assert.Equal(2, afterRetry.Value.RestoredCount);
            Assert.Equal(1, afterRetry.Value.UnrestorableCount);
            Assert.Equal(0, afterRetry.Value.Remaining);
            AssertReconciles(afterRetry.Value);
            Assert.True(File.Exists(comes.OldFull));
            Assert.True(File.Exists(blocked.OldFull));
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    private static void AssertReconciles(RevertOperationSummary summary) =>
        Assert.Equal(
            summary.OriginalCount,
            summary.RestoredCount + summary.UnrestorableCount + summary.Remaining);

    private static FakePrincipalAccessor Write => FakePrincipalAccessor.WithPermissions(Permissions.VideosWrite);

    // Seeds and forward-renames two files in one batch, returning them in seed order.
    private static async Task<(global::Renamer.Renamer ext, Seeded first, Seeded second)> RenameTwoAsync(
        DbContext db, TempDir dir)
    {
        var (ext, seeded) = await RenameManyAsync(db, dir, ["one", "two"]);
        return (ext, seeded[0], seeded[1]);
    }

    // Seeds one folder holding one video per stems entry, then really renames each into one journal
    // batch - so the batch holds one row per file, which is what makes "acts only on what is left"
    // a statement about rows rather than about batches. The forward half runs through the planner
    // and executor directly rather than through the batch endpoint, because that endpoint fans its
    // files out across per-worker scopes and every scope here resolves the one seeded context. The
    // subject of these cases is the undo endpoint, which is exercised for real.
    private static async Task<(global::Renamer.Renamer ext, IReadOnlyList<Seeded> seeded)> RenameManyAsync(
        DbContext db, TempDir dir, IReadOnlyList<string> stems)
    {
        string folderPath = dir.Root.Replace('\\', '/');
        var folder = new Folder { Path = folderPath, ModTime = DateTime.UtcNow };
        db.Set<Folder>().Add(folder);
        await db.SaveChangesAsync();

        var seeded = new List<Seeded>();
        foreach (string stem in stems)
        {
            var video = new Video { Title = $"{stem} film", Organized = true };
            db.Set<Video>().Add(video);
            await db.SaveChangesAsync();

            var file = new VideoFile
            {
                Basename = $"raw {stem}.mkv",
                ParentFolderId = folder.Id,
                Format = "mkv",
                VideoId = video.Id,
            };
            db.Set<VideoFile>().Add(file);
            await db.SaveChangesAsync();

            string oldFull = Path.Combine(dir.Root, $"raw {stem}.mkv");
            File.WriteAllText(oldFull, $"{stem}-bytes");
            seeded.Add(new Seeded(video.Id, file.Id, oldFull, Path.Combine(dir.Root, $"{stem} film.mkv")));
        }

        var options = new RenamerOptions { FilenameTemplate = "$title" };
        var port = new CoveRenamerDataPort(db);
        await using (var journal = new CoveRevertJournal(db))
        {
            await journal.BeginBatchAsync(RunId, RunId, RenamerFileKind.Video, Opened);
            foreach (var s in seeded)
            {
                var plan = await new RenamerPlanner(port)
                    .PlanAsync(RenamerFileKind.Video, s.VideoId, options, default);
                var forward = await new RenamerExecutor(
                        port, new CapturingEventBus(), journal, RunId)
                    .ExecuteAsync(plan, options, default);
                Assert.Single(forward.Renamed);
            }
        }

        var (ext, _) = await ExtensionHarness.CreateWithSharedContextAsync(db, options: options);

        foreach (var s in seeded)
        {
            Assert.True(File.Exists(s.NewFull), $"forward rename landed at {s.NewFull}");
            Assert.False(File.Exists(s.OldFull));
        }

        return (ext, seeded);
    }

    private static UndoResult UndoValue(IResult result) =>
        Assert.IsType<UndoResult>(Assert.IsAssignableFrom<IValueHttpResult>(Unwrap(result)).Value);
}
