using WhisparrSync.Contracts;
using WhisparrSync.Linking;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.Linking;

// Driven against the recording seam rather than a disk: what is asserted is which questions the
// pass asked and in what order, which a real filesystem answers but does not record.
public sealed class TreeReconcileStepTests
{
    private const string CoveRoot = "/data";
    private const string RemoteId = "tt1234567";
    private const string Folder = CoveRoot + "/Blue Harbor";

    private static readonly DateTimeOffset Changed =
        new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // Well past the settle window from everything the helper below places, so a case about a name
    // is never also a case about the clock.
    private static readonly DateTimeOffset Now =
        new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    [Fact]
    public async Task EveryFileTheEntityOwnsGetsOneNameInTheFolderBuiltForIt()
    {
        var port = new RecordingTreeLinkPort();
        var files = Placed(port, count: 3);

        var built = await BuildAsync(port, files);

        Assert.Equal(EntityFolder, built.EntityFolder);
        Assert.Equal(3, built.Linked);
        Assert.Equal(0, built.AlreadyThere);
        Assert.Equal(3, built.LinksThere);
        Assert.Equal(files.Select(file => LinkNameOf(file.Identity, file.Path)), port.NamesIn(EntityFolder!));
    }

    [Fact]
    public async Task ASecondPassOverTheSameEntityMakesNoSecondName()
    {
        var port = new RecordingTreeLinkPort();
        var files = Placed(port, count: 3);
        await BuildAsync(port, files);

        var again = await BuildAsync(port, files);

        Assert.Equal(0, again.Linked);
        Assert.Equal(3, again.AlreadyThere);
        Assert.Equal(3, port.NamesIn(EntityFolder!).Count());
    }

    // A rename on either side leaves the identity alone, so the name the pass composes is the one
    // already there. Without that the folder would gain a second name for one file on every rename.
    [Fact]
    public async Task AFileRenamedSinceTheLastPassStillMatchesItsOwnLink()
    {
        var port = new RecordingTreeLinkPort();
        var files = Placed(port, count: 1);
        await BuildAsync(port, files);

        var renamed = Folder + "/what the reader called it instead.mp4";
        port.Place(renamed, files[0].Identity, Changed);

        var again = await BuildAsync(port, [new PlacedFile(renamed, files[0].Identity)]);

        Assert.Equal(0, again.Linked);
        Assert.Equal(1, again.AlreadyThere);
        Assert.Single(port.NamesIn(EntityFolder!));
    }

    // A hard link cannot cross a filesystem, and the act a caller reaches for instead is a copy,
    // which is a second set of the reader's bytes.
    [Fact]
    public async Task AFileOnAnotherDeviceIsCountedAndNothingIsCopied()
    {
        var port = new RecordingTreeLinkPort();
        var elsewhere = new PlacedFile("/other/Blue Harbor/scene.mp4", new FileIdentity(222, 9));
        port.Place(elsewhere.Path, elsewhere.Identity, Changed);
        port.AnswerLink(
            LinkPathOf(elsewhere.Identity, elsewhere.Path), LinkOutcome.OnAnotherDevice);

        var built = await BuildAsync(port, [elsewhere]);

        Assert.Equal(1, built.OnAnotherDevice);
        Assert.Equal(0, built.Linked);
        Assert.Empty(port.NamesIn(EntityFolder!));
    }

    [Fact]
    public async Task AFileThatCouldNotBeReadIsCountedAndNeverLinked()
    {
        var port = new RecordingTreeLinkPort();
        var gone = Folder + "/deleted between the query and the read.mp4";
        port.AnswerNothingFor(gone);

        var built = await BuildAsync(port, [new PlacedFile(gone, default)]);

        Assert.Equal(1, built.Refused);
        Assert.DoesNotContain(port.Calls, call => call.Verb == "link");
    }

    // The file keeps the host's scan out of everything below it, so it belongs where the tree
    // starts. Written inside an entity folder it would leave every other entity's links visible.
    [Fact]
    public async Task TheIgnoreFileIsWrittenAtTheTreeRootAndOncePerRun()
    {
        var port = new RecordingTreeLinkPort();
        const string Beside = "tt7654321";
        var step = new TreeReconcileStep(port, Clock);
        var files = Placed(port, count: 1);
        port.AnswerLink(
            TreePathGuard.LinkPathIn(
                TreePathGuard.EntityFolderIn(TreeRoot, Beside)!,
                files[0].Identity,
                files[0].Path)!,
            LinkOutcome.Linked);

        await step.BuildAsync(CoveRoot, WhisparrGeneration.V3, RemoteId, Streamed(files), TestCt);
        await step.BuildAsync(CoveRoot, WhisparrGeneration.V3, Beside, Streamed(files), TestCt);

        Assert.Equal([TreeRoot], port.IgnoredTrees);
        Assert.Equal(
            port.IgnoredTrees[0],
            port.Calls.First(call => call.Verb == "ignore").Path);
    }

