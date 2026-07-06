using Renamer.Options;
using Renamer.Planner;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Concurrency;

/// <summary>
/// Locks PHASE A's load de-duplication: the batch runner reads each file's free-space
/// <c>SizeBytes</c> off the entity the planner already loaded (via
/// <c>RenamerPlanner.PlanWithEntityAsync</c>) instead of loading it a second time. The load
/// counter on <see cref="FakeRenamerDataPort"/> is the seam every PHASE A load flows through, so
/// N ids must produce exactly N loads (not 2N), and the surfaced entity must still carry the seeded
/// sizes so the de-dup cannot silently drop them.
/// </summary>
public sealed class PhaseALoadOnceTests
{
    private static RenamerFile File(int id, string basename, long sizeBytes) =>
        new(FileId: id, Kind: RenamerFileKind.Video, Basename: basename, ParentFolderId: 5,
            ParentFolderPath: "media/videos", Format: "mkv", SizeBytes: sizeBytes);

    [Fact]
    public async Task PhaseA_LoadsEachEntityExactlyOnce()
    {
        const int n = 4;
        var port = new FakeRenamerDataPort();
        for (int i = 1; i <= n; i++)
        {
            port.SeedEntity(new RenamerEntity(
                EntityId: i, Kind: RenamerFileKind.Video, Title: $"Film {i}", Code: null, StudioName: null,
                Date: null, Organized: true, Performers: [], Tags: [],
                Files: [File(i, $"raw {i}.mkv", sizeBytes: 100L * i)]));
        }
        var planner = new RenamerPlanner(port);

        for (int i = 1; i <= n; i++)
        {
            _ = await planner.PlanWithEntityAsync(RenamerFileKind.Video, i, new RenamerOptions(), default);
        }

        Assert.Equal(n, port.LoadEntityCallCount);
    }

    [Fact]
    public async Task PhaseA_SurfacesSeededSizesForTheFreeSpaceSum()
    {
        var port = new FakeRenamerDataPort();
        port.SeedEntity(new RenamerEntity(
            EntityId: 10, Kind: RenamerFileKind.Video, Title: "My Film", Code: null, StudioName: null,
            Date: null, Organized: true, Performers: [], Tags: [],
            Files: [File(1, "part1.mkv", sizeBytes: 1234), File(2, "part2.mkv", sizeBytes: 5678)]));
        var planner = new RenamerPlanner(port);

        var (_, entity) = await planner.PlanWithEntityAsync(
            RenamerFileKind.Video, 10, new RenamerOptions(), default);

        Assert.Equal(1, port.LoadEntityCallCount);
        Assert.NotNull(entity);
        var sizeByFileId = entity!.Files.ToDictionary(f => f.FileId, f => f.SizeBytes);
        Assert.Equal(1234, sizeByFileId[1]);
        Assert.Equal(5678, sizeByFileId[2]);
        // A file id absent from the map contributes 0 to the free-space sum, exactly as before.
        Assert.Equal(0, sizeByFileId.GetValueOrDefault(999));
    }

    [Fact]
    public async Task PhaseA_MissingEntity_LoadsOnce_YieldsNullEntityAndEmptyPlan()
    {
        var port = new FakeRenamerDataPort();
        var planner = new RenamerPlanner(port);

        var (plan, entity) = await planner.PlanWithEntityAsync(
            RenamerFileKind.Video, 42, new RenamerOptions(), default);

        Assert.Equal(1, port.LoadEntityCallCount);
        Assert.Null(entity);
        Assert.Empty(plan.Items);
    }
}
