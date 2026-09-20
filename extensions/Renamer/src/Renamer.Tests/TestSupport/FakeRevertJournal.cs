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

    /// <summary>
    /// When set, <see cref="AppendAsync"/> throws this instead of recording the row — the seam that
    /// drives the executor's post-commit failure path, where the database save has already committed.
    /// </summary>
    public Exception? AppendThrow { get; set; }

    public Task BeginBatchAsync(
        string runId, string operationId, RenamerFileKind kind, DateTime nowUtc,
        CancellationToken ct = default)
    {
        Interlocked.Exchange(ref _lastSeq, 0);
        _batches[runId] = new Batch(operationId, kind, nowUtc.Ticks);
        return Task.CompletedTask;
    }

    public Task AppendAsync(RevertRow row, CancellationToken ct = default)
    {
        if (AppendThrow is not null)
        {
            throw AppendThrow;
        }

        _appended.Enqueue(row with { Seq = Interlocked.Increment(ref _lastSeq) });
        if (_batches.TryGetValue(row.RunId, out var batch))
        {
            batch.CountAppended();
        }

        return Task.CompletedTask;
    }

    // The real journal's semantics, including the fallback and the keyset cursors — a double that
    // answered an easier question would let a case pass here that the storage would fail.
    public Task<RevertOperationSummary?> ReadUndoTargetAsync(CancellationToken ct = default)
    {
        var replayable = NewestOperation(
            PendingRows.Select(r => EffectiveOperation(r.RunId)).Distinct(StringComparer.Ordinal));
        var target = replayable ?? NewestOperation(_batches.Keys.Select(EffectiveOperation).Distinct(StringComparer.Ordinal));
        if (target is null)
        {
            return Task.FromResult<RevertOperationSummary?>(null);
        }

        var batches = BatchesOf(target);
        return Task.FromResult<RevertOperationSummary?>(new RevertOperationSummary(
            target,
            batches.Min(b => _batches[b].OpenedAtUtcTicks),
            batches.Sum(b => _batches[b].Original),
            batches.Sum(b => _batches[b].Restored),
            batches.Sum(b => _batches[b].Unrestorable)));
    }

    public Task<RevertBatchSummary?> ReadNextBatchAsync(
        string operationId, long beforeOpenedAtTicks, string beforeRunId, CancellationToken ct = default)
    {
        var pending = PendingRows.Select(r => r.RunId).ToHashSet(StringComparer.Ordinal);

        var next = BatchesOf(operationId)
            .Where(pending.Contains)
            .Where(id => _batches[id].OpenedAtUtcTicks < beforeOpenedAtTicks
                || (_batches[id].OpenedAtUtcTicks == beforeOpenedAtTicks
                    && string.CompareOrdinal(id, beforeRunId) < 0))
            .OrderByDescending(id => _batches[id].OpenedAtUtcTicks)
            .ThenByDescending(id => id, StringComparer.Ordinal)
            .FirstOrDefault();

        return Task.FromResult(next is null ? null : (RevertBatchSummary?)Summarize(next));
    }

    public Task<IReadOnlyList<RenamerFileKind>> ReadOperationKindsAsync(
        string operationId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<RenamerFileKind>>(
            [.. BatchesOf(operationId).Select(id => _batches[id].Kind).Distinct()]);

    public Task<IReadOnlyList<RevertRow>> ReadBatchPageAsync(
        string runId, long belowSeq, int limit, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<RevertRow>>(
        [
            .. PendingRows
                .Where(r => r.RunId == runId && r.Seq < belowSeq)
                .OrderByDescending(r => r.Seq)
                .Take(limit),
        ]);

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
            runId, EffectiveOperation(runId), batch.Kind, batch.OpenedAtUtcTicks,
            batch.Original, batch.Restored, batch.Unrestorable);
    }

    // A batch with no operation of its own is an operation of one, exactly as the storage reads it.
    private string EffectiveOperation(string runId) =>
        _batches.TryGetValue(runId, out var batch) && batch.OperationId.Length > 0
            ? batch.OperationId
            : runId;

    private IReadOnlyList<string> BatchesOf(string operationId) =>
        [.. _batches.Keys.Where(id => EffectiveOperation(id) == operationId)];

    private string? NewestOperation(IEnumerable<string> operationIds) =>
        operationIds
            .Select(op => (Operation: op, Batches: BatchesOf(op)))
            .Where(o => o.Batches.Count > 0)
            .OrderByDescending(o => o.Batches.Max(id => _batches[id].OpenedAtUtcTicks))
            .ThenByDescending(o => o.Operation, StringComparer.Ordinal)
            .Select(o => o.Operation)
            .FirstOrDefault();

    private sealed class Batch(string operationId, RenamerFileKind kind, long openedAtUtcTicks)
    {
        public string OperationId { get; } = operationId;

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
