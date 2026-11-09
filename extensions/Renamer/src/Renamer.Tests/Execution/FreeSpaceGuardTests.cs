using Renamer.Execution;
using static Renamer.Tests.TestSupport.VolumePaths;

namespace Renamer.Tests.Execution;

public sealed class FreeSpaceGuardTests
{
    [Theory]
    [MemberData(nameof(Batches))]
    public void Shortfall_SumsOnlyCrossVolumeBytes_AgainstTheDestinationVolumesFreeSpacePlusHeadroom(
        (string OldFullPath, string NewFullPath, long SizeBytes)[] moves,
        long headroomBytes,
        long available,
        string? expectedVolume,
        long expectedNeeded)
    {
        var result = FreeSpaceGuard.Shortfall(moves, headroomBytes, _ => available, Mounts);

        if (expectedVolume is null)
        {
            Assert.Empty(result);
            return;
        }

        var entry = Assert.Single(result);
        Assert.Equal(RootOf(expectedVolume), entry.Volume);
        Assert.Equal(expectedNeeded, entry.Needed);
    }

    public static TheoryData<(string OldFullPath, string NewFullPath, long SizeBytes)[], long, long, string?, long>
        Batches() => new()
    {
        {
            [
                (OnVol("C", "a.mkv"), OnVol("D", "a.mkv"), 2L << 30),
                (OnVol("C", "b.mkv"), OnVol("D", "b.mkv"), 1L << 30),
            ],
            0, 2L << 30, "D", 3L << 30
        },

        { [(OnVol("C", "a.mkv"), OnVol("D", "a.mkv"), 1L << 30)], 0, 10L << 30, null, 0 },

        { [(OnVol("C", "a.mkv"), OnVol("D", "a.mkv"), 1L << 30)], 1L << 30, 1L << 30, "D", 2L << 30 },

        // The probe returns 1 byte for every volume, so summing these same-volume moves would report
        // a shortfall.
        {
            [
                (OnVol("C", "a.mkv"), OnVol("C", "renamed.mkv"), 5L << 30),
                (OnVol("C", "b.mkv"), OnVol("C", "moved.mkv"), 5L << 30),
            ],
            0, 1L, null, 0
        },

        // Mixed: dest volume D has exactly the 1 GiB the cross-volume move needs, so the 5 GiB
        // same-volume move must not push it over budget.
        {
            [
                (OnVol("C", "stay.mkv"), OnVol("C", "stay-renamed.mkv"), 5L << 30),
                (OnVol("C", "go.mkv"), OnVol("D", "go.mkv"), 1L << 30),
            ],
            0, 1L << 30, null, 0
        },
    };

    [Fact]
    public void PerVolumeGrouping_SumsVolumesIndependently_ReportsOnlyOverBudget()
    {
        var moves = new[]
        {
            (OnVol("C", "big.mkv"), OnVol("D", "big.mkv"), 5L << 30),
            (OnVol("C", "small.mkv"), OnVol("E", "small.mkv"), 1L << 30),
        };

        // D and E each have 2 GiB free: D needs 5 GiB (over), E needs 1 GiB (under).
        var result = FreeSpaceGuard.Shortfall(moves, headroomBytes: 0, _ => 2L << 30, Mounts);

        var entry = Assert.Single(result);
        Assert.Equal(RootOf("D"), entry.Volume);
    }

    [Fact]
    public void PartitionByPair_GroupsCrossVolumeBySrcDestPair_SameVolumeUnthrottled()
    {
        var moves = new[]
        {
            (OnVol("C", "a.mkv"), OnVol("D", "a.mkv"), 1L),
            (OnVol("C", "b.mkv"), OnVol("D", "b.mkv"), 1L),
            (OnVol("C", "c.mkv"), OnVol("E", "c.mkv"), 1L),
            (OnVol("C", "x.mkv"), OnVol("C", "y.mkv"), 1L),
        };

        var groups = FreeSpaceGuard.PartitionByPair(moves, m => (m.Item1, m.Item2), Mounts);

        Assert.Equal(3, groups.Count);

        var cToD = Assert.Single(groups, g => g.Pair == (RootOf("C"), RootOf("D")));
        Assert.Equal(2, cToD.Items.Count);

        var cToE = Assert.Single(groups, g => g.Pair == (RootOf("C"), RootOf("E")));
        Assert.Single(cToE.Items);

        var sameVol = Assert.Single(groups, g => g.Pair == FreeSpaceGuard.SameVolumePair);
        Assert.Single(sameVol.Items);
    }
}
