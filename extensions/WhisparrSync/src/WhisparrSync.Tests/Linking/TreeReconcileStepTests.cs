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
        var step = new TreeReconcileStep(port);
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
    // half never lists a folder at all: one identity read and one link attempt per file.
    [Fact]
    public async Task APassOverManyFilesAsksAboutEachOneAndHoldsNone()
    {
        const int Owned = 500;
        var port = new RecordingTreeLinkPort();
        var files = Placed(port, Owned);

        var built = await BuildAsync(port, files);
        var asked = port.Calls.ToList();

        Assert.Equal(Owned, built.Linked);
        Assert.DoesNotContain(asked, call => call.Verb == "names");
        Assert.Equal(Owned, asked.Count(call => call.Verb == "identify"));
        Assert.Equal(Owned, asked.Count(call => call.Verb == "link"));
    }

    // The run was stopped part-way through the entity's files. What was linked before the stop
    // stays linked and the pass reports it rather than throwing.
    [Fact]
    public async Task ABuildStoppedPartWayThroughTheLibraryKeepsWhatItLinked()
    {
        var port = new RecordingTreeLinkPort();
        var files = Placed(port, count: 2);
        using var stopping = new CancellationTokenSource();

        var built = await new TreeReconcileStep(port).BuildAsync(
            CoveRoot,
            WhisparrGeneration.V3,
            RemoteId,
            ReadThatStops(files[0].Path, stopping),
            stopping.Token);

        Assert.Equal(1, built.Linked);
        Assert.Single(port.NamesIn(EntityFolder!));
    }

    private static string TreeRoot
        => TreePathGuard.TreeRootUnder(CoveRoot, WhisparrGeneration.V3)!;

    private static string? EntityFolder => TreePathGuard.EntityFolderIn(TreeRoot, RemoteId);

    private static Task<TreeBuild> BuildAsync(
        RecordingTreeLinkPort port, IReadOnlyList<PlacedFile> files)
        => new TreeReconcileStep(port)
            .BuildAsync(CoveRoot, WhisparrGeneration.V3, RemoteId, Streamed(files), TestCt);

    // The run stopped while the entity's files were being read.
    private static async IAsyncEnumerable<string> ReadThatStops(
        string reached, CancellationTokenSource stopping)
    {
        yield return reached;
        await stopping.CancelAsync();
        yield return reached;
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
}
