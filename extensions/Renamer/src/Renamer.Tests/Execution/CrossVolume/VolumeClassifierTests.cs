using Renamer.Execution;

namespace Renamer.Tests.Execution.CrossVolume;

/// <summary>
/// Pure-string assertions for <see cref="VolumeClassifier"/>: two paths on one volume are the same volume,
/// different volumes are cross-volume, and the volume key is what every cross-volume grouping agrees on.
/// No disk, no TempDir.
/// </summary>
/// <remarks>
/// Volume identity is expressed differently per platform, so each case runs on the platform whose semantics it
/// asserts and skips WITH a reason on the other — a Windows drive literal has no root on Unix, and a Unix mount
/// path has no meaning on Windows, so one shared assertion would be testing neither.
/// </remarks>
[Trait("Tier", "L0")]
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
    public void InFolderRenamerPair_IsSameVolume_GatesTheFastPath()
    {
        WindowsOnly();

        // A plain in-folder renamer keeps the same volume. The executor then keeps the DiskMover atomic
        // File.Move fast path — the MOVE-01 contract at the unit level.
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
    public void InFolderRenamerPairOnAMount_IsSameVolume_GatesTheFastPath()
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
}
