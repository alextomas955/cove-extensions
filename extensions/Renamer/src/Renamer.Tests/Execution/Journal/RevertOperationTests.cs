using Microsoft.EntityFrameworkCore;
using Renamer.Execution;
using Renamer.Planner;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Execution.Journal;

/// <summary>
/// The journal read over an operation: several batches of one user action aggregate into one summary,
/// are walked newest-first by a cursor, and a batch written before the operation column existed reads
/// as an operation of one.
/// </summary>
[Collection(CoveDataExtensionScope.CollectionName)]
public sealed class RevertOperationTests
{
    private static readonly DateTime Opened = new(2026, 8, 3, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task TheSummary_SumsTheOperationsCounts_AndTakesItsEarliestTimestamp()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        await using var _ = db;
        await using var __ = conn;

        var journal = new CoveRevertJournal(db);
        await SeedAsync(journal, "run-video", "op", RenamerFileKind.Video, rows: 2, Opened);
        await SeedAsync(journal, "run-image", "op", RenamerFileKind.Image, rows: 3, Opened.AddMinutes(5));

        await journal.DeleteRowAsync("run-video", seq: 1, unrestorable: false);
        await journal.DeleteRowAsync("run-image", seq: 1, unrestorable: true);

        var summary = await journal.ReadUndoTargetAsync();
        Assert.NotNull(summary);
        Assert.Equal("op", summary!.Value.OperationId);
        Assert.Equal(5, summary.Value.OriginalCount);
        Assert.Equal(1, summary.Value.RestoredCount);
        Assert.Equal(1, summary.Value.UnrestorableCount);
        Assert.Equal(3, summary.Value.Remaining);

        // The moment the user clicked, not the moment the last kind started.
        Assert.Equal(Opened.Ticks, summary.Value.OpenedAtUtcTicks);
    }

    [Fact]
    public async Task TheOperationsBatches_ComeBackNewestFirst_AndTheCursorEndsTheWalk()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        await using var _ = db;
        await using var __ = conn;

        var journal = new CoveRevertJournal(db);
        await SeedAsync(journal, "run-video", "op", RenamerFileKind.Video, rows: 1, Opened);
        await SeedAsync(journal, "run-image", "op", RenamerFileKind.Image, rows: 1, Opened.AddMinutes(5));
        await SeedAsync(journal, "run-other", "op-other", RenamerFileKind.Video, rows: 1, Opened.AddMinutes(9));

        var walked = new List<RenamerFileKind>();
        long ticks = IRevertJournal.FirstBatchTicks;
        string runId = IRevertJournal.FirstBatchRunId;

        // Nothing is retired between the reads, so every batch still holds every row it started with —
        // the state in which a visited-set-free walk would re-offer the same batch forever.
        while (await journal.ReadNextBatchAsync("op", ticks, runId) is { } batch)
        {
            walked.Add(batch.Kind);
            ticks = batch.WrittenAtUtcTicks;
            runId = batch.RunId;
            Assert.True(walked.Count <= 3, "the batch cursor failed to advance");
        }

        Assert.Equal([RenamerFileKind.Image, RenamerFileKind.Video], walked);
    }

    [Fact]
    public async Task TheKindsOfAnOperation_AreEveryKindItsBatchesName()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        await using var _ = db;
        await using var __ = conn;

        var journal = new CoveRevertJournal(db);
        await SeedAsync(journal, "run-video", "op", RenamerFileKind.Video, rows: 1, Opened);
        await SeedAsync(journal, "run-image", "op", RenamerFileKind.Image, rows: 1, Opened.AddMinutes(5));
        await SeedAsync(journal, "run-other", "op-other", RenamerFileKind.Audio, rows: 1, Opened.AddMinutes(9));

        var kinds = await journal.ReadOperationKindsAsync("op");

        Assert.Equal(
            [RenamerFileKind.Image, RenamerFileKind.Video],
            kinds.OrderBy(k => k.ToString(), StringComparer.Ordinal));
    }

    [Fact]
    public async Task ABatchWithNoOperationId_ReadsAsAnOperationOfOne()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        await using var _ = db;
        await using var __ = conn;

        // The state a batch written before the operation column existed is in.
        var journal = new CoveRevertJournal(db);
        await SeedAsync(journal, "legacy", operationId: "", RenamerFileKind.Video, rows: 2, Opened);

        var summary = await journal.ReadUndoTargetAsync();
        Assert.NotNull(summary);
        Assert.Equal("legacy", summary!.Value.OperationId);
        Assert.Equal(2, summary.Value.OriginalCount);

        var batch = await journal.ReadNextBatchAsync(
            "legacy", IRevertJournal.FirstBatchTicks, IRevertJournal.FirstBatchRunId);
        Assert.NotNull(batch);
        Assert.Equal("legacy", batch!.Value.RunId);
        Assert.Equal("legacy", batch.Value.OperationId);
        Assert.Equal(2, (await journal.ReadBatchPageAsync("legacy", long.MaxValue, limit: 10)).Count);
    }

    private static async Task SeedAsync(
        CoveRevertJournal journal, string runId, string operationId, RenamerFileKind kind, int rows,
        DateTime openedAt)
    {
        await journal.BeginBatchAsync(runId, operationId, kind, openedAt);

        for (int i = 1; i <= rows; i++)
        {
            await journal.AppendAsync(
                new RevertRow(runId, Seq: 0, EntityId: 100 + i, FileId: 200 + i, $"/media/old/{runId}-{i}.mkv", ""));
        }
    }
}
