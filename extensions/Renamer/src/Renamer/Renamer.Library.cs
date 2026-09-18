using System.Text.Json;
using Cove.Extensions.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Renamer.Execution;
using Renamer.Options;
using Renamer.Planner;
using static Renamer.Contracts.PreviewContracts;

namespace Renamer;

/// <summary>
/// Whole-library work: the dry-run scan that plans every entity and stores a bounded aggregate, and
/// the rename run that walks every kind. Both are job bodies - the endpoints that enqueue them live
/// in <c>Renamer.Api.cs</c>, and neither returns rows, because nothing here may grow with the
/// library.
/// </summary>
public sealed partial class Renamer
{
    /// <summary>
    /// The whole-library scan job body: resolves the options, opens a scope for a live data port, and
    /// runs <see cref="RunScanCoreAsync"/> over it.
    /// </summary>
    /// <param name="readableKinds">
    /// The kinds the enqueuing principal held read permission for, captured at enqueue time — the job
    /// runs detached from the original request, so this is the only way the per-kind skip (Pitfall 2:
    /// a partial-permission caller's scan must omit a kind they cannot read, never 403 the whole job)
    /// reaches the job body.
    /// </param>
    /// <param name="overrideOptions">
    /// The caller's current options for a dry run on unsaved edits, or null to scan the saved options.
    /// Captured at enqueue time (the detached job cannot re-read the request body).
    /// </param>
    /// <param name="progress">The job-progress sink reported a final <c>1.0</c> on completion.</param>
    /// <param name="ct">Cancellation token; a genuine cancellation aborts the scan.</param>
    internal async Task RunScanLibraryJobAsync(
        IReadOnlyList<RenamerFileKind> readableKinds, RenamerOptions? overrideOptions,
        Cove.Plugins.IJobProgress progress, CancellationToken ct)
    {
        // A dry run previews the caller's CURRENT (possibly unsaved) options when they were sent;
        // otherwise it scans the saved options — the original behavior.
        var options = overrideOptions ?? await new OptionsStore(Store, _log).LoadAsync(ct);

        // The widest of the elevated bodies, because RunScanCoreAsync takes a PORT rather than a service
        // provider - deliberately, so its boundedness is provable over a fake - and so there is no
        // narrower seam here to wrap. Per-kind authorization still reaches the detached job through
        // readableKinds, captured at enqueue time.
        await RunAsSystem.RunInSystemScopeAsync(ScopeFactory, services =>
        {
            var db = services.GetRequiredService<DbContext>();
            return RunScanCoreAsync(new CoveRenamerDataPort(db, _coveConfig), readableKinds, options, progress, ct);
        });
    }

