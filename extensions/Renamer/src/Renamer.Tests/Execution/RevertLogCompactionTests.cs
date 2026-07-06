using Cove.Core.Events;
using Renamer.Execution;
using Renamer.Options;
using Renamer.Planner;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Execution;

/// <summary>
/// Pins the three compaction guarantees, one test per requirement. Undo only ever reads the LAST
/// still-open batch, so every consumed batch and everything before the last open header is a pure
/// audit trail the log may drop — that fact is what makes append cost and stored footprint bounded.
/// <list type="bullet">
/// <item>Test A (REVERTLOG-01): after many historical + consumed batches, the stored blob holds ONLY
/// the final live batch, so a single append writes the live tail — not the whole history.</item>
/// <item>Test B (REVERTLOG-02): the stored footprint stays flat (one batch) across a rename/undo cycle
/// repeated over the install's life instead of growing with every batch ever written.</item>
/// <item>Test C (REVERTLOG-03): a real forward-rename's batch is still read back and reverse-replayed
/// by <see cref="UndoReplayer"/> AFTER compaction has dropped earlier, already-consumed batches —
/// compaction never drops or corrupts the still-replayable last open batch.</item>
/// </list>
/// Tests A/B are DB-free over <see cref="FakeStore"/>; Test C is the integration-tier UndoReplayer
/// round-trip spine over SQLite + a real <see cref="TempDir"/>. The private
/// <c>StatusOpen</c>/<c>StatusConsumed</c> constants are not visible, so the stored footprint is
/// measured by inspecting the raw blob (its length, its line/<c>#batch</c>-header count).
/// </summary>
public sealed class RevertLogCompactionTests
{
    private static RevertLog NewLog(FakeStore store) => new(store);

    private static int CountHeaders(string blob) =>
        blob.Split('\n').Count(l => l.StartsWith("#batch", StringComparison.Ordinal));

    [Fact]
    public async Task ManyHistoricalAndConsumedBatches_StoredBlobHoldsOnlyFinalLiveBatch()
    {
        var store = new FakeStore();
        var log = NewLog(store);

        // Open 50 batches in sequence. Each Begin makes the prior batch old (only the last open header
        // is reachable by undo); consume the completed ones too, so both drop-triggers are exercised.
        const int historical = 50;
        for (int n = 0; n < historical; n++)
        {
            var runId = $"R{n}";
            await log.BeginBatchAsync(runId, RenamerFileKind.Video);
            await log.AppendAsync(entityId: n, fileId: 1000 + n, oldPath: $"m/old-{n}.mkv", newPath: $"m/new-{n}.mkv");
            await log.MarkLastBatchConsumedAsync(runId);
        }

        // One final live batch with K rows.
        const int k = 3;
        await log.BeginBatchAsync("FINAL", RenamerFileKind.Video);
        for (int j = 0; j < k; j++)
        {
            await log.AppendAsync(entityId: 900 + j, fileId: 9000 + j, oldPath: $"m/f-{j}.mkv", newPath: $"m/F-{j}.mkv");
        }

        var blob = await store.GetAsync(RevertLog.Key);
        Assert.NotNull(blob);

        // Exactly ONE header remains (the final batch's), and exactly K data rows after it — the stored
        // value is a function of the live tail (K), not of the 50 historical batches.
        Assert.Equal(1, CountHeaders(blob!));
        var lines = blob!.Split('\n');
        Assert.Equal(1 + k, lines.Length);
        Assert.StartsWith("#batch|FINAL|", lines[0]);

        // No line from any earlier run survives.
        for (int n = 0; n < historical; n++)
        {
            Assert.DoesNotContain($"R{n}", blob, StringComparison.Ordinal);
            Assert.DoesNotContain($"old-{n}", blob, StringComparison.Ordinal);
        }

        // The last open batch reads back as exactly the K live rows.
        var batch = await log.ReadLastOpenBatchAsync();
        Assert.NotNull(batch);
        Assert.Equal(k, batch!.Entries.Count);
    }

