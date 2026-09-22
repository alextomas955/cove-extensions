using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using WhisparrSync.Library;
using WhisparrSync.Monitoring;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Monitoring;

// Every case asserts both the count the pass answered and that the scenes after the failing one
// were still reached. A pass that gave up at the first refusal answers counts that look the same
// until the next scene is asked for.
public sealed class SiteSceneMonitorPassTests
{
    private static readonly LibrarySiteIdentity Site =
        new(4, "a30bc641-6afe-4c80-9c73-ecb68104a68d");

    // The instance's own id for that site, off the answer the run already read.
    private const int SiteId = 11;

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    // The timeout shape is the one WhisparrTransport.RequestTimeout produces, and it derives from the
    // shape a host stop raises. A filter written for the other two lets it through unnoticed, so
    // every contained read is driven against all three.
    public static TheoryData<Type> UnanswerableReads =>
        [typeof(HttpRequestException), typeof(IOException), typeof(TaskCanceledException)];

    // Seeded past SiteSceneMonitorPass.ChunkSize, read from the constant rather than restated, so a
    // ceiling introduced at any value below the seed fails here. A cap would leave part of the
    // library unmonitored and report a total that reads like a complete one. The chunk bounds what
    // is held, so the numbers are asked about in reads of at most the chunk size.
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

    // Its own count rather than a refusal: a scene the instance has never seen is a different fact
    // from one it holds and declines to flag, and a reader acts on the two differently.
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

    // Contained rather than propagated, because a provider that went away leaves the rest of the
    // library to offer, which is what the per-entity job already does with a refusal.
    [Theory]
    [MemberData(nameof(UnanswerableReads))]
    public async Task AProviderThatStoppedAnsweringCostsThoseScenesTheirNumberAndTheRunGoesOn(
        Type shape)
    {
        var owned = Scenes(3);
        var instance = new Instance([NumberOf(owned[2])]);
        var provider = Answering(owned);

        var (tally, _) = await MonitorAsync(
            owned,
            instance,
            provider,
            beforeEachScene: scene =>
                provider.Unreachable = scene < 3 ? () => Unanswerable(shape) : null);

        Assert.Equal(2, tally.Unnumbered);
        Assert.Equal(1, tally.Monitored);
        Assert.Equal(owned, provider.Resolved);
        Assert.Equal([NumberOf(owned[2])], instance.Flagged);
    }

    // The read raises rather than answering an empty map, because an empty map claims the site holds
    // a row for none of the numbers asked about. Those scenes count as unresolved.
    [Theory]
    [MemberData(nameof(UnanswerableReads))]
    public async Task ARowReadThatCouldNotBeAnsweredCountsThoseScenesAsUnresolvedAndTheRunGoesOn(
        Type shape)
    {
        var owned = Scenes(SiteSceneMonitorPass.ChunkSize + 1);
        var instance = new Instance(owned.Select(NumberOf))
        {
            RefusingReadNumber = 1,
            Refusal = () => Unanswerable(shape),
        };

        var (tally, _) = await MonitorAsync(owned, instance);

        Assert.Equal(SiteSceneMonitorPass.ChunkSize, tally.Unresolved);
        Assert.Equal(1, tally.Monitored);
        Assert.Equal(2, instance.RowReads.Count);
    }

    // The stop arrives in the same shape a timeout does, so a pass containing it would end Completed
    // carrying counts that read like a finished site.
    [Fact]
    public async Task AStopDuringTheNumberReadEndsTheRunRatherThanCountingTheScene()
    {
        var owned = Scenes(3);
        var instance = new Instance(owned.Select(NumberOf));
        var provider = Answering(owned);
        using var stopping = new CancellationTokenSource();
        provider.Unreachable = () =>
        {
            stopping.Cancel();
            return new TaskCanceledException("the host stopped the run");
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => MonitorAsync(owned, instance, provider, stopping: stopping));

        Assert.Empty(instance.Flagged);
    }

    [Fact]
    public async Task AStopDuringTheRowReadEndsTheRunRatherThanCountingTheChunk()
    {
        var owned = Scenes(3);
        using var stopping = new CancellationTokenSource();
        var instance = new Instance(owned.Select(NumberOf))
        {
            RefusingReadNumber = 1,
            Refusal = () =>
            {
                stopping.Cancel();
                return new TaskCanceledException("the host stopped the run");
            },
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => MonitorAsync(owned, instance, stopping: stopping));

        Assert.Empty(instance.Flagged);
    }

    private static List<string> Scenes(int count)
        => [.. Enumerable.Range(1, count).Select(n => $"{n:x8}-0000-4000-8000-000000000000")];

    private static int NumberOf(string identity)
        => 1_000_000 + int.Parse(identity[..8], NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    private static RecordingProviderCatalogue Answering(IEnumerable<string> owned)
        => new(owned.ToDictionary(
            identity => identity, identity => (int?)NumberOf(identity), StringComparer.Ordinal));

    private static Exception Unanswerable(Type shape)
    {
        if (shape == typeof(HttpRequestException))
        {
            return new HttpRequestException("nothing answered");
        }

        if (shape == typeof(IOException))
        {
            return new IOException("the answer stopped part way");
        }

        if (shape == typeof(TaskCanceledException))
        {
            return new TaskCanceledException("the read timed out");
        }

        throw new ArgumentOutOfRangeException(nameof(shape), shape, "No failure is written for this.");
    }

    private static async Task<(SceneMonitorTally Tally, RecordingProviderCatalogue Provider)>
        MonitorAsync(
            IReadOnlyList<string> owned,
            Instance instance,
            RecordingProviderCatalogue? provider = null,
            Action<int>? beforeEachScene = null,
            CancellationTokenSource? stopping = null)
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
            stopping?.Token ?? TestCt);

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

    // The row identifier is the number itself. Which row a flag was set on is the fact under test,
    // and a second numbering beside it would only be a mapping this file also had to state.
    private sealed class Instance(IEnumerable<int> held)
    {
        private readonly HashSet<int> _held = [.. held];

        public List<IReadOnlyCollection<int>> RowReads { get; } = [];

        public List<int> SitesRead { get; } = [];

        public List<int> Flagged { get; } = [];

        public int? RefusingReadNumber { get; init; }

        // A factory rather than an instance, so a case can stop the run at the moment the read is
        // made and raise the shape that stop arrives in.
        public Func<Exception> Refusal { get; init; } = () => new HttpRequestException("nothing answered");

        // The row the instance declines to flag, or null where it declines none.
        public int? Declining { get; init; }

        public Task<IReadOnlyDictionary<int, int>> ReadRowsAsync(
            int siteId, IReadOnlyCollection<int> numbers, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            RowReads.Add([.. numbers]);
            SitesRead.Add(siteId);

            if (RowReads.Count == RefusingReadNumber)
            {
                throw Refusal();
            }

            return Task.FromResult<IReadOnlyDictionary<int, int>>(
                numbers.Where(_held.Contains).ToDictionary(number => number, number => number));
        }

        public Task<WhisparrResponse?> SetMonitoredAsync(int rowId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Flagged.Add(rowId);
            return Task.FromResult<WhisparrResponse?>(
                rowId == Declining ? null : RecordingWhisparrCore.Json(202, "{}"));
        }
    }
}
