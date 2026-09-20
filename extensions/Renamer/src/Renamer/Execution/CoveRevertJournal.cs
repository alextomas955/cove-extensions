using Microsoft.EntityFrameworkCore;
using Renamer.Planner;

namespace Renamer.Execution;

// The revert journal over Cove's own database: the one class that reads and writes the
// extension-owned journal tables.
//
// The dependency is the base DbContext because Cove.Data, where CoveContext lives, is not referenced
// by this project. The host registers its context resolvable as DbContext, and the journal's entity
// types reach it through db.Set<T>().
//
// One instance wraps one scope's context and is shared by every parallel worker of a rename batch,
// because the sequence number that half-identifies a row is minted per instance. A DbContext is not
// thread-safe and Cove disables EF's thread-safety checks, so concurrent writes through one context
// corrupt silently instead of throwing. The write gate serializes them.
public sealed class CoveRevertJournal : IRevertJournal, IDisposable
{
    // A read granularity, not a ceiling on what an undo restores: the run pages until a page comes back
    // empty, so the whole batch comes back however many pages that takes.
    public const int DefaultPageSize = 500;

    // The age past which a batch, and every row it still holds, is dropped.
    //
    // It is what bounds how much the table accumulates: the auto-renamer opens a batch per metadata
    // edit, so the table grows with how much the library is edited. A batch is wholly inside the window
    // or wholly gone, never partly; a sweep that left half a batch would make a later undo quietly
    // partial.
    public static readonly TimeSpan RetentionWindow = TimeSpan.FromDays(7);

    private readonly DbContext _db;

    // The single-writer gate over this instance's context. Held across the whole mutation, so a
    // concurrent worker never observes or saves a half-built change set. Reads are gated too, because
    // they materialize through the same context.
    private readonly SemaphoreSlim _writes = new(1, 1);

    // Minted here because an auto-numbering column would be provider-specific, and the shipped schema
    // has to run unchanged on every provider this extension is tested against.
    private long _lastSeq;

    public CoveRevertJournal(DbContext db) => _db = db;

    public async Task BeginBatchAsync(
        string runId, string operationId, RenamerFileKind kind, DateTime nowUtc,
        CancellationToken ct = default)
    {
        // Retention runs here and nowhere else. Opening a batch is the only place a batch is created, so
        // it is the only place the window can be crossed by new work, and no timer or background service
        // is needed. Outside the gate because the purge takes it.
        await PurgeExpiredAsync(nowUtc, ct);

        await _writes.WaitAsync(ct);
        try
        {
            Interlocked.Exchange(ref _lastSeq, 0);

            var batch = new RevertBatchEntity
            {
                RunId = runId,
                OpenedAtUtcTicks = nowUtc.Ticks,
                Kind = kind.ToString(),
                OperationId = operationId,
            };
            _db.Set<RevertBatchEntity>().Add(batch);

            await _db.SaveChangesAsync(ct);
        }
        finally
        {
            _writes.Release();
        }
    }

    public async Task AppendAsync(RevertRow row, CancellationToken ct = default)
    {
        await _writes.WaitAsync(ct);
        try
        {
            var entity = new RevertRowEntity
            {
                RunId = row.RunId,
                Seq = Interlocked.Increment(ref _lastSeq),
                EntityId = row.EntityId,
                FileId = row.FileId,
                OldPath = row.OldPath,
                SidecarsJson = row.SidecarsJson,
            };
            _db.Set<RevertRowEntity>().Add(entity);

            var batch = await FindBatchAsync(row.RunId, ct);
            if (batch is not null)
            {
                batch.OriginalCount++;
            }

            await _db.SaveChangesAsync(ct);

            // One context lives for the whole batch, so a tracked saved row would make the change tracker
            // grow with the batch. Detaching keeps this instance's memory flat.
            _db.Entry(entity).State = EntityState.Detached;
        }
        finally
        {
            _writes.Release();
        }
    }