    [Fact]
    public async Task RenameUndoCycle_KeepsFootprintBounded_NeverAccumulatesHistory()
    {
        var store = new FakeStore();
        var log = NewLog(store);

        // Model the real endpoint cycle over the install's life: a rename opens a batch, then /undo
        // consumes it, then the next rename opens the next batch. Consuming drops the dead batch (only
        // the just-consumed one is kept, for the panel), so the footprint never accumulates across the
        // N cycles — the monotonic growth the log used to have.
        const int cycles = 30;
        for (int n = 0; n < cycles; n++)
        {
            var runId = $"R{n}";
            await log.BeginBatchAsync(runId, RenamerFileKind.Video);
            await log.AppendAsync(entityId: n, fileId: 100 + n, oldPath: $"m/o-{n}.mkv", newPath: $"m/n-{n}.mkv");
            await log.AppendAsync(entityId: n, fileId: 200 + n, oldPath: $"m/p-{n}.mkv", newPath: $"m/q-{n}.mkv");
            await log.MarkLastBatchConsumedAsync(runId);

            var mid = (await store.GetAsync(RevertLog.Key))!;
            // After each consume the log holds exactly ONE batch (the just-consumed one, for the panel)
            // with its two data rows — never the accumulated history. Structural (one header + two rows),
            // constant at every cycle, so the footprint is flat across the install's life rather than
            // growing with the number of batches ever written.
            Assert.Equal(1, CountHeaders(mid));
            Assert.Equal(3, mid.Split('\n').Length);
        }

        // A subsequent Begin (a fresh rename) drops the trailing consumed batch — its panel role passes to
        // the new open header — so the footprint does not even carry the last consumed batch forward.
        await log.BeginBatchAsync("FINAL-OPEN", RenamerFileKind.Video);
        var afterBegin = (await store.GetAsync(RevertLog.Key))!;
        Assert.Equal(1, CountHeaders(afterBegin));
        Assert.StartsWith("#batch|FINAL-OPEN|", afterBegin);

        // The final open batch is undo-reachable; consuming it (with no earlier open batch behind it)
        // leaves at most the single consumed batch — never a growing trail.
        Assert.NotNull(await log.ReadLastOpenBatchAsync());
        await log.MarkLastBatchConsumedAsync("FINAL-OPEN");
        var final = await store.GetAsync(RevertLog.Key);
        Assert.True(string.IsNullOrEmpty(final) || CountHeaders(final!) <= 1);
        Assert.Null(await log.ReadLastOpenBatchAsync());
    }

    [Trait("Tier", "Integration")]
    [Fact]
    public async Task UndoRoundTrip_ReplaysLastOpenBatch_AfterEarlierBatchesCompactedAway()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string folderPath = dir.Root.Replace('\\', '/');
            // Offset the Video id sequence so videoId ≠ fileId — the published event must carry the
            // ENTITY id (the row's), never the file id.
            db.Set<Cove.Core.Entities.Video>().Add(new Cove.Core.Entities.Video { Title = "decoy", Organized = true });
            await db.SaveChangesAsync();
            var (_, videoId, fileId) =
                await ExecutorTestSeed.SeedVideoAsync(db, folderPath, "raw clip.mkv", "My Film");
            Assert.NotEqual(videoId, fileId);

            var port = new CoveRenamerDataPort(db);
            var revertLog = new RevertLog(new FakeStore());

            // Write and consume several EARLIER batches so compaction has already dropped them by the
            // time the real forward-rename opens its own batch.
            for (int n = 0; n < 5; n++)
            {
                var earlier = $"EARLIER-{n}";
                await revertLog.BeginBatchAsync(earlier, RenamerFileKind.Video);
                await revertLog.AppendAsync(entityId: n, fileId: 5000 + n, oldPath: $"m/x-{n}.mkv", newPath: $"m/X-{n}.mkv");
                await revertLog.MarkLastBatchConsumedAsync(earlier);
            }

            string oldFull = Path.Combine(dir.Root, "raw clip.mkv");
            File.WriteAllText(oldFull, "video-bytes");

            var options = new RenamerOptions { FilenameTemplate = "$title" }; // → "My Film.mkv"

            // Forward-rename a real file through the live planner + executor + RevertLog under a NEW open
            // batch. The Begin here compacts the earlier consumed batches away first.
            await revertLog.BeginBatchAsync("LIVE", RenamerFileKind.Video);
            var plan = await new RenamerPlanner(port).PlanAsync(RenamerFileKind.Video, videoId, options, default);
            var fwd = await new RenamerExecutor(port, new CapturingEventBus(), revertLog, new DiskMover())
                .ExecuteAsync(plan, options, default);
            Assert.Single(fwd.Renamed);

            string newFull = Path.Combine(dir.Root, "My Film.mkv");
            Assert.True(File.Exists(newFull));
            Assert.False(File.Exists(oldFull));

            // Reverse-replay the last open batch — it must survive the earlier compaction intact.
            var batch = await revertLog.ReadLastOpenBatchAsync();
            Assert.NotNull(batch);
            var undoBus = new CapturingEventBus();
            var result = await new UndoReplayer(port, undoBus, new DiskMover()).RevertAsync(batch!, default);

            Assert.Equal(1, result.Undone);
            Assert.Empty(result.Failed);
            Assert.Empty(result.Skipped);

            // Disk restored byte-for-byte.
            Assert.True(File.Exists(oldFull), "file restored to old path after compaction");
            Assert.False(File.Exists(newFull), "new path gone after undo");
            Assert.Equal("video-bytes", File.ReadAllText(oldFull));

            // DB restored.
            var (basename, path) = await ExecutorTestSeed.ReadFileAsync(db, fileId);
            Assert.Equal("raw clip.mkv", basename);
            Assert.Equal(folderPath + "/raw clip.mkv", path);

            // Exactly one event with the ROW's entity id (== videoId, ≠ fileId).
            var evt = Assert.IsType<EntityEvent>(Assert.Single(undoBus.Published));
            Assert.Equal(EventType.VideoUpdated, evt.Type);
            Assert.Equal(videoId, evt.EntityId);
            Assert.NotEqual(fileId, evt.EntityId);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }
}
