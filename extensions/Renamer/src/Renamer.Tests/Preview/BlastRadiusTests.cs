using Renamer.Planner;

namespace Renamer.Tests.Preview;

/// <summary>
/// Pure-string assertions for <see cref="BatchPreview"/>: the whole-batch blast-radius aggregate
/// over a planned <see cref="RenamerPlanItem"/> set (count, same/cross split, per-destination-volume
/// byte sum + count, and the scaled <see cref="ConfirmLevel"/>). Like
/// <c>FreeSpaceGuardTests</c> this needs no real second drive — only OS-aware path roots, an injected
/// FileId→size map, and arithmetic are exercised, so it runs identically on Windows and Unix.
/// </summary>
[Trait("Tier", "Unit")]
public sealed class BlastRadiusTests
{
    // OS-aware path literals so the same/cross split (VolumeClassifier) resolves on Windows and Unix.
    private static string OnVol(string vol, string name) =>
        OperatingSystem.IsWindows() ? $@"{vol}:\dir\{name}" : $"/{vol.ToLowerInvariant()}/dir/{name}";

    private static string RootOf(string vol) => Path.GetPathRoot(OnVol(vol, "x")) ?? string.Empty;

    // A minimal acting item: only the fields the aggregate reads (FileId, paths, status, target volume).
    private static RenamerPlanItem Item(
        int fileId, string oldPath, string newPath, RenamerStatus status, string targetVolume) =>
        new(fileId, oldPath, newPath, status, NewBasename: "x", TargetFolderPath: "x",
            TargetVolume: targetVolume);

    [Fact]
    public void Empty_YieldsZeroCount_AndLightConfirm()
    {
        var summary = BatchPreview.Summarize([], new Dictionary<int, long>());

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

        var summary = BatchPreview.Summarize(items, new Dictionary<int, long>());

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

        var summary = BatchPreview.Summarize(items, sizes);

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

        var summary = BatchPreview.Summarize(items, sizes);

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

        var summary = BatchPreview.Summarize(items, sizes);

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

        var summary = BatchPreview.Summarize(items, sizes);

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

        var summary = BatchPreview.Summarize(items, sizes);

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

        var summary = BatchPreview.Summarize(items, sizes);

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

        var summary = BatchPreview.Summarize(items, sizes);

        Assert.Equal(ConfirmLevel.Heavy, summary.ConfirmLevel);
    }
}
