using System.Collections.Concurrent;
using Renamer.Planner;

namespace Renamer.Tests.TestSupport;

/// <summary>
/// In-memory <see cref="IRevertJournal"/> for tests that exercise a rename or an undo without a
/// database: this is the seam faked so the executor and replayer are testable with no CoveContext.
/// </summary>
/// <remarks>
/// The collections are concurrent because one journal instance is shared by every parallel worker of
/// a run, so a plain list here would tear exactly where the real thing is exercised hardest.
/// </remarks>
public sealed class FakeRevertJournal : IRevertJournal
{
    private readonly ConcurrentDictionary<string, Batch> _batches = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<RevertRow> _appended = new();
    private readonly ConcurrentDictionary<(string RunId, long Seq), bool> _retired = new();
    private readonly ConcurrentQueue<DateTime> _purgeCalls = new();
    private long _lastSeq;

    /// <summary>Every appended row, in append order, whether or not it has since been retired.</summary>
    public IReadOnlyList<RevertRow> Rows => [.. _appended];

    /// <summary>The rows still awaiting restore — what a real journal would still be holding.</summary>
    public IReadOnlyList<RevertRow> PendingRows =>
        [.. _appended.Where(r => !_retired.ContainsKey((r.RunId, r.Seq)))];

    /// <summary>Each <see cref="PurgeExpiredAsync"/> call's timestamp, in order.</summary>
    public IReadOnlyList<DateTime> PurgeCalls => [.. _purgeCalls];

    public Task BeginBatchAsync(
        string runId, RenamerFileKind kind, DateTime nowUtc, CancellationToken ct = default)
    {
        Interlocked.Exchange(ref _lastSeq, 0);
        _batches[runId] = new Batch(kind, nowUtc.Ticks);
        return Task.CompletedTask;
    }

    public Task AppendAsync(RevertRow row, CancellationToken ct = default)
    {
        _appended.Enqueue(row with { Seq = Interlocked.Increment(ref _lastSeq) });
        if (_batches.TryGetValue(row.RunId, out var batch))
        {
            batch.CountAppended();
        }

        return Task.CompletedTask;
    }

    public Task<RevertBatchSummary?> ReadLastBatchSummaryAsync(CancellationToken ct = default)
    {
        var newest = Newest(_batches.Keys);
        return Task.FromResult(newest is null ? null : (RevertBatchSummary?)Summarize(newest));
    }

    public Task<RevertBatch?> ReadLastOpenBatchAsync(CancellationToken ct = default)
    {
        var pending = PendingRows;
        var newest = Newest(pending.Select(r => r.RunId).Distinct(StringComparer.Ordinal));
        if (newest is null)
        {
            return Task.FromResult<RevertBatch?>(null);
        }

        return Task.FromResult<RevertBatch?>(new RevertBatch(
            newest,
            _batches[newest].Kind,
            [.. pending.Where(r => r.RunId == newest).OrderByDescending(r => r.Seq)]));
    }

    public Task DeleteRowAsync(
        string runId, long seq, bool unrestorable, CancellationToken ct = default)
    {
        // Retiring a row that is already gone does nothing, so a retried undo is safe here too.
        if (_retired.TryAdd((runId, seq), true) && _batches.TryGetValue(runId, out var batch))
        {
            batch.CountRetired(unrestorable);
        }

        return Task.CompletedTask;
    }

    // Recorded rather than thrown: a fake exists to be called, and a caller that reaches the purge is
    // exactly what a test of the retention window needs to assert on.
    public Task PurgeExpiredAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        _purgeCalls.Enqueue(nowUtc);
        return Task.CompletedTask;
    }

    private RevertBatchSummary Summarize(string runId)
    {
        var batch = _batches[runId];
        return new RevertBatchSummary(
            runId, batch.OpenedAtUtcTicks, batch.Original, batch.Restored, batch.Unrestorable);
    }

    private string? Newest(IEnumerable<string> runIds) =>
        runIds
            .Where(_batches.ContainsKey)
            .OrderByDescending(id => _batches[id].OpenedAtUtcTicks)
            .ThenByDescending(id => id, StringComparer.Ordinal)
            .FirstOrDefault();

    private sealed class Batch(RenamerFileKind kind, long openedAtUtcTicks)
    {
        public RenamerFileKind Kind { get; } = kind;

        public long OpenedAtUtcTicks { get; } = openedAtUtcTicks;

        public int Original => _original;

        public int Restored => _restored;

        public int Unrestorable => _unrestorable;

        private int _original;
        private int _restored;
        private int _unrestorable;

        public void CountAppended() => Interlocked.Increment(ref _original);

        public void CountRetired(bool unrestorable)
        {
            if (unrestorable)
            {
                Interlocked.Increment(ref _unrestorable);
            }
            else
            {
                Interlocked.Increment(ref _restored);
            }
        }
    }
}