    public async Task<RevertOperationSummary?> ReadUndoTargetAsync(CancellationToken ct = default)
    {
        await _writes.WaitAsync(ct);
        try
        {
            var rows = _db.Set<RevertRowEntity>();

            // Having a row left is the first sort key, so an operation that can still be replayed
            // outranks a newer one that is settled. Ties fall back to the newest operation, which keeps
            // a fully-settled rename describable.
            var operation = await _db.Set<RevertBatchEntity>().AsNoTracking()
                .Select(b => new
                {
                    OperationId = b.OperationId == "" ? b.RunId : b.OperationId,
                    b.OpenedAtUtcTicks,
                    b.OriginalCount,
                    b.RestoredCount,
                    b.UnrestorableCount,
                    // Projected per batch and maxed below: the question is whether any of this
                    // operation's batches has a row left.
                    HasRows = rows.Any(r => r.RunId == b.RunId) ? 1 : 0,
                })
                .GroupBy(b => b.OperationId)
                .Select(g => new
                {
                    OperationId = g.Key,
                    // The minimum is when the user clicked, which the panel states; the maximum decides
                    // which operation is the most recent one.
                    OpenedAtUtcTicks = g.Min(b => b.OpenedAtUtcTicks),
                    NewestAtUtcTicks = g.Max(b => b.OpenedAtUtcTicks),
                    OriginalCount = g.Sum(b => b.OriginalCount),
                    RestoredCount = g.Sum(b => b.RestoredCount),
                    UnrestorableCount = g.Sum(b => b.UnrestorableCount),
                    HasRows = g.Max(b => b.HasRows),
                })
                .OrderByDescending(g => g.HasRows)
                .ThenByDescending(g => g.NewestAtUtcTicks)
                // Ties broken by operation id so the newest operation is one deterministic operation and
                // not whichever row the provider returned first.
                .ThenByDescending(g => g.OperationId)
                .FirstOrDefaultAsync(ct);

            return operation is null
                ? null
                : new RevertOperationSummary(
                    operation.OperationId,
                    operation.OpenedAtUtcTicks,
                    operation.OriginalCount,
                    operation.RestoredCount,
                    operation.UnrestorableCount);
        }
        finally
        {
            _writes.Release();
        }
    }

    public async Task<RevertBatchSummary?> ReadNextBatchAsync(
        string operationId, long beforeOpenedAtTicks, string beforeRunId, CancellationToken ct = default)
    {
        await _writes.WaitAsync(ct);
        try
        {
            var rows = _db.Set<RevertRowEntity>();

            var batch = await _db.Set<RevertBatchEntity>().AsNoTracking()
                .Where(b => (b.OperationId == "" ? b.RunId : b.OperationId) == operationId)
                // The keyset is strictly below the cursor on (opened, run id), so the caller's loop
                // advances even when a batch it just tried still holds every row it started with.
                .Where(b => b.OpenedAtUtcTicks < beforeOpenedAtTicks
                    || (b.OpenedAtUtcTicks == beforeOpenedAtTicks
                        && b.RunId.CompareTo(beforeRunId) < 0))
                .Where(b => rows.Any(r => r.RunId == b.RunId))
                .OrderByDescending(b => b.OpenedAtUtcTicks)
                .ThenByDescending(b => b.RunId)
                .FirstOrDefaultAsync(ct);

            return batch is null ? null : Summarize(batch);
        }
        finally
        {
            _writes.Release();
        }
    }

    public async Task<IReadOnlyList<RenamerFileKind>> ReadOperationKindsAsync(
        string operationId, CancellationToken ct = default)
    {
        await _writes.WaitAsync(ct);
        try
        {
            var stored = await _db.Set<RevertBatchEntity>().AsNoTracking()
                .Where(b => (b.OperationId == "" ? b.RunId : b.OperationId) == operationId)
                .Select(b => b.Kind)
                .Distinct()
                .ToListAsync(ct);

            return [.. stored.Select(ParseKind)];
        }
        finally
        {
            _writes.Release();
        }
    }

