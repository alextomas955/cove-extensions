using Renamer.Planner;

namespace Renamer.Contracts;

/// <summary>How many files of a completed scan landed on one <see cref="RenamerStatus"/>.</summary>
/// <remarks>The count is exact, never sampled or estimated.</remarks>
public sealed record ScanStatusCount(RenamerStatus Status, int Count);

/// <summary>One kind's slice of a completed whole-library scan.</summary>
/// <remarks>
/// The scan result is stored under one fixed key by whoever ran it last, so a video-only reader can
/// read back a scan a higher-permission user ran. The figures stay split per kind so the readback can
/// drop the kinds the caller may not see. <c>Files</c> is the sum of <c>StatusCounts</c>, which lists
/// every <see cref="RenamerStatus"/> in declaration order, and <c>VolumePairsTruncated</c> reports
/// that <see cref="PreviewSummary.VolumePairs"/> was topped at
/// <see cref="ScanSummary.MaxVolumePairsPerKind"/>.
/// </remarks>
public sealed record ScanKindSummary(
    RenamerFileKind Kind,
    int Entities,
    int Files,
    IReadOnlyList<ScanStatusCount> StatusCounts,
    PreviewSummary BlastRadius,
    bool VolumePairsTruncated);

/// <summary>What a completed whole-library scan persists: per-kind counts and blast radius.</summary>
/// <remarks>
/// Nothing per file is stored. The value's size is a function of the kind count, the
/// <see cref="RenamerStatus"/> member count and <see cref="MaxVolumePairsPerKind"/>, never of the
/// library size. <c>Kinds</c> holds one entry per kind that had at least one entity.
/// </remarks>
public sealed record ScanSummary(
    int SchemaVersion,
    long CompletedAtUtcTicks,
    IReadOnlyList<ScanKindSummary> Kinds)
{
    // The readback compares this stamp and 404s on a value it does not recognise, so a later reshape
    // of the record degrades to "no scan yet" instead of throwing on an old stored value.
    public const int CurrentSchemaVersion = 1;

    // The per-kind volume-pair itemisation is the one part of this record that would grow with the
    // library's shape, so it is capped. The cap tops the list only: CrossVolumeCount and
    // CrossVolumeBytes stay exact, VolumePairsTruncated reports the topping, and the confirm level is
    // computed over the untruncated pairs.
    public const int MaxVolumePairsPerKind = 64;

    /// <summary>
    /// Materialises accumulated volume-pair tallies as the wire list, keeping at most
    /// <see cref="MaxVolumePairsPerKind"/> of them.
    /// </summary>
    /// <remarks>
    /// The largest by bytes are kept, and ties break on the volume names so the same tallies always
    /// serialize identically. <paramref name="truncated"/> reports whether anything was dropped.
    /// </remarks>
    public static IReadOnlyList<VolumePairDelta> TopVolumePairs(
        IReadOnlyDictionary<(string From, string To), (int Count, long Bytes)> pairs, out bool truncated)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        truncated = pairs.Count > MaxVolumePairsPerKind;
        return
        [
            .. pairs
                .OrderByDescending(p => p.Value.Bytes)
                .ThenBy(p => p.Key.From, StringComparer.Ordinal)
                .ThenBy(p => p.Key.To, StringComparer.Ordinal)
                .Take(MaxVolumePairsPerKind)
                .Select(p => new VolumePairDelta(p.Key.From, p.Key.To, p.Value.Count, p.Value.Bytes)),
        ];
    }
}

