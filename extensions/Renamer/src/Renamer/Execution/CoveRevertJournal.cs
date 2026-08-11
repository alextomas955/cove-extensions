using Microsoft.EntityFrameworkCore;
using Renamer.Planner;

namespace Renamer.Execution;

/// <summary>
/// The revert journal over Cove's own database: the one class that reads and writes the
/// extension-owned journal tables, implementing <see cref="IRevertJournal"/>.
///
/// WHY <see cref="DbContext"/> and not the concrete <c>CoveContext</c>: the production
/// <c>Renamer.csproj</c> references <c>Cove.Plugins</c>/<c>Cove.Sdk</c> (compile-time, runtime
/// excluded), which transitively expose EF Core — but NOT <c>Cove.Data</c>, where <c>CoveContext</c>
/// lives. The host registers its context resolvable as the base <see cref="DbContext"/>, and the
/// journal's own entity types reach it through <c>db.Set&lt;T&gt;()</c>, which is why this compiles
/// with no reference to the host's data assembly. Tests inject a SQLite-backed <c>CoveContext</c>
/// (which IS-A <see cref="DbContext"/>) directly.
///
/// One instance wraps one scope's context, exactly as <see cref="CoveRenamerDataPort"/> does — but
/// unlike that port, ONE instance is shared by every parallel worker of a rename batch, because the
/// sequence number that half-identifies a row is minted per instance. That sharing is what
/// <see cref="_writes"/> exists for: a <see cref="DbContext"/> is not thread-safe and Cove disables
/// EF's thread-safety checks, so concurrent writes through one context corrupt silently rather than
/// throwing. Serializing them costs nothing measurable next to the disk move each one follows.
/// </summary>
public sealed class CoveRevertJournal : IRevertJournal, IDisposable
{
    private readonly DbContext _db;

    // The single-writer gate over this instance's context. Held across the whole mutation — the Add,
    // the counter move and the save — so a concurrent worker never observes or saves a half-built
    // change set. Reads are gated too: they materialize through the same context.
    private readonly SemaphoreSlim _writes = new(1, 1);

    // Minted here rather than by the database: an auto-numbering column would be provider-specific,
    // and the shipped schema has to run unchanged on every provider this extension is tested against.
    private long _lastSeq;

    public CoveRevertJournal(DbContext db) => _db = db;

    public async Task BeginBatchAsync(
        string runId, RenamerFileKind kind, DateTime nowUtc, CancellationToken ct = default)
    {
        // Retention runs HERE and nowhere else. Opening a batch is the only place a batch is created,
        // so it is the only place the window can be crossed by new work — which is what lets the whole
        // model be a constant plus this one line, with no timer, scheduler or background service to own
        // a lifetime, fail silently, or need a setting. Outside the gate because the purge takes it.
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

            // Library size is unbounded input, so a whole-library rename appends unboundedly many rows
            // through this one long-lived context. Leaving each saved row tracked would make the change
            // tracker grow with the library; detaching keeps this instance's memory flat.
            _db.Entry(entity).State = EntityState.Detached;
        }
        finally
        {
            _writes.Release();
        }
    }

    public async Task<RevertBatchSummary?> ReadLastBatchSummaryAsync(CancellationToken ct = default)
    {
        await _writes.WaitAsync(ct);
        try
        {
            var batch = await NewestFirst(_db.Set<RevertBatchEntity>().AsNoTracking())
                .FirstOrDefaultAsync(ct);

            return batch is null
                ? null
                : new RevertBatchSummary(
                    batch.RunId,
                    batch.OpenedAtUtcTicks,
                    batch.OriginalCount,
                    batch.RestoredCount,
                    batch.UnrestorableCount);
        }
        finally
        {
            _writes.Release();
        }
    }

    public async Task<RevertBatch?> ReadLastOpenBatchAsync(CancellationToken ct = default)
    {
        await _writes.WaitAsync(ct);
        try
        {
            var rows = _db.Set<RevertRowEntity>();

            var batch = await NewestFirst(_db.Set<RevertBatchEntity>().AsNoTracking())
                .Where(b => rows.Any(r => r.RunId == b.RunId))
                .FirstOrDefaultAsync(ct);

            if (batch is null)
            {
                return null;
            }

            var pending = await rows.AsNoTracking()
                .Where(r => r.RunId == batch.RunId)
                .OrderByDescending(r => r.Seq)
                .ToListAsync(ct);

            return new RevertBatch(
                batch.RunId,
                ParseKind(batch.Kind),
                [.. pending.Select(r => new RevertRow(r.RunId, r.Seq, r.EntityId, r.FileId, r.OldPath, r.SidecarsJson))]);
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
            long cutoff = (nowUtc - JournalRetention.Window).Ticks;

            // Keyed on the BATCH's own open timestamp, never on a row. That single choice is what makes
            // "a batch expires whole" structural instead of a rule someone has to remember: there is no
            // per-row age to disagree with the batch's, so no sweep can leave half a batch behind and
            // turn a later undo silently partial.
            var expired = _db.Set<RevertBatchEntity>().Where(b => b.OpenedAtUtcTicks < cutoff);

            // TWO statements, whatever the batch holds. The run ids are deliberately NOT materialized
            // into an IN(...) list: the auto-renamer opens a batch per metadata edit, so the number of
            // expired batches is itself unbounded input, and a parameter per batch (or a delete per row)
            // would make the purge the O(library) failure retention exists to prevent.
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

    /// <summary>Releases the write gate. The context is the scope's to dispose, never this instance's.</summary>
    public void Dispose() => _writes.Dispose();

    private Task<RevertBatchEntity?> FindBatchAsync(string runId, CancellationToken ct) =>
        _db.Set<RevertBatchEntity>().FirstOrDefaultAsync(b => b.RunId == runId, ct);

    // Ties are broken by run id so "the newest batch" is one batch, deterministically, rather than
    // whichever row the provider happened to return first.
    private static IQueryable<RevertBatchEntity> NewestFirst(IQueryable<RevertBatchEntity> batches) =>
        batches.OrderByDescending(b => b.OpenedAtUtcTicks).ThenByDescending(b => b.RunId);

    // Tolerant, matching how the journal's stored lines have always been read: a kind that no longer
    // parses costs the batch its entity type, not the user's whole undo.
    private static RenamerFileKind ParseKind(string stored) =>
        Enum.TryParse<RenamerFileKind>(stored, out var kind) ? kind : RenamerFileKind.Video;
}
