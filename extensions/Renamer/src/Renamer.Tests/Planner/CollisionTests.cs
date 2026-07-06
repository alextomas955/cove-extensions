using Renamer.Options;
using Renamer.Planner;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Planner;

/// <summary>
/// Collision handling, plan side: when the first candidate basename is taken (per the data-port
/// collision check), the planner applies the configured <see cref="RenamerOptions.DuplicateSuffixFormat"/>
/// counter until free and the resulting NewFullPath carries the suffix; if no free name is found
/// within a sane bound the item is <see cref="RenamerStatus.SkipCollision"/>. NO mutation.
/// </summary>
public sealed class CollisionTests
{
    private static RenamerFile File(int id, string basename, int folderId = 5) =>
        new(FileId: id, Kind: RenamerFileKind.Video, Basename: basename, ParentFolderId: folderId,
            ParentFolderPath: "media/videos", Format: "mkv");

    private static RenamerEntity Entity(params RenamerFile[] files) =>
        new(EntityId: 10, Kind: RenamerFileKind.Video, Title: "My Film", Code: null, StudioName: null,
            Date: null, Organized: true, Performers: [], Tags: [], Files: files);

    [Fact]
    public async Task FirstCandidateTaken_SuffixApplied()
    {
        var port = new FakeRenamerDataPort();
        port.SeedEntity(Entity(File(1, "raw.mkv")));
        // "My Film.mkv" is taken by some OTHER file (id 99) in folder 5 → suffix to " (1)".
        port.SeedOccupied(folderId: 5, basename: "My Film.mkv", fileId: 99);
        var planner = new RenamerPlanner(port);

        var plan = await planner.PlanAsync(RenamerFileKind.Video, 10, new RenamerOptions(), default);

        var item = Assert.Single(plan.Items);
        Assert.Equal(RenamerStatus.Renamer, item.Status);
        Assert.Equal("My Film (1).mkv", item.NewBasename);
        Assert.EndsWith("My Film (1).mkv", item.NewFullPath);
        Assert.Empty(port.SaveCalls);
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
        Assert.Empty(port.SaveCalls);
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
        Assert.Empty(port.SaveCalls);
    }
}
