using Renamer.Options;
using Renamer.Planner;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Planner;

public sealed class PreviewPurityTests
{
    private static RenamerFile File(int id, string basename, int folderId = 5) =>
        new(FileId: id, Kind: RenamerFileKind.Video, Basename: basename, ParentFolderId: folderId,
            ParentFolderPath: "media/videos", Format: "mkv");

    private static RenamerEntity Entity(params RenamerFile[] files) =>
        new(EntityId: 10, Kind: RenamerFileKind.Video, Title: "My Film", Code: null, StudioName: null,
            Date: null, Organized: true, Performers: [], TagRefs: [], Files: files);

    // A folder-template move: the rendered subfolder makes this a Move whose destination folder
    // ("media/videos/Archive") is not seeded in the fake port, so a get-or-create would mint+record it.
    private static RenamerOptions MoveOptions() =>
        new() { FilenameTemplate = "$title", FolderTemplate = "Archive" };

    [Fact]
    public async Task Preview_MoveToMissingFolder_CreatesNoFolderRow()
    {
        var port = new FakeRenamerDataPort();
        port.SeedLibraryPaths("media/videos");
        port.SeedEntity(Entity(File(1, "raw.mkv")));
        var planner = new RenamerPlanner(port);

        var plan = await planner.PlanAsync(RenamerFileKind.Video, 10, MoveOptions(), default);

        var item = Assert.Single(plan.Items);
        Assert.Equal(RenamerStatus.Move, item.Status);
        Assert.EndsWith("Archive/My Film.mkv", item.NewFullPath);
        Assert.Empty(port.CreatedFolderPaths);
        Assert.Empty(port.ApplyAndSaveCalls);
    }

    [Fact]
    public async Task Preview_MissingSource_ClassifiedSkipMissingSource()
    {
        var port = new FakeRenamerDataPort();
        port.SeedLibraryPaths("media/videos");
        port.SeedEntity(Entity(File(1, "raw.mkv")));
        port.SeedMissingSource("media/videos/raw.mkv");
        var planner = new RenamerPlanner(port);

        var plan = await planner.PlanAsync(RenamerFileKind.Video, 10, MoveOptions(), default);

        var item = Assert.Single(plan.Items);
        Assert.Equal(RenamerStatus.SkipMissingSource, item.Status);
        Assert.Equal(item.OldFullPath, item.NewFullPath);
        Assert.Contains("missing", item.Reason);
    }

    [Fact]
    public async Task Preview_MoveToExistingFolder_StillDetectsCollision()
    {
        var port = new FakeRenamerDataPort();
        port.SeedLibraryPaths("media/videos");
        port.SeedEntity(Entity(File(1, "raw.mkv")));
        port.SeedFolder("media/videos/Archive", 42);
        port.SeedOccupied(folderId: 42, basename: "My Film.mkv", fileId: 99);
        var planner = new RenamerPlanner(port);

        var plan = await planner.PlanAsync(RenamerFileKind.Video, 10, MoveOptions(), default);

        var item = Assert.Single(plan.Items);
        Assert.Equal(RenamerStatus.Move, item.Status);
        Assert.EndsWith("Archive/My Film (1).mkv", item.NewFullPath);
    }
}
