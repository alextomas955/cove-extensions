using Renamer.Contracts;
using Renamer.Execution;

namespace Renamer.Planner;

/// <summary>
/// Folds a whole-library dry run one entity at a time into the bounded <see cref="ScanSummary"/> the
/// scan persists.
/// </summary>
/// <remarks>
/// The class exists to keep the scan's memory and its stored value independent of library size: it holds
/// per-kind counters and volume-pair tallies, never a per-file or per-entity collection. Any collection
/// that grows with the number of folded entities breaks that guarantee.
/// <para>
/// The same/cross split and the pair grouping read <see cref="VolumeClassifier"/>, the same source of
/// truth <see cref="BatchPreview.Summarize"/> and the free-space guard read, so the aggregate cannot
/// disagree with them about which volume a path is on.
/// </para>
/// </remarks>
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

    /// <param name="fullPathMax">
    /// The caller's <see cref="Options.RenamerOptions.FullPathMax"/>, for the in-flight overflow count.
    /// Required and first rather than optional and last: a default would let a construction site fold a
    /// whole library against a budget nobody configured, silently, which is the class of defect this
    /// count exists to surface.
    /// </param>
    /// <param name="mountPoints">Mount table to resolve Unix volumes against; omit for the real one.</param>
    public ScanAggregator(int fullPathMax, IReadOnlyCollection<string>? mountPoints = null)
    {
        _fullPathMax = fullPathMax;
        _mountPoints = mountPoints;
    }

    /// <summary>Total files folded so far, across every kind.</summary>
    public int TotalFiles => _byKind.Values.Sum(t => t.Files);

    /// <summary>Folds one entity's plan into the aggregate.</summary>
    /// <param name="kind">The planned entity's kind.</param>
    /// <param name="plan">The entity's plan; every item is counted, acting items also feed the blast radius.</param>
    /// <param name="sizeByFileId">FileId → bytes for the files of this entity; a missing id contributes 0.</param>
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

            if (item.Status is not (RenamerStatus.Renamer or RenamerStatus.Move))
            {
                continue;
            }

            tally.ActingFiles++;
            if (VolumeClassifier.SameVolume(item.OldFullPath, item.NewFullPath, _mountPoints))
            {
                continue;
            }

            tally.CrossVolumeFiles++;

            // Counted here, on the pass that already visits every file, rather than derived afterwards:
            // deriving it would mean retaining the items or planning the library a second time, and this
            // class exists to keep the scan's cost independent of library size. The predicate is CALLED
            // rather than restated — its comparison has one declaration, and a copy of it here could
            // report a count with no flagged row under it. Its status and same-volume arms are redundant
            // at this point in the fold, which is the price of reading the one comparison.
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

    /// <summary>Materialises what the scan persists.</summary>
    /// <remarks>
    /// Kinds are emitted in <see cref="RenamerFileKind"/> order and statuses in
    /// <see cref="RenamerStatus"/> declaration order, so two scans of an unchanged library produce the
    /// same string. The confirm level is computed over the UNTRUNCATED pair tallies — topping the
    /// itemisation must never soften the confirm a move earns.
    /// </remarks>
    /// <param name="completedAtUtcTicks">UTC ticks to stamp the summary with.</param>
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
                    // A count, and never the offending paths: this aggregate is stored and served for a
                    // library of unbounded size, and a per-file collection under one key is the shape that
                    // has already broken an extension's settings page here. The rows carry their own flag
                    // on the page that serves them, which is where a user needs to see WHICH file.
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
