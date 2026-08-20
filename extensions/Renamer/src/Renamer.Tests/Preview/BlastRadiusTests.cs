using Renamer.Execution;
using Renamer.Options;
using Renamer.Planner;

namespace Renamer.Tests.Preview;

/// <summary>
/// Pure-string assertions for <see cref="BatchPreview"/>: the whole-batch blast-radius aggregate
/// over a planned <see cref="RenamerPlanItem"/> set (count, same/cross split, per-destination-volume
/// byte sum + count, and the scaled <see cref="ConfirmLevel"/>). Like
/// <c>FreeSpaceGuardTests</c> this needs no real second drive — only OS-aware path roots, an injected
/// FileId→size map, and arithmetic are exercised, so it runs identically on Windows and Unix.
/// </summary>
[Trait("Tier", "L0")]
public sealed class BlastRadiusTests
{
    // OS-aware path literals so the same/cross split (VolumeClassifier) resolves on Windows and Unix.
    private static string OnVol(string vol, string name) =>
        OperatingSystem.IsWindows() ? $@"{vol}:\dir\{name}" : $"/{vol.ToLowerInvariant()}/dir/{name}";

    // Every Unix path shares the "/" root. These mounts carry the volume identity a cross-volume test needs,
    // standing in for the C:/D:/E: drives the Windows literals use.
    private static readonly IReadOnlyCollection<string>? Mounts =
        OperatingSystem.IsWindows() ? null : ["/", "/c", "/d", "/e"];

    private static string RootOf(string vol) => VolumeClassifier.VolumeKey(OnVol(vol, "x"), Mounts);

    // The shipped path budget, read from the options rather than written as 259: every case below passes it
    // to Summarize, and the in-flight overflow cases are positioned relative to it.
    private static readonly int Budget = new RenamerOptions().FullPathMax;

    // OnVol yields "C:\dir\{name}" on Windows and "/c/dir/{name}" on Unix — SEVEN characters either way,
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
    public void Empty_YieldsZeroCount_AndLightConfirm()
    {
        var summary = BatchPreview.Summarize([], new Dictionary<int, long>(), Budget, Mounts);

        Assert.Equal(0, summary.TotalCount);
        Assert.Equal(0, summary.SameVolumeCount);
        Assert.Equal(0, summary.CrossVolumeCount);
        Assert.Equal(0, summary.CrossVolumeBytes);
        Assert.Empty(summary.VolumePairs);
        Assert.Equal(ConfirmLevel.Light, summary.ConfirmLevel);
    }

    [Fact]
    public void NonActingItems_AreExcludedFromEveryAggregate()
    {
        var items = new[]
        {
            Item(1, OnVol("C", "a.mkv"), OnVol("C", "a.mkv"), RenamerStatus.NoOp, ""),
            Item(2, OnVol("C", "b.mkv"), OnVol("D", "b.mkv"), RenamerStatus.SkipGated, RootOf("D")),
            Item(3, OnVol("C", "c.mkv"), OnVol("D", "c.mkv"), RenamerStatus.SkipCollision, RootOf("D")),
        };

        var summary = BatchPreview.Summarize(items, new Dictionary<int, long>(), Budget, Mounts);

        Assert.Equal(0, summary.TotalCount);
        Assert.Empty(summary.VolumePairs);
        Assert.Equal(ConfirmLevel.Light, summary.ConfirmLevel);
    }

    [Fact]
    public void SameVolumeOnlyBatch_IsLight_WithNoCrossBytesOrPairs()
    {
        var items = new[]
        {
            Item(1, OnVol("C", "a.mkv"), OnVol("C", "an.mkv"), RenamerStatus.Renamer, RootOf("C")),
            Item(2, OnVol("C", "b.mkv"), OnVol("C", "bn.mkv"), RenamerStatus.Renamer, RootOf("C")),
            Item(3, OnVol("C", "c.mkv"), OnVol("C", "cn.mkv"), RenamerStatus.Move, RootOf("C")),
        };
        var sizes = new Dictionary<int, long> { [1] = 5L << 30, [2] = 5L << 30, [3] = 5L << 30 };

        var summary = BatchPreview.Summarize(items, sizes, Budget, Mounts);

        Assert.Equal(3, summary.TotalCount);
        Assert.Equal(3, summary.SameVolumeCount);
        Assert.Equal(0, summary.CrossVolumeCount);
        Assert.Equal(0, summary.CrossVolumeBytes);
        Assert.Empty(summary.VolumePairs);
        // Same-drive renames are cheap/reversible — never escalate beyond Light regardless of size.
        Assert.Equal(ConfirmLevel.Light, summary.ConfirmLevel);
    }

