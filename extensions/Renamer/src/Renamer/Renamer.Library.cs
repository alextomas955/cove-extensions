using System.Text.Json;
using Cove.Core.Auth;
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
/// the rename run that walks every kind. Neither returns rows, because nothing here may grow with
/// the library.
/// </summary>
public sealed partial class Renamer
{
    // readableKinds is captured at enqueue time: the job runs detached from the request, so this is
    // the only way per-kind authorization reaches the body. A caller holding partial permission gets
    // a scan that omits the kinds they cannot read, not a 403 over the whole job. overrideOptions and
    // caller are captured for the same reason; the detached job cannot re-read the request body and
    // cannot re-resolve the principal.
    internal async Task RunScanLibraryJobAsync(
        CovePrincipal? caller, IReadOnlyList<RenamerFileKind> readableKinds, RenamerOptions? overrideOptions,
        Cove.Plugins.IJobProgress progress, CancellationToken ct)
    {
        var options = overrideOptions ?? await StoredOptions.LoadAsync(ct);

        // Held for the whole run, and deliberately not elevated: the decision reads the caller from
        // its argument, not from the ambient principal. The calls are sequential from the page loop,
        // so one instance serves the run.
        await using var authzScope = ScopeFactory.CreateAsyncScope();
        var authz = authzScope.ServiceProvider.GetRequiredService<IAuthorizationService>();
        AllowedIds allowedIds = (kind, ids, token) => EntityAccessGuard.AllowedOnlyAsync(
            authz, caller, kind, PermissionsFor(kind).Read, ids, token);

        // The whole body is elevated because RunScanCoreAsync takes a port, not a service provider,
        // so there is no narrower seam to wrap here.
        await RunAsSystem.RunInSystemScopeAsync(ScopeFactory, services =>
        {
            var db = services.GetRequiredService<DbContext>();
            return RunScanCoreAsync(
                new CoveRenamerDataPort(db, _coveConfig), readableKinds, options, allowedIds, progress, ct);
        });
    }

