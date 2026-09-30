using System.Text;
using WhisparrSync.Linking;

namespace WhisparrSync.Tests.Linking;

// Against the real filesystem, because the subject is what the platform does with a second name for
// one file. A double supplying its own idea of a hard link would agree with itself whatever the
// platform's own call answers, and the answers here are what every removal in this extension turns
// on. Both platforms the suite runs on give a file kept under the temporary directory more than one
// name; a filesystem that does not would fail the linking cases rather than skip them.
public sealed class TreeLinkPortTests : IDisposable
{
    private static readonly byte[] Bytes = Encoding.UTF8.GetBytes("a scene the reader owns");

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "whisparrsync-linking-" + Guid.NewGuid().ToString("n"));

    private readonly TreeLinkPort _port = new();

    public TreeLinkPortTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void TwoNamesOfOneFileReportOneIdentity()
    {
        var file = Place("scene.mp4");
        var link = At("link.mp4");

        Assert.Equal(LinkOutcome.Linked, _port.Link(file, link));

        var probed = _port.Identify(file);
        Assert.NotNull(probed);
        Assert.Equal(probed.Identity, _port.Identify(link)?.Identity);
    }

    [Fact]
    public void ACopyOfAFileReportsADifferentIdentity()
    {
        var file = Place("scene.mp4");
        var copy = Place("copy.mp4");

        Assert.NotEqual(_port.Identify(file)?.Identity, _port.Identify(copy)?.Identity);
    }

    [Fact]
    public void AFileReportsOneNameUntilASecondIsMade()
    {
        var file = Place("scene.mp4");
        Assert.Equal(1, _port.Identify(file)?.Names);

        Assert.Equal(LinkOutcome.Linked, _port.Link(file, At("link.mp4")));

        Assert.Equal(2, _port.Identify(file)?.Names);
    }

    [Fact]
    public void RemovingOneOfTwoNamesLeavesTheOtherHoldingTheBytes()
    {
        var file = Place("scene.mp4");
        var link = At("link.mp4");
        Assert.Equal(LinkOutcome.Linked, _port.Link(file, link));

        Assert.Equal(NameRemoval.Removed, _port.Remove(_root, link));

        Assert.Equal(Bytes, File.ReadAllBytes(file));
        Assert.Equal(1, _port.Identify(file)?.Names);
        Assert.Null(_port.Identify(link));
    }

    [Fact]
    public void ALinkOntoANameAlreadyThereLeavesWhatIsThere()
    {
        var file = Place("scene.mp4");
        var taken = At("taken.mp4");
        File.WriteAllText(taken, "something else");

        Assert.Equal(LinkOutcome.NameAlreadyThere, _port.Link(file, taken));

        Assert.Equal("something else", File.ReadAllText(taken));
        Assert.NotEqual(_port.Identify(file)?.Identity, _port.Identify(taken)?.Identity);
    }

    [Fact]
    public void ALinkFromASourceThatIsNotThereMakesNoName()
    {
        var link = At("link.mp4");

        Assert.Equal(LinkOutcome.SourceNotThere, _port.Link(At("absent.mp4"), link));

        Assert.False(File.Exists(link));
    }

    [Fact]
    public void AListingAnswersTheNamesDirectlyInsideTheFolder()
    {
        Place("scene.mp4");
        Place("other.mp4");
        Assert.True(_port.EnsureFolder(At("below")));
        File.WriteAllBytes(Path.Combine(At("below"), "buried.mp4"), Bytes);

        Assert.Equal(["below", "other.mp4", "scene.mp4"], _port.NamesIn(_root).Order(StringComparer.Ordinal));
        Assert.Empty(_port.NamesIn(At("absent")));
    }

    [Fact]
    public void ARemovalOutsideTheTreeRootIsRefusedWithTheFileStillThere()
    {
        var file = Place("scene.mp4");

        Assert.Equal(NameRemoval.Refused, _port.Remove(At("tree"), file));

        Assert.True(File.Exists(file));
    }

    [Fact]
    public void TheIgnoreFileIsWrittenOnceAndNotRewritten()
    {
        var tree = At("tree");
        Assert.True(_port.WriteIgnore(tree));

        var ignoreFile = Path.Combine(tree, ".coveignore");
        Assert.Equal("*\n", File.ReadAllText(ignoreFile));

        // Stamped into the past rather than compared with what the first write left, so a second
        // write shows even where the platform's timestamps are coarser than the gap between them.
        var stamped = new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(ignoreFile, stamped);

        Assert.True(_port.WriteIgnore(tree));
        Assert.Equal(stamped, File.GetLastWriteTimeUtc(ignoreFile));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A temporary directory the platform will not drop is not this suite's subject.
        }
    }

    private string At(string name) => Path.Combine(_root, name);

    private string Place(string name)
    {
        var path = At(name);
        File.WriteAllBytes(path, Bytes);
        return path;
    }
}
