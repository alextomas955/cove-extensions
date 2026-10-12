using WhisparrSync.Contracts;
using WhisparrSync.Linking;

namespace WhisparrSync.Tests.Linking;

// Pure throughout, so every branch is reachable here and none of it needs a disk.
public sealed class TreePathGuardTests
{
    private const string CoveRoot = "/data";
    private static readonly FileIdentity Scene = new(114, 544903);

    [Fact]
    public void EachGenerationHasItsOwnTreeAtTheTopOfACoveRoot()
    {
        var v3 = TreePathGuard.TreeRootUnder(CoveRoot, WhisparrGeneration.V3);
        var v2 = TreePathGuard.TreeRootUnder(CoveRoot, WhisparrGeneration.V2);

        Assert.Equal("/data/.wsync-v3", v3);
        Assert.Equal("/data/.wsync-v2", v2);
        Assert.NotEqual(v2, v3);
    }

    [Fact]
    public void AnIdentifierAFolderNameCanCarryIsTheFolderName()
    {
        Assert.Equal(
            "/data/.wsync-v3/tt1234567",
            TreePathGuard.EntityFolderIn("/data/.wsync-v3", "tt1234567"));
    }

    [Fact]
    public void AnIdentifierAFolderNameCannotCarryIsReplacedAndMarked()
    {
        var folder = TreePathGuard.EntityFolderIn("/data/.wsync-v3", "studio/one:two");

        Assert.NotNull(folder);
        var name = folder["/data/.wsync-v3/".Length..];
        Assert.StartsWith("studio-one-two-", name, StringComparison.Ordinal);
        Assert.DoesNotContain('/', name);
        Assert.DoesNotContain(':', name);
    }

    // Two entities sharing a folder would attach each other's files to each other.
    [Theory]
    [InlineData("studio/one", "studio:one")]
    [InlineData("a.b", "a b")]
    public void TwoIdentifiersThatSpellAlikeOnceReplacedStillLandApart(string first, string second)
        => Assert.NotEqual(
            TreePathGuard.EntityFolderIn("/data/.wsync-v3", first),
            TreePathGuard.EntityFolderIn("/data/.wsync-v3", second));

    [Fact]
    public void TwoLongIdentifiersDifferingPastTheCapStillLandApart()
    {
        var shared = new string('a', 200);

        Assert.NotEqual(
            TreePathGuard.EntityFolderIn("/data/.wsync-v3", shared + "one"),
            TreePathGuard.EntityFolderIn("/data/.wsync-v3", shared + "two"));
    }

    // A folder name the platform reads as the folder above or as the folder itself would put an
    // entity's links loose in the tree root, where nothing identifies whose they are.
    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    public void AnIdentifierNamingAFolderRatherThanASpellingGetsOneOfItsOwn(string remoteId)
    {
        var folder = TreePathGuard.EntityFolderIn("/data/.wsync-v3", remoteId);

        Assert.NotNull(folder);
        Assert.StartsWith("/data/.wsync-v3/", folder, StringComparison.Ordinal);
        Assert.NotEqual("/data/.wsync-v3", folder);
    }

    [Fact]
    public void ALinkIsNamedForTheIdentityOfTheFileAndKeepsItsExtension()
    {
        var first = TreePathGuard.LinkPathIn(
            "/data/.wsync-v3/tt1234567", Scene, "/data/studio/Scene Title (2019).mp4");
        var renamed = TreePathGuard.LinkPathIn(
            "/data/.wsync-v3/tt1234567", Scene, "/data/elsewhere/Something Else.mp4");

        Assert.Equal("/data/.wsync-v3/tt1234567/72-85087.mp4", first);
        Assert.Equal(first, renamed);
    }

    [Fact]
    public void ALinkPathThatWouldLeaveItsFolderIsRefused()
        => Assert.Null(TreePathGuard.LinkPathIn("..", Scene, "/data/studio/scene.mp4"));

    [Fact]
    public void ANameSpellingTheIdentityOfTheFileAtItIsOneThisExtensionComposed()
        => Assert.True(TreePathGuard.IsComposedName("72-85087.mp4", Scene));

    // The spelling cannot be forged, because it is checked against the identity of the file the
    // name actually holds. This is what decides whether a name may be removed.
    [Theory]
    [InlineData("72-85088.mp4")]
    [InlineData("Scene Title (2019).mp4")]
    [InlineData("72-85087")]
    public void ANameAnythingElseLeftIsNotOneThisExtensionComposed(string name)
        => Assert.False(TreePathGuard.IsComposedName(name, Scene));

    [Theory]
    [InlineData("/data/.wsync-v3/tt1234567/72-85087.mp4")]
    [InlineData("/data2/.wsync-v2/studio/72-85087.mp4")]
    public void APathBelowAGenerationsTreeIsInsideATree(string path)
        => Assert.True(TreePathGuard.IsInsideATree(path, ["/data", "/data2"]));

    // A tree is at the top of a Cove root and nowhere else, so a reader's own folder of that name
    // is a library folder like any other.
    [Theory]
    [InlineData("/data/studio/.wsync-v3/scene.mp4")]
    [InlineData("/data/studio/scene.mp4")]
    [InlineData("/elsewhere/.wsync-v3/scene.mp4")]
    public void APathAnywhereElseIsNotInsideATree(string path)
        => Assert.False(TreePathGuard.IsInsideATree(path, ["/data", "/data2"]));

    [Fact]
    public void TheIgnoreFileSitsAtTheTreeRoot()
        => Assert.Equal("/data/.wsync-v3/.coveignore", TreePathGuard.IgnoreFileIn("/data/.wsync-v3"));
}