    [Fact]
    public async Task TheIgnoreFileLandsBeforeTheFirstLink()
    {
        var port = new RecordingTreeLinkPort();
        var files = Placed(port, count: 1);

        await BuildAsync(port, files);

        Assert.True(
            port.Calls.FindIndex(call => call.Verb == "ignore")
                < port.Calls.FindIndex(call => call.Verb == "link"));
    }

    // The test that goes red the day someone reintroduces a collection. An entity reaches the size
    // of the library, so a pass may ask about one file at a time and hold none of them. The linking
    // half never lists the folder at all, and the half that takes names back lists it once and
    // streams it: one listing, one identity read per file and per name, one link attempt per file.
    [Fact]
    public async Task APassOverManyFilesAsksAboutEachOneAndHoldsNone()
    {
        const int Owned = 500;
        var port = new RecordingTreeLinkPort();
        var files = Placed(port, Owned);

        var built = await BuildAsync(port, files);
        var asked = port.Calls.ToList();

        Assert.Equal(Owned, built.Linked);
        Assert.Equal(1, asked.Count(call => call.Verb == "names"));
        Assert.Equal(Owned * 2, asked.Count(call => call.Verb == "identify"));
        Assert.Equal(Owned, asked.Count(call => call.Verb == "link"));
        Assert.True(
            asked.FindLastIndex(call => call.Verb == "link")
                < asked.FindIndex(call => call.Verb == "names"),
            "the folder was listed before the last file was linked");
    }

    // The reader threw the file away and the link was the only thing still holding its bytes, which
    // is the state this whole capability has to be able to end.
    [Fact]
    public async Task ALinkWhoseLibraryFileIsGoneIsRemoved()
    {
        var port = new RecordingTreeLinkPort();
        var files = Placed(port, count: 1);
        await BuildAsync(port, files);
        port.Forget(files[0].Path);

        var again = await BuildAsync(port, []);

        Assert.Equal(1, again.Swept.Removed);
        Assert.Empty(port.NamesIn(EntityFolder!));
    }

    // A rename by the reader, by the Renamer or by the instance, and a move within one drive, are
    // one act to the filesystem: the name changes and the identity does not. The library's own name
    // for the file is what the second name in the count is.
    [Fact]
    public async Task ALibraryFileRenamedKeepsItsLinkAndNothingIsRemoved()
    {
        var port = new RecordingTreeLinkPort();
        var files = Placed(port, count: 1);
        await BuildAsync(port, files);
        var renamed = Folder + "/what the reader called it instead.mp4";
        port.Place(renamed, files[0].Identity, Changed);
        port.Forget(files[0].Path);

        var again = await BuildAsync(port, [new PlacedFile(renamed, files[0].Identity)]);

        Assert.Equal(0, again.Swept.Removed);
        Assert.Equal(1, again.Swept.StillNamedElsewhere);
        Assert.Single(port.NamesIn(EntityFolder!));
    }

    // The file the instance downloaded into the folder and could not place, or one a reader put
    // there. It has no library row, it is unpaired for good, and it ages past any window: the only
    // thing that keeps it is that this extension did not compose its name.
    [Fact]
    public async Task AFileThisExtensionDidNotComposeIsKeptAndCountedHoweverOldItIs()
    {
        var port = new RecordingTreeLinkPort();
        var left = EntityFolder + "/Studio.Name.-.2025-01-01.-.Scene.XXX.1080p.WEBDL.mp4";
        port.Place(left, new FileIdentity(114, 999), Now - TimeSpan.FromDays(400));

        var built = await BuildAsync(port, []);

        Assert.Equal(0, built.Swept.Removed);
        Assert.Equal(1, built.Swept.NotComposedHere);
        Assert.Equal([Path.GetFileName(left)], port.NamesIn(EntityFolder!));
        Assert.DoesNotContain(port.Calls, call => call.Verb == "remove");
    }

    // The removal rests on no library file answering to the name's own file, and a read that
    // stopped short establishes nothing about the files it never reached. The arrangement is one
    // the finished pass would empty.
    [Fact]
    public async Task APassWhoseReadOfTheLibraryDidNotFinishRemovesNothing()
    {
        var port = new RecordingTreeLinkPort();
        var files = Placed(port, count: 2);
        await BuildAsync(port, files);
        port.Forget(files[0].Path);

        await Assert.ThrowsAsync<IOException>(
            () => new TreeReconcileStep(port, Clock).BuildAsync(
                CoveRoot,
                WhisparrGeneration.V3,
                RemoteId,
                ReadThatFails(files[1].Path),
                TestCt));

        Assert.DoesNotContain(port.Calls, call => call.Verb == "remove");
        Assert.Equal(2, port.NamesIn(EntityFolder!).Count());
    }

