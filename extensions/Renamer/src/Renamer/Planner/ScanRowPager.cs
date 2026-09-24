using System.Runtime.CompilerServices;

using Renamer.Contracts;
using Renamer.Options;

namespace Renamer.Planner;

// Serves one page of a whole-library dry run, planning only the entities that page needs.
//
// It pages through the port's ascending keyset of entity ids and plans each entity with
// RenamerPlanner.PlanLoadedEntity, the same method the scan job calls, so a slice equals the
// corresponding slice of a full plan because there is one planner. ScanPagingEquivalenceTests is what
// holds that.
//
// mountPoints resolves Unix volumes; omit for the real table. It is carried for the same reason
// ScanAggregator and BatchPreview.Summarize carry it: a page's in-flight overflow flag is a cross-volume
// decision, and on Unix volume identity comes from the runner's mount table, so a flagged row is
// otherwise not reproducible off the machine that produced it.
public sealed class ScanRowPager(
    RenamerPlanner planner, IRenamerDataPort port, IReadOnlyCollection<string>? mountPoints = null)
{
    // A page the UI virtualises comfortably: enough rows that scrolling rarely waits on a request, few
    // enough that one request stays a small response and a short planning burst.
    public const int DefaultTake = 100;

    // The response-size ceiling. Without it a caller-supplied take could ask for the whole library in one
    // body and re-create, in a response, exactly the oversized-payload failure this design removes.
    public const int MaxTake = 500;

    // The work ceiling per request, independent of how many rows survive the filters. A narrow filter can
    // walk many entities per matched row, so without this a single filtered request would plan the entire
    // library; with it the request stops and says so (ScanRowsPage.BudgetExhausted).
    public const int MaxEntitiesPerRequest = 500;

    // Reads the next page of rows. Only readableKinds are walked or cursored into. A null cursor starts
    // at the first readable kind. take is clamped to MaxTake, and a non-positive value falls back to
    // DefaultTake.
    public async Task<ScanRowsPage> PageAsync(
        IReadOnlyList<RenamerFileKind> readableKinds, ScanCursor? cursor, int take, ScanRowFilter filter,
        RenamerOptions options, RouteLookups lookups, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(readableKinds);

        int pageSize = take <= 0 ? DefaultTake : Math.Min(take, MaxTake);
        string? needle = NormalizeQuery(filter.Query);

        // Walk the readable kinds in enum order so the traversal matches the scan job's fixed kind order.
        var order = readableKinds.Distinct().OrderBy(k => k).ToList();

        if (StartOf(order, cursor) is not { } start)
        {
            return new ScanRowsPage([], null, 0, false);
        }

        var rows = new List<ScanRow>(Math.Min(pageSize, DefaultTake));
        int examined = 0;

        await foreach (var (kind, id, entity) in WalkAsync(order, start.Index, start.AfterEntityId, ct))
        {
            ct.ThrowIfCancellationRequested();

            if (entity is not null)
            {
                var plan = await planner.PlanLoadedEntity(entity, options, lookups, ct);
                rows.AddRange(plan.Items
                    .Select(item => ToRow(kind, plan.EntityId, item, options))
                    .Where(row => Passes(row, filter.Bucket, needle)));
            }

            examined++;

            // Both stops are tested at an entity boundary: an entity's files are planned together
            // and the cursor addresses entities, so splitting one would make the cursor ambiguous
            // about which of its rows were already served. A page may therefore overshoot
            // pageSize by the last entity's file count.
            if (rows.Count >= pageSize)
            {
                return new ScanRowsPage(rows, new ScanCursor(kind, id), examined, false);
            }

            if (examined >= MaxEntitiesPerRequest)
            {
                return new ScanRowsPage(rows, new ScanCursor(kind, id), examined, true);
            }
        }

        return new ScanRowsPage(rows, null, examined, false);
    }

    // Ids are pulled in batches so a filtered page issues one id query per batch rather than one per
    // entity, and the same batch feeds one chunked graph load. Bound to the port's own chunk decision so
    // there is a single number.
    private const int IdBatchSize = IRenamerDataPort.LoadChunkSize;

    // A cursor naming a kind the caller cannot read, whether stale or hand-crafted, resumes at the next
    // kind they can read, from its beginning, never inside the unreadable kind. Null when the cursor lies
    // past every readable kind.
    private static (int Index, int AfterEntityId)? StartOf(List<RenamerFileKind> order, ScanCursor? cursor)
    {
        if (cursor is null)
        {
            return (0, 0);
        }

        int index = order.FindIndex(k => k >= cursor.Kind);
        if (index < 0)
        {
            return null;
        }

        return (index, order[index] == cursor.Kind ? cursor.AfterEntityId : 0);
    }

    // Yields each walked id in keyset order with its loaded entity, or null for an id that loaded none.
    // A batch is read only when the caller asks past the previous one, so a caller that stops also stops
    // the reads. Each batch is sized to the entity budget the caller has not yet spent, which assumes the
    // caller examines every id it is given.
    private async IAsyncEnumerable<(RenamerFileKind Kind, int Id, RenamerEntity? Entity)> WalkAsync(
        List<RenamerFileKind> order, int startIndex, int startAfter,
        [EnumeratorCancellation] CancellationToken ct)
    {
        int walked = 0;
        for (int i = startIndex; i < order.Count; i++)
        {
            var kind = order[i];
            int after = i == startIndex ? startAfter : 0;

            // The caller stops once the entity budget is spent, so every pass starts under it.
            while (true)
            {
                int idBatch = Math.Min(IdBatchSize, MaxEntitiesPerRequest - walked);
                var ids = await port.LoadEntityIdPageAsync(kind, after, idBatch, ct);
                if (ids.Count == 0)
                {
                    break;
                }

                var loaded = await port.LoadEntitiesAsync(kind, ids, ct);
                var byId = loaded.ToDictionary(e => e.EntityId);

                foreach (var id in ids)
                {
                    yield return (kind, id, byId.GetValueOrDefault(id));
                    after = id;
                    walked++;
                }

                if (ids.Count < idBatch)
                {
                    break;
                }
            }
        }
    }

    // The budget comes from the same options instance the planner just planned against, so the row
    // cannot be measured against a separately sourced one.
    private ScanRow ToRow(RenamerFileKind kind, int entityId, RenamerPlanItem item, RenamerOptions options)
        => ScanRow.From(
            kind, entityId, item,
            BatchPreview.InFlightPathOverflows(item, options.FullPathMax, mountPoints));

    private static bool Passes(ScanRow row, ScanBucketKind? bucket, string? needle)
        => (bucket is not { } wanted || ScanBucket.Of(row.Status) == wanted) && Matches(row, needle);

    // Trims and lower-cases a search query; a blank query becomes null, meaning no filter.
    internal static string? NormalizeQuery(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        return query.Trim().ToLowerInvariant();
    }

    // True when the row matches the needle; a null needle matches everything. Reproduces the client's
    // in-table search exactly - the same four haystack fields joined the same way, the same trim, the
    // same case-insensitive substring test - because a user must not see their result set change under
    // them now that the box filters server-side. The basename and folder are derived from NewFullPath,
    // which is where the client reads them from too.
    internal static bool Matches(ScanRow row, string? needle)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (needle is null)
        {
            return true;
        }

        string haystack =
            $"{row.OldFullPath}\n{row.NewFullPath}\n{PathOps.BasenameOf(row.NewFullPath)}\n{PathOps.DirOf(row.NewFullPath)}";
        return haystack.ToLowerInvariant().Contains(needle, StringComparison.Ordinal);
    }
}

// The row filters of one page request. A null or blank Query means no search filter, and a null Bucket
// means no bucket filter.
public readonly record struct ScanRowFilter(string? Query, ScanBucketKind? Bucket);
