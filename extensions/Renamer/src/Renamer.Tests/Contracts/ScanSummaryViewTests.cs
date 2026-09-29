using Renamer.Contracts;
using Renamer.Planner;

namespace Renamer.Tests.Contracts;

public sealed class ScanSummaryViewTests
{
    private static ScanKindSummary KindWithOverflows(RenamerFileKind kind, int overflows) => new(
        kind,
        Entities: 1,
        Files: 1,
        StatusCounts: [new ScanStatusCount(RenamerStatus.Move, 1)],
        BlastRadius: new PreviewSummary(
            TotalCount: 1,
            SameVolumeCount: 0,
            CrossVolumeCount: 1,
            CrossVolumeBytes: 0,
            VolumePairs: [],
            ConfirmLevel: ConfirmLevel.Standard,
            InFlightPathOverflowCount: overflows),
        VolumePairsTruncated: false);

    private static readonly ScanSummary ThreeKinds = new(
        ScanSummary.CurrentSchemaVersion,
        CompletedAtUtcTicks: 1,
        Kinds:
        [
            KindWithOverflows(RenamerFileKind.Video, 1),
            KindWithOverflows(RenamerFileKind.Image, 2),
            KindWithOverflows(RenamerFileKind.Audio, 0),
        ]);

    [Fact]
    public void OverflowCount_IsTheSumOfTheReadableKindsCounts()
    {
        var everyKind = ScanSummaryView.From(
            ThreeKinds, [RenamerFileKind.Video, RenamerFileKind.Image, RenamerFileKind.Audio]);

        Assert.Equal(3, everyKind.BlastRadius.InFlightPathOverflowCount);
    }

    [Fact]
    public void OverflowCount_LeavesOutTheKindsTheCallerCannotRead()
    {
        // A video-only reader must not receive image figures.
        var videoOnly = ScanSummaryView.From(ThreeKinds, [RenamerFileKind.Video]);

        Assert.Equal(1, videoOnly.BlastRadius.InFlightPathOverflowCount);
    }

    private static ScanKindSummary KindMoving(RenamerFileKind kind, params VolumePairDelta[] pairs) => new(
        kind,
        Entities: 1,
        Files: pairs.Sum(p => p.Count),
        StatusCounts: [new ScanStatusCount(RenamerStatus.Move, pairs.Sum(p => p.Count))],
        BlastRadius: new PreviewSummary(
            TotalCount: pairs.Sum(p => p.Count),
            SameVolumeCount: 0,
            CrossVolumeCount: pairs.Sum(p => p.Count),
            CrossVolumeBytes: pairs.Sum(p => p.Bytes),
            VolumePairs: pairs,
            ConfirmLevel: ConfirmLevel.Standard,
            InFlightPathOverflowCount: 0),
        VolumePairsTruncated: false);

    [Fact]
    public void VolumePairs_OfTheReadableKinds_MergeIntoOnePairPerRoute()
    {
        var summary = new ScanSummary(
            ScanSummary.CurrentSchemaVersion,
            CompletedAtUtcTicks: 1,
            Kinds:
            [
                KindMoving(RenamerFileKind.Video, new VolumePairDelta("/a", "/b", 2, 10)),
                KindMoving(RenamerFileKind.Image, new VolumePairDelta("/a", "/b", 3, 20), new VolumePairDelta("/a", "/c", 1, 5)),
                KindMoving(RenamerFileKind.Audio, new VolumePairDelta("/a", "/b", 100, 1000)),
            ]);

        var view = ScanSummaryView.From(summary, [RenamerFileKind.Video, RenamerFileKind.Image]);

        Assert.Equal(
            [new VolumePairDelta("/a", "/b", 5, 30), new VolumePairDelta("/a", "/c", 1, 5)],
            view.BlastRadius.VolumePairs.OrderBy(p => p.To));
        Assert.False(view.VolumePairsTruncated);
    }
}
