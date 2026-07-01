using Renamer.Planner;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Planner;

/// <summary>
/// Exercises the <see cref="IRenamerDataPort"/> seam contract through the in-memory fake
/// (per Task 2 behavior block) so the seam is asserted, not merely compiled.
/// </summary>
public sealed class IRenamerDataPortSeamTests
{
    [Fact]
    public async Task CollisionExistsAsync_True_ForSeededPair_ExcludingSelf()
    {
        var port = new FakeRenamerDataPort();
        port.SeedOccupied(folderId: 7, basename: "b.mp4", fileId: 42);

        // A different file (id 99) wanting "b.mp4" in folder 7 collides with file 42.
        Assert.True(await port.CollisionExistsAsync(7, "b.mp4", selfFileId: 99));
    }

    [Fact]
    public async Task CollisionExistsAsync_False_WhenOnlySelfHoldsTheName()
    {
        var port = new FakeRenamerDataPort();
        port.SeedOccupied(folderId: 7, basename: "b.mp4", fileId: 42);

        // File 42 already owns the name; renaming itself to its own name is not a collision.
        Assert.False(await port.CollisionExistsAsync(7, "b.mp4", selfFileId: 42));
    }

    [Fact]
    public async Task CollisionExistsAsync_False_ForUnseededName()
    {
        var port = new FakeRenamerDataPort();
        Assert.False(await port.CollisionExistsAsync(7, "free.mp4", selfFileId: 1));
    }

    [Fact]
    public async Task GetOrCreateFolderId_IsStable_AcrossCalls()
    {
        var port = new FakeRenamerDataPort();
        var a = await port.GetOrCreateFolderIdAsync("media/2024");
        var b = await port.GetOrCreateFolderIdAsync("media/2024");
        var c = await port.GetOrCreateFolderIdAsync("media/2025");

        Assert.Equal(a, b);       // same path → same id
        Assert.NotEqual(a, c);    // different path → different id
    }

    [Fact]
    public async Task SaveAsync_RecordsMutations_ForAssertion()
    {
        var port = new FakeRenamerDataPort();
        var muts = new List<RenamerFileMutation> { new(FileId: 1, NewBasename: "new.mp4", NewParentFolderId: null) };

        var changed = await port.SaveAsync(muts);

        Assert.Equal(1, changed);
        Assert.Single(port.SaveCalls);
        Assert.Equal("new.mp4", port.SaveCalls[0][0].NewBasename);
    }
}
