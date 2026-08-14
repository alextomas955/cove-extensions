using Renamer.Contracts;
using Renamer.Execution;
using Renamer.Options;

namespace Renamer.Planner;

/// <summary>
/// Serves one page of a whole-library dry run, planning only the entities that page needs.
/// </summary>
/// <remarks>
/// Pages through <see cref="IRenamerDataPort.LoadEntityIdPageAsync"/>'s ascending keyset and plans each
/// entity with <see cref="RenamerPlanner.PlanLoadedEntityAsync"/> — the SAME method the scan job calls. A
/// slice therefore equals the corresponding slice of a full plan because there is one planner, not
/// because two paths were written to agree; <c>ScanPagingEquivalenceTests</c> is what holds that.
/// </remarks>
/// <param name="planner">The one planner; a second planning path would break the equivalence this rests on.</param>
/// <param name="port">The read seam ids and entity graphs come from.</param>
/// <param name="mountPoints">
/// Mount table to resolve Unix volumes against; omit for the real one. Carried for the same reason
/// <see cref="ScanAggregator"/> and <see cref="BatchPreview.Summarize"/> carry it: a page's in-flight
/// overflow flag is a cross-volume decision, and on Unix volume identity comes from the RUNNER's mount
/// table, so a flagged row is otherwise not reproducible off the machine that produced it.
/// </param>
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

    /// <summary>Reads the next page of rows.</summary>
    /// <param name="readableKinds">The kinds the caller may read; no other kind is walked or cursored into.</param>
    /// <param name="cursor">Where to resume, or null to start at the first readable kind.</param>
    /// <param name="take">Requested row count; clamped to <see cref="MaxTake"/>, non-positive falls back to <see cref="DefaultTake"/>.</param>
    /// <param name="query">Case-insensitive path substring filter, or null/blank for no filter.</param>
    /// <param name="bucket">Bucket filter, or null for no filter.</param>
    /// <param name="options">The options to plan with.</param>
    /// <param name="lookups">The routing lookups built once from <paramref name="options"/>.</param>
    /// <param name="ct">Cancellation token.</param>
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
            // A cursor naming a kind the caller cannot read (a stale cursor, or one hand-crafted) resumes
            // at the next kind they CAN read, from its beginning — never inside the unreadable kind.
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

            while (true)
            {
                if (examined >= MaxEntitiesPerRequest)
                {
                    return new ScanRowsPage(rows, new ScanCursor(kind, after), examined, true);
                }

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
                        var plan = await planner.PlanLoadedEntityAsync(entity, options, lookups, ct);
                        foreach (var item in plan.Items)
                        {
                            // The budget comes from the SAME options instance the planner just planned
                            // against, so the row cannot be measured against a separately-sourced one.
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

                    // Both stops are tested at an ENTITY boundary: an entity's files are planned together
                    // and the cursor addresses entities, so splitting one would make the cursor ambiguous
                    // about which of its rows were already served. A page may therefore overshoot pageSize
                    // by the last entity's file count.
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

    // Ids are pulled in batches so a filtered page issues one id query per ~200 entities rather than one
    // per entity; the same batch feeds one chunked graph load. Bound to the port's own chunk decision so
    // there is a single number.
    private const int IdBatchSize = CoveRenamerDataPort.LoadChunkSize;

    /// <summary>Trims and lower-cases a search query; a blank query becomes null, meaning no filter.</summary>
    internal static string? NormalizeQuery(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        return query.Trim().ToLowerInvariant();
    }

    /// <summary>True iff <paramref name="row"/> matches <paramref name="needle"/> (null matches everything).</summary>
    /// <remarks>
    /// Reproduces the client's in-table search exactly — the same four haystack fields joined the same
    /// way, the same trim, the same case-insensitive substring test — because a user must not see their
    /// result set change under them now that the box filters server-side. The basename and folder are
    /// derived from <see cref="ScanRow.NewFullPath"/>, which is where the client reads them from too.
    /// </remarks>
    internal static bool Matches(ScanRow row, string? needle)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (needle is null)
        {
            return true;
        }

        string haystack =
            $"{row.OldFullPath}\n{row.NewFullPath}\n{Basename(row.NewFullPath)}\n{Folder(row.NewFullPath)}";
        return haystack.ToLowerInvariant().Contains(needle, StringComparison.Ordinal);
    }

    private static string Basename(string path)
    {
        int i = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\'));
        return i >= 0 ? path[(i + 1)..] : path;
    }

    private static string Folder(string path)
    {
        int i = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\'));
        return i >= 0 ? path[..i] : string.Empty;
    }
}
