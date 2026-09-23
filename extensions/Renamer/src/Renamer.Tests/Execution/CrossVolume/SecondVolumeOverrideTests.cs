using Renamer.Planner;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Execution.CrossVolume;

[Collection(SubstDriveScope.CollectionName)]
public sealed class SecondVolumeOverrideTests
{
    [Fact]
    public void SameVolumeOverride_FailsLoudly()
    {
        // A directory under the temp tree - by construction the same volume the tests move from,
        // which is exactly the misconfiguration (e.g. macOS with the variable pointed at ~/tmp).
        using var sameVolume = new TempDir();

        var ex = Assert.Throws<InvalidOperationException>(() => new SecondVolume(sameVolume.Root));

        // The message must name the variable and both volume keys: a maintainer reading CI output
        // has to see that the two resolved to one volume, not merely that something was rejected.
        Assert.Contains("COVE_TEST_SECOND_VOLUME", ex.Message);
        Assert.Contains(sameVolume.Root, ex.Message);
        Assert.Contains(VolumeClassifier.VolumeKey(Path.GetTempPath()), ex.Message);
    }

    [Fact]
    public void DifferentVolumeOverride_IsHonored()
    {
        Assert.SkipUnless(SecondVolume.IsAvailable, SecondVolume.UnavailableReason);

        // The inferred arm supplies a genuinely different filesystem (a subst root on Windows, a
        // /dev/shm dir on Unix); feeding its root back in as an override is what puts the override
        // arm under test without needing a second real volume of its own.
        using var provider = new SecondVolume(overridePath: null);
        string overrideDir = provider.Root;

        var overridden = new SecondVolume(overrideDir);
        string root = overridden.Root;
        try
        {
            Assert.True(Directory.Exists(root));

            // A fresh per-instance subdir under the override, never the override dir itself:
            // parallel fixtures must not share one directory.
            Assert.Contains("renamer-vol-", root);
            Assert.NotEqual(
                Path.TrimEndingDirectorySeparator(overrideDir),
                Path.TrimEndingDirectorySeparator(root));
            Assert.Equal(
                Path.TrimEndingDirectorySeparator(overrideDir),
                Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(root)!));

            // The point of the whole fixture: the classifier the 12 gated tests key on must call
            // this cross-volume. Asserting the classifier (not just "a different path") is what
            // makes a green run mean the copy/verify/delete path was actually taken.
            Assert.NotEqual(
                VolumeClassifier.VolumeKey(Path.GetTempPath()),
                VolumeClassifier.VolumeKey(root));
        }
        finally
        {
            overridden.Dispose();
        }

        // Dispose owns only what it created. Removing a caller-supplied override directory would
        // delete a real mount point's contents on a machine where the variable names one.
        Assert.False(Directory.Exists(root));
        Assert.True(Directory.Exists(overrideDir));
    }
}
