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
    // DefaultTake. A null or blank query means no filter, as does a null bucket.
    public async Task<ScanRowsPage> PageAsync(
        IReadOnlyList<RenamerFileKind> readableKinds, ScanCursor? cursor, int take,
        string? query, ScanBucketKind? bucket, RenamerOptions options, RouteLookups lookups,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(readableKinds);

        int pageSize = take <= 0 ? DefaultTake : Math.Min(take, MaxTake);
        string? needle = NormalizeQuery(query);

        // Walk the readable kinds in enum order so the traversal matches the scan job's fixed kind order.
        var order = readableKinds.Distinct().OrderBy(k => k).ToList();

        int startIndex = 0;
        int startAfter = 0;
        if (cursor is not null)
        {
            // A cursor naming a kind the caller cannot read, whether stale or hand-crafted, resumes at
            // the next kind they can read, from its beginning, never inside the unreadable kind.
            startIndex = order.FindIndex(k => k >= cursor.Kind);
            if (startIndex < 0)
            {
                return new ScanRowsPage([], null, 0, false);
            }

            startAfter = order[startIndex] == cursor.Kind ? cursor.AfterEntityId : 0;
        }

        var rows = new List<ScanRow>(Math.Min(pageSize, DefaultTake));
        int examined = 0;

        for (int i = startIndex; i < order.Count; i++)
        {
            var kind = order[i];
            int after = i == startIndex ? startAfter : 0;

            // The inner loop returns once the entity budget is spent, so every pass starts under it.
            while (true)
            {
                int idBatch = Math.Min(IdBatchSize, MaxEntitiesPerRequest - examined);
                var ids = await port.LoadEntityIdPageAsync(kind, after, idBatch, ct);
                if (ids.Count == 0)
                {
                    break;
                }

                var loaded = await port.LoadEntitiesAsync(kind, ids, ct);
                var byId = loaded.ToDictionary(e => e.EntityId);

                foreach (var id in ids)
                {
                    ct.ThrowIfCancellationRequested();

                    if (byId.TryGetValue(id, out var entity))
                    {
                        var plan = await planner.PlanLoadedEntity(entity, options, lookups, ct);
                        foreach (var item in plan.Items)
                        {
                            // The budget comes from the same options instance the planner just planned
                            // against, so the row cannot be measured against a separately sourced one.
                            var row = ScanRow.From(
                                kind, plan.EntityId, item,
                                BatchPreview.InFlightPathOverflows(item, options.FullPathMax, mountPoints));
                            if (bucket is { } wanted && ScanBucket.Of(row.Status) != wanted)
                            {
                                continue;
                            }

                            if (!Matches(row, needle))
                            {
                                continue;
                            }

                            rows.Add(row);
                        }
                    }

                    after = id;
                    examined++;

                    // Both stops are tested at an entity boundary: an entity's files are planned together
                    // and the cursor addresses entities, so splitting one would make the cursor ambiguous
                    // about which of its rows were already served. A page may therefore overshoot
                    // pageSize by the last entity's file count.
                    if (rows.Count >= pageSize)
                    {
                        return new ScanRowsPage(rows, new ScanCursor(kind, after), examined, false);
                    }

                    if (examined >= MaxEntitiesPerRequest)
                    {
                        return new ScanRowsPage(rows, new ScanCursor(kind, after), examined, true);
                    }
                }

                if (ids.Count < idBatch)
                {
                    break;
                }
            }
        }

        return new ScanRowsPage(rows, null, examined, false);
    }

    // Ids are pulled in batches so a filtered page issues one id query per batch rather than one per
    // entity, and the same batch feeds one chunked graph load. Bound to the port's own chunk decision so
    // there is a single number.
    private const int IdBatchSize = IRenamerDataPort.LoadChunkSize;

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
