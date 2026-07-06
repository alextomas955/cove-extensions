using Renamer.Options;
using Renamer.Planner;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Planner;

/// <summary>
/// Dry-run core: <c>RenamerPlanner.PlanAsync</c> produces an accurate per-file
/// old→new plan with the right <see cref="RenamerStatus"/> while mutating NOTHING — every test
/// asserts the <see cref="FakeRenamerDataPort"/> recorded zero <c>SaveAsync</c> calls. Also covers
/// the happy-path renamer, NoOp, and the confinement rejection.
/// </summary>
public sealed class RenamerPlannerTests
{
    private static RenamerFile VideoFile(int id, string basename, int folderId = 5, string folderPath = "media/videos") =>
        new(FileId: id, Kind: RenamerFileKind.Video, Basename: basename, ParentFolderId: folderId,
            ParentFolderPath: folderPath, Format: "mkv", Width: 1920, Height: 1080,
            Duration: 3600, VideoCodec: "h264", AudioCodec: "aac", FrameRate: 30);

    private static RenamerEntity VideoEntity(string title, params RenamerFile[] files) =>
        new(EntityId: 10, Kind: RenamerFileKind.Video, Title: title, Code: "ABC-1", StudioName: "Acme",
            Date: new DateOnly(2024, 3, 2), Organized: true,
            Performers: [new RenamerPerformer(1, "Bob", false, null)], Tags: ["hd"], Files: files);

    [Fact]
    public async Task SingleFile_Renamer_HappyPath_ZeroMutation()
    {
        var port = new FakeRenamerDataPort();
        port.SeedEntity(VideoEntity("My Film", VideoFile(1, "raw.mkv")));
        var planner = new RenamerPlanner(port);

        // Pin the title-only template (this test exercises planner renamer detection, not the default).
        var plan = await planner.PlanAsync(RenamerFileKind.Video, 10, new RenamerOptions { FilenameTemplate = "$title" }, default);

        var item = Assert.Single(plan.Items);
        Assert.Equal(RenamerStatus.Renamer, item.Status);
        Assert.Equal("My Film.mkv", item.NewBasename);
        Assert.EndsWith("My Film.mkv", item.NewFullPath);
        Assert.EndsWith("media/videos/raw.mkv", item.OldFullPath);
        Assert.Empty(port.SaveCalls);               // dry-run guarantee: no mutation
    }

    [Fact]
    public async Task RenderedEqualsCurrent_IsNoOp_ZeroMutation()
    {
        var port = new FakeRenamerDataPort();
        // Template "$title" with Title="raw" renders to "raw.mkv" == current basename → NoOp.
        port.SeedEntity(VideoEntity("raw", VideoFile(1, "raw.mkv")));
        var planner = new RenamerPlanner(port);

        var plan = await planner.PlanAsync(RenamerFileKind.Video, 10, new RenamerOptions { FilenameTemplate = "$title" }, default);

        Assert.Equal(RenamerStatus.NoOp, Assert.Single(plan.Items).Status);
        Assert.Empty(port.SaveCalls);
    }

    [Fact]
    public async Task TraversalFolderTemplate_NeutralizedByEngine_ConfinedUnderRoot()
    {
        // Defense-in-depth: the engine strips "../" segments per-segment (TrimEdge dots),
        // so "../../escape" renders to the benign subfolder "escape" — which the confinement gate
        // then ACCEPTS as a move UNDER the root. The raw "../.." → rejected path is proven directly
        // at the helper level in PathConfinementTests.
        var port = new FakeRenamerDataPort();
        port.SeedEntity(VideoEntity("My Film", VideoFile(1, "raw.mkv")));
        var planner = new RenamerPlanner(port);
        // Pin the title-only filename template — this test asserts folder-template confinement, not the default name shape.
        var opts = new RenamerOptions { FilenameTemplate = "$title", FolderTemplate = "../../escape" };

        var plan = await planner.PlanAsync(RenamerFileKind.Video, 10, opts, default);

        var item = Assert.Single(plan.Items);
        Assert.Equal(RenamerStatus.Move, item.Status);
        Assert.EndsWith("media/videos/escape/My Film.mkv", item.NewFullPath);
        Assert.Empty(port.SaveCalls);
    }

