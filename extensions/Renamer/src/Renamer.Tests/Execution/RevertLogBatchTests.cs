using Renamer.Execution;
using Renamer.Planner;

namespace Renamer.Tests.Execution;

/// <summary>
/// The batch-aware, backward-readable revert log. Each batch header carries the
/// run-level kind; each row carries its own entityId (entityId|fileId|old|new). Two runs append into
/// one persisted blob; reading the last open batch returns ONLY the second run's rows newest-first
/// with its kind. A 2-entity batch round-trips both distinct entityIds (each ≠ its fileId), proving
/// undo can publish the right per-row event id. A flat legacy blob (id|old|new, no #batch headers)
/// parses as one implicit Video batch with EntityId=FileId. Consumed-marking makes a second read skip
/// the consumed batch. Malformed/short lines are tolerated (skipped, never thrown). DB-free over
/// <see cref="FakeStore"/>.
/// </summary>
public sealed class RevertLogBatchTests
{
    private static RevertLog NewLog(FakeStore store) => new(store);

    [Fact]
    public async Task TwoRuns_ReadLastOpenBatch_ReturnsOnlySecondRun_WithItsKind()
    {
        var store = new FakeStore();
        var log = NewLog(store);

        // Run 1 (kind=Video): two rows.
        await log.BeginBatchAsync("R1", RenamerFileKind.Video);
        await log.AppendAsync(entityId: 7, fileId: 70, oldPath: "media/a.mkv", newPath: "media/A.mkv");
        await log.AppendAsync(entityId: 8, fileId: 80, oldPath: "media/b.mkv", newPath: "media/B.mkv");

        // Run 2 (kind=Video): one row.
        await log.BeginBatchAsync("R2", RenamerFileKind.Video);
        await log.AppendAsync(entityId: 9, fileId: 90, oldPath: "media/c.mkv", newPath: "media/C.mkv");

        // Reading the last open batch returns ONLY R2's one entry, with R2's kind.
        var batch = await log.ReadLastOpenBatchAsync();
        Assert.NotNull(batch);
        Assert.Equal(RenamerFileKind.Video, batch!.Kind);
        var only = Assert.Single(batch.Entries);
        // The entry's EntityId is 9 (the parent entity), NOT its fileId (90).
        Assert.Equal(9, only.EntityId);
        Assert.Equal(90, only.FileId);
        Assert.NotEqual(only.EntityId, only.FileId);
    }

    [Fact]
    public async Task TwoEntityBatch_RoundTrips_BothDistinctEntityIds()
    {
        var store = new FakeStore();
        var log = NewLog(store);

        await log.BeginBatchAsync("R1", RenamerFileKind.Video);
        await log.AppendAsync(entityId: 7, fileId: 70, oldPath: "media/a.mkv", newPath: "media/A.mkv");
        await log.AppendAsync(entityId: 8, fileId: 80, oldPath: "media/b.mkv", newPath: "media/B.mkv");

        var batch = await log.ReadLastOpenBatchAsync();
        Assert.NotNull(batch);
        Assert.Equal(2, batch!.Entries.Count);

        // Each entry's EntityId is distinct from its fileId; both entity ids present.
        var entityIds = batch.Entries.Select(e => e.EntityId).ToHashSet();
        Assert.Contains(7, entityIds);
        Assert.Contains(8, entityIds);
        foreach (var e in batch.Entries)
        {
            Assert.NotEqual(e.EntityId, e.FileId);
        }
    }

    [Fact]
    public async Task ReadLastOpenBatch_ReturnsRowsNewestFirst()
    {
        var store = new FakeStore();
        var log = NewLog(store);

        await log.BeginBatchAsync("R1", RenamerFileKind.Video);
        await log.AppendAsync(entityId: 1, fileId: 11, oldPath: "x/1.mkv", newPath: "x/A.mkv");
        await log.AppendAsync(entityId: 2, fileId: 22, oldPath: "x/2.mkv", newPath: "x/B.mkv");
        await log.AppendAsync(entityId: 3, fileId: 33, oldPath: "x/3.mkv", newPath: "x/C.mkv");

        var batch = await log.ReadLastOpenBatchAsync();
        Assert.NotNull(batch);
        // Newest-first (reverse append order): last appended (entity 3) is first.
        Assert.Equal(new[] { 3, 2, 1 }, batch!.Entries.Select(e => e.EntityId).ToArray());
    }

