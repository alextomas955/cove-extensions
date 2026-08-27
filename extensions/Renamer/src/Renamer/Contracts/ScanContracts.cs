using Renamer.Planner;

namespace Renamer.Contracts;

/// <summary>How many files of a completed scan landed on one <see cref="RenamerStatus"/>.</summary>
/// <param name="Status">The planner classification counted.</param>
/// <param name="Count">The exact number of files, never sampled or estimated.</param>
public sealed record ScanStatusCount(RenamerStatus Status, int Count);

/// <summary>
/// One kind's slice of a completed whole-library scan: how much was planned, how it classified, and
/// what it would move.
/// </summary>
/// <remarks>
/// The scan's result is stored under one fixed key by whoever ran it last, so a video-only reader can
/// read back a scan a higher-permission user ran. Keeping the figures split PER KIND is what lets the
/// readback drop the kinds the current caller cannot see; a flat total would hand that reader image and
/// audio counts they hold no permission for.
/// </remarks>
/// <param name="Kind">The entity kind these figures cover.</param>
/// <param name="Entities">Entities planned for this kind.</param>
/// <param name="Files">Files planned for this kind — the sum of <paramref name="StatusCounts"/>.</param>
/// <param name="StatusCounts">Every <see cref="RenamerStatus"/> in declaration order, with its exact count.</param>
/// <param name="BlastRadius">The same summary <see cref="BatchPreview.Summarize"/> produces for a selection, folded over this kind.</param>
/// <param name="VolumePairsTruncated">True iff <see cref="PreviewSummary.VolumePairs"/> was topped at <see cref="ScanSummary.MaxVolumePairsPerKind"/>.</param>
public sealed record ScanKindSummary(
    RenamerFileKind Kind,
    int Entities,
    int Files,
    IReadOnlyList<ScanStatusCount> StatusCounts,
    PreviewSummary BlastRadius,
    bool VolumePairsTruncated);