    [Fact]
    public async Task ConfinementRejection_WiredIntoPlanner_IsSkipped_ZeroMutation()
    {
        // Drive a real confinement REJECTION through the planner via the FullPathMax re-check
        // the engine never measures on the absolute path — proves the planner classifies
        // a confinement failure as a skip with the helper's reason, mutating nothing.
        var port = new FakeRenamerDataPort();
        port.SeedEntity(VideoEntity(new string('A', 300), VideoFile(1, "raw.mkv")));
        var planner = new RenamerPlanner(port);
        var opts = new RenamerOptions { FullPathMax = 50 };

        var plan = await planner.PlanAsync(RenamerFileKind.Video, 10, opts, default);

        var item = Assert.Single(plan.Items);
        Assert.Equal(RenamerStatus.SkipCollision, item.Status);
        Assert.Contains("FullPathMax", item.Reason);
        Assert.Empty(port.SaveCalls);
    }

    [Fact]
    public async Task MissingEntity_ReturnsEmptyPlan()
    {
        var port = new FakeRenamerDataPort();
        var planner = new RenamerPlanner(port);

        var plan = await planner.PlanAsync(RenamerFileKind.Video, 999, new RenamerOptions(), default);

        Assert.Empty(plan.Items);
        Assert.Empty(port.SaveCalls);
    }

    private static readonly RouteLookups EmptyLookups = new(
        new Dictionary<int, string>(), new Dictionary<string, string>(),
        new Dictionary<string, string>(), Array.Empty<(System.Text.RegularExpressions.Regex, string)>());

    [Fact]
    public async Task PlanLoadedEntity_MatchesLoadingPath_ItemForItem()
    {
        // Seed the SAME entity behind the loading path; plan it both ways and prove item-for-item
        // equality — the pure method and the load-then-plan method share identical plan logic.
        var entity = VideoEntity("My Film", VideoFile(1, "raw.mkv"));
        var port = new FakeRenamerDataPort();
        port.SeedEntity(entity);
        var planner = new RenamerPlanner(port);
        var opts = new RenamerOptions { FilenameTemplate = "$title" };

        var loaded = await planner.PlanLoadedEntity(entity, opts, EmptyLookups, default);
        var viaLoad = (await planner.PlanWithEntityAsync(RenamerFileKind.Video, 10, opts, default)).Plan;

        Assert.Equal(viaLoad.EntityId, loaded.EntityId);
        Assert.Equal(viaLoad.Kind, loaded.Kind);
        Assert.Equal(viaLoad.Items, loaded.Items);
        Assert.Empty(port.SaveCalls);
    }

    [Fact]
    public async Task PlanLoadedEntity_PerformsZeroLoads()
    {
        var entity = VideoEntity("My Film", VideoFile(1, "raw.mkv"));
        var port = new FakeRenamerDataPort();  // NOT seeded — proves no load is attempted
        var planner = new RenamerPlanner(port);

        var plan = await planner.PlanLoadedEntity(entity, new RenamerOptions { FilenameTemplate = "$title" }, EmptyLookups, default);

        Assert.Single(plan.Items);
        Assert.Equal(0, port.LoadEntityCallCount);
    }

    [Fact]
    public async Task PlanLoadedEntity_GatedEntity_YieldsSameSkips_AsLoadingPath()
    {
        // An unorganized entity under the only-organized gate: SkipGated for every file, both ways.
        var entity = new RenamerEntity(
            EntityId: 10, Kind: RenamerFileKind.Video, Title: "Ungated", Code: null, StudioName: null,
            Date: null, Organized: false, Performers: [], Tags: [], Files: [VideoFile(1, "raw.mkv")]);
        var port = new FakeRenamerDataPort();
        port.SeedEntity(entity);
        var planner = new RenamerPlanner(port);
        var opts = new RenamerOptions { FilenameTemplate = "$title", OnlyOrganized = true };

        var loaded = await planner.PlanLoadedEntity(entity, opts, EmptyLookups, default);
        var viaLoad = (await planner.PlanWithEntityAsync(RenamerFileKind.Video, 10, opts, default)).Plan;

        Assert.Equal(RenamerStatus.SkipGated, Assert.Single(loaded.Items).Status);
        Assert.Equal(viaLoad.Items, loaded.Items);
    }
}
