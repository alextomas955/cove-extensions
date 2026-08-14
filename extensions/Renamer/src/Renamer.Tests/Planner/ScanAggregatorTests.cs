using Renamer.Contracts;
using Renamer.Execution;
using Renamer.Options;
using Renamer.Planner;

namespace Renamer.Tests.Planner;

/// <summary>
/// The incremental fold the whole-library scan replaces its per-file list with: folding one entity at a
/// time must produce the same blast radius <see cref="BatchPreview.Summarize"/> computes over the whole
/// set at once, exact per-status counts, and a retained-object count that does not grow with the number
/// of entities folded.
/// </summary>
[Trait("Tier", "L0")]
public sealed class ScanAggregatorTests
{
    // Volume identity is per-platform (VolumeClassifier): the path ROOT on Windows, the enclosing mount
    // point on Unix. A POSIX-only literal therefore has the single "\" root on Windows and every item
    // would fold as same-volume, so distinct volumes come from drive letters there and from a fixed
    // synthetic mount table here — the real table would make the cross-volume assertions
    // machine-dependent.
    private static readonly IReadOnlyCollection<string>? Mounts =
        OperatingSystem.IsWindows() ? null : ["/", "/c", "/d"];

    private static string OnVol(string vol, string name) =>
        OperatingSystem.IsWindows() ? $@"{vol}:\dir\{name}" : $"/{vol.ToLowerInvariant()}/dir/{name}";

    // The shipped path budget, read from the options rather than written as 259, and the budget every
    // aggregator below folds against. The overflow cases are positioned relative to it and to the minted
    // segment's own declaration, so a change to either moves them rather than leaving them beside the
    // boundary they claim to sit on.
    private static readonly int Budget = new RenamerOptions().FullPathMax;

    // OnVol yields "C:\dir\{name}" on Windows and "/c/dir/{name}" on Unix — SEVEN characters either way,
    // which is what lets one arithmetic land on the same absolute length on both platforms.
    private const int OnVolPrefixLength = 7;

    // A basename that makes OnVol(vol, name) exactly `total` characters long.
    private static string NameForPathLength(int total) =>
        new string('n', total - OnVolPrefixLength - ".mkv".Length) + ".mkv";

    private static string FolderOf(string path) => Path.GetDirectoryName(path)!.Replace('\\', '/');

    private static RenamerPlanItem Acting(int fileId, string oldPath, string newPath) =>
        new(fileId, oldPath, newPath, RenamerStatus.Rename, Path.GetFileName(newPath), FolderOf(newPath));

    private static RenamerPlanItem WithStatus(int fileId, RenamerStatus status)
    {
        string path = OnVol("C", $"{fileId}.mkv");
        return new(fileId, path, path, status, $"{fileId}.mkv", FolderOf(path), Reason: "seeded");
    }

    // The over-cap fixtures need one distinct volume per pair, more than the 26 drive letters Windows has.
    // A UNC path's root is its \\server\share pair, so a share per index yields unlimited distinct volume
    // keys there; on Unix the same count comes from synthetic mount points.
    private static string OnSynthVol(int index, string name) =>
        OperatingSystem.IsWindows() ? $@"\\vt\v{index}\{name}" : $"/v{index}/{name}";

    private static IReadOnlyCollection<string>? SynthMounts(int count) =>
        OperatingSystem.IsWindows() ? null : [.. Enumerable.Range(0, count).Select(i => $"/v{i}")];

    private static RenamerPlan Plan(RenamerFileKind kind, int entityId, params RenamerPlanItem[] items) =>
        new(entityId, kind, items);