/// <summary>
/// What a completed whole-library scan persists: per-kind counts and blast radius, and nothing per
/// file. This is the whole stored value — its size is a function of the kind count, the
/// <see cref="RenamerStatus"/> member count and <see cref="MaxVolumePairsPerKind"/>, never of the
/// library size.
/// </summary>
/// <param name="SchemaVersion">The shape this blob was written with; see <see cref="CurrentSchemaVersion"/>.</param>
/// <param name="CompletedAtUtcTicks">UTC ticks the scan finished at.</param>
/// <param name="Kinds">One entry per kind that had at least one entity; a kind with none is absent.</param>
public sealed record ScanSummary(
    int SchemaVersion,
    long CompletedAtUtcTicks,
    IReadOnlyList<ScanKindSummary> Kinds)
{
    // Stamped so a later reshape of this record degrades to "no scan yet" instead of throwing on an
    // old blob: the readback compares the stamp and 404s on anything it does not recognise. The cost of
    // a reshape is then one lost dry-run summary, not a broken settings page.
    public const int CurrentSchemaVersion = 1;

    // The per-kind volume-pair ITEMISATION is the only part of this record that would otherwise grow
    // with the library's shape (O(distinct volume pairs)), so it is topped here — that cap is what makes
    // the stored size a fixed ceiling rather than an open-ended one. It tops the list only:
    // PreviewSummary.CrossVolumeCount and CrossVolumeBytes stay exact, VolumePairsTruncated says the
    // list was topped, and the confirm level is computed over the untruncated pairs — so no number on
    // this surface is quietly wrong. 64 is far above any real drive count while still bounding the blob.
    public const int MaxVolumePairsPerKind = 64;

    /// <summary>
    /// Materialises accumulated <c>(from,to) → (count,bytes)</c> tallies as the wire's volume-pair list,
    /// keeping at most <see cref="MaxVolumePairsPerKind"/> of them.
    /// </summary>
    /// <remarks>
    /// Keeps the largest by bytes, because the pairs a user needs to see are the ones that will move the
    /// most data. Ties break on the volume names so the same tallies always serialize to the same string
    /// — a blob that changed shape between identical scans would make the stored-size ceiling untestable.
    /// <paramref name="truncated"/> reports whether anything was dropped.
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
/// <param name="TotalFiles">Files planned across <paramref name="Kinds"/>.</param>
/// <param name="TotalEntities">Entities planned across <paramref name="Kinds"/>.</param>
/// <param name="WillChange">Files whose status buckets as <see cref="ScanBucketKind.WillChange"/>.</param>
/// <param name="Attention">Files whose status buckets as <see cref="ScanBucketKind.Attention"/>.</param>
/// <param name="NoChange">Files whose status buckets as <see cref="ScanBucketKind.NoChange"/>.</param>
/// <param name="StatusCounts">The merged per-status counts, in <see cref="RenamerStatus"/> declaration order.</param>
/// <param name="BlastRadius">The merged blast radius across the readable kinds.</param>
/// <param name="VolumePairsTruncated">True iff any merged kind's volume-pair itemisation was topped.</param>
/// <param name="CompletedAtUtcTicks">UTC ticks the scan finished at.</param>
/// <param name="Kinds">
/// The kinds these figures actually cover, so the UI can name them rather than implying the numbers
/// span the caller's whole library.
/// </param>
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
    /// The confirm level is re-derived from the merged pairs rather than taken from any one kind's, so a
    /// caller reading two kinds sees the confirm their combined move actually earns. Volume pairs are
    /// re-topped at <see cref="ScanSummary.MaxVolumePairsPerKind"/> after the merge, and the truncation
    /// flag is sticky across the kinds so a topped list is never presented as complete.
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
        // Undoable is an UPPER BOUND here: a whole-library run renames one batch PER KIND, so a
        // mixed-kind library can read "not undoable" while its largest single kind still fits.
        // Over-warning ahead of a destructive run is the safe direction.
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
/// Deliberately narrower than <see cref="PreviewItemView"/>: <c>ResolvedDestinationRoot</c>,
/// <c>MatchedRule</c> and <c>TargetVolume</c> have no reader, and the new basename and target folder are
/// both <paramref name="NewFullPath"/> split at its last separator, which the client already does. The
/// trim is for wire weight and dead surface — it is NOT what makes the dry run scale. What makes it
/// scale is that this row is never persisted: it is planned on demand for the page being read.
/// </remarks>
/// <param name="Kind">The entity kind, for the table's type column.</param>
/// <param name="EntityId">The entity the plan is for — every file of one entity shares it, so a row links to its asset.</param>
/// <param name="FileId">The Cove <c>BaseFileEntity.Id</c> this row plans.</param>
/// <param name="OldFullPath">The file's current full path (forward-slash).</param>
/// <param name="NewFullPath">The intended new full path (forward-slash), or the old path for a no-op/skip.</param>
/// <param name="Status">The planner's classification.</param>
/// <param name="Reason">Human-readable reason for a skip/no-op; null for a plain rename or move.</param>
/// <param name="Suffixed">True iff the collision suffix loop ran.</param>
/// <param name="Sanitized">True iff the engine cleaned the rendered name.</param>
/// <param name="InFlightPathOverflow">
/// True iff this row's cross-volume copy would overrun the path budget while it is in flight (see
/// <see cref="BatchPreview.InFlightPathOverflows"/>). It IS read — it drives a red warning badge on the
/// table a user reads before approving a bulk rename — so it earns its place on a row that multiplies by
/// library size.
/// </param>
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
    /// <summary>Projects a planned <paramref name="item"/> of entity <paramref name="entityId"/> onto its wire shape.</summary>
    /// <remarks>
    /// <paramref name="inFlightPathOverflow"/> is handed in rather than classified here: the caller holds
    /// the options, so the budget this row is measured against is the same one the planner used, and this
    /// projection stays a pure mapping.
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
/// A whole entity is the smallest resumable unit — its files are planned together — so the cursor
/// addresses entities, never rows.
/// </remarks>
/// <param name="Kind">The kind to resume within.</param>
/// <param name="AfterEntityId">The last entity id already consumed; the next page starts strictly after it.</param>
public sealed record ScanCursor(RenamerFileKind Kind, int AfterEntityId);

/// <summary>One page of a whole-library dry run.</summary>
/// <param name="Rows">The rows of this page, in walk order.</param>
/// <param name="Next">
/// Where to resume, or null once the walk has observed the end of the last readable kind. A non-null
/// cursor means "there may be more" — the following request is what proves it either way.
/// </param>
/// <param name="EntitiesExamined">Entities planned to fill this page, whether or not their rows survived the filters.</param>
/// <param name="BudgetExhausted">
/// True iff the request hit its per-request entity budget before filling the page — the honest signal
/// that more of the library is unexamined, which a narrow filter over a large library hits routinely.
/// </param>
public sealed record ScanRowsPage(
    IReadOnlyList<ScanRow> Rows,
    ScanCursor? Next,
    int EntitiesExamined,
    bool BudgetExhausted);

/// <summary>
/// The <c>/scan-rows</c> request body: where to resume, how much to take, and how to filter.
/// </summary>
/// <remarks>
/// Bound typed rather than parsed from the raw <c>HttpContext</c> (the treatment <c>/preview-sample</c>
/// needs) because every member is a string or an int — the host's minimal-API serializer lacks a
/// string-enum converter, so only a body carrying a bare enum value would fail typed binding. The
/// options blob travels as a string for exactly that reason, as it does on <c>/scan-library</c>.
/// </remarks>
/// <param name="Options">The caller's current options as a PascalCase JSON blob, or null to plan with the saved options.</param>
/// <param name="Kind">The cursor's kind (<c>video</c>/<c>image</c>/<c>audio</c>), or null to start at the first readable kind.</param>
/// <param name="AfterEntityId">The cursor's last consumed entity id, or null to start at the beginning of the kind.</param>
/// <param name="Take">Requested row count; clamped to the pager's maximum, and non-positive falls back to its default.</param>
/// <param name="Query">Case-insensitive path substring filter, or null/blank for no filter.</param>
/// <param name="Bucket">A <see cref="ScanBucketKind"/> name, or null/<c>all</c> for no filter.</param>
public sealed record ScanRowsRequest(
    string? Options,
    string? Kind,
    int? AfterEntityId,
    int? Take,
    string? Query,
    string? Bucket);

/// <summary>
/// Cove's configured library paths - the list every destination root is chosen from.
/// </summary>
/// <remarks>
/// A record rather than a bare array so the panel can tell "the host declares no library path" from
/// a response it failed to read, and so a later field can be added without changing the shape.
/// </remarks>
/// <param name="Paths">The absolute library paths, forward-slash, in configuration order.</param>
public sealed record LibraryPathsView(IReadOnlyList<string> Paths);
