using Renamer.Options;
using Renamer.Planner;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Planner;

/// <summary>
/// Gating: only-organized skips an unorganized item; require-fields skips an
/// item whose required token projects empty. Gated = <see cref="RenamerStatus.SkipGated"/>
/// (never <see cref="RenamerStatus.Failed"/>), with a reason — and zero mutation.
/// </summary>
public sealed class GatingTests
{
    private static RenamerFile File(int id) =>
        new(FileId: id, Kind: RenamerFileKind.Video, Basename: "raw.mkv", ParentFolderId: 5,
            ParentFolderPath: "media/videos", Format: "mkv");

    private static RenamerEntity Entity(string? title, bool organized, params RenamerFile[] files) =>
        new(EntityId: 10, Kind: RenamerFileKind.Video, Title: title, Code: null, StudioName: null,
            Date: null, Organized: organized, Performers: [], Tags: [], Files: files);

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
        Assert.Empty(port.SaveCalls);
    }

    [Fact]
    public async Task RequireFields_EmptyTitle_SkipGated_NotFailed()
    {
        var port = new FakeRenamerDataPort();
        port.SeedEntity(Entity(title: "", organized: true, File(1)));
        var planner = new RenamerPlanner(port);
        // Default RequiredFields = ["title"]. FilenameAsTitle is forced off so the title-less item
        // is gated rather than rescued by the basename fallback (which now defaults on) — this case
        // exercises the require-fields gate, not the fallback.
        var opts = new RenamerOptions { FilenameAsTitle = false };
        var plan = await planner.PlanAsync(RenamerFileKind.Video, 10, opts, default);

        var item = Assert.Single(plan.Items);
        Assert.Equal(RenamerStatus.SkipGated, item.Status);
        Assert.NotEqual(RenamerStatus.Failed, item.Status);
        Assert.Empty(port.SaveCalls);
    }

    [Fact]
    public async Task RequireFields_TokenNeverProjected_SkipGated()
    {
        var port = new FakeRenamerDataPort();
        // Title present, but require a field whose token is never produced by the projector.
        port.SeedEntity(Entity("My Film", organized: true, File(1)));
        var planner = new RenamerPlanner(port);
        var opts = new RenamerOptions { RequiredFields = ["zzznope"] };

        var plan = await planner.PlanAsync(RenamerFileKind.Video, 10, opts, default);

        var item = Assert.Single(plan.Items);
        Assert.Equal(RenamerStatus.SkipGated, item.Status);
        Assert.Empty(port.SaveCalls);
    }

    [Fact]
    public async Task WR02_OnlyOrganized_WithUnorganizedDestination_RoutesInsteadOfGating()
    {
        // WR-02: with OnlyOrganized ON but an UnorganizedDestination configured, an unorganized item
        // must NOT be gated out — the unorganized destination takes precedence and the item routes
        // (ROUTE-05). Without this carve-out the gate would silently nullify the unorganized route.
        string unorgRoot = OperatingSystem.IsWindows() ? @"H:\unsorted" : "/mnt/unsorted";
        string srcFolder = OperatingSystem.IsWindows() ? "C:/library/incoming" : "/srv/library/incoming";

        var port = new FakeRenamerDataPort();
        port.SeedEntity(new RenamerEntity(
            EntityId: 10, Kind: RenamerFileKind.Video, Title: "My Film", Code: null, StudioName: null,
            Date: null, Organized: false, Performers: [], Tags: [],
            Files: [new RenamerFile(1, RenamerFileKind.Video, "raw.mkv", 5, srcFolder, Format: "mkv")]));
        var planner = new RenamerPlanner(port);
        var opts = new RenamerOptions
        {
            OnlyOrganized = true,
            FilenameTemplate = "$title",
            FolderTemplate = "Sorted",
            AllowedRoots = [srcFolder, unorgRoot],
            UnorganizedDestination = unorgRoot,
        };

        var plan = await planner.PlanAsync(RenamerFileKind.Video, 10, opts, default);

        var item = Assert.Single(plan.Items);
        Assert.NotEqual(RenamerStatus.SkipGated, item.Status);
        Assert.Equal(RenamerStatus.Move, item.Status);
        Assert.Equal(unorgRoot, item.ResolvedDestinationRoot);
        Assert.Equal("Unorganized", item.MatchedRule);
        Assert.Empty(port.SaveCalls);
    }

    [Fact]
    public async Task WR02_OnlyOrganized_NoUnorganizedDestination_StillGates()
    {
        // The complement: with no UnorganizedDestination, the only-organized gate behaves exactly as
        // before — an unorganized item is skipped.
        var port = new FakeRenamerDataPort();
        port.SeedEntity(Entity("My Film", organized: false, File(1)));
        var planner = new RenamerPlanner(port);
        var opts = new RenamerOptions { OnlyOrganized = true }; // UnorganizedDestination = "" (default)

        var plan = await planner.PlanAsync(RenamerFileKind.Video, 10, opts, default);

        Assert.Equal(RenamerStatus.SkipGated, Assert.Single(plan.Items).Status);
        Assert.Empty(port.SaveCalls);
    }

    [Fact]
    public async Task OnlyOrganized_OrganizedItem_NotGated()
    {
        var port = new FakeRenamerDataPort();
        port.SeedEntity(Entity("My Film", organized: true, File(1)));
        var planner = new RenamerPlanner(port);
        var opts = new RenamerOptions { OnlyOrganized = true };

        var plan = await planner.PlanAsync(RenamerFileKind.Video, 10, opts, default);

        Assert.NotEqual(RenamerStatus.SkipGated, Assert.Single(plan.Items).Status);
        Assert.Empty(port.SaveCalls);
    }
}
