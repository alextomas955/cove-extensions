using Renamer.Planner;

namespace Renamer.Tests.Execution.CrossVolume;

public sealed class VolumeClassifierTests
{
    // The real mount table's stand-in. The Unix cases need no second drive.
    private static readonly string[] Mounts = ["/", "/mnt/media", "/mnt/media2", "/srv/archive"];

    private static void WindowsOnly()
        => Assert.SkipUnless(OperatingSystem.IsWindows(), "asserts Windows drive/UNC root semantics");

    private static void UnixOnly()
        => Assert.SkipWhen(OperatingSystem.IsWindows(), "asserts Unix mount-point semantics");

    [Fact]
    public void SameDriveRoot_DifferentFolders_IsSameVolume()
    {
        WindowsOnly();
        Assert.True(VolumeClassifier.SameVolume(@"C:\media\a.mkv", @"C:\archive\b.mkv"));
    }

    [Fact]
    public void DifferentDriveRoots_IsCrossVolume()
    {
        WindowsOnly();
        Assert.False(VolumeClassifier.SameVolume(@"C:\media\a.mkv", @"D:\media\a.mkv"));
    }

    [Fact]
    public void DriveLetterCaseDiffers_IsSameVolume()
    {
        WindowsOnly();
        Assert.True(
            VolumeClassifier.SameVolume(@"C:\x\a.mkv", @"c:\y\b.mkv"),
            "drive-letter case must be ignored on Windows");
    }

    [Fact]
    public void TwoPathsUnderOneUncShare_IsSameVolume()
    {
        WindowsOnly();
        Assert.True(VolumeClassifier.SameVolume(@"\\server\share\a.mkv", @"\\server\share\b.mkv"));
    }

    [Fact]
    public void UncShareVersusDriveRoot_IsCrossVolume()
    {
        WindowsOnly();
        Assert.False(VolumeClassifier.SameVolume(@"\\server\share\a.mkv", @"C:\a.mkv"));
    }

    [Fact]
    public void InFolderRenamePair_IsSameVolume_GatesTheFastPath()
    {
        WindowsOnly();

        // A plain in-folder renamer keeps the same volume. The executor then keeps the DiskMover atomic
        // File.Move fast path: the same-volume contract at the unit level.
        Assert.True(VolumeClassifier.SameVolume(@"C:\media\clip.mkv", @"C:\media\Renamed.mkv"));
    }

    [Fact]
    public void SameMount_DifferentFolders_IsSameVolume()
    {
        UnixOnly();
        Assert.True(VolumeClassifier.SameVolume("/mnt/media/a.mkv", "/mnt/media/sub/b.mkv", Mounts));
    }

    [Fact]
    public void DifferentMounts_IsCrossVolume()
    {
        UnixOnly();
        Assert.False(VolumeClassifier.SameVolume("/mnt/media/a.mkv", "/srv/archive/a.mkv", Mounts));
    }

    [Fact]
    public void MountNameIsAPrefixOfAnother_IsStillCrossVolume()
    {
        UnixOnly();

        // "/mnt/media2" must not be read as living under "/mnt/media", or a distinct disk would take the atomic
        // path and skip the free-space and verification spine.
        Assert.False(VolumeClassifier.SameVolume("/mnt/media/a.mkv", "/mnt/media2/a.mkv", Mounts));
    }

    [Fact]
    public void PathOnNoListedMount_FallsBackToRoot()
    {
        UnixOnly();
        Assert.Equal("/", VolumeClassifier.VolumeKey("/elsewhere/a.mkv", Mounts));
        Assert.True(VolumeClassifier.SameVolume("/elsewhere/a.mkv", "/other/b.mkv", Mounts));
    }

    [Fact]
    public void InFolderRenamePairOnAMount_IsSameVolume_GatesTheFastPath()
    {
        UnixOnly();
        Assert.True(VolumeClassifier.SameVolume("/mnt/media/clip.mkv", "/mnt/media/Renamed.mkv", Mounts));
    }

    [Fact]
    public void RelativePath_HasAnEmptyVolumeKey()
    {
        UnixOnly();
        Assert.Equal(string.Empty, VolumeClassifier.VolumeKey("relative/a.mkv", Mounts));
    }

    [Fact]
    public void TheProductionMountTable_SeesPastRoot_AndSeparatesTwoRealMounts()
    {
        UnixOnly();
        Assert.SkipUnless(Directory.Exists("/dev/shm"), "needs /dev/shm as a second real mount");

        // No mountPoints argument anywhere below - this is the production default path.
        Assert.Equal("/dev/shm", VolumeClassifier.VolumeKey("/dev/shm/clip.mkv"));
        Assert.False(
            VolumeClassifier.SameVolume("/tmp/clip.mkv", "/dev/shm/clip.mkv"),
            "the real mount table collapsed two genuinely different filesystems onto one volume key, "
                + "so a cross-device move would take the atomic fast path and skip the free-space "
                + "pre-check, the copy verification and the heavy-batch warning");
    }
}