    /// <summary>
    /// Plans every entity of each readable kind through the planner <c>/preview</c> uses and persists
    /// one bounded aggregate. Mutates neither disk nor database.
    /// </summary>
    /// <remarks>
    /// Persists per-kind counts and blast radius, never the rows: a per-file collection is O(library)
    /// in both the heap and the stored value, and one oversized stored value makes Cove's bulk
    /// extension-data read fail for every key this extension owns. The rows are served on demand by
    /// the <c>/scan-rows</c> page query. The port is a parameter so that boundedness can be proven
    /// over a fake, with no live database.
    /// </remarks>
    internal async Task RunScanCoreAsync(
        IRenamerDataPort port, IReadOnlyList<RenamerFileKind> readableKinds, RenamerOptions options,
        AllowedIds allowedIds, Cove.Plugins.IJobProgress progress, CancellationToken ct)
    {
        // A kind turned off in the options is dropped before it is walked. Planning it would fill the
        // scan with library-many rows whose only content is that the kind is off.
        var kinds = readableKinds.Where(options.IsKindEnabled).ToList();

        var lookups = RouteLookups.From(options, LogInvalidRouteRegex);
        var planner = new RenamerPlanner(port);
        var aggregator = new ScanAggregator(options.FullPathMax);

        // A count per kind, taken before the walk, so each planned entity can advance the bar. The
        // count drifts from what the pages yield while the scan runs, which is why the fraction below
        // is capped short of 1.0.
        var countByKind = new List<(RenamerFileKind Kind, int Count)>(kinds.Count);
        foreach (var kind in kinds)
        {
            ct.ThrowIfCancellationRequested();
            countByKind.Add((kind, await port.CountEntitiesAsync(kind, ct)));
        }

        int total = countByKind.Sum(k => k.Count);
        LogScanStarted(total, countByKind.Count);

        // An empty library or no readable kinds: guard the divisor and report 1.0 so the panel
        // completes.
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
            // A page of ids at a time, keyed on the entity id, so no collection here grows with the
            // library. Each page is batch-loaded, and its entity graphs and file-size map are released
            // with the page. The batch load returns database order, so each page is re-ordered by the
            // ascending id list to hold the progress cadence.
            int afterId = 0;
            while (true)
            {
                var chunk = await port.LoadEntityIdPageAsync(kind, afterId, CoveRenamerDataPort.LoadChunkSize, ct);
                if (chunk.Count == 0)
                {
                    break;
                }

                afterId = chunk[^1];

                // The cursor is already past the whole database page, so a page the caller may read
                // none of moves the walk on rather than ending it.
                var readable = await allowedIds(kind, chunk, ct);
                if (readable.Count == 0)
                {
                    continue;
                }

                var loaded = await port.LoadEntitiesAsync(kind, readable, ct);
                var byId = loaded.ToDictionary(e => e.EntityId);
                var sizeByFileId = loaded
                    .SelectMany(e => e.Files)
                    .ToDictionary(f => f.FileId, f => f.SizeBytes);

                foreach (var id in readable)
                {
                    ct.ThrowIfCancellationRequested();

                    // A missing entry means the id vanished between the id-list query and the batch
                    // load. It contributes nothing.
                    if (byId.TryGetValue(id, out var entity))
                    {
                        var plan = await planner.PlanLoadedEntity(entity, options, lookups, ct);
                        aggregator.Fold(kind, plan, sizeByFileId);
                    }

                    done++;
                    LogScanItemPlanned(done, total, kind, id);

                    // 1.0 is reserved for after the result is persisted, so the panel reads "complete"
                    // only once the result can be fetched.
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
    /// Renames every entity of each writable kind, a chunk at a time, through the same chunk a
    /// single-kind selection drives.
    /// </summary>
    /// <remarks>
    /// Every batch the run opens carries one operation id, which is what <c>/undo</c> acts on, so the
    /// whole run is one undoable action however many kinds and chunks it spanned. The job is enqueued
    /// as a closure, so no id list that grows with the library reaches the host's parameter map. A
    /// kind with no entities is skipped, so no empty batch header opens for it.
    /// </remarks>
    internal async Task RunRenamerLibraryJobAsync(
        CovePrincipal? caller, IReadOnlyList<RenamerFileKind> writableKinds,
        Cove.Plugins.IJobProgress progress, CancellationToken ct, Func<string, long>? freeSpaceProbe = null)
    {
        var options = await StoredOptions.LoadAsync(ct);

        // Held for the whole run, and deliberately not elevated: the decision reads the caller from
        // its argument, not from the ambient principal. The calls are sequential from the page loop,
        // never from a rename worker, so one instance serves the run.
        await using var authzScope = ScopeFactory.CreateAsyncScope();
        var authz = authzScope.ServiceProvider.GetRequiredService<IAuthorizationService>();
        AllowedIds allowedIds = (kind, ids, token) => EntityAccessGuard.AllowedOnlyAsync(
            authz, caller, kind, PermissionsFor(kind).Write, ids, token);

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

        // One click, one operation, however many kinds it spans. Each kind opens its own batches,
        // since a journal row carries no kind, but the operation is what /undo acts on.
        var operationId = Guid.NewGuid().ToString("N");

        var refused = new List<RenamerFileKind>();
        foreach (var (kind, count) in countByKind)
        {
            ct.ThrowIfCancellationRequested();

            LogLibraryKind(kind, count);

            // A kind that ran out of room stops, and the walk moves to the next kind, which may sit on
            // another volume. The refusal is collected because the run's own final report is the only
            // one the host keeps: SliceProgress holds back a kind's closing 1.0, so a kind that refused
            // would otherwise reach the user as nothing at all.
            string? shortfall = await RunRenamerKindAsync(
                new RenameRun(kind, count, operationId, options, freeSpaceProbe), allowedIds,
                new SliceProgress(progress, planned, count, total, holdFinal: true), ct);
            if (shortfall is not null)
            {
                refused.Add(kind);
            }

            planned += count;
        }

        progress.Report(
            1d,
            refused.Count == 0
                ? "Library rename complete."
                : $"Stopped: insufficient free space for {string.Join(", ", refused)}. "
                    + "Files renamed before each stop stay renamed.");
    }
}