    [Fact]
    public void Fold_EntityByEntity_MatchesWholeSetSummarize()
    {
        var items = new List<RenamerPlanItem>
        {
            Acting(1, OnVol("C", "one.mkv"), OnVol("D", "One.mkv")),
            Acting(2, OnVol("C", "two.mkv"), OnVol("D", "Two.mkv")),
            Acting(3, OnVol("C", "three.mkv"), OnVol("C", "Three.mkv")),
            WithStatus(4, RenamerStatus.NoOp),
            WithStatus(5, RenamerStatus.SkipGated),
        };
        var sizes = new Dictionary<int, long> { [1] = 100, [2] = 250, [3] = 999 };

        // Summarize sees the whole set at once; the aggregator sees one entity at a time. Every member of
        // the summary is compared, the overflow count included — the two paths must not disagree about any
        // of them. What this comparison canNOT show is a count that is wrong on both paths, which is why
        // the boundary cases below transcribe their expectations by hand instead.
        var expected = BatchPreview.Summarize(items, sizes, Budget, Mounts);

        var aggregator = new ScanAggregator(Budget, Mounts);
        foreach (var item in items)
        {
            aggregator.Fold(RenamerFileKind.Video, Plan(RenamerFileKind.Video, item.FileId, item), sizes);
        }

        var actual = Assert.Single(aggregator.ToSummary(0L).Kinds).BlastRadius;

        Assert.Equal(expected.TotalCount, actual.TotalCount);
        Assert.Equal(expected.SameVolumeCount, actual.SameVolumeCount);
        Assert.Equal(expected.CrossVolumeCount, actual.CrossVolumeCount);
        Assert.Equal(expected.CrossVolumeBytes, actual.CrossVolumeBytes);
        Assert.Equal(expected.ConfirmLevel, actual.ConfirmLevel);
        Assert.Equal(expected.InFlightPathOverflowCount, actual.InFlightPathOverflowCount);
        // The pair LIST is compared order-insensitively: the aggregator orders by descending bytes so the
        // volume-pair cap keeps the largest movers, while Summarize keeps GroupBy encounter order.
        Assert.Equal(
            expected.VolumePairs.OrderBy(p => p.From).ThenBy(p => p.To).ToList(),
            actual.VolumePairs.OrderBy(p => p.From).ThenBy(p => p.To).ToList());
    }

    [Fact]
    public void ToSummary_StatusCountsSumToFileCount_AcrossEveryKindAndBucket()
    {
        var aggregator = new ScanAggregator(Budget, Mounts);
        int fileId = 0;
        var statuses = Enum.GetValues<RenamerStatus>();

        foreach (var kind in new[] { RenamerFileKind.Video, RenamerFileKind.Image, RenamerFileKind.Audio })
        {
            foreach (var status in statuses)
            {
                fileId++;
                aggregator.Fold(kind, Plan(kind, fileId, WithStatus(fileId, status)), new Dictionary<int, long>());
            }
        }

        var summary = aggregator.ToSummary(0L);

        Assert.Equal(3, summary.Kinds.Count);
        foreach (var kind in summary.Kinds)
        {
            Assert.Equal(statuses.Length, kind.Files);
            Assert.Equal(kind.Files, kind.StatusCounts.Sum(c => c.Count));
            Assert.Equal(statuses.Length, kind.Entities);
            Assert.Equal(statuses, kind.StatusCounts.Select(c => c.Status));
        }

        Assert.Equal(statuses.Length * 3, summary.Kinds.Sum(k => k.Files));
        Assert.Equal(ScanSummary.CurrentSchemaVersion, summary.SchemaVersion);
    }

    [Fact]
    public void ToSummary_KindWithNoEntities_IsAbsent_NotZeroFilled()
    {
        var aggregator = new ScanAggregator(Budget, Mounts);
        aggregator.Fold(
            RenamerFileKind.Image, Plan(RenamerFileKind.Image, 1, WithStatus(1, RenamerStatus.NoOp)),
            new Dictionary<int, long>());

        var summary = aggregator.ToSummary(0L);

        Assert.Equal(RenamerFileKind.Image, Assert.Single(summary.Kinds).Kind);
        Assert.DoesNotContain(summary.Kinds, k => k.Kind == RenamerFileKind.Video);
    }

