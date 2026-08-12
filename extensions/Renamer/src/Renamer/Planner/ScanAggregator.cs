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
        public readonly int[] ByStatus = new int[Enum.GetValues<RenamerStatus>().Length];
        public readonly Dictionary<(string From, string To), (int Count, long Bytes)> Pairs = [];
    }

    private readonly Dictionary<RenamerFileKind, KindTally> _byKind = [];
    private readonly IReadOnlyCollection<string>? _mountPoints;

    /// <param name="mountPoints">Mount table to resolve Unix volumes against; omit for the real one.</param>
    public ScanAggregator(IReadOnlyCollection<string>? mountPoints = null) => _mountPoints = mountPoints;

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
                    // Zero, not a tally: the scan folds one file at a time and this aggregate keeps no
                    // per-file collection, so counting the overflows needs a counter on the per-kind tally
                    // that does not exist yet. Plan 29-06 adds it and populates this from it.
                    InFlightPathOverflowCount: 0);

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
