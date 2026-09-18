using Cove.Core.Auth;
using Cove.Extensions.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Renamer.Contracts;
using Renamer.Execution;
using Renamer.Planner;
using static Cove.Extensions.Shared.MinimalApiPermissions;

namespace Renamer;

/// <summary>
/// Undo: replays the last recorded batch backwards, moving each file back to the path the journal
/// recorded for it. The endpoint that reaches this lives in <c>Renamer.Api.cs</c>; the replay itself
/// is <see cref="UndoReplayer"/>.
/// </summary>
public sealed partial class Renamer
{
    /// <summary>
    /// Reverse-replays the most recent rename OPERATION — every batch one user action opened.
    /// Enforces the write permission in-handler and returns 403 BEFORE any journal read / scope open /
    /// disk touch (the host's <c>[RequiresPermission]</c> filter is inert on minimal-API endpoints;
    /// mirrors <see cref="RenamerEnqueue"/>). Takes no body — it always targets "the operation with
    /// rows left".
    /// <para>
    /// Names its target through <see cref="IRevertJournal.ReadUndoTargetAsync"/> — the same read the
    /// panel's summary uses, so what is described and what is acted on cannot be different — then walks
    /// that operation's batches newest-first and reads each batch's rows A PAGE AT A TIME, replaying
    /// every page via <see cref="UndoReplayer"/> (kind from the batch, entityId from each row — there is
    /// NO hardcoded Video default on this path). Every row whose outcome is settled is RETIRED: one that
    /// was restored, and one that stopped for the single reason no retry can improve on. A row that
    /// stopped for any other reason stays in the journal, so a later <c>/undo</c> retries exactly the
    /// work that is still outstanding, and an operation with nothing left is not offered again. An empty
    /// operation is a clean <c>{undone:0}</c> no-op.
    /// </para>
    /// <para>
    /// The re-gate is over EVERY kind the operation renamed, and missing any one refuses all of it: a
    /// rename the user performed as one action has no coherent half-undone outcome to report.
    /// </para>
    /// <para>
    /// Paging bounds this handler's READ memory by the page size rather than by the operation. Each page
    /// is handed to the replayer wrapped in the same <see cref="RevertBatch"/> record it already
    /// consumed: the replayer's signature, its per-entity path cache and its whole restore spine are
    /// deliberately untouched here. That spine is the destructive path, and a storage change has no
    /// business rewriting it.
    /// </para>
    /// <para>
    /// What bounds the RESPONSE is <see cref="IRevertJournal.MaxJournalledFiles"/>, applied to each
    /// batch as it opens. The two error buckets accumulate across every page, and both cursors strictly
    /// decrease so each row reaches them at most once.
    /// </para>
    /// </summary>
    internal async Task<Results<Ok<UndoResult>, ForbiddenCode>> UndoAsync(
        ICurrentPrincipalAccessor principal, CancellationToken ct)
    {
        // 403 FIRST for a caller holding NO renamer-write permission of any kind — before any journal
        // read or disk touch, so an unauthorized caller cannot even learn whether a batch exists. The
        // SPECIFIC kind's write permission is re-checked below once the batch reveals the kind; this
        // coarse gate only preserves the "no read/disk work for the wholly-unauthorized" property.
        // Read through the shared helper, never a list spelled again here: a kind added to
        // AnyWritePermissions and not to a second copy locks that kind's own writers out of undo while
        // every other path accepts them.
        if (!HasAnyWritePermission(principal))
        {
            return new ForbiddenCode();
        }

        // Open a scope the SAME way RunRenamerBatchAsync does and resolve the scoped DbContext. It is
        // opened before the batch read because the journal now lives in the database rather than in the
        // extension store — a scope open is not a mutation, so the "an under-permissioned caller
        // changes nothing" property the per-kind re-gate below protects is unaffected.
        await using var scope = ScopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DbContext>();
        using var journal = new CoveRevertJournal(db);

        // Retention is enforced here as well as when a batch is opened, because opening one is not the
        // only way the window can be crossed: a library nobody renames for longer than the window keeps
        // its last batch replayable indefinitely, and the panel already refuses that batch on the same
        // constant. A restore the panel says has expired must not succeed for a caller reaching the API
        // directly.
        await journal.PurgeExpiredAsync(DateTime.UtcNow, ct);

        var target = await journal.ReadUndoTargetAsync(ct);
        if (target is null)
        {
            return TypedResults.Ok(new UndoRunAccumulator().ToResult());
        }

        string operationId = target.Value.OperationId;

        // The first batch is read BEFORE the per-kind re-gate, exactly where the whole-batch read used
        // to sit: a settled operation is the "nothing to undo" answer for every caller holding any
        // renamer write permission, not a 403 that would also disclose which kinds it renamed.
        var batch = await journal.ReadNextBatchAsync(
            operationId, IRevertJournal.FirstBatchTicks, IRevertJournal.FirstBatchRunId, ct);
        if (batch is null)
        {
            return TypedResults.Ok(new UndoRunAccumulator().ToResult());
        }

        // Re-gate on the WRITE permission of EVERY kind the operation renamed — undoing an image
        // renamer requires images.write, not videos.write, and one click over videos and images
        // requires both. Missing any one refuses the whole operation: undoing the half a caller holds
        // permission for would leave a rename the user performed as one action half reversed, with no
        // way to describe the result. Checked after the journal read (needed to learn the kinds) but
        // BEFORE any disk touch, so an under-permissioned caller still mutates nothing.
        foreach (var operationKind in await journal.ReadOperationKindsAsync(operationId, ct))
        {
            var (_, undoWritePermission) = PermissionsFor(operationKind);
            if (Forbidden(principal, undoWritePermission) is { } denied)
            {
                return denied;
            }
        }

        // No options load here: undo restores paths the journal recorded and renders no name, so it
        // reads nothing from the configuration. The load that used to sit here was dead work whose
        // result was discarded.
        var replayer = new UndoReplayer(new CoveRenamerDataPort(db, _coveConfig), EventBus, new DiskMover(),
            cross: new CrossVolumeMover());

        // Each page's outcome is folded into a total plus a bounded sample. Retaining every page's
        // entries would rebuild in this handler's memory - and then on the wire - exactly the
        // library-sized value the paged read above removed. The host log still receives every entry,
        // per page, which is where the full detail belongs.
        // ONE accumulator for the whole operation, not one per batch: the response is bounded by the
        // journal cap over the operation exactly as it was over a batch, and a per-batch accumulator
        // would report the last kind's outcome as the run's.
        var accumulated = new UndoRunAccumulator();

        // Newest batch of the operation first, then strictly older ones. A batch whose rows all failed
        // for a clearable reason still holds them, so the cursor — not their absence — is what moves
        // the loop on.
        while (batch is not null)
        {
            var current = batch.Value;
            var page = await journal.ReadBatchPageAsync(
                current.RunId, belowSeq: long.MaxValue, CoveRevertJournal.DefaultPageSize, ct);

            while (page.Count > 0)
            {
                var pageBatch = new RevertBatch(current.RunId, current.Kind, page);
                var run = await replayer.RevertAsync(pageBatch, ct);

                LogUndoEntries(current.RunId, pageBatch, run);

                accumulated.Add(run);

                // Retire each row whose file actually came back. A row that stopped for a reason the world
                // can clear STAYS, so it is offered again on the next undo — which is what makes a retry act
                // on exactly the work still outstanding after the cause is corrected (a folder that
                // didn't yet cover the original location, an offline source drive, a locked file).
                foreach (var row in run.Restored)
                {
                    await journal.DeleteRowAsync(row.RunId, row.Seq, unrestorable: false, ct);
                }

                // A row that can NEVER be restored is retired too, on the other counter. Leaving it would
                // keep the batch offering an undo that cannot complete, and the panel promising work that
                // will never happen; the aggregate still records that it ended unrestorable, and the reason
                // was already surfaced in this very response. The decision reads the TYPED stop reason — the
                // same fact as the note beside it, but as a value, so rewording a message for a human cannot
                // change which rows get deleted for good.
                foreach (var stopped in run.Failed.Concat(run.Skipped)
                    .Where(stopped => UndoTerminalClassifier.IsTerminal(stopped.Stop)))
                {
                    await journal.DeleteRowAsync(stopped.RunId, stopped.Seq, unrestorable: true, ct);
                }

                // The cursor is the LOWEST sequence this page returned, and the next page returns only rows
                // strictly below it — so the cursor strictly decreases and the loop terminates whatever the
                // outcomes were. A cursor that failed to advance would re-read the same page forever, which
                // is a hang rather than an error, so it is pinned by a test rather than left to review.
                page = await journal.ReadBatchPageAsync(
                    current.RunId, page[^1].Seq, CoveRevertJournal.DefaultPageSize, ct);
            }

            // The outer cursor is this batch's own (opened, run id), and the next read returns only a
            // batch strictly below it — so the cursor strictly decreases and the outer loop terminates
            // whatever the outcomes were, exactly as the row cursor does within a batch.
            batch = await journal.ReadNextBatchAsync(
                operationId, current.WrittenAtUtcTicks, current.RunId, ct);
        }

        // The log line reports the run's TOTALS, which are the accumulator's counts and never a sample's
        // length: a host log that under-reported a large undo would be worse than no line at all.
        var result = accumulated.ToResult();
        LogUndoDone(operationId, result.Undone, result.SkippedCount, result.FailedCount);

        return TypedResults.Ok(result);
    }

