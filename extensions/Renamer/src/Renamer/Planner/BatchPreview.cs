using System.Text.Json.Serialization;
using Cove.Extensions.Shared;
using Renamer.Execution;

namespace Renamer.Planner;

/// <summary>
/// How loud the pre-renamer confirmation must be, scaled to the blast radius of the planned batch.
/// Sent to the UI so it renders the level rather than re-deriving it.
/// </summary>
[JsonConverter(typeof(CamelCaseStringEnumConverter))]
public enum ConfirmLevel
{
    /// <summary>A same-drive-only batch (or nothing to do): instant metadata renames, cheap and easy to undo.</summary>
    Light,

    /// <summary>A modest cross-drive move: real bytes copied across a single destination volume.</summary>
    Standard,

    /// <summary>A large cross-drive move (many items, many bytes, or several destination volumes) — the highest-stakes case.</summary>
    Heavy,
}

/// <summary>
/// One "N items (X bytes) from A to B" line of the blast radius: the per-(source-volume,
/// destination-volume) tally of cross-drive moves. <paramref name="From"/> is the source path root,
/// <paramref name="To"/> the destination volume; both may be <c>""</c> for a rootless path (the
/// aggregate classifies, it never throws on odd path content).
/// </summary>
/// <param name="From">The source volume (<see cref="VolumeClassifier.VolumeKey"/> of the old path).</param>
/// <param name="To">The destination volume the routed item lands on.</param>
/// <param name="Count">How many acting items move along this volume pair.</param>
/// <param name="Bytes">The summed file size in bytes of those items.</param>
public sealed record VolumePairDelta(string From, string To, int Count, long Bytes);

/// <summary>
/// The whole-batch blast-radius summary the preview surfaces alongside the per-item plan: how many
/// files actually move, the same- vs cross-volume split, the total cross-volume bytes, the
/// per-volume-pair breakdown, and a <see cref="ConfirmLevel"/> scaled to all of the above. A pure
/// snapshot — it mutates nothing.
/// </summary>
/// <param name="TotalCount">The number of acting items (Renamer | Move); skips/no-ops are excluded.</param>
/// <param name="SameVolumeCount">Acting items whose move stays on one volume (an in-place renamer).</param>
/// <param name="CrossVolumeCount">Acting items whose move crosses volumes (a copy across drives).</param>
/// <param name="CrossVolumeBytes">The summed bytes of the cross-volume moves (same-volume bytes excluded — they consume ~no extra space).</param>
/// <param name="VolumePairs">One <see cref="VolumePairDelta"/> per (source,destination) volume pair touched by a cross-volume move.</param>
/// <param name="ConfirmLevel">How loud the confirm must be, derived from the blast radius.</param>
/// <param name="Undoable">
/// Whether the batch will be journalled, and so can be reversed afterwards. False past
/// <see cref="IRevertJournal.MaxJournalledFiles"/>. Carried on the preview because the user has to
/// learn it BEFORE the rename runs.
/// </param>
/// <param name="InFlightPathOverflowCount">
/// How many acting items would overrun the path budget while their cross-volume copy is in flight (see
/// <see cref="BatchPreview.InFlightPathOverflows"/>). A count, never the paths: a batch reaches library
/// size, and the offending rows carry their own flag on the page that serves them.
/// </param>
public sealed record PreviewSummary(
    int TotalCount,
    int SameVolumeCount,
    int CrossVolumeCount,
    long CrossVolumeBytes,
    IReadOnlyList<VolumePairDelta> VolumePairs,
    ConfirmLevel ConfirmLevel,
    bool Undoable,
    int InFlightPathOverflowCount);