    [Fact]
    public async Task FlatLegacyBlob_ParsesAsOneVideoBatch_EntityIdEqualsFileId()
    {
        var store = new FakeStore();
        // An old flat blob: id|old|new rows, NO #batch headers.
        await store.SetAsync(RevertLog.Key,
            "70|media/a.mkv|media/A.mkv\n80|media/b.mkv|media/B.mkv");
        var log = NewLog(store);

        var batch = await log.ReadLastOpenBatchAsync();
        Assert.NotNull(batch);
        Assert.Equal(RenamerFileKind.Video, batch!.Kind);
        Assert.Equal(2, batch.Entries.Count);
        // Best-effort legacy fallback: each entry's EntityId == its FileId.
        foreach (var e in batch.Entries)
        {
            Assert.Equal(e.FileId, e.EntityId);
        }
        // Still newest-first.
        Assert.Equal(80, batch.Entries[0].FileId);
        Assert.Equal(70, batch.Entries[1].FileId);
    }

    [Fact]
    public async Task ReadLastBatchSummary_ReturnsShape_NullWhenEmpty()
    {
        var store = new FakeStore();
        var log = NewLog(store);

        // Empty blob → null summary.
        Assert.Null(await log.ReadLastBatchSummaryAsync());

        await log.BeginBatchAsync("R1", RenamerFileKind.Video);
        await log.AppendAsync(entityId: 7, fileId: 70, oldPath: "a", newPath: "A");
        await log.BeginBatchAsync("R2", RenamerFileKind.Video);
        await log.AppendAsync(entityId: 8, fileId: 80, oldPath: "b", newPath: "B");
        await log.AppendAsync(entityId: 9, fileId: 90, oldPath: "c", newPath: "C");

        var summary = await log.ReadLastBatchSummaryAsync();
        Assert.NotNull(summary);
        Assert.Equal("R2", summary!.Value.RunId);
        Assert.Equal(2, summary.Value.Count);          // R2's data-row count
        Assert.True(summary.Value.WrittenAtUtcTicks > 0);
        Assert.False(summary.Value.Consumed);
    }

    [Fact]
    public async Task MarkLastBatchConsumed_NextReadSkipsConsumed_FlipsSummary()
    {
        var store = new FakeStore();
        var log = NewLog(store);

        await log.BeginBatchAsync("R1", RenamerFileKind.Video);
        await log.AppendAsync(entityId: 7, fileId: 70, oldPath: "a", newPath: "A");
        await log.BeginBatchAsync("R2", RenamerFileKind.Video);
        await log.AppendAsync(entityId: 9, fileId: 90, oldPath: "c", newPath: "C");

        await log.MarkLastBatchConsumedAsync("R2");

        // The last OPEN batch is now R1 (R2 was consumed).
        var batch = await log.ReadLastOpenBatchAsync();
        Assert.NotNull(batch);
        var only = Assert.Single(batch!.Entries);
        Assert.Equal(7, only.EntityId);

        // The summary (most recent batch = R2) shows Consumed=true.
        var summary = await log.ReadLastBatchSummaryAsync();
        Assert.NotNull(summary);
        Assert.Equal("R2", summary!.Value.RunId);
        Assert.True(summary.Value.Consumed);

        // Consuming R1 too → no open batch left → null (second undo is a no-op).
        await log.MarkLastBatchConsumedAsync("R1");
        Assert.Null(await log.ReadLastOpenBatchAsync());
    }

    [Fact]
    public async Task MalformedLines_AreTolerated_NeverThrow()
    {
        var store = new FakeStore();
        // A blob with a short header (missing fields), a short data row, and a non-int row mixed with
        // one valid header + one valid row. Nothing should throw; the valid row should still parse.
        await store.SetAsync(RevertLog.Key, string.Join("\n",
            "#batch|RBAD",                          // header with too few fields → skipped
            "#batch|R1|123456789|Video|open",       // valid open header
            "notanint|x|y|z",                        // non-int entityId → skipped
            "7|short",                               // too few fields → skipped
            "7|70|media/a.mkv|media/A.mkv"));        // valid row

        var log = NewLog(store);

        var batch = await log.ReadLastOpenBatchAsync();   // must not throw
        Assert.NotNull(batch);
        var only = Assert.Single(batch!.Entries);
        Assert.Equal(7, only.EntityId);
        Assert.Equal(70, only.FileId);
    }
}
