using WhisparrSync.Addressing;
using WhisparrSync.Contracts;
using WhisparrSync.Monitoring;

namespace WhisparrSync.Tests.Monitoring;

// The split ratios come from a measured library: 197 of 199 studios with files sit under exactly
// one root, and the two that do not split 883 to 2 and 62 to 2. The rule applies whatever the
// ratio, so the near-even case takes the same path and no threshold exists. The cases also count
// how often the agreement is asked: asking about every root would establish one per entity rather
// than one per root, which answers correctly and does not scale.
public sealed class EntityRootStepTests
{
    private const string FirstRoot = "G:/Downloads/P";

    private const string SecondRoot = "I:/Downloads/P";

    private static readonly string[] BothRoots = [FirstRoot, SecondRoot];

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ALopsidedSplitTakesTheRootHoldingMostOfTheFiles()
    {
        var asked = new List<string>();

        var resolved = await ResolveAsync(
            new() { [FirstRoot] = 2, [SecondRoot] = 883 }, asked);

        Assert.Equal(SecondRoot, resolved.CoveRoot);
        Assert.Equal(883, resolved.FilesAtChosenRoot);
        Assert.Equal(2, resolved.FilesLeftElsewhere);
        Assert.Equal([FirstRoot], resolved.RootsLeftBehind);
        Assert.Equal([SecondRoot], asked);
    }

    // A 45 to 40 split is near enough to even that a majority threshold would refuse it. Refusing
    // would leave the 45 unlinked to avoid guessing about the 40.
    [Fact]
    public async Task ANearlyEvenSplitTakesTheLargerSideByTheSameRule()
    {
        var asked = new List<string>();

        var resolved = await ResolveAsync(
            new() { [FirstRoot] = 40, [SecondRoot] = 45 }, asked);

        Assert.Equal(SecondRoot, resolved.CoveRoot);
        Assert.Equal(45, resolved.FilesAtChosenRoot);
        Assert.Equal(40, resolved.FilesLeftElsewhere);
        Assert.Equal([SecondRoot], asked);
    }

    // The supplied order is the host's configured order. The resolution runs twice because a tie
    // broken by anything the counts do not determine could answer differently the second time.
    [Fact]
    public async Task AnExactTieTakesTheFirstRootSuppliedAndDoesSoOnEveryRun()
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            [FirstRoot] = 40,
            [SecondRoot] = 40,
        };

        var asked = new List<string>();

        var first = await ResolveAsync(counts, asked);
        var second = await ResolveAsync(counts, asked);

        Assert.Equal(FirstRoot, first.CoveRoot);
        Assert.Equal(FirstRoot, second.CoveRoot);
        Assert.Equal(40, first.FilesLeftElsewhere);
        Assert.Equal([SecondRoot], first.RootsLeftBehind);
        Assert.Equal([FirstRoot, FirstRoot], asked);
    }

    [Fact]
    public async Task FilesUnderOneRootOnlyLeaveNothingBehind()
    {
        var asked = new List<string>();

        var resolved = await ResolveAsync(
            new() { [FirstRoot] = 0, [SecondRoot] = 12 }, asked);

        Assert.Equal(SecondRoot, resolved.CoveRoot);
        Assert.Equal(0, resolved.FilesLeftElsewhere);
        Assert.Empty(resolved.RootsLeftBehind);
        Assert.Equal([SecondRoot], asked);
    }

    // Nothing to derive a root from is a different fact from a root the instance would not agree
    // to, so this is not a refusal and the caller keeps its run-wide value.
    [Fact]
    public async Task AnEntityOwningNoFileAnswersNoRootAndNoRefusal()
    {
        var asked = new List<string>();

        var resolved = await ResolveAsync(
            new() { [FirstRoot] = 0, [SecondRoot] = 0 }, asked);

        Assert.Null(resolved.InstanceRoot);
        Assert.Null(resolved.CoveRoot);
        Assert.Equal(MonitorRefusalKind.None, resolved.Refusal);
        Assert.Empty(asked);
    }

    [Fact]
    public async Task TheAnsweredInstanceRootIsTheChosenRootsOwn()
    {
        var asked = new List<string>();

        var resolved = await ResolveAsync(new() { [FirstRoot] = 1, [SecondRoot] = 9 }, asked);

        Assert.Equal(Agreed(SecondRoot), resolved.InstanceRoot);
        Assert.Equal([SecondRoot], asked);
    }

    [Fact]
    public async Task AChosenRootThatAgreedOnNothingStillNamesWhatWasLeft()
    {
        var resolved = await EntityRootStep.ResolveAsync(
            BothRoots,
            (coveRoot, _) => Task.FromResult(coveRoot == SecondRoot ? 883 : 2),
            (coveRoot, _) => Task.FromResult(
                new AddressedFolder(null, FolderAgreementRefusal.NothingResolved, coveRoot, [])),
            TestCt);

        Assert.Null(resolved.InstanceRoot);
        Assert.Equal(MonitorRefusalKind.NoAgreedRootForThisEntity, resolved.Refusal);
        Assert.Equal(SecondRoot, resolved.CoveRoot);
        Assert.Equal([FirstRoot], resolved.RootsLeftBehind);
    }

    // That library root as the instance spells it.
    private static string Agreed(string coveRoot)
        => "/on-the-instance" + coveRoot[2..].Replace('/', '-');

    // Records which roots the agreement was asked about.
    private static Task<EntityRoot> ResolveAsync(
        Dictionary<string, int> counts, List<string> asked)
        => EntityRootStep.ResolveAsync(
            BothRoots,
            (coveRoot, _) => Task.FromResult(counts[coveRoot]),
            (coveRoot, _) =>
            {
                asked.Add(coveRoot);
                return Task.FromResult(new AddressedFolder(Agreed(coveRoot), null, coveRoot, []));
            },
            TestCt);
}