/// <summary>
/// What <c>/last-scan</c> returns: the stored <see cref="ScanSummary"/> merged down to the kinds the
/// calling principal may read.
/// </summary>
/// <remarks>
/// Every count covers only <c>Kinds</c>, which names the kinds the figures cover so the UI does not
/// imply they span the caller's whole library. <c>StatusCounts</c> is in <see cref="RenamerStatus"/>
/// declaration order, and <c>VolumePairsTruncated</c> is set when any merged kind's volume-pair
/// itemisation was topped.
/// </remarks>
public sealed record ScanSummaryView(
    int TotalFiles,
    int TotalEntities,
    int WillChange,
    int Attention,
    int NoChange,
    IReadOnlyList<ScanStatusCount> StatusCounts,
    PreviewSummary BlastRadius,
    bool VolumePairsTruncated,
    long CompletedAtUtcTicks,
    IReadOnlyList<RenamerFileKind> Kinds)
{
    /// <summary>
    /// Merges <paramref name="summary"/> down to <paramref name="readableKinds"/>, summing the counts
    /// and re-folding the blast radius across them.
    /// </summary>
    /// <remarks>
    /// The confirm level is re-derived from the merged pairs, so a caller reading two kinds sees the
    /// confirm their combined move earns. Volume pairs are re-topped at
    /// <see cref="ScanSummary.MaxVolumePairsPerKind"/> after the merge, and the truncation flag is
    /// sticky across kinds so a topped list is never presented as complete.
    /// </remarks>
    public static ScanSummaryView From(ScanSummary summary, IReadOnlyList<RenamerFileKind> readableKinds)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(readableKinds);

        var kinds = summary.Kinds.Where(k => readableKinds.Contains(k.Kind)).ToList();

        var byStatus = new Dictionary<RenamerStatus, int>();
        foreach (var count in kinds.SelectMany(k => k.StatusCounts))
        {
            byStatus[count.Status] = byStatus.GetValueOrDefault(count.Status) + count.Count;
        }

        var statusCounts = Enum.GetValues<RenamerStatus>()
            .Select(status => new ScanStatusCount(status, byStatus.GetValueOrDefault(status)))
            .ToList();

        var pairs = new Dictionary<(string From, string To), (int Count, long Bytes)>();
        foreach (var pair in kinds.SelectMany(k => k.BlastRadius.VolumePairs))
        {
            var key = (pair.From, pair.To);
            var acc = pairs.GetValueOrDefault(key);
            pairs[key] = (acc.Count + pair.Count, acc.Bytes + pair.Bytes);
        }

        var merged = ScanSummary.TopVolumePairs(pairs, out bool topped);
        bool truncated = topped || kinds.Any(k => k.VolumePairsTruncated);

        int crossCount = kinds.Sum(k => k.BlastRadius.CrossVolumeCount);
        long crossBytes = kinds.Sum(k => k.BlastRadius.CrossVolumeBytes);
        // Undoable is an upper bound here: a whole-library run renames one batch per kind, so a
        // mixed-kind library can read "not undoable" while its largest single kind still fits.
        var blastRadius = new PreviewSummary(
            TotalCount: kinds.Sum(k => k.BlastRadius.TotalCount),
            SameVolumeCount: kinds.Sum(k => k.BlastRadius.SameVolumeCount),
            CrossVolumeCount: crossCount,
            CrossVolumeBytes: crossBytes,
            VolumePairs: merged,
            ConfirmLevel: BatchPreview.ClassifyConfirm(crossCount, crossBytes, merged),
            Undoable: kinds.All(k => k.BlastRadius.Undoable),
            InFlightPathOverflowCount: kinds.Sum(k => k.BlastRadius.InFlightPathOverflowCount));

        int Bucket(ScanBucketKind bucket) => statusCounts
            .Where(c => ScanBucket.Of(c.Status) == bucket)
            .Sum(c => c.Count);

        return new ScanSummaryView(
            TotalFiles: kinds.Sum(k => k.Files),
            TotalEntities: kinds.Sum(k => k.Entities),
            WillChange: Bucket(ScanBucketKind.WillChange),
            Attention: Bucket(ScanBucketKind.Attention),
            NoChange: Bucket(ScanBucketKind.NoChange),
            StatusCounts: statusCounts,
            BlastRadius: blastRadius,
            VolumePairsTruncated: truncated,
            CompletedAtUtcTicks: summary.CompletedAtUtcTicks,
            Kinds: [.. kinds.Select(k => k.Kind)]);
    }
}

