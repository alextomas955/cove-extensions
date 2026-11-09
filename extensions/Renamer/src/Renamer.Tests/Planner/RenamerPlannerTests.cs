using System.Text.RegularExpressions;
using Renamer.Engine;
using Renamer.Options;
using Renamer.Planner;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Planner;

public sealed class RenamerPlannerTests
{
    private static RenamerFile VideoFile(int id, string basename, int folderId = 5, string folderPath = "media/videos") =>
        new(FileId: id, Kind: RenamerFileKind.Video, Basename: basename, ParentFolderId: folderId,
            ParentFolderPath: folderPath, Format: "mkv", Width: 1920, Height: 1080,
            Duration: 3600, VideoCodec: "h264", AudioCodec: "aac", FrameRate: 30);

    private static RenamerEntity VideoEntity(string title, params RenamerFile[] files) =>
        new(EntityId: 10, Kind: RenamerFileKind.Video, Title: title, Code: "ABC-1", StudioName: "Acme",
            Date: new DateOnly(2024, 3, 2), Organized: true,
            Performers: [new RenamerPerformer(1, "Bob", null)], TagRefs: [(1, "hd")], Files: files);

    [Fact]
    public async Task SingleFile_Rename_HappyPath_ZeroMutation()
    {
        var port = new FakeRenamerDataPort();
        port.SeedEntity(VideoEntity("My Film", VideoFile(1, "raw.mkv")));
        var planner = new RenamerPlanner(port);

        var plan = await planner.PlanAsync(RenamerFileKind.Video, 10, new RenamerOptions { FilenameTemplate = "$title" }, default);

        var item = Assert.Single(plan.Items);
        Assert.Equal(RenamerStatus.Rename, item.Status);
        Assert.Equal("My Film.mkv", item.NewBasename);
        Assert.EndsWith("My Film.mkv", item.NewFullPath);
        Assert.EndsWith("media/videos/raw.mkv", item.OldFullPath);
    }

    [Fact]
    public async Task RenderedEqualsCurrent_IsNoOp_ZeroMutation()
    {
        var port = new FakeRenamerDataPort();
        // "$title" over Title="raw" renders the file's current basename.
        port.SeedEntity(VideoEntity("raw", VideoFile(1, "raw.mkv")));
        var planner = new RenamerPlanner(port);

        var plan = await planner.PlanAsync(RenamerFileKind.Video, 10, new RenamerOptions { FilenameTemplate = "$title" }, default);

        Assert.Equal(RenamerStatus.NoOp, Assert.Single(plan.Items).Status);
    }

    [Fact]
    public async Task TraversalFolderTemplate_NeutralizedByEngine_ConfinedUnderRoot()
    {
        // The engine strips the dots from each segment, so "../../escape" renders to the benign
        // subfolder "escape", which the confinement gate then accepts as a move under the root.
        var port = new FakeRenamerDataPort();
        port.SeedLibraryPaths("media/videos");
        port.SeedEntity(VideoEntity("My Film", VideoFile(1, "raw.mkv")));
        var planner = new RenamerPlanner(port);
        var opts = new RenamerOptions { FilenameTemplate = "$title", FolderTemplate = "../../escape" };

        var plan = await planner.PlanAsync(RenamerFileKind.Video, 10, opts, default);

        var item = Assert.Single(plan.Items);
        Assert.Equal(RenamerStatus.Move, item.Status);
        Assert.EndsWith("media/videos/escape/My Film.mkv", item.NewFullPath);
    }

    [Fact]
    public async Task ConfinementRejection_WiredIntoPlanner_IsSkipped_ZeroMutation()
    {
        // The FullPathMax re-check measures the absolute path, which the engine never does.
        var port = new FakeRenamerDataPort();
        port.SeedEntity(VideoEntity(new string('A', 300), VideoFile(1, "raw.mkv")));
        var planner = new RenamerPlanner(port);
        var opts = new RenamerOptions { FullPathMax = 50 };

        var plan = await planner.PlanAsync(RenamerFileKind.Video, 10, opts, default);

        var item = Assert.Single(plan.Items);
        Assert.Equal(RenamerStatus.SkipTooLong, item.Status);
        Assert.Contains("FullPathMax", item.Reason);
    }

    [Fact]
    public async Task MissingEntity_ReturnsEmptyPlan()
    {
        var port = new FakeRenamerDataPort();
        var planner = new RenamerPlanner(port);

        var plan = await planner.PlanAsync(RenamerFileKind.Video, 999, new RenamerOptions(), default);

        Assert.Empty(plan.Items);
    }

    private static readonly RouteLookups EmptyLookups = new(
        new Dictionary<int, Destination>(), new Dictionary<int, Destination>(),
        new Dictionary<string, Destination>(),
        Array.Empty<(Regex, Destination)>());

    [Fact]
    public async Task PlanLoadedEntity_MatchesLoadingPath_ItemForItem()
    {
        var entity = VideoEntity("My Film", VideoFile(1, "raw.mkv"));
        var port = new FakeRenamerDataPort();
        port.SeedEntity(entity);
        var planner = new RenamerPlanner(port);
        var opts = new RenamerOptions { FilenameTemplate = "$title" };

        var loaded = await planner.PlanLoadedEntity(entity, opts, EmptyLookups, default);
        var viaLoad = await planner.PlanAsync(RenamerFileKind.Video, 10, opts, default);

        Assert.Equal(viaLoad.EntityId, loaded.EntityId);
        Assert.Equal(viaLoad.Kind, loaded.Kind);
        Assert.Equal(viaLoad.Items, loaded.Items);
    }

    [Fact]
    public async Task PlanLoadedEntity_PerformsZeroLoads()
    {
        var entity = VideoEntity("My Film", VideoFile(1, "raw.mkv"));
        var port = new FakeRenamerDataPort();  // NOT seeded - proves no load is attempted
        var planner = new RenamerPlanner(port);

        var plan = await planner.PlanLoadedEntity(entity, new RenamerOptions { FilenameTemplate = "$title" }, EmptyLookups, default);

        Assert.Single(plan.Items);
        Assert.Equal(0, port.LoadEntityCallCount);
    }

    [Fact]
    public async Task FreeCleanName_IsNeitherSuffixedNorSanitized()
    {
        var port = new FakeRenamerDataPort();
        port.SeedEntity(VideoEntity("My Film", VideoFile(1, "raw.mkv")));
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
        port.SeedEntity(VideoEntity("My: Film", VideoFile(1, "raw.mkv")));
        var planner = new RenamerPlanner(port);

        var plan = await planner.PlanAsync(RenamerFileKind.Video, 10, new RenamerOptions(), default);

        var item = Assert.Single(plan.Items);
        Assert.True(item.Sanitized);
        Assert.DoesNotContain(':', item.NewBasename);
    }
}
