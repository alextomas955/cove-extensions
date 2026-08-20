using Cove.Core.Entities;
using Cove.Data;
using Renamer.Execution;
using Renamer.Options;
using Renamer.Planner;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Execution.Journal;

/// <summary>
/// The journal's paged row read against a real <see cref="CoveContext"/>: that a batch larger than one
/// page comes back whole, in one order, each row once — and that a run over it ENDS.
/// </summary>
/// <remarks>
/// The defect these cases exist for is a memory one: the read that fed <c>/undo</c> materialized every
/// pending row of a batch, and a batch is as large as the library. Paging fixes that, and introduces a
/// worse failure of its own — a cursor that fails to advance turns a request into a hang rather than an
/// error. That is why the termination case here carries a bounded guard: without one it would prove the
/// bug by never finishing.
/// <para>
/// Driven through the real EF implementation rather than the fake. What is under test is the cursor's
/// behaviour over a table rows are being DELETED from, which a fake reimplementing the same rule would
/// only prove agrees with itself.
/// </para>
/// <para>
/// The page limits used here are deliberately small and deliberately not the shipped default: the
/// subject is that the limit is honoured and that the run does not depend on its value. The default's
/// value is a judgement and is pinned nowhere.
/// </para>
/// </remarks>
[Trait("Tier", "L1")]
[Collection(CoveDataExtensionScope.CollectionName)]
public sealed class JournalPagingTests
{
    // 10 rows read 3 at a time is four pages — three full and a short last one, so both boundary shapes
    // are crossed rather than assumed.
    private const int PageLimit = 3;
    private const int RowCount = 10;

    // Real files are slower to seed than bare rows, so the undo cases use fewer of them — still more
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

        using var journal = await SeedRowsAsync(db, RowCount);

        var pages = new List<IReadOnlyList<RevertRow>>();
        long cursor = long.MaxValue;
        while (true)
        {
            Assert.True(pages.Count <= RowCount, "the cursor stopped advancing — see the guard note below");
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

        // Collected in the order the PAGES yielded them, then asserted as one series: the boundary is
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

        using var journal = await SeedRowsAsync(db, RowCount);

        Assert.Single(await journal.ReadBatchPageAsync(RunId, long.MaxValue, limit: 1));
        Assert.Equal(PageLimit, (await journal.ReadBatchPageAsync(RunId, long.MaxValue, PageLimit)).Count);

        // A limit above what the batch holds is not an error and does not pad: it simply returns the rest.
        Assert.Equal(RowCount, (await journal.ReadBatchPageAsync(RunId, long.MaxValue, RowCount * 10)).Count);
    }

    [Fact]
    public async Task RetiringRowsBetweenPages_NeitherSkipsNorRepeatsARow()
    {
        // The reason the cursor keys on the sequence rather than on an offset. Rows are DELETED as they
        // restore, so an offset-based second page over a table that just lost three rows would start
        // three rows further in than it should and silently skip work.
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        await using var _ = db;
        await using var __ = conn;

        using var journal = await SeedRowsAsync(db, RowCount);

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

        using var journal = await SeedRowsAsync(db, RowCount);

        Assert.Empty(await journal.ReadBatchPageAsync("no-such-run", long.MaxValue, PageLimit));

        // And past the end of a batch that does exist — the state every paging run finishes in.
        Assert.Empty(await journal.ReadBatchPageAsync(RunId, belowSeq: 1, PageLimit));
    }

