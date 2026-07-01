using Renamer.Execution;

namespace Renamer.Tests.Execution;

/// <summary>
/// Pure-string assertions for <see cref="FreeSpaceGuard"/>: the per-destination-volume byte sum
/// (excluding same-volume moves), the headroom margin, and the (src,dst)-pair partition. The
/// free-space probe is an injected <see cref="Func{T,TResult}"/> returning controlled bytes per
/// volume, so these run identically on any host with NO real second drive — only path roots and
/// arithmetic are exercised.
/// </summary>
[Trait("Tier", "Unit")]
public sealed class FreeSpaceGuardTests
{
    // OS-aware path literals so the same/cross split (VolumeClassifier) resolves on Windows and Unix.
    private static string OnVol(string vol, string name) =>
        OperatingSystem.IsWindows() ? $@"{vol}:\dir\{name}" : $"/{vol.ToLowerInvariant()}/dir/{name}";

    private static string RootOf(string vol) => Path.GetPathRoot(OnVol(vol, "x")) ?? string.Empty;

    [Fact]
    public void CrossVolumeBatch_OverBudget_ReportsShortfallForThatVolume()
    {
        // 3 GiB of cross-drive moves onto a volume that only has 2 GiB free → shortfall.
        var moves = new[]
        {
            (OnVol("C", "a.mkv"), OnVol("D", "a.mkv"), 2L << 30),
            (OnVol("C", "b.mkv"), OnVol("D", "b.mkv"), 1L << 30),
        };

        var result = FreeSpaceGuard.Shortfall(moves, headroomBytes: 0, _ => 2L << 30);

        var entry = Assert.Single(result);
        Assert.Equal(RootOf("D"), entry.Volume);
        Assert.Equal(3L << 30, entry.Needed);
        Assert.True(entry.Needed > entry.Available, "an over-budget volume must report Needed > Available");
    }

    [Fact]
    public void CrossVolumeBatch_FitsWithRoom_ReturnsEmptyShortfall()
    {
        var moves = new[]
        {
            (OnVol("C", "a.mkv"), OnVol("D", "a.mkv"), 1L << 30),
        };

        // 1 GiB need + 0 headroom vs 10 GiB available → fits.
        var result = FreeSpaceGuard.Shortfall(moves, headroomBytes: 0, _ => 10L << 30);

        Assert.Empty(result);
    }

    [Fact]
    public void SameVolumeOnlyBatch_ReturnsEmptyShortfall_EvenWithTinyAvailable()
    {
        // Regression: same-volume moves consume ~no extra space and must NEVER be summed.
        var moves = new[]
        {
            (OnVol("C", "a.mkv"), OnVol("C", "renamed.mkv"), 5L << 30),
            (OnVol("C", "b.mkv"), OnVol("C", "moved.mkv"), 5L << 30),
        };

        // Probe returns 1 byte for every volume; if same-volume bytes were summed this would fail.
        var result = FreeSpaceGuard.Shortfall(moves, headroomBytes: 0, _ => 1L);

        Assert.Empty(result);
    }

    [Fact]
    public void MixedBatch_CountsOnlyCrossVolumeBytes()
    {
        // One same-volume move (5 GiB, must be dropped) + one cross-volume move (1 GiB, must count).
        var moves = new[]
        {
            (OnVol("C", "stay.mkv"), OnVol("C", "stay-renamed.mkv"), 5L << 30),   // same-volume → excluded
            (OnVol("C", "go.mkv"), OnVol("D", "go.mkv"), 1L << 30),               // cross-volume → counts
        };

        // Dest volume D has exactly 1 GiB free; the 1 GiB cross-move fits (no headroom), and the
        // 5 GiB same-volume move must NOT push it over budget.
        var result = FreeSpaceGuard.Shortfall(moves, headroomBytes: 0, _ => 1L << 30);

        Assert.Empty(result);
    }

    [Fact]
    public void PerVolumeGrouping_SumsVolumesIndependently_ReportsOnlyOverBudget()
    {
        var moves = new[]
        {
            (OnVol("C", "big.mkv"), OnVol("D", "big.mkv"), 5L << 30),    // → D, over budget
            (OnVol("C", "small.mkv"), OnVol("E", "small.mkv"), 1L << 30), // → E, under budget
        };

        // D and E each have 2 GiB free: D needs 5 GiB (over), E needs 1 GiB (under).
        var result = FreeSpaceGuard.Shortfall(moves, headroomBytes: 0, _ => 2L << 30);

        var entry = Assert.Single(result);
        Assert.Equal(RootOf("D"), entry.Volume);
    }

    [Fact]
    public void Headroom_IsEnforced_FitsRawBytesButNotBytesPlusHeadroom()
    {
        var moves = new[]
        {
            (OnVol("C", "a.mkv"), OnVol("D", "a.mkv"), 1L << 30),   // 1 GiB raw
        };

        // 1 GiB raw fits 1 GiB available; but +1 GiB headroom = 2 GiB need > 1 GiB available → shortfall.
        var result = FreeSpaceGuard.Shortfall(moves, headroomBytes: 1L << 30, _ => 1L << 30);

        var entry = Assert.Single(result);
        Assert.Equal(2L << 30, entry.Needed);
        Assert.True(entry.Needed > entry.Available, "headroom must be added before the comparison");
    }

    [Fact]
    public void PartitionByPair_GroupsCrossVolumeBySrcDestPair_SameVolumeUnthrottled()
    {
        var moves = new[]
        {
            (OnVol("C", "a.mkv"), OnVol("D", "a.mkv"), 1L),   // C→D
            (OnVol("C", "b.mkv"), OnVol("D", "b.mkv"), 1L),   // C→D (same pair)
            (OnVol("C", "c.mkv"), OnVol("E", "c.mkv"), 1L),   // C→E (different pair)
            (OnVol("C", "x.mkv"), OnVol("C", "y.mkv"), 1L),   // same-volume → unthrottled group
        };

        var groups = FreeSpaceGuard.PartitionByPair(moves);

        // Three groups: C→D (2 moves), C→E (1 move), and the same-volume unthrottled group (1 move).
        Assert.Equal(3, groups.Count);

        var cToD = Assert.Single(groups, g => g.Pair == (RootOf("C"), RootOf("D")));
        Assert.Equal(2, cToD.Moves.Count);

        var cToE = Assert.Single(groups, g => g.Pair == (RootOf("C"), RootOf("E")));
        Assert.Single(cToE.Moves);

        var sameVol = Assert.Single(groups, g => g.Pair == FreeSpaceGuard.SameVolumePair);
        Assert.Single(sameVol.Moves);
    }
}
