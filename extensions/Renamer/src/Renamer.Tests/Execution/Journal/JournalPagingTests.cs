using System.Data.Common;
using Cove.Core.Auth;
using Cove.Core.Entities;
using Cove.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Renamer.Contracts;
using Renamer.Execution;
using Renamer.Options;
using Renamer.Planner;
using Renamer.Tests.TestSupport;
using static Cove.Extensions.Shared.Testing.HttpResultUnwrap;

namespace Renamer.Tests.Execution.Journal;

public sealed class JournalPagingTests
{
    // 10 rows read 3 at a time is four pages - three full and a short last one, so both boundary shapes
    // are crossed rather than assumed.
    private const int PageLimit = 3;
    private const int RowCount = 10;

    // Real files are slower to seed than bare rows, so the undo cases use fewer of them - still more
    // than PageLimit, which is what makes them multi-page (7 rows at 3 a page is 3, 3, 1).
    private const int UndoRowCount = 7;

    private const string RunId = "paging-run";

    private static readonly DateTime Opened = new(2026, 8, 11, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task ABatchLargerThanThePageLimit_PagesIntoEveryRowExactlyOnce_InOneDescendingSeries()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        await using var _ = db;
        await using var __ = conn;

        await using var journal = await SeedRowsAsync(db, RowCount);

        var pages = new List<IReadOnlyList<RevertRow>>();
        long cursor = long.MaxValue;
        while (true)
        {
            Assert.True(pages.Count <= RowCount, "the cursor stopped advancing - see the guard note below");
            var page = await journal.ReadBatchPageAsync(RunId, cursor, PageLimit);
            if (page.Count == 0)
            {
                break;
            }

            pages.Add(page);
            cursor = page[^1].Seq;
        }

        // 10 rows at 3 a page: the boundary is genuinely crossed, so what follows is a statement about
        // paging rather than about one page that happened to hold everything.
        Assert.Equal(4, pages.Count);
        Assert.Equal([3, 3, 3, 1], pages.Select(p => p.Count));

        // Collected in the order the pages yielded them, then asserted as one series: the boundary is
        // exactly where an order bug lives, so a per-page assertion would look right while the run
        // reversed two files in the wrong order relative to each other.
        var series = pages.SelectMany(p => p.Select(r => r.Seq)).ToList();
        Assert.Equal(RowCount, series.Count);
        Assert.Equal(RowCount, series.Distinct().Count());
        Assert.Equal(series.OrderByDescending(s => s), series);
        Assert.Equal(Enumerable.Range(1, RowCount).Select(i => (long)i).Reverse(), series);
    }

    [Fact]
    public async Task APageNeverReturnsMoreRowsThanTheLimitItWasGiven()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        await using var _ = db;
        await using var __ = conn;

        await using var journal = await SeedRowsAsync(db, RowCount);

        Assert.Single(await journal.ReadBatchPageAsync(RunId, long.MaxValue, limit: 1));
        Assert.Equal(PageLimit, (await journal.ReadBatchPageAsync(RunId, long.MaxValue, PageLimit)).Count);