    [Fact]
    public async Task AMultiPageRunWhereEveryRowStopsRetryably_Terminates_AndAttemptsEachRowExactlyOnce()
    {
        // The failure this case exists to catch does not fail an assertion — it HANGS. A cursor that did
        // not advance past rows which stayed pending would re-read the first page forever, and nothing
        // retires to end it, because a retryable stop deliberately leaves its row in the table. The
        // bounded page guard inside RunPagedUndoAsync is what turns that hang into a failure.
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            var (journal, seeded) = await RenameManyAsync(db, dir, UndoRowCount);
            using var _ = journal;

            // Occupy every restore slot: the reverse move refuses to clobber, so every row stops for a
            // cause the world can clear and none of them retires.
            foreach (var s in seeded)
            {
                File.WriteAllText(s.OldFull, "someone else's file");
            }

            var run = await RunPagedUndoAsync(db, journal);

            Assert.Equal(0, run.Undone);

            // Counted off what the RUN produced — each stop carries the identity of the row it stopped
            // on — rather than off a number this test also supplied.
            Assert.Equal(UndoRowCount, run.Attempts.Count);
            Assert.Equal(UndoRowCount, run.Attempts.Distinct().Count());
            Assert.True(run.Pages > 1, $"the batch spanned {run.Pages} page(s); the case needs more than one");
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task AMultiPageRunWhereEveryRowStopsRetryably_LeavesEveryRowInTheTable()
    {
        // What remains in the table IS the work left, and paging must not quietly change that: a row
        // that stopped for a clearable cause has to be offered again on the next undo.
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            var (journal, seeded) = await RenameManyAsync(db, dir, UndoRowCount);
            using var _ = journal;

            foreach (var s in seeded)
            {
                File.WriteAllText(s.OldFull, "someone else's file");
            }

            await RunPagedUndoAsync(db, journal);

            var left = await JournalPageReader.ReadAllRowsAsync(journal, RunId, PageLimit);
            Assert.Equal(UndoRowCount, left.Count);

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
    public async Task AMultiPageRun_RestoresEveryRestorableRow_AndTheAggregateReconciles()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            var (journal, seeded) = await RenameManyAsync(db, dir, UndoRowCount);
            using var _ = journal;

            var run = await RunPagedUndoAsync(db, journal);

            Assert.True(run.Pages > 1, $"the batch spanned {run.Pages} page(s); the case needs more than one");
            Assert.Equal(UndoRowCount, run.Undone);
            Assert.Equal(UndoRowCount, run.Attempts.Distinct().Count());

            // On disk, not merely in the response — a page boundary that dropped a row would leave its
            // file at the renamed path with the count still reading right.
            foreach (var s in seeded)
            {
                Assert.True(File.Exists(s.OldFull), $"restored {s.OldFull}");
                Assert.False(File.Exists(s.NewFull));
            }

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

    /// <summary>What one paged undo run did, expressed in what the replayer itself reported.</summary>
    private sealed record PagedRun(int Undone, int Pages, IReadOnlyList<(string RunId, long Seq)> Attempts);

    /// <summary>
    /// Pages the batch and reverse-replays each page, in the shape <c>UndoAsync</c> uses — a page below
    /// the cursor, a replay, retirement of the settled rows, then the cursor moved to the lowest
    /// sequence the page returned.
    /// </summary>
    /// <remarks>
    /// Driven at <see cref="PageLimit"/> rather than at the shipped default so a handful of rows spans
    /// several pages. The guard is the point of the whole helper: a cursor that failed to advance would
    /// otherwise loop here without ever reaching an assertion.
    /// </remarks>
    private static async Task<PagedRun> RunPagedUndoAsync(CoveContext db, CoveRevertJournal journal)
    {
        var replayer = new UndoReplayer(new CoveRenamerDataPort(db), new CapturingEventBus(), new DiskMover());

        var attempts = new List<(string RunId, long Seq)>();
        int undone = 0;
        int pages = 0;
        long cursor = long.MaxValue;

        while (true)
        {
            Assert.True(
                pages <= UndoRowCount,
                $"the paging cursor stopped advancing: {pages} pages read over {UndoRowCount} rows");

            var page = await journal.ReadBatchPageAsync(RunId, cursor, PageLimit);
            if (page.Count == 0)
            {
                return new PagedRun(undone, pages, attempts);
            }

            pages++;
            var result = await replayer.RevertAsync(new RevertBatch(RunId, RenamerFileKind.Video, page));

            undone += result.Undone;
            attempts.AddRange(result.Restored.Select(r => (r.RunId, r.Seq)));
            attempts.AddRange(result.Failed.Concat(result.Skipped).Select(f => (f.RunId, f.Seq)));

            foreach (var row in result.Restored)
            {
                await journal.DeleteRowAsync(row.RunId, row.Seq, unrestorable: false);
            }

            foreach (var stopped in result.Failed.Concat(result.Skipped))
            {
                if (UndoTerminalClassifier.IsTerminal(stopped.Stop))
                {
                    await journal.DeleteRowAsync(stopped.RunId, stopped.Seq, unrestorable: true);
                }
            }

            cursor = page[^1].Seq;
        }
    }

    /// <summary>One seeded video and the paths its forward rename moved between.</summary>
    private sealed record Seeded(int VideoId, int FileId, string OldFull, string NewFull);

    private static async Task<CoveRevertJournal> SeedRowsAsync(CoveContext db, int rows)
    {
        var journal = new CoveRevertJournal(db);
        await journal.BeginBatchAsync(RunId, RenamerFileKind.Video, Opened);

        for (int i = 1; i <= rows; i++)
        {
            await journal.AppendAsync(
                new RevertRow(RunId, Seq: 0, EntityId: 100 + i, FileId: 200 + i, $"/media/old/{i}.mkv", ""));
        }

        return journal;
    }

    /// <summary>
    /// Seeds one folder holding <paramref name="count"/> videos and really renames each into ONE batch,
    /// so the batch holds one row per file and the paging is over rows rather than over batches.
    /// </summary>
    private static async Task<(CoveRevertJournal journal, IReadOnlyList<Seeded> seeded)> RenameManyAsync(
        CoveContext db, TempDir dir, int count)
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
        var journal = new CoveRevertJournal(db);
        await journal.BeginBatchAsync(RunId, RenamerFileKind.Video, Opened);

        foreach (var s in seeded)
        {
            var plan = await new RenamerPlanner(port).PlanAsync(RenamerFileKind.Video, s.VideoId, options, RouteLookupsFixtures.RoutingNeutral, default);
            var forward = await new RenamerExecutor(port, new CapturingEventBus(), journal, RunId, new DiskMover())
                .ExecuteAsync(plan, options, default);
            Assert.Single(forward.Renamed);
            Assert.True(File.Exists(s.NewFull), $"forward rename landed at {s.NewFull}");
            Assert.False(File.Exists(s.OldFull));
        }

        return (journal, seeded);
    }
}