    [Fact]
    public void ToSummary_MoreVolumePairsThanCap_TopsTheListButKeepsTheTotalsExact()
    {
        int overCap = ScanSummary.MaxVolumePairsPerKind + 7;

        var aggregator = new ScanAggregator(Budget, SynthMounts(overCap + 1));
        long expectedBytes = 0;
        for (int i = 0; i < overCap; i++)
        {
            int fileId = i + 1;
            long bytes = (i + 1) * 1000L;
            expectedBytes += bytes;
            aggregator.Fold(
                RenamerFileKind.Video,
                Plan(RenamerFileKind.Video, fileId,
                    Acting(fileId, OnSynthVol(i, "a.mkv"), OnSynthVol(i + 1, "A.mkv"))),
                new Dictionary<int, long> { [fileId] = bytes });
        }

        var kind = Assert.Single(aggregator.ToSummary(0L).Kinds);

        Assert.True(kind.VolumePairsTruncated);
        Assert.Equal(ScanSummary.MaxVolumePairsPerKind, kind.BlastRadius.VolumePairs.Count);
        Assert.Equal(overCap, kind.BlastRadius.CrossVolumeCount);
        Assert.Equal(expectedBytes, kind.BlastRadius.CrossVolumeBytes);
        // Largest movers survive the cap: the smallest seeded pair (1000 bytes) is dropped.
        Assert.DoesNotContain(kind.BlastRadius.VolumePairs, p => p.Bytes == 1000L);
        Assert.Contains(kind.BlastRadius.VolumePairs, p => p.Bytes == overCap * 1000L);
    }

    [Fact]
    public void ToSummary_ConfirmLevel_IsComputedOverUntruncatedPairs()
    {
        // Every pair is a single small file, so only the DESTINATION SPREAD can earn Heavy — and the
        // spread lives in the pairs the cap would drop. A confirm derived from the topped list would
        // still read Heavy here, so the sharper proof is that the untruncated cross count survives too.
        int overCap = ScanSummary.MaxVolumePairsPerKind + 5;

        var aggregator = new ScanAggregator(Budget, SynthMounts(overCap + 1));
        for (int i = 0; i < overCap; i++)
        {
            int fileId = i + 1;
            aggregator.Fold(
                RenamerFileKind.Video,
                Plan(RenamerFileKind.Video, fileId,
                    Acting(fileId, OnSynthVol(i, "a.mkv"), OnSynthVol(i + 1, "A.mkv"))),
                new Dictionary<int, long> { [fileId] = 1L });
        }

        var kind = Assert.Single(aggregator.ToSummary(0L).Kinds);

        Assert.Equal(ConfirmLevel.Heavy, kind.BlastRadius.ConfirmLevel);
        Assert.Equal(overCap, kind.BlastRadius.CrossVolumeCount);
        Assert.Equal(overCap, kind.BlastRadius.TotalCount);
        Assert.Equal(0, kind.BlastRadius.SameVolumeCount);
    }

    [Fact]
    public void ToSummary_SerializedLength_DoesNotGrowWithEntitiesFolded()
    {
        // The aggregator's retained state is asserted through the only observable proxy that cannot lie:
        // the summary it materialises. Ten and ten thousand entities of the same shape must serialize to
        // the same structure, differing only by the digits of the counts.
        static string Summarize(int entities)
        {
            var aggregator = new ScanAggregator(Budget, Mounts);
            for (int i = 1; i <= entities; i++)
            {
                aggregator.Fold(
                    RenamerFileKind.Video,
                    Plan(RenamerFileKind.Video, i, Acting(i, OnVol("C", "a.mkv"), OnVol("D", "A.mkv"))),
                    new Dictionary<int, long> { [i] = 10L });
            }

            return System.Text.Json.JsonSerializer.Serialize(
                aggregator.ToSummary(0L), PreviewContracts.PreviewResponseJsonOptions);
        }

        string small = Summarize(10);
        string large = Summarize(10_000);

        Assert.True(large.Length - small.Length < 32,
            $"aggregate grew by {large.Length - small.Length} bytes over 3 orders of magnitude more entities");
    }

    // ── The in-flight overflow count, on the streaming path and through the whole-library merge ─────

