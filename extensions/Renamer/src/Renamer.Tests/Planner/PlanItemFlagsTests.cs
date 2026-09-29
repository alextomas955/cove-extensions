using Renamer.Options;
using Renamer.Planner;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Planner;

public sealed class PlanItemFlagsTests
{
    private static RenamerFile File(int id, string basename, int folderId = 5) =>
        new(FileId: id, Kind: RenamerFileKind.Video, Basename: basename, ParentFolderId: folderId,
            ParentFolderPath: "media/videos", Format: "mkv");

    private static RenamerEntity Entity(string title, params RenamerFile[] files) =>
        new(EntityId: 10, Kind: RenamerFileKind.Video, Title: title, Code: null, StudioName: null,
            Date: null, Organized: true, Performers: [], TagRefs: [], Files: files);

    [Fact]
    public async Task FreeCleanName_IsNeitherSuffixedNorSanitized()
    {
        var port = new FakeRenamerDataPort();
        port.SeedEntity(Entity("My Film", File(1, "raw.mkv")));
        var planner = new RenamerPlanner(port);

        var plan = await planner.PlanAsync(RenamerFileKind.Video, 10, new RenamerOptions(), default);

        var item = Assert.Single(plan.Items);
        Assert.Equal(RenamerStatus.Rename, item.Status);
        Assert.False(item.Suffixed);
        Assert.False(item.Sanitized);
    }

    [Fact]
    public async Task IllegalChars_Sanitized()
    {
        var port = new FakeRenamerDataPort();
        port.SeedEntity(Entity("My: Film", File(1, "raw.mkv")));
        var planner = new RenamerPlanner(port);

        var plan = await planner.PlanAsync(RenamerFileKind.Video, 10, new RenamerOptions(), default);

        var item = Assert.Single(plan.Items);
        Assert.True(item.Sanitized);
        Assert.DoesNotContain(':', item.NewBasename);
    }
}