    /// <summary>
    /// The scan itself: for each readable kind, plans every entity through the SAME planner
    /// <see cref="PreviewAsync"/> uses and folds each plan into a <see cref="ScanAggregator"/>, then
    /// persists that bounded aggregate under <see cref="LastScanSummaryKey"/> in one write. ZERO
    /// disk/DB mutation — no <c>ApplyAndSaveAsync</c>, no <c>File.Move</c>.
    /// </summary>
    /// <remarks>
    /// Persists per-kind counts and blast radius, never the rows: a per-file collection here is
    /// O(library) in both the managed heap and the stored value, and one oversized stored value makes
    /// Cove's bulk extension-data read fail for every key this extension owns. The rows are served on
    /// demand instead, by the <c>/scan-rows</c> page query, through this same planner.
    /// <para>
    /// The port is a parameter rather than resolved here so the boundedness this method exists to
    /// guarantee can be proven over a fake port, with no live database.
    /// </para>
    /// </remarks>
    /// <param name="port">The read seam the scan plans through.</param>
    /// <param name="readableKinds">The kinds to scan, in the order they are given.</param>
    /// <param name="options">The options to plan with (saved or the caller's unsaved override).</param>
    /// <param name="progress">The job-progress sink reported a final <c>1.0</c> on completion.</param>
    /// <param name="ct">Cancellation token; a genuine cancellation aborts the scan.</param>
    internal async Task RunScanCoreAsync(
        IRenamerDataPort port, IReadOnlyList<RenamerFileKind> readableKinds, RenamerOptions options,
        Cove.Plugins.IJobProgress progress, CancellationToken ct)
    {
        // A kind turned off in the options is dropped before it is walked, not planned and reported as
        // a skip per row: a library-sized kind would otherwise fill the scan with rows whose only
        // content is that the kind is off. The planner still gates it, which is what a selection-based
        // rename of the same kind meets.
        var kinds = readableKinds.Where(options.IsKindEnabled).ToList();

        var lookups = BuildLookups(options);
        var planner = new RenamerPlanner(port);
        var aggregator = new ScanAggregator(options.FullPathMax);

        // A count per kind, taken before the walk, so each planned entity can advance the bar. A count
        // can drift from what the pages yield while the scan runs, which is why the fraction below is
        // capped short of 1.0.
        var countByKind = new List<(RenamerFileKind Kind, int Count)>(kinds.Count);
        foreach (var kind in kinds)
        {
            ct.ThrowIfCancellationRequested();
            countByKind.Add((kind, await port.CountEntitiesAsync(kind, ct)));
        }

        int total = countByKind.Sum(k => k.Count);
        LogScanStarted(total, countByKind.Count);

        // total can be 0 (an empty library / no readable kinds): guard the divisor and report 1.0 so the
        // UI completes instead of dividing by zero or hanging at 0%.
        if (total == 0)
        {
            await WriteScanSummaryAsync(aggregator, ct);
            LogScanDone(0, 0);
            progress.Report(1d, "Scan complete — nothing to scan.");
            return;
        }

        int done = 0;
        foreach (var (kind, _) in countByKind)
        {
            // Walked a page of ids at a time, keyed on the entity id, so no collection here grows with
            // the library. One heavy multi-Include query per entity would cost one sequential round-trip
            // per row, so each page is batch-loaded: the loaded entities and their file-size map are
            // released with each page, and neither the graphs nor the sizes are ever held for the whole
            // library. Each page's entities are re-ordered by the ascending id list, because the batch
            // load returns database order, which preserves the per-id order and the progress cadence.
            int afterId = 0;
            while (true)
            {
                var chunk = await port.LoadEntityIdPageAsync(kind, afterId, CoveRenamerDataPort.LoadChunkSize, ct);
                if (chunk.Count == 0)
                {
                    break;
                }

                afterId = chunk[^1];
                var loaded = await port.LoadEntitiesAsync(kind, chunk, ct);
                var byId = loaded.ToDictionary(e => e.EntityId);
                var sizeByFileId = loaded
                    .SelectMany(e => e.Files)
                    .ToDictionary(f => f.FileId, f => f.SizeBytes);

                foreach (var id in chunk)
                {
                    ct.ThrowIfCancellationRequested();

                    // A missing entry means the id vanished between the id-list query and the batch load
                    // — it contributes nothing, matching the old path where PlanAsync on a missing id
                    // yielded an empty plan.
                    if (byId.TryGetValue(id, out var entity))
                    {
                        var plan = await planner.PlanLoadedEntity(entity, options, lookups, ct);
                        aggregator.Fold(kind, plan, sizeByFileId);
                    }

                    done++;
                    LogScanItemPlanned(done, total, kind, id);
                    // Report the fraction planned with a live message, capped just under 1.0 — the final
                    // 1.0 is reserved for after the result is persisted, so the UI only reads "complete"
                    // once the scan result is actually available to fetch.
                    progress.Report(Math.Min((double)done / total, 0.99), $"Scanning library… {done}/{total}");
                }
            }
        }

        await WriteScanSummaryAsync(aggregator, ct);

        LogScanDone(aggregator.TotalFiles, total);
        progress.Report(1d, "Scan complete.");
    }

    private Task WriteScanSummaryAsync(ScanAggregator aggregator, CancellationToken ct)
        => Store.SetAsync(
            LastScanSummaryKey,
            JsonSerializer.Serialize(aggregator.ToSummary(DateTime.UtcNow.Ticks), PreviewResponseJsonOptions),
            ct);

