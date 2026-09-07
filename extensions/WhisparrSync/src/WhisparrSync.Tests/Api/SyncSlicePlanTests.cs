using Ext = global::WhisparrSync.WhisparrSync;

namespace WhisparrSync.Tests.Api;

/// <summary>
/// The scene-slice partition as pure arithmetic: that it covers the library exactly once, and that its size
/// stops tracking library size.
/// </summary>
/// <remarks>
/// Everything here drives the production planner and the production boundary ordinals, so the ceiling and the
/// smallest-slice size are never restated. A test-local copy of either would keep passing after the real one
/// changed. Cove-free, so this tier also runs where no Cove checkout exists.
/// </remarks>
[Trait("Tier", "L0")]
public sealed class SyncSlicePlanTests
{
    // The ids a library of this many scenes would hold, deliberately GAPPY: they start above 1 and step by
    // more than one, the way a real library's ids look after deletions.
    private static IReadOnlyList<int> GappyIds(int count)
        => [.. Enumerable.Range(0, count).Select(i => 7 + (i * 3))];

    private static IReadOnlyList<Ext.SyncSlice> PlanFor(IReadOnlyList<int> ids)
        => Ext.PlanSceneSlices(ids.Count, [.. Ext.SceneCutOrdinals(ids.Count).Select(o => ids[o - 1])]);

    [Fact]
    public void The_slices_are_contiguous_from_zero_to_the_maximum_id()
    {
        var slices = PlanFor(GappyIds(10_000));

        Assert.True(slices.Count > 1, "a library this size should plan more than one slice");
        Assert.Equal(0, slices[0].AfterCoveId);
        Assert.Equal(int.MaxValue, slices[^1].UpToCoveId);

        for (var i = 1; i < slices.Count; i++)
        {
            // Consecutive slices SHARE the boundary id. A gap between them loses every scene in it; an overlap
            // registers those scenes twice.
            Assert.Equal(slices[i - 1].UpToCoveId, slices[i].AfterCoveId);
        }

        Assert.All(slices, s => Assert.True(s.FirstOrdinal <= s.LastOrdinal, "no slice may be empty"));
        Assert.Equal(1, slices[0].FirstOrdinal);
    }

    [Fact]
    public void Every_id_lands_in_exactly_one_slice_including_the_ids_sitting_on_the_boundaries()
    {
        var ids = GappyIds(10_000);
        var slices = PlanFor(ids);

        // The interesting ids are the ones an off-by-one would move: the boundaries themselves, their
        // neighbours, the extremes, and an id no library row carries.
        var boundaries = slices.Select(s => s.UpToCoveId).Where(id => id != int.MaxValue).ToList();
        Assert.NotEmpty(boundaries);
        var probes = new List<int> { 1, ids[0], ids[^1], int.MaxValue, ids[^1] + 1_000 };
        probes.AddRange(boundaries);
        probes.AddRange(boundaries.Select(b => b - 1));
        probes.AddRange(boundaries.Select(b => b + 1));

        foreach (var id in probes.Distinct())
        {
            var owners = slices.Count(s => id > s.AfterCoveId && id <= s.UpToCoveId);
            Assert.True(owners == 1, $"id {id} is covered by {owners} slices, not exactly one");
        }
    }

    [Fact]
    public void The_slice_count_stops_growing_once_the_ceiling_is_reached()
    {
        // Below the ceiling the count DOES vary with library size. Asserting this beside the flat case is what
        // shows the flat case is a real ceiling rather than a planner that always answers the same number.
        Assert.True(
            Ext.SceneSliceCount(1_000) < Ext.SceneSliceCount(10_000),
            "below the ceiling the slice count should still grow with the library");

        // A tenfold difference in library size, identical slice count.
        Assert.Equal(Ext.SceneSliceCount(1_000_000), Ext.SceneSliceCount(10_000_000));
        Assert.Equal(Ext.SceneSliceCount(1_000_000), Ext.SceneSliceCount(100_000_000));
    }

    [Fact]
    public void An_empty_library_plans_no_slice_at_all()
    {
        Assert.Empty(Ext.PlanSceneSlices(0, []));
        Assert.Equal(0, Ext.SceneSliceCount(0));
        Assert.Empty(Ext.SceneCutOrdinals(0));
    }

    [Fact]
    public void A_single_scene_plans_one_slice_spanning_every_id()
    {
        var slice = Assert.Single(Ext.PlanSceneSlices(1, []));

        Assert.Equal(0, slice.AfterCoveId);
        Assert.Equal(int.MaxValue, slice.UpToCoveId);
        Assert.Equal(1, slice.FirstOrdinal);
    }

    [Fact]
    public void Fewer_boundary_ids_than_asked_for_plans_fewer_slices_and_still_covers_everything()
    {
        // The count and the boundary pass are two reads; a library that shrank between them yields fewer
        // boundary ids than the ordinals asked for. The plan must degrade to fewer slices, never to a partition
        // with a hole in it.
        var slices = Ext.PlanSceneSlices(10_000, [50, 90]);

        Assert.Equal(3, slices.Count);
        Assert.Equal(0, slices[0].AfterCoveId);
        Assert.Equal(int.MaxValue, slices[^1].UpToCoveId);
        foreach (var id in new[] { 1, 50, 51, 90, 91, 1_000_000 })
        {
            Assert.Equal(1, slices.Count(s => id > s.AfterCoveId && id <= s.UpToCoveId));
        }
    }

    [Fact]
    public void The_boundary_ordinals_are_strictly_increasing_and_one_short_of_the_slice_count()
    {
        foreach (var videoCount in new[] { 1, 499, 500, 501, 5_000, 32_000, 1_000_000 })
        {
            var ordinals = Ext.SceneCutOrdinals(videoCount);
            var slices = Ext.SceneSliceCount(videoCount);

            Assert.Equal(Math.Max(0, slices - 1), ordinals.Count);
            for (var i = 1; i < ordinals.Count; i++)
            {
                Assert.True(ordinals[i] > ordinals[i - 1], $"ordinals must strictly increase at {videoCount}");
            }

            if (ordinals.Count > 0)
            {
                Assert.True(ordinals[0] >= 1 && ordinals[^1] < videoCount);
            }
        }
    }
}