    [Fact]
    public void Fold_CountsAnOverflow_OnlyForTheCrossVolumeItemPastTheBoundary()
    {
        // The longest final path whose cross-volume copy still fits, because the copy is minted
        // CrossVolumeMover.InFlightSuffixLength characters longer beside the destination before being
        // promoted. Three items positioned around it: the boundary itself, one character past it, and a
        // SAME-volume item of that same over-boundary length — which mints no temporary name at all.
        int longestThatFits = Budget - CrossVolumeMover.InFlightSuffixLength;
        string fitsName = NameForPathLength(longestThatFits);
        string overName = NameForPathLength(longestThatFits + 1);

        // The lengths the three cases claim to have. A slip in the path arithmetic fails here rather than
        // silently moving every item to the same side of the boundary and leaving a count of 0 to pass.
        Assert.Equal(longestThatFits, OnVol("D", fitsName).Length);
        Assert.Equal(longestThatFits + 1, OnVol("D", overName).Length);
        Assert.Equal(OnVol("D", overName).Length, OnVol("C", overName).Length);

        var aggregator = new ScanAggregator(Budget, Mounts);
        var sizes = new Dictionary<int, long>();
        aggregator.Fold(
            RenamerFileKind.Video,
            Plan(RenamerFileKind.Video, 1, Acting(1, OnVol("C", "a.mkv"), OnVol("D", fitsName))), sizes);
        aggregator.Fold(
            RenamerFileKind.Video,
            Plan(RenamerFileKind.Video, 2, Acting(2, OnVol("C", "b.mkv"), OnVol("D", overName))), sizes);
        aggregator.Fold(
            RenamerFileKind.Video,
            Plan(RenamerFileKind.Video, 3, Acting(3, OnVol("C", "c.mkv"), OnVol("C", overName))), sizes);

        var blastRadius = Assert.Single(aggregator.ToSummary(0L).Kinds).BlastRadius;

        // One of three: the count is a strict subset of the cross-volume moves, which are themselves a
        // subset of the acting items. Asserting all three is what distinguishes a working counter from one
        // that counts every cross-volume item, or every item.
        Assert.Equal(1, blastRadius.InFlightPathOverflowCount);
        Assert.Equal(2, blastRadius.CrossVolumeCount);
        Assert.Equal(3, blastRadius.TotalCount);
    }

    [Fact]
    public void ScanSummaryView_OverflowCount_IsTheSumOfThePerKindCounts()
    {
        // The whole-library figure a large-library user reads. It is re-derived by summing the per-kind
        // summaries, so it is zero whenever EITHER the per-kind fold or the merge is left unwired — and a
        // count of zero reads as "no overflows" on exactly the libraries most likely to have them.
        string overName = NameForPathLength(Budget - CrossVolumeMover.InFlightSuffixLength + 1);
        string fitsName = NameForPathLength(Budget - CrossVolumeMover.InFlightSuffixLength);

        var aggregator = new ScanAggregator(Budget, Mounts);
        var sizes = new Dictionary<int, long>();
        void FoldOne(RenamerFileKind kind, int fileId, string name) => aggregator.Fold(
            kind, Plan(kind, fileId, Acting(fileId, OnVol("C", $"{fileId}.mkv"), OnVol("D", name))), sizes);

        FoldOne(RenamerFileKind.Video, 1, overName);
        FoldOne(RenamerFileKind.Image, 2, overName);
        FoldOne(RenamerFileKind.Image, 3, overName);
        FoldOne(RenamerFileKind.Audio, 4, fitsName);

        var summary = aggregator.ToSummary(0L);
        int PerKind(RenamerFileKind kind) => summary.Kinds
            .Single(k => k.Kind == kind).BlastRadius.InFlightPathOverflowCount;

        Assert.Equal(1, PerKind(RenamerFileKind.Video));
        Assert.Equal(2, PerKind(RenamerFileKind.Image));
        Assert.Equal(0, PerKind(RenamerFileKind.Audio));

        var everyKind = ScanSummaryView.From(
            summary, [RenamerFileKind.Video, RenamerFileKind.Image, RenamerFileKind.Audio]);
        Assert.Equal(3, everyKind.BlastRadius.InFlightPathOverflowCount);

        // The merge is per-kind for a permission reason (a video-only reader must not receive image
        // figures), so the count follows the kinds the caller may read rather than the whole scan.
        var videoOnly = ScanSummaryView.From(summary, [RenamerFileKind.Video]);
        Assert.Equal(1, videoOnly.BlastRadius.InFlightPathOverflowCount);
    }
}
