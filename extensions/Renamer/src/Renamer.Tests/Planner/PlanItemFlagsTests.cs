using Renamer.Options;
using Renamer.Planner;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Planner;

/// <summary>
/// The two advisory badge signals the planner sets on the final Renamer/Move item:
/// <c>Suffixed</c> is true exactly when the collision suffix loop ran (attempt &gt; 0), and
/// <c>Sanitized</c> is true exactly when the engine's <c>WouldSanitizeFilename</c> reported the name
/// was cleaned. Both default to false (additive) so existing constructions keep compiling. DB-free
/// over <see cref="FakeRenamerDataPort"/> (mirrors <c>CollisionTests</c>).
/// </summary>
public sealed class PlanItemFlagsTests
{
    private static RenamerFile File(int id, string basename, int folderId = 5) =>
        new(FileId: id, Kind: RenamerFileKind.Video, Basename: basename, ParentFolderId: folderId,
            ParentFolderPath: "media/videos", Format: "mkv");

    private static RenamerEntity Entity(string title, params RenamerFile[] files) =>
        new(EntityId: 10, Kind: RenamerFileKind.Video, Title: title, Code: null, StudioName: null,
            Date: null, Organized: true, Performers: [], Tags: [], Files: files);

    [Fact]
    public async Task FreeName_NotSuffixed()
    {
        var port = new FakeRenamerDataPort();
        port.SeedEntity(Entity("My Film", File(1, "raw.mkv")));
        var planner = new RenamerPlanner(port);

        var plan = await planner.PlanAsync(RenamerFileKind.Video, 10, new RenamerOptions(), default);

        var item = Assert.Single(plan.Items);
        Assert.Equal(RenamerStatus.Renamer, item.Status);
        Assert.False(item.Suffixed);
    }

    [Fact]
    public async Task FirstCandidateTaken_Suffixed_AndBasenameCarriesSuffix()
    {
        var port = new FakeRenamerDataPort();
        port.SeedEntity(Entity("My Film", File(1, "raw.mkv")));
        // "My Film.mkv" is taken by another file → suffix loop runs (attempt > 0).
        port.SeedOccupied(folderId: 5, basename: "My Film.mkv", fileId: 99);
        var planner = new RenamerPlanner(port);

        var plan = await planner.PlanAsync(RenamerFileKind.Video, 10, new RenamerOptions(), default);

        var item = Assert.Single(plan.Items);
        Assert.Equal(RenamerStatus.Renamer, item.Status);
        Assert.True(item.Suffixed);
        Assert.Equal("My Film (1).mkv", item.NewBasename);
    }

    [Fact]
    public async Task IllegalChars_Sanitized()
    {
        var port = new FakeRenamerDataPort();
        // A title with an illegal filename char (':') → the engine sanitizes it out.
        port.SeedEntity(Entity("My: Film", File(1, "raw.mkv")));
        var planner = new RenamerPlanner(port);

        var plan = await planner.PlanAsync(RenamerFileKind.Video, 10, new RenamerOptions(), default);

        var item = Assert.Single(plan.Items);
        Assert.True(item.Sanitized);
        // The illegal ':' is gone from the rendered basename.
        Assert.DoesNotContain(':', item.NewBasename);
    }

    [Fact]
    public async Task CleanName_NotSanitized()
    {
        var port = new FakeRenamerDataPort();
        port.SeedEntity(Entity("My Film", File(1, "raw.mkv")));
        var planner = new RenamerPlanner(port);

        var plan = await planner.PlanAsync(RenamerFileKind.Video, 10, new RenamerOptions(), default);

        var item = Assert.Single(plan.Items);
        Assert.False(item.Sanitized);
    }
}
