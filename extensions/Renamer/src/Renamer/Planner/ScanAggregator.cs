using Renamer.Contracts;

namespace Renamer.Planner;

// Folds a whole-library dry run one entity at a time into the bounded ScanSummary the scan persists.
//
// The class exists to keep the scan's memory and its stored value independent of library size: it holds
// per-kind counters and volume-pair tallies, never a per-file or per-entity collection. Any collection
// that grows with the number of folded entities breaks that guarantee.
//
// The same/cross split and the pair grouping read VolumeClassifier, the same source of truth
// BatchPreview.Summarize and the free-space guard read, so the aggregate cannot disagree with them about
// which volume a path is on.
public sealed class ScanAggregator
{
    private sealed class KindTally
    {
        public int Entities;
        public int Files;
        public int ActingFiles;
        public int CrossVolumeFiles;
        public int InFlightPathOverflowFiles;
        public readonly int[] ByStatus = new int[Enum.GetValues<RenamerStatus>().Length];
        public readonly Dictionary<(string From, string To), (int Count, long Bytes)> Pairs = [];
    }

    private readonly Dictionary<RenamerFileKind, KindTally> _byKind = [];
    private readonly int _fullPathMax;
    private readonly IReadOnlyCollection<string>? _mountPoints;

    // fullPathMax is required, so a construction site cannot fold a whole library against a budget
    // nobody configured. mountPoints resolves Unix volumes; omit for the real table.
    public ScanAggregator(int fullPathMax, IReadOnlyCollection<string>? mountPoints = null)
    {
        _fullPathMax = fullPathMax;
        _mountPoints = mountPoints;
    }

    public int TotalFiles => _byKind.Values.Sum(t => t.Files);

    // Folds one entity's plan into the aggregate. Every item is counted; acting items also feed the blast
    // radius. A file id missing from sizeByFileId contributes 0 bytes.
    public void Fold(RenamerFileKind kind, RenamerPlan plan, IReadOnlyDictionary<int, long> sizeByFileId)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(sizeByFileId);

        if (!_byKind.TryGetValue(kind, out var tally))
        {
            tally = new KindTally();
            _byKind[kind] = tally;
        }

        tally.Entities++;

        foreach (var item in plan.Items)
        {
            tally.Files++;
            tally.ByStatus[(int)item.Status]++;

            if (item.Status is not (RenamerStatus.Rename or RenamerStatus.Move))
            {
                continue;
            }

            tally.ActingFiles++;
            if (VolumeClassifier.SameVolume(item.OldFullPath, item.NewFullPath, _mountPoints))
            {
                continue;
            }

            tally.CrossVolumeFiles++;

            // Counted on the pass that already visits every file. Deriving it afterwards would mean
            // retaining the items or planning the library a second time, and this class exists to keep
            // the scan's cost independent of library size. The shared predicate is called so the
            // comparison has one declaration; its status and same-volume arms are redundant at this point
            // in the fold.
            if (BatchPreview.InFlightPathOverflows(item, _fullPathMax, _mountPoints))
            {
                tally.InFlightPathOverflowFiles++;
            }

            var key = (
                From: VolumeClassifier.VolumeKey(item.OldFullPath, _mountPoints),
                To: VolumeClassifier.VolumeKey(item.NewFullPath, _mountPoints));
            var acc = tally.Pairs.GetValueOrDefault(key);
            tally.Pairs[key] = (acc.Count + 1, acc.Bytes + sizeByFileId.GetValueOrDefault(item.FileId));
        }
    }

    // Materialises what the scan persists. Kinds are emitted in RenamerFileKind order and statuses in
    // RenamerStatus declaration order, so two scans of an unchanged library produce the same string. The
    // confirm level is computed over the untruncated pair tallies: topping the itemisation must never
    // soften the confirm a move earns.
    public ScanSummary ToSummary(long completedAtUtcTicks)
    {
        var statuses = Enum.GetValues<RenamerStatus>();

        var kinds = _byKind
            .OrderBy(kv => kv.Key)
            .Select(kv =>
            {
                var tally = kv.Value;
                var pairs = ScanSummary.TopVolumePairs(tally.Pairs, out bool truncated);

                int crossCount = tally.CrossVolumeFiles;
                long crossBytes = tally.Pairs.Values.Sum(v => v.Bytes);
                var untruncated = tally.Pairs
                    .Select(p => new VolumePairDelta(p.Key.From, p.Key.To, p.Value.Count, p.Value.Bytes))
                    .ToList();

                var blastRadius = new PreviewSummary(
                    TotalCount: tally.ActingFiles,
                    SameVolumeCount: tally.ActingFiles - crossCount,
                    CrossVolumeCount: crossCount,
                    CrossVolumeBytes: crossBytes,
                    VolumePairs: pairs,
                    ConfirmLevel: BatchPreview.ClassifyConfirm(crossCount, crossBytes, untruncated),
                    InFlightPathOverflowCount: tally.InFlightPathOverflowFiles);

                return new ScanKindSummary(
                    kv.Key,
                    tally.Entities,
                    tally.Files,
                    [.. statuses.Select(s => new ScanStatusCount(s, tally.ByStatus[(int)s]))],
                    blastRadius,
                    truncated);
            })
            .ToList();

        return new ScanSummary(ScanSummary.CurrentSchemaVersion, completedAtUtcTicks, kinds);
    }
}
