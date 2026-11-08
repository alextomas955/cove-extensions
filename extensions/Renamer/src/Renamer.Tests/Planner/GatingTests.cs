using Renamer.Engine;
using Renamer.Options;
using Renamer.Planner;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Planner;

public sealed class GatingTests
{
    private static RenamerFile File(int id) =>
        new(FileId: id, Kind: RenamerFileKind.Video, Basename: "raw.mkv", ParentFolderId: 5,
            ParentFolderPath: "media/videos", Format: "mkv");

    private static RenamerEntity Entity(string? title, bool organized, params RenamerFile[] files) =>
        new(EntityId: 10, Kind: RenamerFileKind.Video, Title: title, Code: null, StudioName: null,
            Date: null, Organized: organized, Performers: [], TagRefs: [], Files: files);

    [Fact]
    public async Task OnlyOrganized_UnorganizedItem_SkipGated()
    {
        var port = new FakeRenamerDataPort();
        port.SeedEntity(Entity("My Film", organized: false, File(1), File(2)));
        var planner = new RenamerPlanner(port);
        var opts = new RenamerOptions { OnlyOrganized = true };

        var plan = await planner.PlanAsync(RenamerFileKind.Video, 10, opts, default);

        Assert.Equal(2, plan.Items.Count);
        Assert.All(plan.Items, i =>
        {
            Assert.Equal(RenamerStatus.SkipGated, i.Status);
            Assert.NotNull(i.Reason);
        });
    }

    [Fact]
    public async Task RequireFields_EmptyTitle_SkipGated_NotFailed()
    {
        var port = new FakeRenamerDataPort();
        port.SeedEntity(Entity(title: "", organized: true, File(1)));
        var planner = new RenamerPlanner(port);
        // RequiredFields defaults to ["title"]. FilenameAsTitle is forced off so the title-less item
        // is gated rather than rescued by the basename fallback, which defaults on.
        var opts = new RenamerOptions { FilenameAsTitle = false };
        var plan = await planner.PlanAsync(RenamerFileKind.Video, 10, opts, default);

        var item = Assert.Single(plan.Items);
        Assert.Equal(RenamerStatus.SkipGated, item.Status);
    }

    [Theory]
    // A token the projector never produces at all.
    [InlineData("zzznope")]
    // A token the projector produces only for an item carrying one, which this item does not.
    [InlineData("performers")]
    public async Task RequireFields_AFieldThisItemCannotSupply_SkipGated(string field)
    {
        var port = new FakeRenamerDataPort();
        port.SeedEntity(Entity("My Film", organized: true, File(1)));
        var planner = new RenamerPlanner(port);
        var opts = new RenamerOptions { RequiredFields = [field] };

        var plan = await planner.PlanAsync(RenamerFileKind.Video, 10, opts, default);

        Assert.Equal(RenamerStatus.SkipGated, Assert.Single(plan.Items).Status);
    }

    [Theory]
    [InlineData("performers")]
    [InlineData("tags")]
    [InlineData("resolution")]
    public async Task RequireFields_AMultiValueOrDerivedToken_IsSatisfiedByTheItemThatHasIt(string field)
    {
        var port = new FakeRenamerDataPort();
        var file = File(1) with { Width = 1920, Height = 1080 };
        port.SeedEntity(Entity("My Film", organized: true, file) with
        {
            Performers = [new RenamerPerformer(3, "Jane", Gender: null)],
            TagRefs = [(4, "tagA")],
        });
        var planner = new RenamerPlanner(port);
        var opts = new RenamerOptions { RequiredFields = [field] };

        var plan = await planner.PlanAsync(RenamerFileKind.Video, 10, opts, default);

        Assert.Equal(RenamerStatus.Rename, Assert.Single(plan.Items).Status);
    }

    [Fact]
    public async Task OnlyOrganized_WithUnorganizedDestination_RoutesInsteadOfGating()
    {
        // With OnlyOrganized on and an UnorganizedDestination configured, the unorganized
        // destination takes precedence over the gate and the item routes.
        string unorgRoot = OperatingSystem.IsWindows() ? @"H:\unsorted" : "/mnt/unsorted";
        string srcFolder = OperatingSystem.IsWindows() ? "C:/library/incoming" : "/srv/library/incoming";

        var port = new FakeRenamerDataPort();
        port.SeedLibraryPaths(srcFolder, unorgRoot);
        port.SeedEntity(new RenamerEntity(
            EntityId: 10, Kind: RenamerFileKind.Video, Title: "My Film", Code: null, StudioName: null,
            Date: null, Organized: false, Performers: [], TagRefs: [],
            Files: [new RenamerFile(1, RenamerFileKind.Video, "raw.mkv", 5, srcFolder, Format: "mkv")]));
        var planner = new RenamerPlanner(port);
        var opts = new RenamerOptions
        {
            OnlyOrganized = true,
            FilenameTemplate = "$title",
            FolderTemplate = "Sorted",
            UnorganizedDestination = Dest.At(unorgRoot, "Sorted"),
        };

        var plan = await planner.PlanAsync(RenamerFileKind.Video, 10, opts, default);

        var item = Assert.Single(plan.Items);
        Assert.Equal(RenamerStatus.Move, item.Status);
        Assert.Equal(unorgRoot.Replace('\\', '/'), item.ResolvedDestinationRoot);
        Assert.Equal("Unorganized", item.MatchedRule);
    }

    [Fact]
    public async Task OnlyOrganized_OrganizedItem_NotGated()
    {
        var port = new FakeRenamerDataPort();
        port.SeedEntity(Entity("My Film", organized: true, File(1)));
        var planner = new RenamerPlanner(port);
        var opts = new RenamerOptions { OnlyOrganized = true };

        var plan = await planner.PlanAsync(RenamerFileKind.Video, 10, opts, default);

        Assert.Equal(RenamerStatus.Rename, Assert.Single(plan.Items).Status);
    }
}
