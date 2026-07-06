using Renamer.Execution;
using Renamer.Planner;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Concurrency;

/// <summary>
/// Proves the RevertLog single-writer invariant under parallelism. The persisted blob is a
/// read-modify-write KV value (GetAsync → concat → SetAsync); without serialization, two appends
/// that read the same prior blob would each overwrite the other, dropping a row. Cove disables EF's
/// thread-safety checks, so a shared-state bug corrupts SILENTLY with no exception — these tests
/// therefore assert the PARSED blob invariant (exact row count + per-row well-formedness via
/// ReadLastOpenBatchAsync), never an exception. The store is a thread-safe ConcurrentFakeStore so
/// the proof isolates the RevertLog gate, not the store. Both tests would fail (count &lt; N, rows
/// dropped) if the per-store SemaphoreSlim serialization were removed.
/// </summary>
[Trait("Tier", "Integration")]
public sealed class RevertLogConcurrencyTests
{
    private const int N = 200;

    [Fact]
    public async Task InterleavedAppends_OneInstance_BlobHasExactlyNWellFormedRows()
    {
        var store = new ConcurrentFakeStore();
        var log = new RevertLog(store);

        await log.BeginBatchAsync("R-concurrent", RenamerFileKind.Video);

        // Fire N appends concurrently with a yield so the read-modify-write windows overlap and a
        // missing gate would reliably drop rows. Distinct entityId/fileId/paths per item.
        await Task.WhenAll(Enumerable.Range(0, N).Select(async i =>
        {
            await Task.Yield();
            await log.AppendAsync(
                entityId: 1000 + i,
                fileId: 5000 + i,
                oldPath: $"media/old-{i}.mkv",
                newPath: $"media/new-{i}.mkv");
        }));

        var batch = await log.ReadLastOpenBatchAsync();
        Assert.NotNull(batch);
        Assert.Equal(RenamerFileKind.Video, batch!.Kind);

        // Exactly N rows persisted; no torn/dropped/duplicated line.
        Assert.Equal(N, batch.Entries.Count);

        // Every expected fileId present exactly once, every row well-formed (entityId pairs with its
        // fileId, both non-default paths). This catches an interleaved/torn line that happened to
        // keep the count right but corrupt a field.
        var byFileId = batch.Entries.ToDictionary(e => e.FileId);
        Assert.Equal(N, byFileId.Count);
        for (int i = 0; i < N; i++)
        {
            Assert.True(byFileId.TryGetValue(5000 + i, out var e), $"missing fileId {5000 + i}");
            Assert.Equal(1000 + i, e.EntityId);
            Assert.Equal($"media/old-{i}.mkv", e.OldPath);
            Assert.Equal($"media/new-{i}.mkv", e.NewPath);
        }

        // The in-memory row list also did not race.
        Assert.Equal(N, log.Rows.Count);
    }

    [Fact]
    public async Task ConcurrentJobs_TwoInstances_SameStore_NoTornOrLostRow()
    {
        // Two RevertLog instances over the SAME store key model two concurrent batch JOBS sharing
        // the one blob. They must serialize against each other via the static per-store-key gate.
        var store = new ConcurrentFakeStore();
        var jobA = new RevertLog(store);
        var jobB = new RevertLog(store);

        const int perJob = 100;

        // One shared open-batch header, written once before the parallel appends.
        await jobA.BeginBatchAsync("R-shared", RenamerFileKind.Video);

        var appendsA = Enumerable.Range(0, perJob).Select(async i =>
        {
            await Task.Yield();
            await jobA.AppendAsync(entityId: 2000 + i, fileId: 6000 + i,
                oldPath: $"media/a-old-{i}.mkv", newPath: $"media/a-new-{i}.mkv");
        });
        var appendsB = Enumerable.Range(0, perJob).Select(async i =>
        {
            await Task.Yield();
            await jobB.AppendAsync(entityId: 3000 + i, fileId: 7000 + i,
                oldPath: $"media/b-old-{i}.mkv", newPath: $"media/b-new-{i}.mkv");
        });

        await Task.WhenAll(appendsA.Concat(appendsB));

        // The shared blob's open batch must contain ALL 200 rows from both instances, no torn/lost.
        var batch = await jobA.ReadLastOpenBatchAsync();
        Assert.NotNull(batch);
        Assert.Equal(2 * perJob, batch!.Entries.Count);

        var fileIds = batch.Entries.Select(e => e.FileId).ToHashSet();
        Assert.Equal(2 * perJob, fileIds.Count);
        for (int i = 0; i < perJob; i++)
        {
            Assert.Contains(6000 + i, fileIds); // job A
            Assert.Contains(7000 + i, fileIds); // job B
        }
    }
}
