using Renamer.Options;
using Renamer.Planner;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Planner;

/// <summary>
/// Collision handling, plan side: when the first candidate basename is taken (per the data-port
/// collision check), the planner applies the configured <see cref="RenamerOptions.DuplicateSuffixFormat"/>
/// counter until free and the resulting NewFullPath carries the suffix; if no free name is found
/// within a sane bound the item is <see cref="RenamerStatus.SkipCollision"/>; and if the first free
/// name is the one the file already carries the item is <see cref="RenamerStatus.NoOp"/> rather than a
/// move onto itself. NO mutation.
/// </summary>
[Trait("Tier", "L0")]
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
        // "My Film.mkv" is taken by some OTHER file (id 99) in folder 5 → suffix to " (1)".
        port.SeedOccupied(folderId: 5, basename: "My Film.mkv", fileId: 99);
        var planner = new RenamerPlanner(port);

        var plan = await planner.PlanAsync(RenamerFileKind.Video, 10, new RenamerOptions(), RouteLookupsFixtures.RoutingNeutral, default);

        var item = Assert.Single(plan.Items);
        Assert.Equal(RenamerStatus.Rename, item.Status);
        Assert.Equal("My Film (1).mkv", item.NewBasename);
        Assert.EndsWith("My Film (1).mkv", item.NewFullPath);
        Assert.Empty(port.ApplyAndSaveCalls);
    }

    [Fact]
    public async Task SecondCandidateAlsoTaken_NextSuffix()
    {
        var port = new FakeRenamerDataPort();
        port.SeedEntity(Entity(File(1, "raw.mkv")));
        port.SeedOccupied(5, "My Film.mkv", 99);
        port.SeedOccupied(5, "My Film (1).mkv", 98);
        var planner = new RenamerPlanner(port);

        var plan = await planner.PlanAsync(RenamerFileKind.Video, 10, new RenamerOptions(), RouteLookupsFixtures.RoutingNeutral, default);

        Assert.Equal("My Film (2).mkv", Assert.Single(plan.Items).NewBasename);
        Assert.Empty(port.ApplyAndSaveCalls);
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

        var plan = await planner.PlanAsync(RenamerFileKind.Video, 10, new RenamerOptions(), RouteLookupsFixtures.RoutingNeutral, default);

        var item = Assert.Single(plan.Items);
        Assert.Equal(RenamerStatus.SkipCollision, item.Status);
        Assert.Empty(port.ApplyAndSaveCalls);
    }

    /// <summary>
    /// A settled candidate that is the file's OWN current name is a no-op, never a move onto itself.
    /// </summary>
    /// <remarks>
    /// The planner's earlier no-op comparison runs on the RENDERED name, before the suffix loop. The loop
    /// then lengthens that name to free the slot a sibling holds, and the first free candidate can be the
    /// numbered name this file already carries — so the settled destination is the source. Classified as
    /// an act, such an item is executed and SAVED, and on the auto-rename path a save is what makes the
    /// host re-raise the update event.
    /// </remarks>
    [Fact]
    public async Task SettledCandidateIsTheFilesCurrentName_NoOp_NotAMoveToItself()
    {
        var port = new FakeRenamerDataPort();
        // The file already carries the numbered form the loop settles on, while a DIFFERENT file (id 99)
        // holds the un-numbered name the template renders.
        port.SeedEntity(Entity(File(1, "My Film (1).mkv")));
        port.SeedOccupied(folderId: 5, basename: "My Film.mkv", fileId: 99);
        var planner = new RenamerPlanner(port);

        var plan = await planner.PlanAsync(RenamerFileKind.Video, 10, new RenamerOptions(), RouteLookupsFixtures.RoutingNeutral, default);

        var item = Assert.Single(plan.Items);
        Assert.Equal(RenamerStatus.NoOp, item.Status);
        // The path equality stated on its own, because that is the property — a status assertion alone
        // would pass on an item that changed the path. Both sides carry the transcribed literal rather
        // than each other, so two identical WRONG paths cannot satisfy it either.
        Assert.Equal("media/videos/My Film (1).mkv", item.OldFullPath);
        Assert.Equal("media/videos/My Film (1).mkv", item.NewFullPath);
        Assert.Empty(port.ApplyAndSaveCalls);
    }
}
