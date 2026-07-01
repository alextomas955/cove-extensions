using Renamer.Options;
using Renamer.Planner;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Planner;

/// <summary>
/// Every file of a multi-file item is planned independently — one
/// <see cref="RenamerPlanItem"/> per file, no first-file-only assumption, none dropped.
/// </summary>
public sealed class MultiFileTests
{
    private static RenamerFile File(int id, string basename) =>
        new(FileId: id, Kind: RenamerFileKind.Video, Basename: basename, ParentFolderId: 5,
            ParentFolderPath: "media/videos", Format: "mkv");

    [Fact]
    public async Task TwoFileItem_ProducesTwoItems_OnePerFile()
    {
        var port = new FakeRenamerDataPort();
        port.SeedEntity(new RenamerEntity(
            EntityId: 10, Kind: RenamerFileKind.Video, Title: "My Film", Code: null, StudioName: null,
            Date: null, Organized: true, Performers: [], Tags: [],
            Files: [File(1, "part1.mkv"), File(2, "part2.mkv")]));
        var planner = new RenamerPlanner(port);

        var plan = await planner.PlanAsync(RenamerFileKind.Video, 10, new RenamerOptions(), default);

        Assert.Equal(2, plan.Items.Count);
        Assert.Contains(plan.Items, i => i.FileId == 1);
        Assert.Contains(plan.Items, i => i.FileId == 2);
        Assert.Empty(port.SaveCalls);
    }
}