    [Fact]
    public void CrossVolumePairs_GroupByFromTo_WithCorrectCountAndSummedBytes()
    {
        var items = new[]
        {
            Item(1, OnVol("C", "a.mkv"), OnVol("D", "a.mkv"), RenamerStatus.Move, RootOf("D")),
            Item(2, OnVol("C", "b.mkv"), OnVol("D", "b.mkv"), RenamerStatus.Move, RootOf("D")),
            Item(3, OnVol("C", "c.mkv"), OnVol("E", "c.mkv"), RenamerStatus.Move, RootOf("E")),
        };
        var sizes = new Dictionary<int, long> { [1] = 1L << 30, [2] = 2L << 30, [3] = 4L << 30 };

        var summary = BatchPreview.Summarize(items, sizes, Budget, Mounts);

        Assert.Equal(3, summary.TotalCount);
        Assert.Equal(0, summary.SameVolumeCount);
        Assert.Equal(3, summary.CrossVolumeCount);
        Assert.Equal(7L << 30, summary.CrossVolumeBytes);

        var cToD = Assert.Single(summary.VolumePairs, p => p.From == RootOf("C") && p.To == RootOf("D"));
        Assert.Equal(2, cToD.Count);
        Assert.Equal(3L << 30, cToD.Bytes);

        var cToE = Assert.Single(summary.VolumePairs, p => p.From == RootOf("C") && p.To == RootOf("E"));
        Assert.Equal(1, cToE.Count);
        Assert.Equal(4L << 30, cToE.Bytes);
    }

    [Fact]
    public void MixedBatch_ExcludesSameVolumeFromCrossSums()
    {
        var items = new[]
        {
            Item(1, OnVol("C", "stay.mkv"), OnVol("C", "stayn.mkv"), RenamerStatus.Renamer, RootOf("C")),
            Item(2, OnVol("C", "go.mkv"), OnVol("D", "go.mkv"), RenamerStatus.Move, RootOf("D")),
        };
        var sizes = new Dictionary<int, long> { [1] = 9L << 30, [2] = 1L << 30 };

        var summary = BatchPreview.Summarize(items, sizes, Budget, Mounts);

        Assert.Equal(2, summary.TotalCount);
        Assert.Equal(1, summary.SameVolumeCount);
        Assert.Equal(1, summary.CrossVolumeCount);
        // The 9 GiB same-volume move is NOT counted; only the 1 GiB cross-volume move.
        Assert.Equal(1L << 30, summary.CrossVolumeBytes);
        var pair = Assert.Single(summary.VolumePairs);
        Assert.Equal(1L << 30, pair.Bytes);
    }

    [Fact]
    public void ModestCrossVolumeMove_IsStandard()
    {
        // A handful of items, a few GiB, ONE destination volume → Standard (not Light, not Heavy).
        var items = new[]
        {
            Item(1, OnVol("C", "a.mkv"), OnVol("D", "a.mkv"), RenamerStatus.Move, RootOf("D")),
            Item(2, OnVol("C", "b.mkv"), OnVol("D", "b.mkv"), RenamerStatus.Move, RootOf("D")),
        };
        var sizes = new Dictionary<int, long> { [1] = 1L << 30, [2] = 1L << 30 };

        var summary = BatchPreview.Summarize(items, sizes, Budget, Mounts);

        Assert.Equal(ConfirmLevel.Standard, summary.ConfirmLevel);
    }

    [Fact]
    public void LargeByItemCount_IsHeavy()
    {
        // >= 50 cross-volume items → Heavy by item count alone (tiny bytes, one volume).
        var items = Enumerable.Range(1, 50)
            .Select(i => Item(i, OnVol("C", $"f{i}.mkv"), OnVol("D", $"f{i}.mkv"), RenamerStatus.Move, RootOf("D")))
            .ToArray();
        var sizes = items.ToDictionary(i => i.FileId, _ => 1L);

        var summary = BatchPreview.Summarize(items, sizes, Budget, Mounts);

        Assert.Equal(ConfirmLevel.Heavy, summary.ConfirmLevel);
    }

    [Fact]
    public void LargeByBytes_IsHeavy()
    {
        // A single cross-volume move of >= 10 GiB → Heavy by byte total.
        var items = new[]
        {
            Item(1, OnVol("C", "huge.mkv"), OnVol("D", "huge.mkv"), RenamerStatus.Move, RootOf("D")),
        };
        var sizes = new Dictionary<int, long> { [1] = 10L << 30 };

        var summary = BatchPreview.Summarize(items, sizes, Budget, Mounts);

        Assert.Equal(ConfirmLevel.Heavy, summary.ConfirmLevel);
    }

    [Fact]
    public void MultipleDistinctDestinationVolumes_IsHeavy()
    {
        // Two small cross-volume moves to TWO different destination volumes → Heavy by volume count.
        var items = new[]
        {
            Item(1, OnVol("C", "a.mkv"), OnVol("D", "a.mkv"), RenamerStatus.Move, RootOf("D")),
            Item(2, OnVol("C", "b.mkv"), OnVol("E", "b.mkv"), RenamerStatus.Move, RootOf("E")),
        };
        var sizes = new Dictionary<int, long> { [1] = 1L, [2] = 1L };

        var summary = BatchPreview.Summarize(items, sizes, Budget, Mounts);

        Assert.Equal(ConfirmLevel.Heavy, summary.ConfirmLevel);
    }

    [Fact]
    public void CrossVolumeMove_IsFlagged_OneCharacterPastTheBudgetLessTheMintedSegment()
    {
        // A cross-volume move copies to a name CrossVolumeMover.InFlightSuffixLength characters longer
        // beside the destination and promotes it, so the longest final path whose copy still fits is
        // Budget - InFlightSuffixLength. One character past that the copy overruns a REAL platform limit —
        // no "\\?\" extended-length prefix is ever applied — while the planner, which budgets only the
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
        // all — a same-volume move is one atomic rename — so an identical path length that overruns when
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