    /// <summary>
    /// Records the outcome of reverse-replaying ONE PAGE of a batch to the host log: a line per
    /// restored file (current → original) and one per skip/failure (with reason).
    /// </summary>
    /// <remarks>
    /// The run's closing summary line is deliberately NOT written here: an undo replays as many pages
    /// as the batch has, and a "done" line per page would report a fraction of the run as the whole of
    /// it. The caller writes it once, from the totals.
    /// <para>
    /// The restored rows are the ones the replayer reports, never a set derived by subtracting the
    /// problem buckets from the batch. A single batch can legitimately hold two rows for the same file
    /// (renamed twice within one run), so a derived set has to reconstruct each row's identity from its
    /// paths to bucket it once — and get that reconstruction exactly right. The replayer already knows
    /// which rows it restored.
    /// </para>
    /// </remarks>
    private void LogUndoEntries(string runId, RevertBatch batch, UndoReplayer.UndoRunResult run)
    {
        foreach (var entry in run.Restored)
        {
            LogUndoRestored(runId, batch.Kind, entry.EntityId, entry.OldPath);
        }

        foreach (var s in run.Skipped)
        {
            LogUndoSkipped(runId, s.FileId, s.Reason);
        }

        foreach (var f in run.Failed)
        {
            LogUndoFailed(runId, f.FileId, f.Reason);
        }

        // Entries that were restored anyway, minus a companion file. They are in no counted bucket, so
        // this loop is the only place a partial restore surfaces in the log.
        foreach (var w in run.Warnings)
        {
            LogUndoSidecarStranded(runId, w.FileId, w.Detail);
        }
    }
}
