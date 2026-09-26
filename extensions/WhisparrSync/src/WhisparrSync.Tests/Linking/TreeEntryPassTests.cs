using WhisparrSync.Linking;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Linking;

// The pass answers which name in an entity's folder belongs to which row the instance holds. Each
// case states what the ports answered, so nothing here asserts on a value it supplied to itself
// through the thing under test.
public class TreeEntryPassTests
{
    private const int SiteRow = 12;

    private static TreeEntryPorts Ports(
        IReadOnlyList<string> scenes,
        Dictionary<string, int> numbers,
        Dictionary<int, int> rows,
        Dictionary<string, IReadOnlyList<string>> names,
        List<IReadOnlyCollection<int>>? asked = null)
        => new(
            _ => scenes.ToAsyncEnumerable(),
            (identity, _) => Task.FromResult(
                numbers.TryGetValue(identity, out var number) ? number : (int?)null),
            (numbers_, _) =>
            {
                asked?.Add([.. numbers_]);
                return Task.FromResult<IReadOnlyDictionary<int, int>>(
                    numbers_.Where(rows.ContainsKey).ToDictionary(number => number, number => rows[number]));
            },
            (identity, _) => (names.TryGetValue(identity, out var held) ? held : [])
                .ToAsyncEnumerable());

    [Fact]
    public async Task EveryNameOfAnAddressedSceneCarriesThatScenesRowAndTheSite()
    {
        var (byName, tally) = await TreeEntryPass.AddressedAsync(
            Ports(
                ["scene-a"],
                new Dictionary<string, int> { ["scene-a"] = 900 },
                new Dictionary<int, int> { [900] = 88 },
                new Dictionary<string, IReadOnlyList<string>>
                {
                    ["scene-a"] = ["aa-01.mp4", "aa-02.mkv"],
                }),
            SiteRow,
            TestContext.Current.CancellationToken);

        Assert.Equal(new TreeEntryTally(Addressed: 1, Unnumbered: 0, Unresolved: 0), tally);
        Assert.Equal(new EntryAddress(88, SiteRow), byName["aa-01.mp4"]);
        Assert.Equal(new EntryAddress(88, SiteRow), byName["aa-02.mkv"]);
    }

    // A scene the metadata source named no number for and one the site's catalogue holds no row for
    // are separate facts: the first is this product failing to identify, the second is the instance
    // not holding it, and reporting either as the other points a reader at the wrong system.
    [Fact]
    public async Task ASceneWithNoNumberAndOneWithNoRowAreCountedApart()
    {
        var (byName, tally) = await TreeEntryPass.AddressedAsync(
            Ports(
                ["numbered-and-held", "unnumbered", "numbered-not-held"],
                new Dictionary<string, int>
                {
                    ["numbered-and-held"] = 900,
                    ["numbered-not-held"] = 901,
                },
                new Dictionary<int, int> { [900] = 88 },
                new Dictionary<string, IReadOnlyList<string>>
                {
                    ["numbered-and-held"] = ["held.mp4"],
                    ["unnumbered"] = ["unnumbered.mp4"],
                    ["numbered-not-held"] = ["absent.mp4"],
                }),
            SiteRow,
            TestContext.Current.CancellationToken);

        Assert.Equal(new TreeEntryTally(Addressed: 1, Unnumbered: 1, Unresolved: 1), tally);
        Assert.Equal(["held.mp4"], byName.Keys);
    }

    // Two identifiers under one scene are two spellings of one source rather than two scenes, so
    // the names of the second must not replace the first's.
    [Fact]
    public async Task TwoIdentifiersResolvingToOneNumberKeepBothTheirNames()
    {
        var (byName, _) = await TreeEntryPass.AddressedAsync(
            Ports(
                ["spelling-one", "spelling-two"],
                new Dictionary<string, int> { ["spelling-one"] = 900, ["spelling-two"] = 900 },
                new Dictionary<int, int> { [900] = 88 },
                new Dictionary<string, IReadOnlyList<string>>
                {
                    ["spelling-one"] = ["first.mp4"],
                    ["spelling-two"] = ["second.mp4"],
                }),
            SiteRow,
            TestContext.Current.CancellationToken);

        Assert.Equal(["first.mp4", "second.mp4"], byName.Keys.Order());
    }

    // The instance answers its whole list however few numbers are asked about, so what is bounded
    // is what the pass holds at once, not how many scenes it reaches.
    [Fact]
    public async Task MoreScenesThanOneChunkAreAskedAboutInSeveralReads()
    {
        var scenes = Enumerable.Range(1, TreeEntryPass.ChunkSize + 3)
            .Select(number => $"scene-{number}")
            .ToArray();
        var asked = new List<IReadOnlyCollection<int>>();

        var (byName, tally) = await TreeEntryPass.AddressedAsync(
            Ports(
                scenes,
                scenes.Select((identity, index) => (identity, index))
                    .ToDictionary(pair => pair.identity, pair => 900 + pair.index),
                Enumerable.Range(0, scenes.Length)
                    .ToDictionary(index => 900 + index, index => 88 + index),
                scenes.Select((identity, index) => (identity, index))
                    .ToDictionary(
                        pair => pair.identity,
                        pair => (IReadOnlyList<string>)[$"name-{pair.index}.mp4"]),
                asked),
            SiteRow,
            TestContext.Current.CancellationToken);

        Assert.Equal([TreeEntryPass.ChunkSize, 3], asked.Select(chunk => chunk.Count));
        Assert.Equal(scenes.Length, tally.Addressed);
        Assert.Equal(scenes.Length, byName.Count);
    }

    [Fact]
    public async Task AnEntityWithNoScenesAsksTheInstanceNothing()
    {
        var asked = new List<IReadOnlyCollection<int>>();

        var (byName, tally) = await TreeEntryPass.AddressedAsync(
            Ports([], new Dictionary<string, int>(), new Dictionary<int, int>(),
                new Dictionary<string, IReadOnlyList<string>>(), asked),
            SiteRow,
            TestContext.Current.CancellationToken);

        Assert.Empty(asked);
        Assert.Empty(byName);
        Assert.Equal(TreeEntryTally.Nothing, tally);
    }
}
