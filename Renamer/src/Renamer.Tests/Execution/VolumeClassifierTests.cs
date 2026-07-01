using Renamer.Execution;

namespace Renamer.Tests.Execution;

/// <summary>
/// Pure-string assertions for <see cref="VolumeClassifier.SameVolume"/>: two paths under one root
/// (drive or UNC share) are the same volume; different roots (other drive, or UNC-vs-drive) are
/// cross-volume; the drive letter is compared case-insensitively on Windows. No disk, no TempDir —
/// these run identically on any host because the classifier only reads path roots.
/// </summary>
[Trait("Tier", "Unit")]
public sealed class VolumeClassifierTests
{
    [Fact]
    public void SameDriveRoot_DifferentFolders_IsSameVolume()
    {
        Assert.True(VolumeClassifier.SameVolume(@"C:\media\a.mkv", @"C:\archive\b.mkv"));
    }

    [Fact]
    public void DifferentDriveRoots_IsCrossVolume()
    {
        Assert.False(VolumeClassifier.SameVolume(@"C:\media\a.mkv", @"D:\media\a.mkv"));
    }

    [Fact]
    public void DriveLetterCaseDiffersOnWindows_IsSameVolume()
    {
        // OrdinalIgnoreCase on Windows: C: and c: are the same root.
        bool result = VolumeClassifier.SameVolume(@"C:\x\a.mkv", @"c:\y\b.mkv");
        if (OperatingSystem.IsWindows())
        {
            Assert.True(result, "drive-letter case must be ignored on Windows");
        }
    }

    [Fact]
    public void TwoPathsUnderOneUncShare_IsSameVolume()
    {
        Assert.True(VolumeClassifier.SameVolume(@"\\server\share\a.mkv", @"\\server\share\b.mkv"));
    }

    [Fact]
    public void UncShareVersusDriveRoot_IsCrossVolume()
    {
        Assert.False(VolumeClassifier.SameVolume(@"\\server\share\a.mkv", @"C:\a.mkv"));
    }

    [Fact]
    public void InFolderRenamerPair_IsSameVolume_GatesTheFastPath()
    {
        // A plain in-folder renamer keeps the same root, so the executor keeps the DiskMover
        // atomic File.Move fast path — the MOVE-01 contract at the unit level.
        Assert.True(VolumeClassifier.SameVolume(@"C:\media\clip.mkv", @"C:\media\Renamed.mkv"));
    }
}
