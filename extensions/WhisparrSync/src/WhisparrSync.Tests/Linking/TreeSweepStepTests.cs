using WhisparrSync.Contracts;
using WhisparrSync.Linking;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.Linking;

// Driven against the recording seam rather than a disk: what is asserted is which names the pass
// took back and which it left, which a real filesystem answers but does not record.
//
// The tree is arranged by placing files and second names for them, never by running the pass that
// builds folders. What the pass has to decide is what is in the tree, whatever put it there.
public sealed class TreeSweepStepTests
{
    private const string CoveRoot = "/data";
    private const string OtherRoot = "/data2";
    private const string RemoteId = "tt1234567";
    private const string LibraryFolder = CoveRoot + "/Blue Harbor";

    private static readonly DateTimeOffset Changed = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset Now = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    // The whole reason this pass walks the tree rather than the entities a run offered. The reader
    // deleted the entity's last file, so the entity owns nothing, no walk of the library reaches it,
    // and the name in its folder is the only thing still holding the bytes of the deleted file.
    [Fact]
    public void ALinkWhoseLibraryFileIsGoneIsTakenBackWithNothingHavingOfferedItsEntity()
    {
        var port = new RecordingTreeLinkPort();
        var link = Linked(port, LibraryFolder + "/scene.mp4", new FileIdentity(114, 544903));
        port.Forget(LibraryFolder + "/scene.mp4");

        var swept = Sweep(port);

        Assert.Equal(1, swept.Removed);
        Assert.Empty(port.NamesIn(EntityFolder));
        Assert.Contains(port.Calls, call => call.Verb == "remove" && call.Path == link);
    }

    // A rename by the reader, by the Renamer or by the instance, and a move within one drive, are
    // one act to the filesystem: the name changes and the identity does not. The library's own name
    // for the file is what the second name in the count is.
    [Fact]
    public void ALinkWhoseFileTheLibraryStillNamesIsKept()
    {
        var port = new RecordingTreeLinkPort();
        Linked(port, LibraryFolder + "/scene.mp4", new FileIdentity(114, 544903));

        var swept = Sweep(port);

        Assert.Equal(0, swept.Removed);
        Assert.Equal(1, swept.StillNamedElsewhere);
        Assert.Single(port.NamesIn(EntityFolder));
    }

    // The file the instance downloaded into the folder and could not place, or one a reader put
    // there. It has no library row, it is unpaired for good, and it ages past any window: the only
    // thing that keeps it is that this extension did not compose its name.
    [Fact]
    public void AFileThisExtensionDidNotComposeIsKeptAndCountedHoweverOldItIs()
    {
        var port = new RecordingTreeLinkPort();
        var left = EntityFolder + "/Studio.Name.-.2025-01-01.-.Scene.XXX.1080p.WEBDL.mp4";
        port.Place(left, new FileIdentity(114, 999), Now - TimeSpan.FromDays(400));

        var swept = Sweep(port);

        Assert.Equal(0, swept.Removed);
        Assert.Equal(1, swept.NotComposedHere);
        Assert.Equal([Path.GetFileName(left)], port.NamesIn(EntityFolder));
        Assert.DoesNotContain(port.Calls, call => call.Verb == "remove");
    }

    // A tree root holds folders this extension never made: both generations import into a folder of
    // their own naming, and a folder full of names nothing derived from a file is what that leaves.
    [Fact]
    public void AFolderInTheTreeThisProductDidNotBuildKeepsEveryNameInIt()
    {
        var port = new RecordingTreeLinkPort();
        var theirs = TreeRoot + "/Blue Harbor";
        var left = theirs + "/Blue.Harbor.-.2025-01-01.-.Scene.XXX.1080p.WEBDL.mp4";
        port.Place(left, new FileIdentity(114, 8080), Now - TimeSpan.FromDays(400));

        var swept = Sweep(port);

        Assert.Equal(0, swept.Removed);
        Assert.Equal(1, swept.NotComposedHere);
        Assert.Equal([Path.GetFileName(left)], port.NamesIn(theirs));
    }

    // The removal rests on no library file answering to the name's own file, and a run that stopped
    // short establishes nothing about the files it never reached. The arrangement is one the
    // finished run would empty, and no tree is even listed.
    [Fact]
    public void ARunThatDidNotReachTheEndOfTheLibraryListsNothingAndRemovesNothing()
    {
        var port = new RecordingTreeLinkPort();
        Linked(port, LibraryFolder + "/scene.mp4", new FileIdentity(114, 544903));
        port.Forget(LibraryFolder + "/scene.mp4");

        var swept = new TreeSweepStep(port, Clock).Over(
            [CoveRoot], WhisparrGeneration.V3, libraryReadToTheEnd: false, TestCt);

        Assert.Equal(TreeSweep.Nothing, swept);
        Assert.DoesNotContain(port.Calls, call => call.Verb == "folders");
        Assert.Single(port.NamesIn(EntityFolder));
    }

