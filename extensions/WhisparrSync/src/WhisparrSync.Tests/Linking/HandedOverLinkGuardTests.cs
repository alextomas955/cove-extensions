using WhisparrSync.Linking;

namespace WhisparrSync.Tests.Linking;

// The check that tells the instance reporting back a file this extension handed it from the
// instance reporting a file it downloaded. Getting the first wrong gives a reader a second row per
// owned file; getting the second wrong strands a download in a folder the host's scan never reads.
public sealed class HandedOverLinkGuardTests
{
    private const string CoveRoot = "/data";
    private const string EntityFolder = CoveRoot + "/.wsync-v3/tt1234567";

    private static readonly string[] CoveRoots = [CoveRoot];

    private static readonly FileIdentity Identity = new(0x38, 0x200000007e4eb);

    private static readonly DateTimeOffset Changed = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // The name the linking half composes, transcribed the way the product spells it rather than
    // built from the product: a spelling read back off the code under test would agree with itself.
    private const string ComposedName = "38-200000007e4eb.mp4";

    [Fact]
    public void ALinkThisExtensionComposedIsOneItHandedOver()
        => Assert.True(Asked(EntityFolder + "/" + ComposedName));

    // What an instance downloads into the folder keeps the name the instance gave it, so the
    // arrival placement still has it to work on.
    [Fact]
    public void AFileTheInstanceDownloadedIntoTheFolderIsNotOne()
        => Assert.False(
            Asked(EntityFolder + "/Studio.Name.-.2025-01-01.-.Scene.XXX.1080p.WEBDL.mp4"));

    // A name spelled for some other file's identity is not a link to the file it holds, and the
    // library holds no row for it.
    [Fact]
    public void ANameSpelledForAnotherFilesIdentityIsNotOne()
        => Assert.False(Asked(EntityFolder + "/38-100000000000f.mp4"));

    // The reader's own library, where a file named after an identity is a file they named that way.
    [Fact]
    public void AComposedNameOutsideEveryTreeIsNotOne()
        => Assert.False(Asked(CoveRoot + "/per-studio/" + ComposedName));

    [Fact]
    public void ANameNothingCouldBeReadAtIsNotOne()
        => Assert.False(
            HandedOverLinkGuard.NamesALinkComposedHere(
                EntityFolder + "/" + ComposedName, CoveRoots, _ => null));

    // The reading costs a filesystem call, and every ordinary delivery would pay it.
    [Fact]
    public void ADeliveryFromOutsideEveryTreeReadsNothingFromDisk()
    {
        var asked = new List<string>();

        HandedOverLinkGuard.NamesALinkComposedHere(
            CoveRoot + "/per-studio/a scene.mp4",
            CoveRoots,
            path =>
            {
                asked.Add(path);
                return null;
            });

        Assert.Empty(asked);
    }

    private static bool Asked(string path)
        => HandedOverLinkGuard.NamesALinkComposedHere(
            path, CoveRoots, _ => new ProbedLink(Identity, 2, Changed));
}