/// <summary>
/// Pure whole-batch blast-radius aggregate over a planned <see cref="RenamerPlanItem"/> set — the
/// analog of <see cref="FreeSpaceGuard"/> but for the user-facing preview rather than the disk-space
/// decision. Touches NO disk and NO DB: it reads only the plan items' paths/status/target volume plus
/// an injected FileId→size map (bytes live on the loaded entity's <c>RenamerFile.SizeBytes</c>, not on
/// the plan item), so it is fully unit-testable with no real second drive.
/// <para>
/// Like <see cref="FreeSpaceGuard"/> it reuses <see cref="VolumeClassifier.SameVolume"/> for the
/// same/cross split and <see cref="VolumeClassifier.VolumeKey"/> for volume grouping; same-volume moves
/// are excluded from every cross-volume sum because an in-place renamer consumes ~no extra space and is
/// trivially reversible.
/// </para>
/// <para>
/// classify-not-throw: a malformed/rootless path simply groups under its
/// <see cref="VolumeClassifier.VolumeKey"/> value (possibly <c>""</c>); the method never throws on path
/// content.
/// </para>
/// </summary>
public static class BatchPreview
{
    // Blast-radius thresholds for the confirm level. A same-drive-only batch is always Light: an
    // in-place renamer is an instant metadata change and trivially reversible, so its size never
    // escalates the confirm. A cross-drive move copies real bytes across volumes — it is the
    // higher-stakes operation, so it starts at Standard and escalates to Heavy when it is large by
    // ANY of three independent measures: a lot of files, a lot of bytes, or a spread across several
    // destination volumes (each of which multiplies the chance of a surprise). The exact numbers are
    // a product judgment; the SHAPE (cross-volume + scale -> louder) is the requirement.
    private const int HeavyItemCountThreshold = 50;       // >= 50 cross-volume items is "a lot of files"
    private const long HeavyByteThreshold = 10L << 30;    // >= 10 GiB across drives is "a lot of bytes"
    private const int HeavyVolumeCountThreshold = 2;       // >= 2 distinct destination volumes is "spread out"

    /// <summary>
    /// Summarizes the acting subset of <paramref name="items"/> (Status Renamer | Move) into the
    /// whole-batch blast radius: total count, same/cross split, per-(source,dest) volume-pair byte
    /// sums, and a <see cref="ConfirmLevel"/> scaled to the cross-volume blast radius. Bytes come from
    /// <paramref name="sizeByFileId"/> (missing ids contribute 0). Pure — no disk/DB.
    /// </summary>
    /// <param name="items">The full planned item set (skips/no-ops are filtered out internally).</param>
    /// <param name="sizeByFileId">FileId → file size in bytes, from the loaded entity's <c>RenamerFile.SizeBytes</c>.</param>
    /// <param name="fullPathMax">
    /// The caller's <see cref="Options.RenamerOptions.FullPathMax"/>, for the in-flight overflow count.
    /// Required and third rather than optional and last, so an unwired caller cannot count against a
    /// budget nobody configured.
    /// </param>
    /// <param name="mountPoints">Mount table to resolve Unix volumes against; omit for the real one.</param>
    public static PreviewSummary Summarize(
        IReadOnlyList<RenamerPlanItem> items,
        IReadOnlyDictionary<int, long> sizeByFileId,
        int fullPathMax,
        IReadOnlyCollection<string>? mountPoints = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(sizeByFileId);

        var acting = items
            .Where(i => i.Status is RenamerStatus.Renamer or RenamerStatus.Move)
            .ToList();

        // The cross/same split AND the destination-volume ("To") grouping key both read the SAME value —
        // VolumeClassifier.VolumeKey(NewFullPath). The preview's per-volume aggregation then matches exactly what
        // FreeSpaceGuard.Shortfall sums (it groups by VolumeKey(NewFullPath) too).
        // Previously "To" used i.TargetVolume, a SEPARATELY-derived value: for a routed move it normally
        // equals the NewFullPath root, but any UNC/normalization divergence between TargetFolderPath and
        // the joined NewFullPath could let an item be classified cross-volume by the SameVolume filter
        // yet contribute an odd/empty "To", skewing the Heavy/Standard confirm level away from what the
        // free-space guard actually saw. One source of truth removes that drift.
        var volumePairs = acting
            .Where(i => !VolumeClassifier.SameVolume(i.OldFullPath, i.NewFullPath, mountPoints))   // cross-volume only
            .GroupBy(i => (
                From: VolumeClassifier.VolumeKey(i.OldFullPath, mountPoints),
                To: VolumeClassifier.VolumeKey(i.NewFullPath, mountPoints)))
            .Select(g => new VolumePairDelta(
                g.Key.From,
                g.Key.To,
                g.Count(),
                g.Sum(i => sizeByFileId.GetValueOrDefault(i.FileId))))
            .ToList();

        int totalCount = acting.Count;
        int crossCount = volumePairs.Sum(p => p.Count);
        int sameCount = totalCount - crossCount;
        long crossBytes = volumePairs.Sum(p => p.Bytes);

        var level = ClassifyConfirm(crossCount, crossBytes, volumePairs);
        int inFlightOverflowCount = acting.Count(i => InFlightPathOverflows(i, fullPathMax, mountPoints));

        return new PreviewSummary(
            totalCount, sameCount, crossCount, crossBytes, volumePairs, level,
            Undoable: !IRevertJournal.ExceedsCap(totalCount),
            InFlightPathOverflowCount: inFlightOverflowCount);
    }