    [Fact]
    public void ANameWhoseFileChangedInsideTheSettleWindowIsCountedAsWaitingAndLeft()
    {
        var port = new RecordingTreeLinkPort();
        var justWritten = LibraryFolder + "/just written.mp4";
        Linked(port, justWritten, new FileIdentity(114, 77), Now - TimeSpan.FromMinutes(1));
        port.Forget(justWritten);

        var swept = Sweep(port);

        Assert.Equal(0, swept.Removed);
        Assert.Equal(1, swept.WaitingToSettle);
        Assert.Single(port.NamesIn(EntityFolder));
    }

    [Fact]
    public void ARemovalTheSeamRefusesIsCountedRatherThanRetried()
    {
        var port = new RecordingTreeLinkPort();
        var link = Linked(port, LibraryFolder + "/scene.mp4", new FileIdentity(114, 544903));
        port.Forget(LibraryFolder + "/scene.mp4");
        port.RefuseRemovalOf(link);

        var swept = Sweep(port);

        Assert.Equal(0, swept.Removed);
        Assert.Equal(1, swept.Refused);
        Assert.Equal(1, port.Calls.Count(call => call.Verb == "remove"));
    }

    // A library has roots on more than one drive, and a tree lives on the drive its files live on,
    // so a run that swept the first root alone would leave every name under the second.
    [Fact]
    public void EveryRootsTreeIsSwept()
    {
        var port = new RecordingTreeLinkPort();
        var here = LibraryFolder + "/scene.mp4";
        var there = OtherRoot + "/Blue Harbor/scene.mp4";
        Linked(port, here, new FileIdentity(114, 544903));
        Linked(port, there, new FileIdentity(222, 9), under: OtherRoot);
        port.Forget(here);
        port.Forget(there);

        var swept = new TreeSweepStep(port, Clock).Over(
            [CoveRoot, OtherRoot], WhisparrGeneration.V3, libraryReadToTheEnd: true, TestCt);

        Assert.Equal(2, swept.Removed);
        Assert.Empty(port.NamesIn(EntityFolder));
        Assert.Empty(port.NamesIn(EntityFolderUnder(OtherRoot)));
    }

    // The test that goes red the day someone reintroduces a collection. An entity reaches the size
    // of the library, so the pass may ask about one name at a time and hold none of them: one
    // listing of the tree, one listing per folder in it, one identity read per name.
    [Fact]
    public void ASweepOverManyNamesAsksAboutEachOneAndHoldsNone()
    {
        const int Owned = 500;
        var port = new RecordingTreeLinkPort();
        for (var index = 0; index < Owned; index++)
        {
            Linked(
                port,
                $"{LibraryFolder}/scene {index}.mp4",
                new FileIdentity(114, (ulong)(544903 + index)));
        }

        Sweep(port);
        var asked = port.Calls;

        Assert.Equal(1, asked.Count(call => call.Verb == "folders"));
        Assert.Equal(1, asked.Count(call => call.Verb == "names"));
        Assert.Equal(Owned, asked.Count(call => call.Verb == "identify"));
    }

    private static string TreeRoot
        => TreePathGuard.TreeRootUnder(CoveRoot, WhisparrGeneration.V3)!;

    private static string EntityFolder => EntityFolderUnder(CoveRoot);

    private static TimeProvider Clock { get; } = new FixedClock(Now);

    private static string EntityFolderUnder(string coveRoot)
        => TreePathGuard.EntityFolderIn(
            TreePathGuard.TreeRootUnder(coveRoot, WhisparrGeneration.V3)!, RemoteId)!;

    // One library file and the name this extension writes for it, which is the state every pass
    // before this one leaves behind. Answers the link's path.
    private static string Linked(
        RecordingTreeLinkPort port,
        string libraryFile,
        FileIdentity identity,
        DateTimeOffset? changed = null,
        string under = CoveRoot)
    {
        port.Place(libraryFile, identity, changed ?? Changed);
        var link = TreePathGuard.LinkPathIn(EntityFolderUnder(under), identity, libraryFile)!;
        port.PlaceLink(link, libraryFile);

        return link;
    }

    private static TreeSweep Sweep(RecordingTreeLinkPort port)
        => new TreeSweepStep(port, Clock).Over(
            [CoveRoot], WhisparrGeneration.V3, libraryReadToTheEnd: true, TestCt);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
