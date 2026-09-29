using Renamer.Planner;

namespace Renamer.Tests.Planner;

public sealed class VolumeClassifierTests
{
    // The real mount table's stand-in. The Unix cases need no second drive.
    private static readonly string[] Mounts = ["/", "/mnt/media", "/mnt/media2", "/srv/archive"];

    private static void WindowsOnly()
        => Assert.SkipUnless(OperatingSystem.IsWindows(), "asserts Windows drive/UNC root semantics");

    private static void UnixOnly()
        => Assert.SkipWhen(OperatingSystem.IsWindows(), "asserts Unix mount-point semantics");

    [Theory]
    [InlineData(@"C:\media\a.mkv", @"C:\archive\b.mkv", true)]
    [InlineData(@"C:\media\a.mkv", @"D:\media\a.mkv", false)]
    // Drive-letter case is ignored on Windows.
    [InlineData(@"C:\x\a.mkv", @"c:\y\b.mkv", true)]
    [InlineData(@"\\server\share\a.mkv", @"\\server\share\b.mkv", true)]
    [InlineData(@"\\server\share\a.mkv", @"C:\a.mkv", false)]
    public void OnWindows_TheVolumeIsThePathRoot(string a, string b, bool sameVolume)
    {
        WindowsOnly();
        Assert.Equal(sameVolume, VolumeClassifier.SameVolume(a, b));
    }

    [Theory]
    [InlineData("/mnt/media/a.mkv", "/mnt/media/sub/b.mkv", true)]
    [InlineData("/mnt/media/a.mkv", "/srv/archive/a.mkv", false)]
    // "/mnt/media2" must not be read as living under "/mnt/media", or a distinct disk would take the
    // atomic path and skip the free-space and verification spine.
    [InlineData("/mnt/media/a.mkv", "/mnt/media2/a.mkv", false)]
    public void OnUnix_TheVolumeIsTheEnclosingMount(string a, string b, bool sameVolume)
    {
        UnixOnly();
        Assert.Equal(sameVolume, VolumeClassifier.SameVolume(a, b, Mounts));
    }

    [Fact]
    public void PathOnNoListedMount_FallsBackToRoot()
    {
        UnixOnly();
        Assert.Equal("/", VolumeClassifier.VolumeKey("/elsewhere/a.mkv", Mounts));
        Assert.True(VolumeClassifier.SameVolume("/elsewhere/a.mkv", "/other/b.mkv", Mounts));
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
