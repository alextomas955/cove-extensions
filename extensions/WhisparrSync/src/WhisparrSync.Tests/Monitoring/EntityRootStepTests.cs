using WhisparrSync.Addressing;
using WhisparrSync.Contracts;
using WhisparrSync.Monitoring;

namespace WhisparrSync.Tests.Monitoring;

/// <summary>
/// Which library root an entity is registered at when its files do not all sit under one.
/// </summary>
/// <remarks>
/// The split case is the measured one: on the owner's library 197 of 199 studios with files sit
/// under exactly one root, and the two that do not are 883 to 2 and 62 to 2. The rule is the same
/// whatever the ratio, so the near-even case below runs through the same code path and not through a
/// threshold.
/// <para>
/// How many times the agreement is asked is as much the subject as the root chosen. A step that
/// asked about every root would establish one agreement per entity rather than one per root, which
/// answers correctly and is unusable at the entity count a library reaches.
/// </para>
/// </remarks>
public sealed class EntityRootStepTests
{
    private const string FirstRoot = "G:/Downloads/P";

    private const string SecondRoot = "I:/Downloads/P";

    private static readonly string[] BothRoots = [FirstRoot, SecondRoot];

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    /// <summary>883 files under one root and 2 under another takes the 883.</summary>
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

    /// <summary>45 and 40 takes the 45 by the same rule, with no threshold anywhere.</summary>
    /// <remarks>
    /// Near enough to even that a rule with a majority threshold would refuse it. This product has
    /// none: refusing a split studio would leave the 45 unlinked to avoid guessing about the 40.
    /// </remarks>
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

    /// <summary>An exact tie takes the root the supplied list names first, on every run.</summary>
    /// <remarks>
    /// The supplied order is the host's own configured order, so the choice does not vary between two
    /// runs over one configuration. Run twice for exactly that: a tie broken by anything the counts
    /// do not determine could answer differently the second time.
    /// </remarks>
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

    /// <summary>Files under one root only leave nothing behind and name no other root.</summary>
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

    /// <summary>
    /// An entity owning no file answers no root and no refusal, and nothing is asked about.
    /// </summary>
    /// <remarks>
    /// Not a refusal: there is nothing to derive a root from, which is a different fact from a root
    /// the instance would not agree to. A caller keeps its run-wide value for this reading.
    /// </remarks>
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

    /// <summary>The chosen root's own instance spelling is what is answered.</summary>
    [Fact]
    public async Task TheAnsweredInstanceRootIsTheChosenRootsOwn()
    {
        var asked = new List<string>();

        var resolved = await ResolveAsync(new() { [FirstRoot] = 1, [SecondRoot] = 9 }, asked);

        Assert.Equal(Agreed(SecondRoot), resolved.InstanceRoot);
        Assert.Equal([SecondRoot], asked);
    }

    /// <summary>A chosen root the instance agreed no spelling for still names what was left.</summary>
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

    /// <summary>That library root as the instance spells it.</summary>
    private static string Agreed(string coveRoot)
        => "/on-the-instance" + coveRoot[2..].Replace('/', '-');

    /// <summary>
    /// One resolution over both roots, recording which of them the agreement was asked about.
    /// </summary>
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