/// <summary>
/// One planned file of a whole-library dry run, served by <c>/scan-rows</c> a page at a time.
/// </summary>
/// <remarks>
/// The row is never persisted: it is planned on demand for the page being read. Paths are
/// forward-slash, and <c>NewFullPath</c> repeats the old path for a no-op or skip. <c>Reason</c> is
/// null for a plain rename or move. <c>InFlightPathOverflow</c> says the cross-volume copy would
/// overrun the path budget while in flight; see <see cref="BatchPreview.InFlightPathOverflows"/>.
/// </remarks>
public sealed record ScanRow(
    RenamerFileKind Kind,
    int EntityId,
    int FileId,
    string OldFullPath,
    string NewFullPath,
    RenamerStatus Status,
    string? Reason,
    bool Suffixed,
    bool Sanitized,
    bool InFlightPathOverflow)
{
    /// <summary>Projects a planned item onto its wire shape.</summary>
    /// <remarks>
    /// <paramref name="inFlightPathOverflow"/> is handed in because the caller holds the options, so
    /// the budget this row is measured against is the one the planner used.
    /// </remarks>
    public static ScanRow From(
        RenamerFileKind kind, int entityId, RenamerPlanItem item, bool inFlightPathOverflow)
    {
        ArgumentNullException.ThrowIfNull(item);
        return new ScanRow(
            kind,
            entityId,
            item.FileId,
            item.OldFullPath,
            item.NewFullPath,
            item.Status,
            item.Reason,
            item.Suffixed,
            item.Sanitized,
            inFlightPathOverflow);
    }
}

/// <summary>
/// Where a <c>/scan-rows</c> walk resumes: the kind being walked and the last entity id it consumed.
/// </summary>
/// <remarks>
/// An entity's files are planned together, so the cursor addresses entities, never rows. The next
/// page starts strictly after <c>AfterEntityId</c>.
/// </remarks>
public sealed record ScanCursor(RenamerFileKind Kind, int AfterEntityId);

/// <summary>One page of a whole-library dry run.</summary>
/// <remarks>
/// <c>Next</c> is null once the walk has observed the end of the last readable kind; a non-null
/// cursor means there may be more, which the following request settles. <c>EntitiesExamined</c>
/// counts entities planned to fill this page, whether or not their rows survived the filters, and
/// <c>BudgetExhausted</c> says the request hit its per-request entity budget before filling the page.
/// </remarks>
public sealed record ScanRowsPage(
    IReadOnlyList<ScanRow> Rows,
    ScanCursor? Next,
    int EntitiesExamined,
    bool BudgetExhausted);

/// <summary>
/// The <c>/scan-rows</c> request body: where to resume, how much to take, and how to filter.
/// </summary>
/// <remarks>
/// Every member is a string or an int, so the body binds typed. The host's minimal-API serializer
/// has no string-enum converter, so a body carrying a bare enum value would fail typed binding, and
/// <c>Options</c> therefore travels as a PascalCase JSON string, as it does on <c>/scan-library</c>.
/// <c>Options</c> null plans with the saved options, <c>Kind</c> null starts at the first readable
/// kind, <c>AfterEntityId</c> null starts at the beginning of the kind, a non-positive <c>Take</c>
/// falls back to the pager default and is otherwise clamped to its maximum, <c>Query</c> is a
/// case-insensitive path substring, and <c>Bucket</c> takes a <see cref="ScanBucketKind"/> name or
/// <c>all</c>.
/// </remarks>
public sealed record ScanRowsRequest(
    string? Options,
    string? Kind,
    int? AfterEntityId,
    int? Take,
    string? Query,
    string? Bucket);

/// <summary>
/// Cove's configured library paths, the list every destination root is chosen from.
/// </summary>
/// <remarks>
/// A record, not a bare array, so the panel can tell "the host declares no library path" from a
/// response it failed to read. The paths are absolute and forward-slash, in configuration order.
/// </remarks>
public sealed record LibraryPathsView(IReadOnlyList<string> Paths);
