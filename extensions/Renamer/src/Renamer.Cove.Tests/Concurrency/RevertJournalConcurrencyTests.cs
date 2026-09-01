using Renamer.Execution;
using Renamer.Planner;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Concurrency;

/// <summary>
/// What is still at risk once the journal is rows in a table: the sequence number that half-identifies
/// a row is minted by the extension, not by the database, and one journal instance is shared by every
/// parallel worker of a batch. So several workers appending at once must each land as their own row
/// with their own number, and none may be lost.
/// </summary>
/// <remarks>
/// This replaces the suite that proved the blob's single-writer gate. That gate covered a
/// read-modify-write of one stored value, which a row insert makes structurally impossible, and an
/// in-memory row list the executor no longer keeps — so the property it asserted no longer exists to
/// be broken. What is asserted here is read BACK from the journal, never from an in-memory mirror of
/// it: a mirror would only prove that the mirror agrees with itself.
/// <para>
/// Cove disables EF's thread-safety checks, so getting this wrong corrupts silently rather than
/// throwing. Every assertion is therefore on the rows that came back, never on an exception.
/// </para>
/// </remarks>
[Collection(CoveDataExtensionScope.CollectionName)]
public sealed class RevertJournalConcurrencyTests
{
    private const int N = 200;

    private static readonly DateTime Opened = new(2026, 8, 3, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task ParallelAppendsToOneBatch_EachLandAsTheirOwnRow_WithDistinctSequenceNumbers()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        await using var _ = db;
        await using var __ = conn;

        using var journal = new CoveRevertJournal(db);
        await journal.BeginBatchAsync("R-parallel", RenamerFileKind.Video, Opened);

        // Yield first so the append windows genuinely overlap rather than running in turn.
        await Task.WhenAll(Enumerable.Range(0, N).Select(async i =>
        {
            await Task.Yield();
            await journal.AppendAsync(
                new RevertRow("R-parallel", Seq: 0, EntityId: 1000 + i, FileId: 5000 + i, $"media/old-{i}.mkv", ""));
        }));

        var batch = await JournalPageReader.ReadWholeUndoTargetAsync(journal);
        Assert.NotNull(batch);
        Assert.Equal("R-parallel", batch.RunId);

        // Exactly N rows, N distinct sequence numbers, and N distinct file ids: a lost append shows as
        // a short count, a colliding sequence number as a repeated key.
        Assert.Equal(N, batch.Rows.Count);
        Assert.Equal(N, batch.Rows.Select(r => r.Seq).Distinct().Count());
        Assert.Equal(N, batch.Rows.Select(r => r.FileId).Distinct().Count());

        // Every row is whole — a torn write could keep the count right while corrupting a field.
        var byFileId = batch.Rows.ToDictionary(r => r.FileId);
        for (int i = 0; i < N; i++)
        {
            Assert.True(byFileId.TryGetValue(5000 + i, out var row), $"missing fileId {5000 + i}");
            Assert.Equal(1000 + i, row.EntityId);
            Assert.Equal($"media/old-{i}.mkv", row.OldPath);
        }

        // The aggregate counted every one of them, since it accrues per append rather than being
        // declared up front — a race there would leave the panel promising the wrong number.
        var summary = await journal.ReadUndoTargetAsync();
        Assert.NotNull(summary);
        Assert.Equal(N, summary.Value.OriginalCount);
    }

    [Fact]
    public async Task ConcurrentAppendsAndReads_NeverThrow_AndTheReadsSeeAConsistentBatch()
    {
        // A read interleaved with appends is the ordinary state: the panel polls the summary while a
        // batch is running. It must never observe a half-built change set, and never throw.
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        await using var _ = db;
        await using var __ = conn;

        using var journal = new CoveRevertJournal(db);
        await journal.BeginBatchAsync("R-mixed", RenamerFileKind.Video, Opened);

        var appends = Enumerable.Range(0, N).Select(async i =>
        {
            await Task.Yield();
            await journal.AppendAsync(
                new RevertRow("R-mixed", Seq: 0, EntityId: 2000 + i, FileId: 6000 + i, $"media/m-{i}.mkv", ""));
        });

        var reads = Enumerable.Range(0, 40).Select(async _ =>
        {
            await Task.Yield();
            var seen = await journal.ReadUndoTargetAsync();
            Assert.NotNull(seen);

            // Whatever moment it caught, the aggregate describes a real state: nothing settled, and a
            // count that never exceeds what was asked for.
            Assert.InRange(seen.Value.OriginalCount, 0, N);
            Assert.Equal(0, seen.Value.RestoredCount);
            Assert.Equal(seen.Value.OriginalCount, seen.Value.Remaining);
        });

        await Task.WhenAll(appends.Concat(reads));

        var batch = await JournalPageReader.ReadWholeUndoTargetAsync(journal);
        Assert.NotNull(batch);
        Assert.Equal(N, batch.Rows.Count);
        Assert.Equal(N, batch.Rows.Select(r => r.Seq).Distinct().Count());
    }
}