    /// <summary>
    /// True iff <paramref name="item"/> will act, will cross volumes, and the in-flight copy of it would
    /// overrun <paramref name="fullPathMax"/> — the band the planner accepts but the executor cannot fit.
    /// </summary>
    /// <remarks>
    /// The gap is a real platform limit rather than a self-imposed budget: no <c>\\?\</c> extended-length
    /// prefix is ever applied, and a cross-volume move
    /// copies to a name <see cref="CrossVolumeMover.InFlightSuffixLength"/> characters longer beside the
    /// destination before promoting it. <see cref="PathConfinement"/> budgets only the FINAL path, so an
    /// item accepted at the limit still overruns while its copy is in flight. Warned about here, beside
    /// the decision, and deliberately never fed back into it: subtracting the suffix length from the
    /// budget the engine reduces against would drop fields and truncate earlier, changing the planned
    /// filename for every item near the limit.
    /// <para>
    /// <c>internal</c> rather than <c>private</c> so the aggregate count and the per-row flag on the
    /// preview response both read THIS comparison — a second copy could disagree and report a count with
    /// no flagged row under it.
    /// </para>
    /// <para>
    /// Same-volume items are excluded because <see cref="DiskMover"/> mints no temporary name at all, and
    /// non-acting items because nothing is copied for them. Either would be a warning on a correct plan,
    /// and a skip already carries its own reason.
    /// </para>
    /// </remarks>
    /// <param name="item">The planned item to test.</param>
    /// <param name="fullPathMax">The configured absolute-path budget the executor must also fit.</param>
    /// <param name="mountPoints">Mount table to resolve Unix volumes against; omit for the real one.</param>
    internal static bool InFlightPathOverflows(
        RenamerPlanItem item,
        int fullPathMax,
        IReadOnlyCollection<string>? mountPoints = null)
    {
        ArgumentNullException.ThrowIfNull(item);

        return item.Status is RenamerStatus.Renamer or RenamerStatus.Move
            && !VolumeClassifier.SameVolume(item.OldFullPath, item.NewFullPath, mountPoints)
            && item.NewFullPath.Length + CrossVolumeMover.InFlightSuffixLength > fullPathMax;
    }

    /// <remarks>
    /// <c>internal</c> rather than <c>private</c> so the whole-library scan's incremental aggregate and
    /// its per-caller readback merge derive the confirm level from THIS map instead of restating the
    /// thresholds — a second copy could disagree and soften a Heavy confirm.
    /// </remarks>
    internal static ConfirmLevel ClassifyConfirm(
        int crossCount, long crossBytes, IReadOnlyList<VolumePairDelta> volumePairs)
    {
        if (crossCount == 0)
        {
            // Same-drive-only (or empty): always the cheapest, most-reversible case.
            return ConfirmLevel.Light;
        }

        int distinctDestinations = volumePairs.Select(p => p.To).Distinct().Count();
        bool heavy =
            crossCount >= HeavyItemCountThreshold
            || crossBytes >= HeavyByteThreshold
            || distinctDestinations >= HeavyVolumeCountThreshold;

        return heavy ? ConfirmLevel.Heavy : ConfirmLevel.Standard;
    }
}
