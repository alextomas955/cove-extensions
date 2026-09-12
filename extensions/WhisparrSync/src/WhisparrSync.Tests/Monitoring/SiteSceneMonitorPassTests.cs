using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using WhisparrSync.Library;
using WhisparrSync.Monitoring;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Monitoring;

/// <summary>
/// The pass that marks the scenes a reader owns on one site: how many it reaches, and what each way
/// of failing costs.
/// </summary>
/// <remarks>
/// Driven on recorded calls rather than on source text. Every case asserts both the count the pass
/// answered and that the scenes after the failing one were still reached, because a pass that gave up
/// at the first refusal would answer counts that look the same until the next scene is asked for.
/// </remarks>
public sealed class SiteSceneMonitorPassTests
{
    /// <summary>The site as the library holds it: Cove's own studio id beside the identifier.</summary>
    private static readonly LibrarySiteIdentity Site =
        new(4, "a30bc641-6afe-4c80-9c73-ecb68104a68d");

    /// <summary>The instance's own id for that site, off the answer the run already read.</summary>
    private const int SiteId = 11;

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    /// <summary>Every scene the reader owns on the site is reached, at any number of scenes.</summary>
    /// <remarks>
    /// Seeded past <see cref="SiteSceneMonitorPass.ChunkSize"/>, read from the constant rather than
    /// restated, so a ceiling introduced at any value below the seed reddens this. That is the
    /// failure nobody can observe at three scenes: a cap would leave part of the library unmonitored
    /// and report a total that reads exactly like a complete one.
    /// <para>
    /// The chunk bounds what is held, which is asserted beside it: the numbers are asked about in
    /// reads of at most the chunk size, and every one of them is asked about.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task EverySceneTheReaderOwnsIsReachedAndNothingCapsHowMany()
    {
        var owned = Scenes(SiteSceneMonitorPass.ChunkSize + 1);
        var instance = new Instance(owned.Select(NumberOf));

        var (tally, provider) = await MonitorAsync(owned, instance);

        Assert.Equal(owned.Count, provider.Resolved.Count);
        Assert.Equal(owned.Count, instance.Flagged.Count);
        Assert.Equal(owned.Count, tally.Monitored);
        Assert.Equal(0, tally.NotMonitored);
        Assert.All(instance.SitesRead, read => Assert.Equal(SiteId, read));
        Assert.All(
            instance.RowReads, read => Assert.True(read.Count <= SiteSceneMonitorPass.ChunkSize));
        Assert.Equal(owned.Count, instance.RowReads.Sum(read => read.Count));
    }

    /// <summary>A scene the provider names no number for is counted, and the next is still read.</summary>
    [Fact]
    public async Task ASceneTheProviderNamesNoNumberForIsCountedAndTheNextIsStillRead()
    {
        var owned = Scenes(3);
        var instance = new Instance([NumberOf(owned[0]), NumberOf(owned[2])]);
        var provider = new RecordingProviderCatalogue(
            new Dictionary<string, int?>(StringComparer.Ordinal)
            {
                [owned[0]] = NumberOf(owned[0]),
                [owned[1]] = null,
                [owned[2]] = NumberOf(owned[2]),
            });

        var (tally, _) = await MonitorAsync(owned, instance, provider);

        Assert.Equal(1, tally.Unnumbered);
        Assert.Equal(2, tally.Monitored);
        Assert.Equal(owned, provider.Resolved);
        Assert.Equal([NumberOf(owned[0]), NumberOf(owned[2])], instance.Flagged);
    }

    /// <summary>
    /// A scene the instance holds no row for is counted apart, and the next is still flagged.
    /// </summary>
    /// <remarks>
    /// Its own count rather than a refusal: a scene the instance has never seen is a different fact
    /// from one it holds and declines to flag, and a reader acts on the two differently.
    /// </remarks>
    [Fact]
    public async Task ASceneTheInstanceHoldsNoRowForIsCountedApartAndTheNextIsStillFlagged()
    {
        var owned = Scenes(3);
        var instance = new Instance([NumberOf(owned[0]), NumberOf(owned[2])]);

        var (tally, _) = await MonitorAsync(owned, instance);

        Assert.Equal(1, tally.Unresolved);
        Assert.Equal(0, tally.Refused);
        Assert.Equal(2, tally.Monitored);
        Assert.Equal([NumberOf(owned[0]), NumberOf(owned[2])], instance.Flagged);
    }

    /// <summary>A flag the instance refuses is counted, and the rest are still flagged.</summary>
    [Fact]
    public async Task AFlagTheInstanceRefusesIsCountedAndTheRestAreStillFlagged()
    {
        var owned = Scenes(3);
        var instance = new Instance(owned.Select(NumberOf)) { Declining = NumberOf(owned[1]) };

        var (tally, _) = await MonitorAsync(owned, instance);

        Assert.Equal(1, tally.Refused);
        Assert.Equal(2, tally.Monitored);
        Assert.Equal(owned.Select(NumberOf), instance.Flagged);
    }

