using System.Text.Json.Serialization;
using Cove.Extensions.Shared;
using Renamer.Execution;

namespace Renamer.Planner;

// How loud the pre-rename confirmation must be, scaled to the blast radius of the planned batch. Sent
// to the UI so it renders the level rather than re-deriving it.
[JsonConverter(typeof(CamelCaseStringEnumConverter))]
public enum ConfirmLevel
{
    // A same-drive-only batch, or nothing to do: instant metadata renames, cheap and easy to undo.
    Light,

    // A modest cross-drive move: real bytes copied across a single destination volume.
    Standard,

    // A large cross-drive move: many items, many bytes, or several destination volumes.
    Heavy,
}

// One "N items (X bytes) from A to B" line of the blast radius: the per-(source volume, destination
// volume) tally of cross-drive moves. Either volume may be "" for a rootless path, because the
// aggregate classifies and never throws on odd path content.
public sealed record VolumePairDelta(string From, string To, int Count, long Bytes);

// The whole-batch blast-radius summary the preview surfaces alongside the per-item plan. Every member
// is a scalar or a per-volume-pair tally, so nothing here grows with the library. CrossVolumeBytes
// excludes same-volume moves, which consume no extra space.
// InFlightPathOverflowCount is a count and never the paths: a batch reaches library size, and the
// offending rows carry their own flag on the page that serves them.
public sealed record PreviewSummary(
    int TotalCount,
    int SameVolumeCount,
    int CrossVolumeCount,
    long CrossVolumeBytes,
    IReadOnlyList<VolumePairDelta> VolumePairs,
    ConfirmLevel ConfirmLevel,
    int InFlightPathOverflowCount);

// Pure whole-batch blast-radius aggregate over a planned item set - the preview's counterpart to
// FreeSpaceGuard's disk-space decision. Touches no disk and no DB: it reads the plan items' paths,
// status and target volume plus an injected FileId-to-size map, because bytes live on the loaded
// entity's RenamerFile.SizeBytes and not on the plan item.
//
// The same/cross split and the volume grouping read VolumeClassifier, the same source of truth
// FreeSpaceGuard reads. Same-volume moves are excluded from every cross-volume sum because an in-place
// rename consumes no extra space and is trivially reversible. A malformed or rootless path groups under
// its VolumeKey value, possibly "", and no method here throws on path content.
public static class BatchPreview
{
    // Blast-radius thresholds for the confirm level. A same-drive-only batch is always Light: an
    // in-place rename is an instant metadata change and trivially reversible, so its size never
    // escalates the confirm. A cross-drive move copies real bytes across volumes, so it starts at
    // Standard and escalates to Heavy when it is large by any of three independent measures: a lot of
    // files, a lot of bytes, or a spread across several destination volumes. The exact numbers are a
    // product judgment; the shape is the requirement.
    private const int HeavyItemCountThreshold = 50;
    private const long HeavyByteThreshold = 10L << 30;
    private const int HeavyVolumeCountThreshold = 2;

    // Summarizes the acting subset of items into the whole-batch blast radius. Bytes come from
    // sizeByFileId, where a missing id contributes 0. fullPathMax is required, so an unwired caller
    // cannot count against a budget nobody configured. mountPoints resolves Unix volumes; omit for the
    // real table.
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

        // The cross/same split and the destination-volume grouping key both read one value,
        // VolumeKey(NewFullPath), so the preview's per-volume aggregation matches what
        // FreeSpaceGuard.Shortfall sums. A separately derived destination volume could diverge from the
        // joined NewFullPath under UNC or normalization differences and skew the confirm level away from
        // what the free-space guard saw.
        var volumePairs = acting
            .Where(i => !VolumeClassifier.SameVolume(i.OldFullPath, i.NewFullPath, mountPoints))
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
            InFlightPathOverflowCount: inFlightOverflowCount);
    }

    // True when the item will act, will cross volumes, and its in-flight copy would overrun
    // fullPathMax - the band the planner accepts but the executor cannot fit. The gap is a real platform
    // limit: no \\?\ extended-length prefix is ever applied, and a cross-volume move copies to a name
    // CrossVolumeMover.InFlightSuffixLength characters longer beside the destination before promoting
    // it, while PathConfinement budgets only the final path. The suffix length is deliberately not fed
    // back into that budget, which would drop fields and truncate earlier for every item near the limit.
    //
    // Internal so the aggregate count and the per-row flag on the preview response read this one
    // comparison; a second copy could report a count with no flagged row under it.
    //
    // Same-volume items are excluded because DiskMover mints no temporary name, and non-acting items
    // because nothing is copied for them.
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

    // Internal so the whole-library scan's incremental aggregate and its per-caller readback merge
    // derive the confirm level from this map; a second copy could soften a Heavy confirm.
    internal static ConfirmLevel ClassifyConfirm(
        int crossCount, long crossBytes, IReadOnlyList<VolumePairDelta> volumePairs)
    {
        if (crossCount == 0)
        {
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