    // The run was stopped part-way through the entity's files. What was linked before the stop
    // stays linked, and the arrangement is one the finished pass would have emptied.
    [Fact]
    public async Task APassStoppedPartWayThroughTheLibraryRemovesNothing()
    {
        var port = new RecordingTreeLinkPort();
        var files = Placed(port, count: 2);
        await BuildAsync(port, files);
        port.Forget(files[0].Path);
        using var stopping = new CancellationTokenSource();

        var again = await new TreeReconcileStep(port, Clock).BuildAsync(
            CoveRoot,
            WhisparrGeneration.V3,
            RemoteId,
            ReadThatStops(files[1].Path, stopping),
            stopping.Token);

        Assert.Equal(0, again.Swept.Removed);
        Assert.DoesNotContain(port.Calls, call => call.Verb == "remove");
        Assert.Equal(2, port.NamesIn(EntityFolder!).Count());
    }

    [Fact]
    public async Task ANameWhoseFileChangedInsideTheSettleWindowIsCountedAsWaitingAndLeft()
    {
        var port = new RecordingTreeLinkPort();
        var justWritten = new PlacedFile(Folder + "/just written.mp4", new FileIdentity(114, 77));
        port.Place(justWritten.Path, justWritten.Identity, Now - TimeSpan.FromMinutes(1));
        port.AnswerLink(LinkPathOf(justWritten.Identity, justWritten.Path), LinkOutcome.Linked);
        await BuildAsync(port, [justWritten]);
        port.Forget(justWritten.Path);

        var again = await BuildAsync(port, []);

        Assert.Equal(0, again.Swept.Removed);
        Assert.Equal(1, again.Swept.WaitingToSettle);
        Assert.Single(port.NamesIn(EntityFolder!));
    }

    [Fact]
    public async Task ARemovalTheSeamRefusesIsCountedRatherThanRetried()
    {
        var port = new RecordingTreeLinkPort();
        var files = Placed(port, count: 1);
        await BuildAsync(port, files);
        port.Forget(files[0].Path);
        port.RefuseRemovalOf(LinkPathOf(files[0].Identity, files[0].Path));

        var again = await BuildAsync(port, []);

        Assert.Equal(0, again.Swept.Removed);
        Assert.Equal(1, again.Swept.Refused);
        Assert.Equal(1, port.Calls.Count(call => call.Verb == "remove"));
    }

    private static string TreeRoot
        => TreePathGuard.TreeRootUnder(CoveRoot, WhisparrGeneration.V3)!;

    private static string? EntityFolder => TreePathGuard.EntityFolderIn(TreeRoot, RemoteId);

    private static TimeProvider Clock { get; } = new FixedClock(Now);

    private static Task<TreeBuild> BuildAsync(
        RecordingTreeLinkPort port, IReadOnlyList<PlacedFile> files)
        => new TreeReconcileStep(port, Clock)
            .BuildAsync(CoveRoot, WhisparrGeneration.V3, RemoteId, Streamed(files), TestCt);

    // The library read failing part-way, which is what a dropped connection to the host's own
    // database leaves the pass holding.
    // The run stopped while the entity's files were being read.
    private static async IAsyncEnumerable<string> ReadThatStops(
        string reached, CancellationTokenSource stopping)
    {
        yield return reached;
        await stopping.CancelAsync();
        yield return reached;
    }

    private static async IAsyncEnumerable<string> ReadThatFails(string reached)
    {
        yield return reached;
        await Task.CompletedTask;
        throw new IOException("the library could not be read to the end");
    }

    private static async IAsyncEnumerable<string> Streamed(IReadOnlyList<PlacedFile> files)
    {
        foreach (var file in files)
        {
            yield return file.Path;
        }

        await Task.CompletedTask;
    }

    private static List<PlacedFile> Placed(RecordingTreeLinkPort port, int count)
    {
        var files = new List<PlacedFile>(count);
        for (var index = 0; index < count; index++)
        {
            var file = new PlacedFile(
                $"{Folder}/scene {index}.mp4", new FileIdentity(114, (ulong)(544903 + index)));
            port.Place(file.Path, file.Identity, Changed);
            port.AnswerLink(LinkPathOf(file.Identity, file.Path), LinkOutcome.Linked);
            files.Add(file);
        }

        return files;
    }

    private static string LinkPathOf(FileIdentity identity, string libraryFile)
        => TreePathGuard.LinkPathIn(EntityFolder!, identity, libraryFile)!;

    private static string LinkNameOf(FileIdentity identity, string libraryFile)
        => LinkPathOf(identity, libraryFile)[(EntityFolder!.Length + 1)..];

    private sealed record PlacedFile(string Path, FileIdentity Identity);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
