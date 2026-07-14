using Cove.Plugins;
using WhisparrSync.Client;
using WhisparrSync.Ingest;
using WhisparrSync.State;

namespace WhisparrSync.Reconcile;

/// <summary>
/// The IMPT-02 polling-reconcile backstop: it pages Whisparr history newest-first since a stored
/// <see cref="Checkpoint"/>, feeds each NEW import record through the SAME <see cref="IngestCoordinator"/>
/// the webhook uses, and advances the checkpoint so a re-run is incremental. It is the guarantee the
/// extension is never webhook-only — an On-Import the webhook dropped is still ingested here, exactly once.
/// </summary>
/// <remarks>
/// CROSS-CHANNEL DEDUP: before ingesting a record the job derives the SHARED
/// <see cref="EventLedger.ImportKey"/>(<c>downloadId</c>, importedPath) — the identical key the 03-01
/// webhook path derives — and <c>SeenAsync</c>-checks it, so a webhook-then-poll overlap of the same import
/// ingests once. The supplementary <c>hist:{record.id}</c> self-key only makes a re-poll of the same page a
/// cheap no-op; it is NEVER the cross-channel dedup.
///
/// Transport is injected as a page-fetch delegate (not a client) so the run body is unit-testable host-free
/// against a fabricated history envelope; the store-backed ledger/log/checkpoint are built from the one
/// <see cref="IExtensionStore"/> exactly as <c>WebhookReceiver</c> does.
/// </remarks>
internal sealed class ReconcileJob(
    IExtensionStore store,
    IngestCoordinator coordinator,
    Func<int, CancellationToken, Task<WhisparrResult<WhisparrHistoryPage>>> listHistoryPage)
{
    /// <summary>The <c>ExtensionJobDefinition</c> id the scheduler enqueues each tick.</summary>
    public const string JobId = "whisparr-reconcile";

    /// <summary>The history page size the scheduler's page-fetch delegate requests.</summary>
    public const int PageSize = 50;

    // The single named import eventType (RESEARCH A2 — Radarr-family; kept one const so the live-confirm is
    // a one-line edit). Only records of this type are ingested; other event rows still advance the checkpoint.
    private const string ImportEventType = "downloadFolderImported";

    // A hard page cap so a mis-sorted / unbounded history can never spin the job forever (T-03-10 DoS).
    private const int MaxPages = 200;

    /// <summary>
    /// Runs one reconcile pass. On the FIRST run (no seeded checkpoint) it seeds the high-water mark at the
    /// newest existing record and ingests nothing (the prior history is never retro-ingested). On every later
    /// run it pages newest-first, collects the records above the checkpoint, ingests the new import records
    /// through the coordinator (deduped cross-channel by the shared ledger key), and advances the checkpoint.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        var checkpoint = new Checkpoint(store);
        var current = await checkpoint.LoadAsync(ct);

        if (!current.Seeded)
        {
            await SeedAsync(checkpoint, ct);
            return;
        }

        var (newImports, highWater, completed) = await CollectNewRecordsAsync(current.LastRecordId, ct);

        // Ingest oldest-first: the pages arrive newest-first, so reverse to import in the order Whisparr did.
        newImports.Reverse();
        var ledger = new EventLedger(store);
        var log = new ImportLog(store);
        foreach (var record in newImports)
        {
            await IngestRecordAsync(record, ledger, log, ct);
        }

        // Advance the checkpoint ONLY when the pass fully traversed newest-first down to the prior checkpoint
        // (or exhausted the history). A mid-paging transport fault or the MaxPages cap leaves `completed=false`:
        // the pages that DID load are the newest, so their max id (highWater) sits ABOVE an un-fetched OLDER
        // window — advancing to it would push that window permanently below the checkpoint and silently drop
        // those import events (defeating the IMPT-02 "never webhook-only" backstop). Keeping the checkpoint
        // re-fetches the window next run; the already-ingested newest rows are deduped cheaply by the ledger.
        if (completed && highWater != current.LastRecordId)
        {
            await checkpoint.SaveAsync(current with { LastRecordId = highWater }, ct);
        }
    }

    // First run: read only the newest page to find the highest existing record id and seed the mark there,
    // so subsequent runs process only records that arrive AFTER now — the whole prior history is not ingested.
    private async Task SeedAsync(Checkpoint checkpoint, CancellationToken ct)
    {
        var first = await listHistoryPage(1, ct);
        if (!first.IsOk || first.Value is null)
        {
            // Could NOT read history (Whisparr unreachable/rejecting — a common boot-order state). Do NOT seed:
            // leave the checkpoint unseeded so the seed is DEFERRED to the next tick, not falsely completed at 0.
            // Seeding at 0 here would be indistinguishable from a legitimately-empty instance and would make the
            // next successful tick retro-ingest the ENTIRE prior library (CR-02). Seed nothing, ingest nothing.
            return;
        }

        // The genuine empty-instance case (a successful fetch with zero records) still seeds at 0 so the next
        // real import is caught; only a FETCH FAILURE is excluded from seeding.
        var newest = 0;
        if (first.Value.Records is { Length: > 0 } records)
        {
            foreach (var record in records)
            {
                if (record.Id > newest)
                {
                    newest = record.Id;
                }
            }
        }

        await checkpoint.SaveAsync(new CheckpointState(newest, DateTime.UtcNow.Ticks, Seeded: true), ct);
    }

    // Page newest-first, gathering every record above the checkpoint. Returns the new IMPORT records, the new
    // high-water mark (the max id seen this pass, regardless of eventType), and whether the pass COMPLETED a
    // clean traversal — true when it reached the prior checkpoint, saw a short (final) page, or exhausted the
    // history on a successful empty page; false on a transport fault or the MaxPages cap. Only a completed pass
    // may advance the checkpoint (see RunAsync) — an aborted pass would jump past an un-fetched older window.
    private async Task<(List<WhisparrHistoryRecord> Imports, int HighWater, bool Completed)> CollectNewRecordsAsync(
        int lastRecordId, CancellationToken ct)
    {
        var imports = new List<WhisparrHistoryRecord>();
        var highWater = lastRecordId;

        for (var page = 1; page <= MaxPages; page++)
        {
            var result = await listHistoryPage(page, ct);
            if (!result.IsOk)
            {
                // Transport fault mid-paging: the older records on the pages that never loaded are NOT covered.
                // Report NOT completed so RunAsync keeps the checkpoint and re-fetches the gap next run (CR-03).
                return (imports, highWater, Completed: false);
            }

            if (result.Value is not { Records: { Length: > 0 } records })
            {
                // A successful but empty page means there are no more history rows — a clean full traversal.
                return (imports, highWater, Completed: true);
            }

            var reachedCheckpoint = false;
            foreach (var record in records)
            {
                if (record.Id <= lastRecordId)
                {
                    reachedCheckpoint = true;
                    break;
                }

                if (record.Id > highWater)
                {
                    highWater = record.Id;
                }

                if (string.Equals(record.EventType, ImportEventType, StringComparison.OrdinalIgnoreCase))
                {
                    imports.Add(record);
                }
            }

            if (reachedCheckpoint || records.Length < PageSize)
            {
                return (imports, highWater, Completed: true); // reached the checkpoint or a short final page
            }
        }

        // Fell out of the loop by hitting the MaxPages cap (T-03-10 DoS guard) WITHOUT reaching the checkpoint:
        // an older window is still un-fetched, so this is NOT a clean traversal — do not advance past the gap.
        return (imports, highWater, Completed: false);
    }

    private async Task IngestRecordAsync(WhisparrHistoryRecord record, EventLedger ledger, ImportLog log, CancellationToken ct)
    {
        var path = ResolveImportedPath(record);
        if (string.IsNullOrWhiteSpace(path))
        {
            return; // an import record with no path is nothing to ingest and nothing to audit
        }

        // The SHARED cross-channel key (identical to the webhook's) is the real dedup; hist:{id} is only a
        // supplementary poll-self key so a re-poll of the same page is cheap.
        var key = EventLedger.ImportKey(ValueOf(record, "downloadId"), path);
        var histKey = $"hist:{record.Id}";
        if (await ledger.SeenAsync(histKey, ct))
        {
            return; // this exact history row was already processed by a prior poll — a cheap re-poll no-op
        }

        // Atomically CLAIM the shared key: this races the webhook path's TryClaimAsync in one gated critical
        // section, so a webhook-then-poll overlap of the same import has exactly one winner and ingests once
        // (IMPT-03). The loser lost the race — the webhook already claimed it — so it skips without a second entity.
        if (!await ledger.TryClaimAsync(key, ct))
        {
            return; // already ingested via the webhook or a prior poll — a duplicate no-op (IMPT-03)
        }

        IngestOutcome outcome;
        try
        {
            outcome = await coordinator.IngestAsync(path, existingId: null, ct);
        }
        catch
        {
            // An unexpected ingest fault must not permanently swallow the import: release the shared claim so a
            // later poll (or the webhook) re-processes it rather than skipping it forever (IMPT-02).
            await ledger.ReleaseAsync(key, ct);
            throw;
        }

        var result = outcome.Result == IngestResult.Imported ? "Imported" : "Flagged";
        await log.AppendAsync(
            new ImportLogEntry(
                DateTime.UtcNow.Ticks, "poll", record.EventType, path,
                outcome.Kind?.ToString(), outcome.CoveEntityId, result, outcome.Reason, key),
            ct);

        // The shared key is already recorded by the claim; record the poll-self key so a re-poll of the same
        // page short-circuits cheaply above.
        await ledger.RecordAsync(histKey, ct);
    }

    // The imported absolute path, preferring importedPath and falling back to droppedPath (the pre-import
    // location) — both are free-form entries in the history record's data map.
    private static string? ResolveImportedPath(WhisparrHistoryRecord record)
        => ValueOf(record, "importedPath") ?? ValueOf(record, "droppedPath");

    // Case-insensitive lookup over the history data map (Whisparr emits camelCase keys, but a dictionary key
    // is bound verbatim, so match tolerantly). Returns null for an absent/blank value.
    private static string? ValueOf(WhisparrHistoryRecord record, string key)
    {
        if (record.Data is not { } data)
        {
            return null;
        }

        foreach (var pair in data)
        {
            if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(pair.Value))
            {
                return pair.Value;
            }
        }

        return null;
    }
}