    /// <summary>
    /// The whole-library renamer job body: for each kind the caller can write, walks the kind's entity
    /// ids a chunk at a time through <see cref="RunRenamerKindAsync"/>, which drives the same chunk
    /// <c>/renamer</c> drives for a single-kind selection. A kind with no entities is skipped entirely,
    /// so no empty batch header opens for it.
    /// </summary>
    /// <remarks>
    /// Every batch this run opens carries one operation id, which is what <c>/undo</c> acts on, so the
    /// whole run is one undoable action however many kinds and chunks it spanned. Ids never reach the
    /// host's parameter map on this path: the job is enqueued as a closure, so nothing here has to
    /// serialize a list that grows with the library.
    /// <para>
    /// The denominator is a count per kind, taken before the walk. A count can drift from what the pages
    /// yield, so the reported fraction stays below 1.0 and the run's own final report lands the bar.
    /// </para>
    /// </remarks>
    /// <param name="writableKinds">The kinds the enqueuing principal held write permission for, captured at enqueue time (same rationale as <see cref="RunScanLibraryJobAsync"/>'s <c>readableKinds</c> parameter).</param>
    /// <param name="progress">The job-progress sink; each kind reports into its own slice of it (see <see cref="KindSliceProgress"/>).</param>
    /// <param name="ct">Cancellation token; a genuine cancellation aborts the remaining kinds.</param>
    internal async Task RunRenamerLibraryJobAsync(
        IReadOnlyList<RenamerFileKind> writableKinds, Cove.Plugins.IJobProgress progress, CancellationToken ct)
    {
        // Read the options once, for the same reason the scan does: a kind turned off is dropped before
        // it is walked at all, rather than planned into a run of skips.
        var options = await new OptionsStore(Store, _log).LoadAsync(ct);

        var countByKind = new List<(RenamerFileKind Kind, int Count)>(writableKinds.Count);
        foreach (var kind in writableKinds.Where(options.IsKindEnabled))
        {
            ct.ThrowIfCancellationRequested();

            int count = await RunAsSystem.RunInSystemScopeAsync(
                ScopeFactory,
                services =>
                {
                    var db = services.GetRequiredService<DbContext>();
                    return new CoveRenamerDataPort(db, _coveConfig).CountEntitiesAsync(kind, ct);
                });

            if (count > 0)
            {
                countByKind.Add((kind, count));
            }
        }

        int total = countByKind.Sum(k => k.Count);
        int planned = 0;

        // One click, one operation, however many kinds it spans. Each kind still opens its own batches —
        // a journal row carries no kind — but the operation is what /undo acts on, so the whole run
        // comes back or none of it does. The journal cap is measured over that same operation, so a run
        // too large to journal drops all of itself rather than part of itself.
        var budget = new OperationJournalBudget(Guid.NewGuid().ToString("N"));

        foreach (var (kind, count) in countByKind)
        {
            ct.ThrowIfCancellationRequested();

            LogLibraryKind(kind, count);

            await RunRenamerKindAsync(
                kind, count, budget, options, new KindSliceProgress(progress, planned, count, total), ct);
            planned += count;
        }

        progress.Report(1d, "Library rename complete.");
    }

    /// <summary>
    /// Maps one kind's own <c>[0, 1]</c> batch progress onto that kind's share of a whole-library run.
    /// </summary>
    /// <remarks>
    /// The run's own final report owns <c>1.0</c>. A batch reports <c>1.0</c> on every exit it has, so
    /// the LAST kind's is dropped rather than landing the bar at 100% ahead of the run's own; an earlier
    /// kind's maps to its slice end and passes through with whatever message it carried.
    /// </remarks>
    /// <param name="inner">The whole-run sink.</param>
    /// <param name="offset">Entities already covered by earlier kinds.</param>
    /// <param name="share">This kind's entity count.</param>
    /// <param name="total">The run's summed entity count; never zero, since a kind is only run when it has ids.</param>
    private sealed class KindSliceProgress(
        Cove.Plugins.IJobProgress inner, int offset, int share, int total) : Cove.Plugins.IJobProgress
    {
        public void Report(double percent, string? message = null)
        {
            // Clamped because the slice arithmetic trusts its input: a batch reporting below 0 would
            // map under the offset this kind starts at, which is the backward step the slice exists to
            // prevent.
            double scaled = (offset + (Math.Clamp(percent, 0d, 1d) * share)) / total;
            if (scaled >= 1d)
            {
                return;
            }

            inner.Report(scaled, message);
        }
    }
}
