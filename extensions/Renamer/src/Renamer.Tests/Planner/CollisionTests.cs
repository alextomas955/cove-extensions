using Renamer.Options;
using Renamer.Planner;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Planner;

public sealed class CollisionTests
{
    private static RenamerFile File(int id, string basename, int folderId = 5) =>
        new(FileId: id, Kind: RenamerFileKind.Video, Basename: basename, ParentFolderId: folderId,
            ParentFolderPath: "media/videos", Format: "mkv");

    private static RenamerEntity Entity(params RenamerFile[] files) =>
        new(EntityId: 10, Kind: RenamerFileKind.Video, Title: "My Film", Code: null, StudioName: null,
            Date: null, Organized: true, Performers: [], TagRefs: [], Files: files);

    [Fact]
    public async Task FirstCandidateTaken_SuffixApplied()
    {
        var port = new FakeRenamerDataPort();
        port.SeedEntity(Entity(File(1, "raw.mkv")));
        // "My Film.mkv" is taken by some other file (id 99) in folder 5 → suffix to " (1)".
        port.SeedOccupied(folderId: 5, basename: "My Film.mkv", fileId: 99);
        var planner = new RenamerPlanner(port);

        var plan = await planner.PlanAsync(RenamerFileKind.Video, 10, new RenamerOptions(), default);

        var item = Assert.Single(plan.Items);
        Assert.Equal(RenamerStatus.Rename, item.Status);
        Assert.Equal("My Film (1).mkv", item.NewBasename);
        Assert.EndsWith("My Film (1).mkv", item.NewFullPath);
    }

    [Fact]
    public async Task SecondCandidateAlsoTaken_NextSuffix()
    {
        var port = new FakeRenamerDataPort();
        port.SeedEntity(Entity(File(1, "raw.mkv")));
        port.SeedOccupied(5, "My Film.mkv", 99);
        port.SeedOccupied(5, "My Film (1).mkv", 98);
        var planner = new RenamerPlanner(port);

        var plan = await planner.PlanAsync(RenamerFileKind.Video, 10, new RenamerOptions(), default);

        Assert.Equal("My Film (2).mkv", Assert.Single(plan.Items).NewBasename);
    }

    [Fact]
    public async Task AllCandidatesTaken_SkipCollision()
    {
        var port = new FakeRenamerDataPort();
        port.SeedEntity(Entity(File(1, "raw.mkv")));
        // Occupy the base name and every suffix up to the planner's bound so it never finds free.
        port.SeedOccupied(5, "My Film.mkv", 99);
        for (int n = 1; n <= 1000; n++)
        {
            port.SeedOccupied(5, $"My Film ({n}).mkv", 100 + n);
        }
        var planner = new RenamerPlanner(port);

        var plan = await planner.PlanAsync(RenamerFileKind.Video, 10, new RenamerOptions(), default);

        var item = Assert.Single(plan.Items);
        Assert.Equal(RenamerStatus.SkipCollision, item.Status);
    }

    [Fact]
    public async Task SettledCandidateIsTheFilesCurrentName_NoOp_NotAMoveToItself()
    {
        var port = new FakeRenamerDataPort();
        // The file already carries the numbered form the loop settles on, while a different file (id 99)
        // holds the un-numbered name the template renders.
        port.SeedEntity(Entity(File(1, "My Film (1).mkv")));
        port.SeedOccupied(folderId: 5, basename: "My Film.mkv", fileId: 99);
        var planner = new RenamerPlanner(port);

        var plan = await planner.PlanAsync(RenamerFileKind.Video, 10, new RenamerOptions(), default);

        var item = Assert.Single(plan.Items);
        Assert.Equal(RenamerStatus.NoOp, item.Status);
        // Both sides carry the transcribed literal rather than each other, so two identical wrong paths
        // cannot satisfy this either.
        Assert.Equal("media/videos/My Film (1).mkv", item.OldFullPath);
        Assert.Equal("media/videos/My Film (1).mkv", item.NewFullPath);
    }

    [Fact]
    public async Task TwoFilesOfOneEntity_RenderingOneName_PlanToDistinctPaths()
    {
        var port = new FakeRenamerDataPort();
        // Different current basenames so the seeding is unambiguous; both render "My Film.mkv".
        port.SeedEntity(Entity(File(1, "raw.mkv"), File(2, "extra.mkv")));
        var planner = new RenamerPlanner(port);

        var plan = await planner.PlanAsync(RenamerFileKind.Video, 10, new RenamerOptions(), default);

        // The fake hands back the entity's files as the fixture listed them, so first and second are
        // deterministic here. The same assumption would be wrong at a tier reading a real database.
        Assert.Equal([1, 2], plan.Items.Select(i => i.FileId));
        var first = plan.Items[0];
        var second = plan.Items[1];

        Assert.Equal("My Film.mkv", first.NewBasename);
        Assert.Equal("My Film (1).mkv", second.NewBasename);

        // Asserted directly: a pair of basename assertions could both hold while a folder difference
        // made the two whole paths agree.
        Assert.NotEqual(first.NewFullPath, second.NewFullPath);
        Assert.Equal("media/videos/My Film.mkv", first.NewFullPath);
        Assert.Equal("media/videos/My Film (1).mkv", second.NewFullPath);

        Assert.False(first.Suffixed);
        Assert.True(second.Suffixed);
    }

    [Fact]
    public async Task TwoFilesOfOneEntity_RenderingNamesThatDifferOnlyInCase_AreSuffixed_WhereCaseIsIgnored()
    {
        Assert.SkipUnless(
            OperatingSystem.IsWindows() || OperatingSystem.IsMacOS(),
            "needs a platform whose default filesystem ignores case");

        var port = new FakeRenamerDataPort();
        port.SeedEntity(Entity(File(1, "raw.MKV"), File(2, "extra.mkv")));
        var planner = new RenamerPlanner(port);

        var plan = await planner.PlanAsync(RenamerFileKind.Video, 10, new RenamerOptions(), default);

        Assert.Equal("My Film.MKV", plan.Items[0].NewBasename);
        Assert.Equal("My Film (1).mkv", plan.Items[1].NewBasename);
    }
}
