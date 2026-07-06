using Renamer.Execution;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Execution;

/// <summary>
/// A caption sidecar ("video.en.vtt") moves alongside its video to
/// track the new stem; when the sidecar's target already exists it is SKIPPED with a warning and
/// the pre-existing target is left untouched (never clobbered). Also proves the rollback helper
/// restores the primary file AND every moved sidecar to their old paths (the executor's
/// save-failure cleanup). Exercised on the real filesystem via <see cref="TempDir"/>.
/// </summary>
[Trait("Tier", "Integration")]
public sealed class SidecarTests
{
    [Fact]
    public void SidecarMovesAlongsideVideo_TracksNewStem()
    {
        using var dir = new TempDir();
        var oldVideo = dir.Touch("video.mkv", "v");
        var oldCaption = dir.Touch("video.en.vtt", "c");
        var newVideo = Path.Combine(dir.Root, "My Film.mkv");
        var newCaption = Path.Combine(dir.Root, "My Film.en.vtt");
        var mover = new DiskMover();

        var result = mover.Move(oldVideo, newVideo,
            [new DiskMover.SidecarMove(oldCaption, newCaption)]);

        Assert.True(result.Moved);
        Assert.False(File.Exists(oldVideo));
        Assert.True(File.Exists(newVideo));
        // Sidecar tracked the new stem.
        Assert.False(File.Exists(oldCaption), "old caption must be gone");
        Assert.True(File.Exists(newCaption), "caption must move alongside the video");
        Assert.Equal("c", File.ReadAllText(newCaption));
        var moved = Assert.Single(result.MovedSidecars);
        Assert.Equal(newCaption, moved.To);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void SidecarTargetExists_SkippedNotClobbered_PreExistingLeftUntouched()
    {
        using var dir = new TempDir();
        var oldVideo = dir.Touch("video.mkv", "v");
        var oldCaption = dir.Touch("video.en.vtt", "mine");
        var newVideo = Path.Combine(dir.Root, "My Film.mkv");
        var newCaption = dir.Touch("My Film.en.vtt", "theirs"); // pre-existing target
        var mover = new DiskMover();

        var result = mover.Move(oldVideo, newVideo,
            [new DiskMover.SidecarMove(oldCaption, newCaption)]);

        Assert.True(result.Moved, "the primary video still moves");
        // The pre-existing caption target is NOT clobbered.
        Assert.Equal("theirs", File.ReadAllText(newCaption));
        // The source caption is left where it was (skipped, not moved).
        Assert.True(File.Exists(oldCaption), "skipped sidecar source must remain");
        Assert.Equal("mine", File.ReadAllText(oldCaption));
        Assert.Empty(result.MovedSidecars);
        Assert.Single(result.Warnings);
        Assert.Contains("skipped", result.Warnings[0]);
    }

    [Fact]
    public void Rollback_RestoresPrimaryAndMovedSidecars_ToOldPaths()
    {
        using var dir = new TempDir();
        var oldVideo = dir.Touch("video.mkv", "v");
        var oldCaption = dir.Touch("video.en.vtt", "c");
        var newVideo = Path.Combine(dir.Root, "My Film.mkv");
        var newCaption = Path.Combine(dir.Root, "My Film.en.vtt");
        var mover = new DiskMover();

        var result = mover.Move(oldVideo, newVideo,
            [new DiskMover.SidecarMove(oldCaption, newCaption)]);
        Assert.True(result.Moved);
        // Sanity: the move really happened before we roll it back (no false-pass).
        Assert.True(File.Exists(newVideo) && File.Exists(newCaption));

        var warnings = mover.Rollback(oldVideo, newVideo, result.MovedSidecars);

        Assert.Empty(warnings);
        Assert.True(File.Exists(oldVideo), "primary file restored to its old path");
        Assert.True(File.Exists(oldCaption), "sidecar restored to its old path");
        Assert.False(File.Exists(newVideo), "new primary path emptied by rollback");
        Assert.False(File.Exists(newCaption), "new sidecar path emptied by rollback");
        Assert.Equal("v", File.ReadAllText(oldVideo));
        Assert.Equal("c", File.ReadAllText(oldCaption));
    }
}