        // A limit above what the batch holds is not an error and does not pad: it simply returns the rest.
        Assert.Equal(RowCount, (await journal.ReadBatchPageAsync(RunId, long.MaxValue, RowCount * 10)).Count);
    }

    [Fact]
    public async Task RetiringRowsBetweenPages_NeitherSkipsNorRepeatsARow()
    {
        // The reason the cursor keys on the sequence rather than on an offset. Rows are deleted as they
        // restore, so an offset-based second page over a table that just lost three rows would start
        // three rows further in than it should and silently skip work.
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        await using var _ = db;
        await using var __ = conn;

        await using var journal = await SeedRowsAsync(db, RowCount);

        var seen = new List<long>();
        long cursor = long.MaxValue;
        for (int guard = 0; guard <= RowCount; guard++)
        {
            Assert.True(guard < RowCount, "the cursor stopped advancing");
            var page = await journal.ReadBatchPageAsync(RunId, cursor, PageLimit);
            if (page.Count == 0)
            {
                break;
            }

            seen.AddRange(page.Select(r => r.Seq));
            cursor = page[^1].Seq;

            foreach (var row in page)
            {
                await journal.DeleteRowAsync(row.RunId, row.Seq, unrestorable: false);
            }
        }

        Assert.Equal(Enumerable.Range(1, RowCount).Select(i => (long)i).Reverse(), seen);
        Assert.Empty(await JournalPageReader.ReadAllRowsAsync(journal, RunId, PageLimit));
    }

    [Fact]
    public async Task APageReadForARunWithNoRows_IsEmptyRatherThanAThrow()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        await using var _ = db;
        await using var __ = conn;

        await using var journal = await SeedRowsAsync(db, RowCount);

        Assert.Empty(await journal.ReadBatchPageAsync("no-such-run", long.MaxValue, PageLimit));

        // And past the end of a batch that does exist - the state every paging run finishes in.
        Assert.Empty(await journal.ReadBatchPageAsync(RunId, belowSeq: 1, PageLimit));
    }

    [Fact]
    public async Task AMultiPageUndoWhereEveryRowStopsRetryably_Terminates_AttemptsEachRowOnce_AndLeavesThemAll()
    {
        // A cursor that did not advance past rows which stayed pending would re-read the first page
        // forever, because a retryable stop deliberately leaves its row in the table. The command
        // budget turns that hang into a failure without depending on how loaded the machine is.
        using var dir = new TempDir();
        using var budget = new CommandBudget();
        var (db, conn) = await ContextWithBudgetAsync(budget);
        try
        {
            var (ext, seeded) = await RenameManyAsync(db, dir, UndoRowCount);

            // Occupy every restore slot: the reverse move refuses to clobber, so every row stops for a
            // cause the world can clear and none of them retires.
            foreach (var s in seeded)
            {
                File.WriteAllText(s.OldFull, "someone else's file");
            }

            budget.Arm(MaxUndoCommands);
            UndoResult undo;
            try
            {
                undo = UndoValue(await ext.UndoAsync(
                    Write, new RecordingAuthorizationService(), budget.Token));
            }
            catch (OperationCanceledException)
            {
                Assert.Fail(
                    $"the undo issued over {MaxUndoCommands} commands for {UndoRowCount} rows, "
                    + "so its paging cursor stopped advancing");
                throw;
            }

            Assert.Equal(0, undo.Undone);
            Assert.Equal(UndoRowCount, undo.SkippedCount);
            Assert.Equal(
                seeded.Select(s => s.FileId).Order(),
                undo.SkippedSample.Select(e => e.FileId).Order());

            // What remains in the table is the work left: a row that stopped for a clearable cause has
            // to be offered again on the next undo.
            await using var journal = new CoveRevertJournal(db);
            Assert.Equal(UndoRowCount, (await JournalPageReader.ReadAllRowsAsync(journal, RunId, PageLimit)).Count);

            var summary = await journal.ReadUndoTargetAsync();
            Assert.NotNull(summary);
            Assert.Equal(UndoRowCount, summary.Value.Remaining);
            Assert.Equal(0, summary.Value.RestoredCount);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task AMultiPageUndo_RestoresEveryRestorableRow_AndTheAggregateReconciles()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            var (ext, seeded) = await RenameManyAsync(db, dir, UndoRowCount);

            var undo = UndoValue(await ext.UndoAsync(Write, new RecordingAuthorizationService(), default));

            Assert.Equal(UndoRowCount, undo.Undone);
            Assert.Equal(0, undo.SkippedCount);
            Assert.Equal(0, undo.FailedCount);

            // On disk, not merely in the response - a page boundary that dropped a row would leave its
            // file at the renamed path with the count still reading right.
            foreach (var s in seeded)
            {
                Assert.True(File.Exists(s.OldFull), $"restored {s.OldFull}");
                Assert.False(File.Exists(s.NewFull));
            }

            await using var journal = new CoveRevertJournal(db);
            Assert.Empty(await JournalPageReader.ReadAllRowsAsync(journal, RunId, PageLimit));

            var summary = await journal.ReadUndoTargetAsync();
            Assert.NotNull(summary);
            Assert.Equal(UndoRowCount, summary.Value.OriginalCount);
            Assert.Equal(UndoRowCount, summary.Value.RestoredCount);
            Assert.Equal(0, summary.Value.Remaining);
            Assert.Equal(
                summary.Value.OriginalCount,
                summary.Value.RestoredCount + summary.Value.UnrestorableCount + summary.Value.Remaining);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    // Far above what a correct undo of UndoRowCount rows issues, and reached within moments by a loop
    // that re-reads the same page.
    private const int MaxUndoCommands = 5_000;

    private static FakePrincipalAccessor Write => FakePrincipalAccessor.WithPermissions(Permissions.VideosWrite);

    // The same context CoveContextFactory builds, with a command counter on it.
    private static async Task<(DbContext db, SqliteConnection conn)> ContextWithBudgetAsync(CommandBudget budget)
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync(CancellationToken.None);
        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseSqlite(conn)
            .AddInterceptors(budget)
            .ReplaceService<IModelCacheKeyFactory, CoveModelCacheKeyFactory>()
            .Options;
        var db = new CoveContext(options, principalAccessor: null);
        await db.Database.EnsureCreatedAsync(CancellationToken.None);
        return (db, conn);
    }

    // Cancels its token once more commands than the armed budget have executed.
    private sealed class CommandBudget : DbCommandInterceptor, IDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private int _remaining = int.MaxValue;

        public CancellationToken Token => _cts.Token;

        public void Arm(int commands) => Volatile.Write(ref _remaining, commands);

        public void Dispose() => _cts.Dispose();

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Spend();
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Spend();
            return ValueTask.FromResult(result);
        }

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            Spend();
            return result;
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Spend();
            return ValueTask.FromResult(result);
        }

        private void Spend()
        {
            if (Interlocked.Decrement(ref _remaining) < 0)
            {
                _cts.Cancel();
            }
        }
    }

    private static UndoResult UndoValue(IResult result) =>
        Assert.IsType<UndoResult>(Assert.IsType<IValueHttpResult>(Unwrap(result), exactMatch: false).Value);

    private sealed record Seeded(int VideoId, int FileId, string OldFull, string NewFull);

    private static async Task<CoveRevertJournal> SeedRowsAsync(DbContext db, int rows)
    {
        var journal = new CoveRevertJournal(db);
        await journal.BeginBatchAsync(RunId, RunId, RenamerFileKind.Video, Opened);

        for (int i = 1; i <= rows; i++)
        {
            await journal.AppendAsync(
                new RevertRow(RunId, Seq: 0, EntityId: 100 + i, FileId: 200 + i, $"/media/old/{i}.mkv", ""));
        }

        return journal;
    }

    // Seeds one folder holding count videos and really renames each into one batch, so the batch
    // holds one row per file and the paging is over rows rather than over batches. The returned
    // extension reads the journal PageLimit rows at a time, so a handful of rows spans several pages.
    // The batch opens now rather than at Opened, because the undo refuses a batch past retention.
    private static async Task<(global::Renamer.Renamer ext, IReadOnlyList<Seeded> seeded)> RenameManyAsync(
        DbContext db, TempDir dir, int count)
    {
        string folderPath = dir.Root.Replace('\\', '/');
        var folder = new Folder { Path = folderPath, ModTime = DateTime.UtcNow };
        db.Set<Folder>().Add(folder);
        await db.SaveChangesAsync();

        var seeded = new List<Seeded>();
        for (int i = 1; i <= count; i++)
        {
            var video = new Video { Title = $"film {i}", Organized = true };
            db.Set<Video>().Add(video);
            await db.SaveChangesAsync();

            var file = new VideoFile
            {
                Basename = $"raw {i}.mkv",
                ParentFolderId = folder.Id,
                Format = "mkv",
                VideoId = video.Id,
            };
            db.Set<VideoFile>().Add(file);
            await db.SaveChangesAsync();

            string oldFull = Path.Combine(dir.Root, $"raw {i}.mkv");
            File.WriteAllText(oldFull, $"bytes-{i}");
            seeded.Add(new Seeded(video.Id, file.Id, oldFull, Path.Combine(dir.Root, $"film {i}.mkv")));
        }

        var options = new RenamerOptions { FilenameTemplate = "$title" };
        var port = new CoveRenamerDataPort(db);
        await using (var journal = new CoveRevertJournal(db))
        {
            await journal.BeginBatchAsync(RunId, RunId, RenamerFileKind.Video, DateTime.UtcNow);

            foreach (var s in seeded)
            {
                var plan = await new RenamerPlanner(port).PlanAsync(RenamerFileKind.Video, s.VideoId, options, default);
                var forward = await new RenamerExecutor(port, new CapturingEventBus(), journal, RunId)
                    .ExecuteAsync(plan, options, default);
                Assert.Single(forward.Renamed);
                Assert.True(File.Exists(s.NewFull), $"forward rename landed at {s.NewFull}");
                Assert.False(File.Exists(s.OldFull));
            }
        }

        var (ext, _) = await ExtensionHarness.CreateWithSharedContextAsync(db, options: options);
        ext.UndoPageSize = PageLimit;
        return (ext, seeded);
    }
}
