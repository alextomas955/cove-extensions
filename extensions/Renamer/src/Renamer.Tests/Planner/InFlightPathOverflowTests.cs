using Renamer.Execution;
using Renamer.Options;
using Renamer.Planner;

namespace Renamer.Tests.Planner;

/// <summary>
/// The band <see cref="PathConfinement"/> accepts and the executor cannot fit: a cross-volume move copies
/// to a name <see cref="CrossVolumeMover.InFlightSuffixLength"/> characters longer beside the destination
/// before promoting it, while the planner budgets only the FINAL path. These cases position a destination
/// on each side of that boundary and read <see cref="BatchPreview.InFlightPathOverflows"/> plus the count
/// <see cref="BatchPreview.Summarize"/> folds from it.
/// </summary>
/// <remarks>
/// A sibling of <c>BlastRadiusTests</c> rather than part of it, so the arms that decide whether the
/// warning is trustworthy compile on the cove-absent leg, which is the leg CI runs the unit tier on.
/// PURE - path arithmetic, a synthetic mount table and no disk, so it runs identically on Windows and Unix.
/// </remarks>
public sealed class InFlightPathOverflowTests
{
    // OS-aware path literals so the same/cross split (VolumeClassifier) resolves on Windows and Unix.
    private static string OnVol(string vol, string name) =>
        OperatingSystem.IsWindows() ? $@"{vol}:\dir\{name}" : $"/{vol.ToLowerInvariant()}/dir/{name}";

    // Every Unix path shares the "/" root. These mounts carry the volume identity a cross-volume case needs,
    // standing in for the C:/D: drives the Windows literals use.
    private static readonly IReadOnlyCollection<string>? Mounts =
        OperatingSystem.IsWindows() ? null : ["/", "/c", "/d"];

    private static string RootOf(string vol) => VolumeClassifier.VolumeKey(OnVol(vol, "x"), Mounts);

    // The shipped path budget, read from the options rather than transcribed, so a change to it moves these
    // cases rather than leaving them beside a boundary they no longer sit on.
    private static readonly int Budget = new RenamerOptions().FullPathMax;

    // OnVol yields "C:\dir\{name}" on Windows and "/c/dir/{name}" on Unix - SEVEN characters either way,
    // which is what lets one arithmetic land on the same absolute length on both platforms.
    private const int OnVolPrefixLength = 7;

    // A basename that makes OnVol(vol, name) exactly `total` characters long.
    private static string NameForPathLength(int total) =>
        new string('n', total - OnVolPrefixLength - ".mkv".Length) + ".mkv";

    // A minimal acting item: only the fields the aggregate reads (FileId, paths, status, target volume).
    private static RenamerPlanItem Item(
        int fileId, string oldPath, string newPath, RenamerStatus status, string targetVolume) =>
        new(fileId, oldPath, newPath, status, NewBasename: "x", TargetFolderPath: "x",
            TargetVolume: targetVolume);

    [Fact]
    public void CrossVolumeMove_IsFlagged_OneCharacterPastTheBudgetLessTheMintedSegment()
    {
        // A cross-volume move copies to a name CrossVolumeMover.InFlightSuffixLength characters longer
        // beside the destination and promotes it, so the longest final path whose copy still fits is
        // Budget - InFlightSuffixLength. One character past that the copy overruns a REAL platform limit -
        // no "\?\" extended-length prefix is ever applied - while the planner, which budgets only the
        // final path, accepted the plan the user is about to approve.
        int longestThatFits = Budget - CrossVolumeMover.InFlightSuffixLength;

        var fits = Item(
            1, OnVol("C", "a.mkv"), OnVol("D", NameForPathLength(longestThatFits)),
            RenamerStatus.Move, RootOf("D"));
        var overflows = Item(
            2, OnVol("C", "b.mkv"), OnVol("D", NameForPathLength(longestThatFits + 1)),
            RenamerStatus.Move, RootOf("D"));

        // The lengths the two cases claim to have, so a slip in the path arithmetic fails here rather than
        // silently moving both items to the same side of the boundary.
        Assert.Equal(longestThatFits, fits.NewFullPath.Length);
        Assert.Equal(longestThatFits + 1, overflows.NewFullPath.Length);

        Assert.False(BatchPreview.InFlightPathOverflows(fits, Budget, Mounts));
        Assert.True(BatchPreview.InFlightPathOverflows(overflows, Budget, Mounts));

        var summary = BatchPreview.Summarize([fits, overflows], new Dictionary<int, long>(), Budget, Mounts);

        Assert.Equal(1, summary.InFlightPathOverflowCount);
    }

    [Fact]
    public void SameVolumeMove_OfTheIdenticalOverBoundaryLength_IsNotFlagged()
    {
        // The arm that decides whether the warning is trustworthy. DiskMover mints no temporary name at
        // all - a same-volume move is one atomic rename - so an identical path length that overruns when
        // copied across drives is perfectly fine in place. A warning here would fire on a correct plan,
        // and most plans are same-volume.
        string overLength = NameForPathLength(Budget - CrossVolumeMover.InFlightSuffixLength + 1);

        var sameVolume = Item(
            1, OnVol("C", "a.mkv"), OnVol("C", overLength), RenamerStatus.Renamer, RootOf("C"));
        var crossVolume = Item(
            2, OnVol("C", "b.mkv"), OnVol("D", overLength), RenamerStatus.Move, RootOf("D"));

        // The two destinations are the same length, so the only difference between them is the volume.
        Assert.Equal(crossVolume.NewFullPath.Length, sameVolume.NewFullPath.Length);

        Assert.False(BatchPreview.InFlightPathOverflows(sameVolume, Budget, Mounts));
        Assert.True(BatchPreview.InFlightPathOverflows(crossVolume, Budget, Mounts));

        var summary = BatchPreview.Summarize(
            [sameVolume, crossVolume], new Dictionary<int, long>(), Budget, Mounts);

        Assert.Equal(1, summary.InFlightPathOverflowCount);
    }

    [Fact]
    public void ANonActingItemPastTheBoundary_IsNotFlagged()
    {
        // A skip copies nothing, so it has no in-flight path to overrun, and it already carries its own
        // reason. Two warnings on one row, one of which describes work that will not happen, is noise.
        var skipped = Item(
            1,
            OnVol("C", "a.mkv"),
            OnVol("D", NameForPathLength(Budget - CrossVolumeMover.InFlightSuffixLength + 1)),
            RenamerStatus.SkipCollision,
            RootOf("D"));

        Assert.False(BatchPreview.InFlightPathOverflows(skipped, Budget, Mounts));

        var summary = BatchPreview.Summarize([skipped], new Dictionary<int, long>(), Budget, Mounts);

        Assert.Equal(0, summary.InFlightPathOverflowCount);
    }
}