    /// <summary>
    /// A provider that stopped answering costs those scenes their number, and the next is still read.
    /// </summary>
    /// <remarks>
    /// Contained rather than propagated, because a provider that went away leaves the rest of the
    /// library to offer - which is what the per-entity job already does with a refusal.
    /// </remarks>
    [Fact]
    public async Task AProviderThatStoppedAnsweringCostsThoseScenesTheirNumberAndTheRunGoesOn()
    {
        var owned = Scenes(3);
        var instance = new Instance([NumberOf(owned[2])]);
        var provider = Answering(owned);

        var (tally, _) = await MonitorAsync(
            owned, instance, provider, beforeEachScene: scene => provider.Unreachable = scene < 3);

        Assert.Equal(2, tally.Unnumbered);
        Assert.Equal(1, tally.Monitored);
        Assert.Equal(owned, provider.Resolved);
        Assert.Equal([NumberOf(owned[2])], instance.Flagged);
    }

    /// <summary>
    /// A row read that could not be answered counts the scenes it asked about as unresolved, and the
    /// next read is still made.
    /// </summary>
    /// <remarks>
    /// The read raises rather than answering an empty map, because an empty map claims the site holds
    /// a row for none of the numbers asked about. Counting those scenes as unresolved is what the
    /// role's own remarks say a caller does.
    /// </remarks>
    [Fact]
    public async Task ARowReadThatCouldNotBeAnsweredCountsThoseScenesAsUnresolvedAndTheRunGoesOn()
    {
        var owned = Scenes(SiteSceneMonitorPass.ChunkSize + 1);
        var instance = new Instance(owned.Select(NumberOf)) { RefusingReadNumber = 1 };

        var (tally, _) = await MonitorAsync(owned, instance);

        Assert.Equal(SiteSceneMonitorPass.ChunkSize, tally.Unresolved);
        Assert.Equal(1, tally.Monitored);
        Assert.Equal(2, instance.RowReads.Count);
    }

    /// <summary>The identifiers of <paramref name="count"/> scenes the reader owns.</summary>
    private static List<string> Scenes(int count)
        => [.. Enumerable.Range(1, count).Select(n => $"{n:x8}-0000-4000-8000-000000000000")];

    /// <summary>The provider's own number for one stored scene identifier.</summary>
    private static int NumberOf(string identity)
        => 1_000_000 + int.Parse(identity[..8], NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    /// <summary>A provider naming every one of <paramref name="owned"/>.</summary>
    private static RecordingProviderCatalogue Answering(IEnumerable<string> owned)
        => new(owned.ToDictionary(
            identity => identity, identity => (int?)NumberOf(identity), StringComparer.Ordinal));

    private static async Task<(SceneMonitorTally Tally, RecordingProviderCatalogue Provider)>
        MonitorAsync(
            IReadOnlyList<string> owned,
            Instance instance,
            RecordingProviderCatalogue? provider = null,
            Action<int>? beforeEachScene = null)
    {
        var answering = provider ?? Answering(owned);
        var scene = 0;

        var tally = await SiteSceneMonitorPass.MonitorAsync(
            new SiteSceneMonitorPorts(
                (studioId, ct) => Streamed(studioId == Site.StudioId ? owned : [], ct),
                (identity, ct) =>
                {
                    beforeEachScene?.Invoke(++scene);
                    return answering.ResolveNumericSceneIdAsync(identity, ct);
                },
                instance.ReadRowsAsync,
                instance.SetMonitoredAsync),
            Site,
            SiteId,
            NullLogger.Instance,
            TestCt);

        return (tally, answering);
    }

    private static async IAsyncEnumerable<string> Streamed(
        IEnumerable<string> identities, [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var identity in identities)
        {
            ct.ThrowIfCancellationRequested();
            yield return identity;
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// An instance holding a row for each number it was given, recording what it was asked.
    /// </summary>
    /// <remarks>
    /// The row identifier is the number itself. Which row a flag was set on is the fact under test, and a
    /// second numbering beside it would only be a mapping this file also had to state.
    /// </remarks>
    private sealed class Instance(IEnumerable<int> held)
    {
        private readonly HashSet<int> _held = [.. held];

        /// <summary>The numbers each row read was asked about, in order.</summary>
        public List<IReadOnlyCollection<int>> RowReads { get; } = [];

        /// <summary>The site each row read named, in order.</summary>
        public List<int> SitesRead { get; } = [];

        /// <summary>The rows a flag was set on, in order.</summary>
        public List<int> Flagged { get; } = [];

        /// <summary>Which row read answers nothing at all, counted from one.</summary>
        public int? RefusingReadNumber { get; init; }

        /// <summary>The row the instance declines to flag, or null where it declines none.</summary>
        public int? Declining { get; init; }

        public Task<IReadOnlyDictionary<int, int>> ReadRowsAsync(
            int siteId, IReadOnlyCollection<int> numbers, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            RowReads.Add([.. numbers]);
            SitesRead.Add(siteId);

            if (RowReads.Count == RefusingReadNumber)
            {
                throw new HttpRequestException("nothing answered");
            }

            return Task.FromResult<IReadOnlyDictionary<int, int>>(
                numbers.Where(_held.Contains).ToDictionary(number => number, number => number));
        }

        public Task<WhisparrResponse?> SetMonitoredAsync(int rowId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Flagged.Add(rowId);
            return Task.FromResult<WhisparrResponse?>(
                rowId == Declining ? null : RecordingWhisparrClient.Json(202, "{}"));
        }
    }
}
