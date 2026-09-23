using Cove.Core.Auth;
using Cove.Extensions.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Renamer.Contracts;
using Renamer.Execution;
using static Cove.Extensions.Shared.MinimalApiPermissions;

namespace Renamer;

/// <summary>
/// Undo: replays the last recorded rename operation backwards, moving each file back to the path the
/// journal recorded for it.
/// </summary>
public sealed partial class Renamer
{
    internal async Task<Results<Ok<UndoResult>, ForbiddenCode>> UndoAsync(
        ICurrentPrincipalAccessor principal, IAuthorizationService authz, CancellationToken ct)
    {
        // Refuse a caller holding no write permission before any journal read or disk touch, so an
        // unauthorized caller cannot learn whether a batch exists. The per-kind check below needs the
        // batch's kinds, so it cannot be this gate.
        if (!HasAnyWritePermission(principal))
        {
            return new ForbiddenCode();
        }

        await using var scope = ScopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DbContext>();
        await using var journal = new CoveRevertJournal(db);

        // A library nobody renames keeps its last batch replayable past the retention window, and the
        // panel already refuses that batch on the same constant. A restore the panel calls expired
        // must not succeed for a caller reaching the API directly.
        await journal.PurgeExpiredAsync(DateTime.UtcNow, ct);

        var target = await journal.ReadUndoTargetAsync(ct);
        if (target is null)
        {
            return TypedResults.Ok(new UndoRunAccumulator().ToResult());
        }

        string operationId = target.Value.OperationId;

        // Read before the per-kind gate: a settled operation answers "nothing to undo" for any caller
        // holding any kind's write permission, and a 403 here would disclose which kinds it renamed.
        var batch = await journal.ReadNextBatchAsync(
            operationId, IRevertJournal.FirstBatchTicks, IRevertJournal.FirstBatchRunId, ct);
        if (batch is null)
        {
            return TypedResults.Ok(new UndoRunAccumulator().ToResult());
        }

        // Every kind the operation renamed, all or nothing: undoing only the kinds a caller holds
        // permission for leaves one user action half reversed, with no coherent outcome to report.
        // Still before any disk touch, so an under-permissioned caller mutates nothing.
        foreach (var operationKind in await journal.ReadOperationKindsAsync(operationId, ct))
        {
            var (_, undoWritePermission) = PermissionsFor(operationKind);
            if (Forbidden(principal, undoWritePermission) is { } denied)
            {
                return denied;
            }
        }

        // Holding a kind's write permission is not access to every entity of that kind, and the read
        // scope a journal walk satisfies is evaluated apart from the write scope, so a caller can read
        // a recorded row and still be denied the file it would move back. Every entity the operation
        // would restore is authorized here, before the replayer exists, so a refusal has moved nothing.
        // One denied entity refuses the whole undo: restoring the rest leaves one user action half
        // reversed. The refusal names nothing, so it discloses no id.
        var authorizing = batch;
        while (authorizing is not null)
        {
            var current = authorizing.Value;
            var (_, entityWritePermission) = PermissionsFor(current.Kind);

            var page = await journal.ReadBatchPageAsync(
                current.RunId, belowSeq: long.MaxValue, CoveRevertJournal.DefaultPageSize, ct);

            while (page.Count > 0)
            {
                // One page's ids and no more. A distinct set over the whole operation would grow with
                // the library, and re-asking about an id a page repeats costs less than holding one.
                var pageIds = new List<int>(page.Count);
                foreach (var row in page)
                {
                    pageIds.Add(row.EntityId);
                }

                var allowed = await EntityAccessGuard.AllowedOnlyAsync(
                    authz, principal.Current, current.Kind, entityWritePermission, pageIds, ct);
                if (allowed.Count != pageIds.Count)
                {
                    return new ForbiddenCode();
                }

                page = await journal.ReadBatchPageAsync(
                    current.RunId, page[^1].Seq, CoveRevertJournal.DefaultPageSize, ct);
            }

            // Its own cursor, so the replay below still starts at the batch already read.
            authorizing = await journal.ReadNextBatchAsync(
                operationId, current.WrittenAtUtcTicks, current.RunId, ct);
        }

        // Undo restores the paths the journal recorded and renders no name, so it loads no options.
        var replayer = new UndoReplayer(new CoveRenamerDataPort(db, _coveConfig), EventBus);

        // One accumulator for the whole operation: a per-batch one would report the last kind's
        // outcome as the run's. Pages fold into totals plus a bounded sample, because retaining every
        // page's entries rebuilds the library-sized value the paged read exists to avoid. The host log
        // receives the full detail per page.
        var accumulated = new UndoRunAccumulator();

        // Newest batch of the operation first, then strictly older ones. A batch whose rows all failed
        // for a clearable reason still holds them, so the cursor moves the loop on, not their absence.
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

                // A row whose file came back is retired. A row that stopped for a cause the world can
                // clear stays, so the next undo acts on exactly the work still outstanding once that
                // cause is corrected: an offline source drive, a locked file, a missing folder.
                foreach (var row in run.Restored)
                {
                    await journal.DeleteRowAsync(row.RunId, row.Seq, unrestorable: false, ct);
                }

                // A row that can never be restored is retired on the other counter; leaving it would
                // keep the panel promising work that will never happen. The decision reads the typed
                // stop reason, so rewording a human-facing message cannot change which rows are
                // deleted for good.
                foreach (var stopped in run.Failed.Concat(run.Skipped)
                    .Where(stopped => UndoTerminalClassifier.IsTerminal(stopped.Stop)))
                {
                    await journal.DeleteRowAsync(stopped.RunId, stopped.Seq, unrestorable: true, ct);
                }

                // The cursor is the lowest sequence this page returned and the next page returns only
                // rows strictly below it, so it decreases and the loop terminates whatever the outcomes
                // were.
                page = await journal.ReadBatchPageAsync(
                    current.RunId, page[^1].Seq, CoveRevertJournal.DefaultPageSize, ct);
            }

            // The outer cursor is this batch's (opened, run id) and decreases the same way.
            batch = await journal.ReadNextBatchAsync(
                operationId, current.WrittenAtUtcTicks, current.RunId, ct);
        }

        var result = accumulated.ToResult();
        LogUndoDone(operationId, result.Undone, result.SkippedCount, result.FailedCount);

        return TypedResults.Ok(result);
    }

    // Writes no closing summary line: an undo replays as many pages as the batch has, and a "done"
    // line per page would report a fraction of the run as the whole of it. The caller writes it once.
    private void LogUndoEntries(string runId, RevertBatch batch, UndoReplayer.UndoRunResult run)
    {
        // The rows the replayer reports as restored, never a set derived by subtracting the problem
        // buckets: one batch can hold two rows for the same file, renamed twice within a run.
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

        // Restored anyway, minus a companion file. These are in no counted bucket, so this is the only
        // place a partial restore surfaces.
        foreach (var w in run.Warnings)
        {
            LogUndoSidecarStranded(runId, w.FileId, w.Detail);
        }
    }
}
