using Renamer.Options;
using Renamer.Planner;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Planner;

/// <summary>
/// F-01 regression: a dry-run plan (what <c>/preview</c> runs) must perform ZERO DB mutation — in
/// particular it must NOT create a destination <see cref="Cove.Core.Entities.Folder"/> row when a
/// move/route targets a folder that does not exist yet. The planner resolves the target folder id
/// READ-ONLY (<see cref="IRenamerDataPort.TryGetFolderIdAsync"/>); an absent folder holds no files, so
/// the candidate name is collision-free and the item still plans as a Move. Folder creation is the
/// executor's job, exercised only on a real renamer.
/// </summary>
public sealed class PreviewPurityTests
{
    private static RenamerFile File(int id, string basename, int folderId = 5) =>
        new(FileId: id, Kind: RenamerFileKind.Video, Basename: basename, ParentFolderId: folderId,
            ParentFolderPath: "media/videos", Format: "mkv");

    private static RenamerEntity Entity(params RenamerFile[] files) =>
        new(EntityId: 10, Kind: RenamerFileKind.Video, Title: "My Film", Code: null, StudioName: null,
            Date: null, Organized: true, Performers: [], Tags: [], Files: files);

    // A folder-template move: the rendered subfolder makes this a Move whose destination folder
    // ("media/videos/Archive") is NOT seeded in the fake port, so a get-or-create would mint+record it.
    private static RenamerOptions MoveOptions() =>
        new() { FilenameTemplate = "$title", FolderTemplate = "Archive" };

    [Fact]
    public async Task Preview_MoveToMissingFolder_CreatesNoFolderRow()
    {
        var port = new FakeRenamerDataPort();
        port.SeedEntity(Entity(File(1, "raw.mkv")));
        var planner = new RenamerPlanner(port);

        var plan = await planner.PlanAsync(RenamerFileKind.Video, 10, MoveOptions(), default);

        var item = Assert.Single(plan.Items);
        // It still plans as a Move to the (not-yet-existing) destination folder...
        Assert.Equal(RenamerStatus.Move, item.Status);
        Assert.EndsWith("Archive/My Film.mkv", item.NewFullPath);
        // ...but planning created NO folder and saved NOTHING — the preview-mutation bug is gone.
        Assert.Empty(port.CreatedFolderPaths);
        Assert.Empty(port.SaveCalls);
    }

    [Fact]
    public async Task Preview_MissingSource_ClassifiedSkipMissingSource_NoMutation()
    {
        var port = new FakeRenamerDataPort();
        port.SeedEntity(Entity(File(1, "raw.mkv")));
        // Declare the file's current source (ParentFolderPath + Basename) absent on disk.
        port.SeedMissingSource("media/videos/raw.mkv");
        var planner = new RenamerPlanner(port);

        var plan = await planner.PlanAsync(RenamerFileKind.Video, 10, MoveOptions(), default);

        var item = Assert.Single(plan.Items);
        // The gone source is classified SkipMissingSource, keeping the file at its current path...
        Assert.Equal(RenamerStatus.SkipMissingSource, item.Status);
        Assert.Equal(item.OldFullPath, item.NewFullPath);
        Assert.Contains("missing", item.Reason);
        // ...detected through the read-only port seam, so preview still mutates nothing.
        Assert.Empty(port.CreatedFolderPaths);
        Assert.Empty(port.SaveCalls);
    }

    [Fact]
    public async Task Preview_MoveToExistingFolder_StillDetectsCollision_WithoutCreating()
    {
        var port = new FakeRenamerDataPort();
        port.SeedEntity(Entity(File(1, "raw.mkv")));
        // The destination folder already exists (id 42) AND already holds "My Film.mkv" (file 99).
        port.SeedFolder("media/videos/Archive", 42);
        port.SeedOccupied(folderId: 42, basename: "My Film.mkv", fileId: 99);
        var planner = new RenamerPlanner(port);

        var plan = await planner.PlanAsync(RenamerFileKind.Video, 10, MoveOptions(), default);

        var item = Assert.Single(plan.Items);
        // Collision against the existing folder's real contents → suffix applied, still no creation.
        Assert.Equal(RenamerStatus.Move, item.Status);
        Assert.EndsWith("Archive/My Film (1).mkv", item.NewFullPath);
        Assert.Empty(port.CreatedFolderPaths);
        Assert.Empty(port.SaveCalls);
    }
}