    public async Task<IReadOnlyList<RevertRow>> ReadBatchPageAsync(
        string runId, long belowSeq, int limit, CancellationToken ct = default)
    {
        await _writes.WaitAsync(ct);
        try
        {
            // The Take keeps this materialization bounded by the page and not by the library.
            var page = await _db.Set<RevertRowEntity>().AsNoTracking()
                .Where(r => r.RunId == runId && r.Seq < belowSeq)
                .OrderByDescending(r => r.Seq)
                .Take(limit)
                .ToListAsync(ct);

            return [.. page.Select(r => new RevertRow(r.RunId, r.Seq, r.EntityId, r.FileId, r.OldPath, r.SidecarsJson))];
        }
        finally
        {
            _writes.Release();
        }
    }

    public async Task DeleteRowAsync(
        string runId, long seq, bool unrestorable, CancellationToken ct = default)
    {
        await _writes.WaitAsync(ct);
        try
        {
            var row = await _db.Set<RevertRowEntity>()
                .FirstOrDefaultAsync(r => r.RunId == runId && r.Seq == seq, ct);

            if (row is null)
            {
                return;
            }

            _db.Set<RevertRowEntity>().Remove(row);

            var batch = await FindBatchAsync(runId, ct);
            if (batch is not null)
            {
                if (unrestorable)
                {
                    batch.UnrestorableCount++;
                }
                else
                {
                    batch.RestoredCount++;
                }
            }

            // The removal and the counter move commit together, so the aggregate can never describe a
            // batch that still holds the row it just counted as settled.
            await _db.SaveChangesAsync(ct);
        }
        finally
        {
            _writes.Release();
        }
    }

    public async Task PurgeExpiredAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        await _writes.WaitAsync(ct);
        try
        {
            long cutoff = (nowUtc - RetentionWindow).Ticks;

            // Keyed on the batch's own open timestamp and never on a row. There is no per-row age to
            // disagree with the batch's, so no sweep can leave half a batch behind and turn a later undo
            // silently partial.
            var expired = _db.Set<RevertBatchEntity>().Where(b => b.OpenedAtUtcTicks < cutoff);

            // The run ids are not materialized into an in list. The auto-renamer opens a batch per
            // metadata edit, so the number of expired batches is unbounded input, and a parameter per
            // batch or a delete per row would make the purge grow with the library.
            await _db.Set<RevertRowEntity>()
                .Where(r => expired.Any(b => b.RunId == r.RunId))
                .ExecuteDeleteAsync(ct);

            // Rows first, while their batch row is still there to correlate against.
            await expired.ExecuteDeleteAsync(ct);
        }
        finally
        {
            _writes.Release();
        }
    }

    // The context belongs to the scope, so only the write gate is released here.
    public void Dispose() => _writes.Dispose();

    private static RevertBatchSummary Summarize(RevertBatchEntity batch) =>
        new(batch.RunId,
            // A batch written before the operation id column existed is an operation of one.
            batch.OperationId.Length == 0 ? batch.RunId : batch.OperationId,
            ParseKind(batch.Kind),
            batch.OpenedAtUtcTicks,
            batch.OriginalCount,
            batch.RestoredCount,
            batch.UnrestorableCount);

    private Task<RevertBatchEntity?> FindBatchAsync(string runId, CancellationToken ct) =>
        _db.Set<RevertBatchEntity>().FirstOrDefaultAsync(b => b.RunId == runId, ct);

    // This column is written from a renamable kind by this extension alone, so a value that is not one
    // is corrupt state and not input to tolerate. Undo re-gates on the kind the batch names, so a
    // tolerant read that fell back to a default kind would undo a row under the wrong permission.
    private static RenamerFileKind ParseKind(string stored) =>
        Enum.TryParse<RenamerFileKind>(stored, out var kind) && RenamableKinds.Includes(kind)
            ? kind
            : throw new InvalidOperationException(
                $"revert batch names entity kind '{stored}', which this extension does not rename");
}
