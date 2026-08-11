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
/// One instance wraps one scope's context, exactly as <see cref="CoveRenamerDataPort"/> does.
/// </summary>
public sealed class CoveRevertJournal : IRevertJournal
{
    private readonly DbContext _db;

    // Minted here rather than by the database: an auto-numbering column would be provider-specific,
    // and the shipped schema has to run unchanged on every provider this extension is tested against.
    private long _lastSeq;

    public CoveRevertJournal(DbContext db) => _db = db;

    public async Task BeginBatchAsync(
        string runId, RenamerFileKind kind, DateTime nowUtc, CancellationToken ct = default)
    {
        Interlocked.Exchange(ref _lastSeq, 0);

        _db.Set<RevertBatchEntity>().Add(new RevertBatchEntity
        {
            RunId = runId,
            OpenedAtUtcTicks = nowUtc.Ticks,
            Kind = kind.ToString(),
        });

        await _db.SaveChangesAsync(ct);
    }

    public async Task AppendAsync(RevertRow row, CancellationToken ct = default)
    {
        _db.Set<RevertRowEntity>().Add(new RevertRowEntity
        {
            RunId = row.RunId,
            Seq = Interlocked.Increment(ref _lastSeq),
            EntityId = row.EntityId,
            FileId = row.FileId,
            OldPath = row.OldPath,
            SidecarsJson = row.SidecarsJson,
        });

        var batch = await FindBatchAsync(row.RunId, ct);
        if (batch is not null)
        {
            batch.OriginalCount++;
        }

        await _db.SaveChangesAsync(ct);
    }

    public async Task<RevertBatchSummary?> ReadLastBatchSummaryAsync(CancellationToken ct = default)
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

    public async Task<RevertBatch?> ReadLastOpenBatchAsync(CancellationToken ct = default)
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

    public async Task DeleteRowAsync(
        string runId, long seq, bool unrestorable, CancellationToken ct = default)
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

    public Task PurgeExpiredAsync(DateTime nowUtc, CancellationToken ct = default) =>
        throw new NotImplementedException(
            "The revert journal's retention purge is not built yet. It throws rather than returning "
                + "quietly because a purge that reports success while deleting nothing is "
                + "indistinguishable from one that works, and what it would hide is the journal "
                + "growing without a bound.");

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
