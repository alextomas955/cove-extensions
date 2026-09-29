using System.Collections.Concurrent;
using Renamer.Execution;

namespace Renamer.Tests.TestSupport;

// In-memory IRevertJournal for executor and migration tests that need no database. It answers only
// the reads that bring one batch back: the undo-target ranking, the batch cursor and row retirement
// belong to CoveRevertJournal and are tested against it, so here they throw rather than agree with
// a copy. The collections are concurrent because one instance is shared by every parallel worker of
// a run.
public sealed class FakeRevertJournal : IRevertJournal
{
    private readonly ConcurrentQueue<RevertRow> _appended = new();
    private readonly ConcurrentQueue<(string RunId, string OperationId, RenamerFileKind Kind, long OpenedAtUtcTicks)> _batches = new();
    private long _lastSeq;

    // Every appended row, in append order.
    public IReadOnlyList<RevertRow> Rows => [.. _appended];

    // When set, AppendAsync throws this instead of recording the row - the seam that drives the
    // executor's post-commit failure path, where the database save has already committed.
    public Exception? AppendThrow { get; set; }

    public Task BeginBatchAsync(
        string runId, string operationId, RenamerFileKind kind, DateTime nowUtc,
        CancellationToken ct = default)
    {
        Interlocked.Exchange(ref _lastSeq, 0);
        _batches.Enqueue((runId, operationId.Length > 0 ? operationId : runId, kind, nowUtc.Ticks));
        return Task.CompletedTask;
    }

    public Task AppendAsync(RevertRow row, CancellationToken ct = default)
    {
        if (AppendThrow is not null)
        {
            throw AppendThrow;
        }

        _appended.Enqueue(row with { Seq = Interlocked.Increment(ref _lastSeq) });
        return Task.CompletedTask;
    }

    // The most recently opened batch's operation.
    public Task<RevertOperationSummary?> ReadUndoTargetAsync(CancellationToken ct = default)
    {
        var opened = _batches.ToArray();
        if (opened.Length == 0)
        {
            return Task.FromResult<RevertOperationSummary?>(null);
        }

        var batch = opened[^1];
        int rows = _appended.Count(r => r.RunId == batch.RunId);
        return Task.FromResult<RevertOperationSummary?>(
            new RevertOperationSummary(batch.OperationId, batch.OpenedAtUtcTicks, rows, 0, 0));
    }

    // The first page of the cursor only: the newest batch of operationId that holds a row.
    public Task<RevertBatchSummary?> ReadNextBatchAsync(
        string operationId, long beforeOpenedAtTicks, string beforeRunId, CancellationToken ct = default)
    {
        if (beforeOpenedAtTicks != IRevertJournal.FirstBatchTicks)
        {
            throw new NotSupportedException("the fake journal answers only an operation's first batch");
        }

        var batch = _batches.Reverse()
            .Where(b => b.OperationId == operationId && _appended.Any(r => r.RunId == b.RunId))
            .Select(b => (RevertBatchSummary?)new RevertBatchSummary(b.RunId, b.Kind, b.OpenedAtUtcTicks))
            .FirstOrDefault();
        return Task.FromResult(batch);
    }

    public Task<IReadOnlyList<RevertRow>> ReadBatchPageAsync(
        string runId, long belowSeq, int limit, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<RevertRow>>(
        [
            .. _appended
                .Where(r => r.RunId == runId && r.Seq < belowSeq)
                .OrderByDescending(r => r.Seq)
                .Take(limit),
        ]);

    public Task<IReadOnlyList<RenamerFileKind>> ReadOperationKindsAsync(
        string operationId, CancellationToken ct = default) => throw new NotSupportedException();

    public Task DeleteRowAsync(string runId, long seq, bool unrestorable, CancellationToken ct = default)
        => throw new NotSupportedException();

    public Task PurgeExpiredAsync(DateTime nowUtc, CancellationToken ct = default)
        => throw new NotSupportedException();
}
