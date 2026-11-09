using Cove.Core.Auth;
using Cove.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Renamer.Contracts;
using Renamer.Execution;
using Renamer.Tests.TestSupport;
using static Cove.Extensions.Shared.Testing.HttpResultUnwrap;
using Renamed = Renamer.Tests.TestSupport.RenamedBatchFixture.Renamed;

namespace Renamer.Tests.Api;

public sealed class UndoRetryTests
{
    private const string RunId = "retry-run";

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

            var undo = ValueOf<UndoResult>(await ext.UndoAsync(Write, new RecordingAuthorizationService(), default));

            Assert.Equal(1, undo.Undone);
            Assert.Single(undo.SkippedSample);
            Assert.Equal(1, undo.SkippedCount);
            Assert.Empty(undo.FailedSample);
            Assert.Equal(0, undo.FailedCount);
            Assert.True(File.Exists(comes.OldFull), "the restorable file is back");
            Assert.True(File.Exists(stays.NewFull), "the blocked file never moved");

            // The table is the record of what is left - exactly the row that did not come back. Row
            // presence is the state, so the read that feeds the button must still return this batch.
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

            var undo = ValueOf<UndoResult>(await ext.UndoAsync(Write, new RecordingAuthorizationService(), default));

            Assert.Equal(1, undo.Undone);
            Assert.Equal(1, undo.SkippedCount);
            var stopped = Assert.Single(undo.SkippedSample);
            Assert.Equal(gone.FileId, stopped.FileId);

            await using var journal = new CoveRevertJournal(db);

            // Both rows are gone - the terminal one too, so the batch can reach spent instead of
            // offering an undo that could never complete.
            Assert.Null(await JournalPageReader.ReadWholeUndoTargetAsync(journal));

            // The aggregate still says how it ended, on the counter that keeps the two apart.
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

    private static async Task<(Renamer ext, Renamed first, Renamed second)> RenameTwoAsync(
        DbContext db, TempDir dir)
    {
        var (ext, seeded) = await RenameManyAsync(db, dir, ["one", "two"]);
        return (ext, seeded[0], seeded[1]);
    }

    private static Task<(Renamer ext, IReadOnlyList<Renamed> seeded)> RenameManyAsync(
        DbContext db, TempDir dir, IReadOnlyList<string> stems)
        => RenamedBatchFixture.CreateAsync(dir, db, RunId, stems);
}
