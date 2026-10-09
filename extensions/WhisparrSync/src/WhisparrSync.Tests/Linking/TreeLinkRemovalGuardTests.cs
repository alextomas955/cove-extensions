using WhisparrSync.Contracts;
using WhisparrSync.Linking;

namespace WhisparrSync.Tests.Linking;

// The one check standing between a reader's media and a removal. Every case here is arranged as
// the state it describes on disk, and the two that matter are the file this extension did not
// compose and the pass whose reading of the library stopped short: both are folders a removal
// would empty if the check read them wrong.
public sealed class TreeLinkRemovalGuardTests
{
    private const string CoveRoot = "/data";
    private const string RemoteId = "tt1234567";
    private const string LibraryFile = "/data/Blue Harbor/a scene the reader named.mp4";

    private static readonly FileIdentity Identity = new(114, 544903);

    private static readonly DateTimeOffset Now =
        new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    // Well past any window a constant could name, so a case about the name is never also a case
    // about the clock.
    private static readonly DateTimeOffset LongSettled =
        Now - TreeLinkRemovalGuard.SettleWindow - TimeSpan.FromDays(30);

    [Fact]
    public void ANameThisExtensionComposedWhoseFileCarriesNoOtherNameMayBeRemoved()
    {
        var verdict = Decide(Read(names: 1));

        Assert.True(verdict.Removable);
        Assert.Null(verdict.Kept);
    }

    // The file the instance downloaded into the folder and the one a reader dropped there. Neither
    // carries its own identity as its name, neither has a library row for the tree is invisible to
    // the host's scan, and no amount of waiting makes either safe: this name is the only one its
    // bytes have.
    [Fact]
    public void AFileThisExtensionDidNotComposeIsKeptHoweverLongItHasSatThere()
    {
        var verdict = TreeLinkRemovalGuard.Decide(
            EntityFolder,
            EntityFolder + "/Studio.Name.-.2025-01-01.-.Scene.XXX.1080p.WEBDL.mp4",
            Read(names: 1),
            libraryReadToTheEnd: true,
            Now);

        Assert.False(verdict.Removable);
        Assert.Equal(TreeNameKept.NotComposedHere, verdict.Kept);
    }

    // A rename by the reader, by the Renamer or by the instance, and a move within one drive, all
    // leave the file with its library name and its link. Two names is what says the library's own
    // name is still there.
    [Fact]
    public void ANameWhoseFileStillCarriesAnotherNameIsKept()
    {
        var verdict = Decide(Read(names: 2));

        Assert.False(verdict.Removable);
        Assert.Equal(TreeNameKept.StillNamedElsewhere, verdict.Kept);
    }

    [Theory]
    [InlineData("/data/.wsync-v3")]
    [InlineData("/data/.wsync-v3/tt1234567")]
    [InlineData("/data/Blue Harbor/a scene the reader named.mp4")]
    public void ANameThatIsNotDirectlyInsideTheEntitysFolderIsKept(string path)
    {
        var verdict = TreeLinkRemovalGuard.Decide(
            EntityFolder, path, Read(names: 1), libraryReadToTheEnd: true, Now);

        Assert.False(verdict.Removable);
        Assert.Equal(TreeNameKept.OutsideTheEntityFolder, verdict.Kept);
    }

    // Removable by every other reading, and in the folder of the entity beside this one. A pass
    // that composed the wrong folder would otherwise empty its neighbour.
    [Fact]
    public void AnOtherwiseRemovableNameInAnotherEntitysFolderIsKept()
    {
        var beside = TreePathGuard.EntityFolderIn(
            TreePathGuard.TreeRootUnder(CoveRoot, WhisparrGeneration.V3)!, "tt7654321")!;

        var verdict = TreeLinkRemovalGuard.Decide(
            EntityFolder,
            TreePathGuard.LinkPathIn(beside, Identity, LibraryFile)!,
            Read(names: 1),
            libraryReadToTheEnd: true,
            Now);

        Assert.False(verdict.Removable);
        Assert.Equal(TreeNameKept.OutsideTheEntityFolder, verdict.Kept);
    }

    // The signal a removal rests on is that no library file answers to this name's file, and a read
    // that stopped short establishes nothing about the files it never reached.
    [Fact]
    public void NothingIsRemovedWhereTheEntitysLibraryFilesWereNotReadToTheEnd()
    {
        var verdict = TreeLinkRemovalGuard.Decide(
            EntityFolder, ComposedName, Read(names: 1), libraryReadToTheEnd: false, Now);

        Assert.False(verdict.Removable);
        Assert.Equal(TreeNameKept.LibraryNotReadToTheEnd, verdict.Kept);
    }

    [Fact]
    public void ANameWhoseIdentityCouldNotBeReadIsKept()
    {
        var verdict = Decide(read: null);

        Assert.False(verdict.Removable);
        Assert.Equal(TreeNameKept.IdentityCouldNotBeRead, verdict.Kept);
    }

    [Fact]
    public void ANameChangedInsideTheSettleWindowIsKeptAndReportedAsWaiting()
    {
        var verdict = Decide(
            new ProbedLink(Identity, 1, Now - TreeLinkRemovalGuard.SettleWindow + TimeSpan.FromMinutes(1)));

        Assert.False(verdict.Removable);
        Assert.Equal(TreeNameKept.WaitingToSettle, verdict.Kept);
    }

    private static string EntityFolder
        => TreePathGuard.EntityFolderIn(
            TreePathGuard.TreeRootUnder(CoveRoot, WhisparrGeneration.V3)!, RemoteId)!;

    private static string ComposedName
        => TreePathGuard.LinkPathIn(EntityFolder, Identity, LibraryFile)!;

    private static ProbedLink Read(int names) => new(Identity, names, LongSettled);

    private static TreeNameVerdict Decide(ProbedLink? read)
        => TreeLinkRemovalGuard.Decide(
            EntityFolder, ComposedName, read, libraryReadToTheEnd: true, Now);
}
