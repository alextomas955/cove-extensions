using Renamer.Contracts;
using Renamer.Planner;

namespace Renamer.Tests.Planner;

/// <summary>
/// The incremental fold the whole-library scan replaces its per-file list with: folding one entity at a
/// time must produce the same blast radius <see cref="BatchPreview.Summarize"/> computes over the whole
/// set at once, exact per-status counts, and a retained-object count that does not grow with the number
/// of entities folded.
/// </summary>
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

    private static string FolderOf(string path) => Path.GetDirectoryName(path)!.Replace('\\', '/');

    private static RenamerPlanItem Acting(int fileId, string oldPath, string newPath) =>
        new(fileId, oldPath, newPath, RenamerStatus.Renamer, Path.GetFileName(newPath), FolderOf(newPath));

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

        var expected = BatchPreview.Summarize(items, sizes, Mounts);

        var aggregator = new ScanAggregator(Mounts);
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
        // The pair LIST is compared order-insensitively: the aggregator orders by descending bytes so the
        // volume-pair cap keeps the largest movers, while Summarize keeps GroupBy encounter order.
        Assert.Equal(
            expected.VolumePairs.OrderBy(p => p.From).ThenBy(p => p.To).ToList(),
            actual.VolumePairs.OrderBy(p => p.From).ThenBy(p => p.To).ToList());
    }

    [Fact]
    public void ToSummary_StatusCountsSumToFileCount_AcrossEveryKindAndBucket()
    {
        var aggregator = new ScanAggregator(Mounts);
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
        var aggregator = new ScanAggregator(Mounts);
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

        var aggregator = new ScanAggregator(SynthMounts(overCap + 1));
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

        var aggregator = new ScanAggregator(SynthMounts(overCap + 1));
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
            var aggregator = new ScanAggregator(Mounts);
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

    [Theory]
    [InlineData(IRevertJournal.MaxJournalledFiles, true)]
    [InlineData(IRevertJournal.MaxJournalledFiles + 1, false)]
    public void ToSummary_Undoable_TurnsOffOneActingFilePastTheCap(int actingFiles, bool undoable)
    {
        // A whole-library dry run reaches the same warning through this fold rather than through
        // BatchPreview.Summarize, so the two derive the flag from separate expressions and can drift.
        var aggregator = new ScanAggregator(Mounts);
        var items = Enumerable.Range(1, actingFiles)
            .Select(i => Acting(i, OnVol("C", $"{i}.mkv"), OnVol("C", $"r{i}.mkv")))
            .ToArray();

        aggregator.Fold(
            RenamerFileKind.Video, Plan(RenamerFileKind.Video, 1, items), new Dictionary<int, long>());

        var kind = Assert.Single(aggregator.ToSummary(0L).Kinds);

        Assert.Equal(actingFiles, kind.BlastRadius.TotalCount);
        Assert.Equal(undoable, kind.BlastRadius.Undoable);
    }
}
