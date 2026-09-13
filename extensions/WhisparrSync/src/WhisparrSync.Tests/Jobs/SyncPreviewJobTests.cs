using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WhisparrSync.Contracts;
using WhisparrSync.Jobs;
using WhisparrSync.Library;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Jobs;

/// <summary>
/// What one count reports, what it asks the instance, and what it leaves behind when it fails.
/// </summary>
/// <remarks>
/// The batch bound is asserted as arithmetic over the transport's own ceiling rather than against a
/// number written here, so a later batch size that breaks the bound reddens. Nothing about it can be
/// observed against a small fixture, which is the reason it is pinned at all.
/// </remarks>
public sealed class SyncPreviewJobTests
{
    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    /// <summary>One batch's worst-case answer stays inside what the transport will read.</summary>
    /// <remarks>
    /// The transport refuses an answer past its bound outright rather than truncating it, so a batch
    /// whose every identifier is held would answer nothing at all and the count would fail on a
    /// library that is simply already synced.
    /// </remarks>
    [Fact]
    public void OneBatchsWorstCaseAnswerStaysInsideWhatTheTransportWillRead()
    {
        var worstCase = (long)SyncPreviewJob.ChunkSize * SyncPreviewJob.MeasuredBytesPerHit;

        Assert.True(
            worstCase < WhisparrClient.MaxResponseBytes,
            $"a full batch answered in its entirety is {worstCase} bytes, past the "
                + $"{WhisparrClient.MaxResponseBytes}-byte bound the transport reads within.");
    }

    /// <summary>The composed body is a bare array of identifier strings.</summary>
    /// <remarks>
    /// Parsed rather than compared as text. An object naming the identifiers as a member is answered
    /// 400 by a real instance and would satisfy any assertion made on the body's characters.
    /// </remarks>
    [Fact]
    public async Task TheComposedBodyIsABareArrayOfIdentifiers()
    {
        var bytes = BodyRecordingHandler.Answering(HttpStatusCode.OK, "[]");
        var client = TestWhisparrClient.Over(bytes);

        await client.ReduceHeldScenesAsync(Instance, Key, [FirstScene, SecondScene], TestCt);

        var sent = Assert.Single(bytes.Requests);
        Assert.Equal(HttpMethod.Post, sent.Method);
        using var body = JsonDocument.Parse(sent.Body);
        Assert.Equal(JsonValueKind.Array, body.RootElement.ValueKind);
        Assert.Equal(
            [FirstScene, SecondScene],
            body.RootElement.EnumerateArray().Select(id => id.GetString()));
    }

    /// <summary>
    /// An identifier the instance holds counts as already there, and one it does not counts as not
    /// yet there, over more identifiers than one batch carries.
    /// </summary>
    /// <remarks>
    /// Seeded past the batch size on purpose, so the flush of the final partial batch is exercised:
    /// a run that only asked about full batches would silently drop the remainder.
    /// </remarks>
    [Fact]
    public async Task EachIdentifierIsClassifiedAndThePartialFinalBatchIsAskedAboutToo()
    {
        var identifiers = Identifiers(SyncPreviewJob.ChunkSize + 3);
        var instance = new HeldScenes(identifiers.Take(10));

        var counted = await RunAsync(identifiers, instance);

        Assert.NotNull(counted);
        Assert.Equal(10, counted.AlreadyThere);
        Assert.Equal(identifiers.Count - 10, counted.NotYetThere);
        Assert.Equal(2, instance.Asked.Count);
        Assert.Equal(SyncPreviewJob.ChunkSize, instance.Asked[0].Count);
        Assert.Equal(3, instance.Asked[1].Count);
    }

    /// <summary>
    /// A batch the instance did not answer ends the run and holds no count at all.
    /// </summary>
    /// <remarks>
    /// The whole point of the count arriving in one piece. A partial count written to the slot would
    /// be answered to the page as a complete one, and the reader would act on a figure that is a
    /// fraction of the truth.
    /// </remarks>
    [Fact]
    public async Task ABatchTheInstanceDidNotAnswerEndsTheRunAndHoldsNoCount()
    {
        var identifiers = Identifiers(SyncPreviewJob.ChunkSize + 3);
        var cache = new SyncPreviewCache(TimeProvider.System);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunAsync(identifiers, new HeldScenes([], failOnCall: 2), cache));

        Assert.Null(cache.Held(WhisparrGeneration.V3));
    }

    /// <summary>
    /// A count over a library the instance now holds more of reports fewer scenes left to send.
    /// </summary>
    /// <remarks>
    /// The honesty the comparison exists for. A count derived from Cove's own rows would report the
    /// same figure after a run as before it, and the reader would have no way to tell a sync that
    /// worked from one that did nothing.
    /// </remarks>
    [Fact]
    public async Task ASecondCountAfterTheInstanceTookScenesReportsFewerLeftToSend()
    {
        var identifiers = Identifiers(5);
        var instance = new HeldScenes([]);

        var first = await RunAsync(identifiers, instance);
        instance.NowHolds(identifiers.Take(3));
        var second = await RunAsync(identifiers, instance);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(5, first.NotYetThere);
        Assert.Equal(2, second.NotYetThere);
        Assert.Equal(3, second.AlreadyThere);
    }

    /// <summary>The count's answer is what the slot then holds.</summary>
    [Fact]
    public async Task TheCountsThreeNumbersAreWhatTheSlotThenHolds()
    {
        var cache = new SyncPreviewCache(TimeProvider.System);
        var identifiers = Identifiers(4);

        var counted = await RunAsync(identifiers, new HeldScenes(identifiers.Take(1)), cache);

        Assert.Equal(counted, cache.Held(WhisparrGeneration.V3));
    }

    /// <summary>A count reaching no instance holds nothing and reports nothing.</summary>
    [Fact]
    public async Task ACountThatCouldNotBeAimedHoldsNothing()
    {
        var cache = new SyncPreviewCache(TimeProvider.System);
        await using var provider = Scopes(StubLibraryIdentities.OfScenes([], unidentified: 0), cache);

        var counted = await SyncPreviewJob.RunAsync(
            provider.GetRequiredService<IServiceScopeFactory>(),
            (_, _) => Task.FromResult<SyncPreviewAiming?>(null),
            NullLogger.Instance,
            TestCt);

        Assert.Null(counted);
        Assert.Null(cache.Held(WhisparrGeneration.V3));
    }

    /// <summary>
    /// A library of three studios, two of which the instance holds, counts two and one from one
    /// request.
    /// </summary>
    [Fact]
    public async Task AThreeStudioLibraryCountsTwoHeldAndOneNotYetThereFromOneRequest()
    {
        var studios = Studios(3);
        var source = new SiteNumbers(NumbersFor(studios));
        var instance = new HeldSites([NumberOf(1), NumberOf(2)]);

        var counted = await RunSitesAsync(studios, source, instance, TestCt);

        Assert.NotNull(counted);
        Assert.Equal(2, counted.AlreadyThere);
        Assert.Equal(1, counted.NotYetThere);
        Assert.Equal(3, Assert.Single(instance.Asked).Count);
        Assert.Equal(3, source.Resolved.Count);
    }

    /// <summary>
    /// A library larger than one batch asks once per batch and the totals are the sum.
    /// </summary>
    /// <remarks>
    /// Seeded past the batch size on purpose, so the flush of the final partial batch is exercised:
    /// a count that only asked about full batches would silently drop the remainder.
    /// </remarks>
    [Fact]
    public async Task ALibraryPastOneBatchAsksOncePerBatchAndTheTotalsAddUp()
    {
        var studios = Studios(SyncPreviewJob.ChunkSize + 3);
        var source = new SiteNumbers(NumbersFor(studios));
        var instance = new HeldSites(Enumerable.Range(1, 10).Select(NumberOf));

        var counted = await RunSitesAsync(studios, source, instance, TestCt);

        Assert.NotNull(counted);
        Assert.Equal(2, instance.Asked.Count);
        Assert.Equal(SyncPreviewJob.ChunkSize, instance.Asked[0].Count);
        Assert.Equal(3, instance.Asked[1].Count);
        Assert.Equal(10, counted.AlreadyThere);
        Assert.Equal(studios.Count - 10, counted.NotYetThere);
    }

    /// <summary>
    /// A studio the library holds no identifier for is counted where it already was, and nothing is
    /// asked about it.
    /// </summary>
    [Fact]
    public async Task AStudioTheLibraryHoldsNoIdentifierForIsCountedWhereItWasAndAsksNothing()
    {
        var studios = Studios(2);
        var source = new SiteNumbers(NumbersFor(studios));
        var instance = new HeldSites([]);

        var counted = await RunSitesAsync(studios, source, instance, TestCt);

        Assert.NotNull(counted);
        Assert.Equal(UnidentifiedStudios, counted.Skipped);
        Assert.Equal(2, source.Resolved.Count);
        Assert.Equal(2, Assert.Single(instance.Asked).Count);
    }

    /// <summary>No more than the bound's own number of metadata resolves is outstanding at once.</summary>
    /// <remarks>
    /// Over a batch twice the bound, and against a source that holds each resolve open until the
    /// bound is reached. A count resolving one at a time never reaches the bound and a count
    /// resolving the whole batch at once passes it, so the assertion reddens in both directions.
    /// </remarks>
    [Fact]
    public async Task NoMoreThanTheBoundsMetadataResolvesAreOutstandingAtOnce()
    {
        var studios = Studios(SyncPreviewJob.MetadataResolvesInFlight * 2);
        var source = new SiteNumbers(NumbersFor(studios), waitsForCompany: true);

        var counted = await RunSitesAsync(studios, source, new HeldSites([]), TestCt);

        Assert.NotNull(counted);
        Assert.Equal(SyncPreviewJob.MetadataResolvesInFlight, source.MaxInFlight);
    }

    /// <summary>
    /// A metadata source that was not reached leaves no count held and counts no studio anywhere.
    /// </summary>
    /// <remarks>
    /// Where a rate-limited answer arrives. Counted as a studio the instance does not hold, it would
    /// offer that studio for registration with nothing saying so.
    /// </remarks>
    [Fact]
    public async Task AMetadataSourceThatWasNotReachedLeavesNoCountAndCountsNoStudio()
    {
        var studios = Studios(3);
        var cache = new SyncPreviewCache(TimeProvider.System);
        var numbers = NumbersFor(studios);
        numbers[Identity(2)] = WhisparrSiteNumber.NotReached;
        var instance = new HeldSites([]);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunSitesAsync(studios, new SiteNumbers(numbers), instance, TestCt, cache));

        Assert.Empty(instance.Asked);
        Assert.Null(cache.Held(WhisparrGeneration.V2));
    }

    /// <summary>An instance read that raised leaves no count held.</summary>
    [Fact]
    public async Task AnInstanceReadThatRaisedLeavesNoCountAtAll()
    {
        var studios = Studios(3);
        var cache = new SyncPreviewCache(TimeProvider.System);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunSitesAsync(
                studios,
                new SiteNumbers(NumbersFor(studios)),
                new HeldSites([], failOnCall: 1),
                TestCt,
                cache));

        Assert.Null(cache.Held(WhisparrGeneration.V2));
    }

    /// <summary>A host stop during the site walk propagates as a stop.</summary>
    /// <remarks>
    /// A stop reported as a count that did not finish would be logged and answered as this
    /// product's own failure, and the run would end Failed rather than Cancelled.
    /// </remarks>
    [Fact]
    public async Task AHostStopDuringTheSiteWalkPropagatesAsAStop()
    {
        using var stopping = new CancellationTokenSource();
        var studios = Studios(SyncPreviewJob.ChunkSize + 2);
        var instance = new HeldSites([], onAsk: stopping.Cancel);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => RunSitesAsync(
                studios, new SiteNumbers(NumbersFor(studios)), instance, stopping.Token));
    }

    /// <summary>
    /// A studio the metadata source names no site for is counted with the studios carrying no
    /// identifier, and in neither other count.
    /// </summary>
    /// <remarks>
    /// Not a hypothetical column: some of the library's studios answer nothing at the metadata
    /// source. Counted as not yet there, each would be offered for registration and the run could
    /// compose no add for it.
    /// </remarks>
    [Fact]
    public async Task AStudioTheSourceNamesNoSiteForIsCountedWithThoseCarryingNoIdentifier()
    {
        var studios = Studios(3);
        var numbers = NumbersFor(studios);
        numbers[Identity(2)] = WhisparrSiteNumber.NamesNone;
        var instance = new HeldSites([NumberOf(1)]);

        var counted = await RunSitesAsync(studios, new SiteNumbers(numbers), instance, TestCt);

        Assert.NotNull(counted);
        Assert.Equal(1, counted.AlreadyThere);
        Assert.Equal(1, counted.NotYetThere);
        Assert.Equal(UnidentifiedStudios + 1, counted.Skipped);
    }

    /// <summary>
    /// The studios the source names no site for are reported once with how many they were.
    /// </summary>
    [Fact]
    public async Task TheStudiosTheSourceNamesNoSiteForAreReportedOnceWithHowManyTheyWere()
    {
        var studios = Studios(3);
        var numbers = NumbersFor(studios);
        numbers[Identity(1)] = WhisparrSiteNumber.NamesNone;
        numbers[Identity(3)] = WhisparrSiteNumber.NamesNone;
        var recorded = new RecordingLogger(NamesNoSiteEventId);

        await RunSitesAsync(
            studios, new SiteNumbers(numbers), new HeldSites([]), TestCt, log: recorded);

        Assert.Contains("2 of the library's studios", Assert.Single(recorded.Lines), StringComparison.Ordinal);
    }

    /// <summary>The scene comparison's third count is what the library answers and nothing else.</summary>
    [Fact]
    public async Task TheSceneComparisonsThirdCountIsUnchanged()
    {
        var identifiers = Identifiers(4);

        var counted = await RunAsync(identifiers, new HeldScenes(identifiers.Take(1)));

        Assert.NotNull(counted);
        Assert.Equal(UnidentifiedScenes, counted.Skipped);
    }

    /// <summary>
    /// A read that timed out inside either comparison leaves no count held and is reported as a
    /// count that did not finish.
    /// </summary>
    /// <remarks>
    /// A timeout arrives in the shape a host stop does, so it reaches neither containment unless
    /// it is named. Walked past, it would leave a count short by a whole batch and reading exactly
    /// like a complete one.
    /// </remarks>
    [Theory]
    [InlineData(SyncRegisters.Sites)]
    [InlineData(SyncRegisters.Scenes)]
    public async Task AReadThatTimedOutLeavesNoCountAndSaysTheCountDidNotFinish(SyncRegisters registers)
    {
        var cache = new SyncPreviewCache(TimeProvider.System);
        var recorded = new RecordingLogger(CountDidNotFinishEventId);
        var timedOut = new TaskCanceledException("the read timed out");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunEitherAsync(registers, timedOut, cache, recorded));

        Assert.Single(recorded.Lines);
        Assert.Null(cache.Held(WhisparrGeneration.V2));
        Assert.Null(cache.Held(WhisparrGeneration.V3));
    }

    /// <summary>A host stop inside either comparison propagates as a stop.</summary>
    [Theory]
    [InlineData(SyncRegisters.Sites)]
    [InlineData(SyncRegisters.Scenes)]
    public async Task AHostStopInsideEitherComparisonPropagatesAsAStop(SyncRegisters registers)
    {
        using var stopping = new CancellationTokenSource();
        var cache = new SyncPreviewCache(TimeProvider.System);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => RunEitherAsync(registers, null, cache, NullLogger.Instance, stopping));
    }

    private static readonly Uri Instance = new("http://whisparr-v3:6969");

    private const string Key = "0e2e0e2e0e2e0e2e0e2e0e2e0e2e0e2e";

    private const string FirstScene = "023bacff-8d1d-4f27-bac5-bdaf833f5616";

    private const string SecondScene = "3c0a6b21-9f7d-4c58-a3e2-71b0d4f5e8a9";

    /// <summary>How many studios the library carries no identifier at all for.</summary>
    private const int UnidentifiedStudios = 7;

    /// <summary>How many scenes the library carries no identifier at all for.</summary>
    private const int UnidentifiedScenes = 7;

    private const int NamesNoSiteEventId = 2129;

    private const int CountDidNotFinishEventId = 2126;

    private static List<string> Identifiers(int count)
        => [.. Enumerable.Range(1, count).Select(Identity)];

    private static string Identity(int n) => $"{n:x8}-0000-4000-8000-000000000000";

    /// <summary>The number the metadata source names the site of studio <paramref name="n"/> by.</summary>
    /// <remarks>
    /// Held apart from Cove's own id for the studio, so a count that compared the instance's answer
    /// against the wrong one of the two would not pass against one shared value.
    /// </remarks>
    private static int NumberOf(int n) => (n * 10) + 3;

    private static List<LibrarySiteIdentity> Studios(int count)
        => [.. Enumerable.Range(1, count).Select(n => new LibrarySiteIdentity(n, Identity(n)))];

    private static Dictionary<string, WhisparrSiteNumber> NumbersFor(
        IEnumerable<LibrarySiteIdentity> studios)
        => studios.ToDictionary(
            studio => studio.RemoteId,
            studio => WhisparrSiteNumber.Numbered(NumberOf(studio.StudioId)),
            StringComparer.Ordinal);

    /// <summary>
    /// One count of either kind, whose read either answers nothing or raises
    /// <paramref name="failure"/>.
    /// </summary>
    /// <remarks>
    /// Seeded past the batch size so a stop signalled on the first batch is met by the walk rather
    /// than by the end of the library.
    /// </remarks>
    private static Task<SyncPreviewView?> RunEitherAsync(
        SyncRegisters registers,
        Exception? failure,
        SyncPreviewCache cache,
        ILogger log,
        CancellationTokenSource? stopping = null)
    {
        var seeded = SyncPreviewJob.ChunkSize + 2;
        var provider = Scopes(
            registers == SyncRegisters.Sites
                ? StubLibraryIdentities.OfSites(Studios(seeded), UnidentifiedStudios)
                : StubLibraryIdentities.OfScenes(Identifiers(seeded), UnidentifiedScenes),
            cache);

        Task<IReadOnlySet<string>> HeldAsync(
            IReadOnlyCollection<string> asked, CancellationToken batchCt)
        {
            stopping?.Cancel();
            return failure is null
                ? Task.FromResult<IReadOnlySet<string>>(new HashSet<string>(StringComparer.Ordinal))
                : Task.FromException<IReadOnlySet<string>>(failure);
        }

        Task<SiteBatchReading> HeldSitesAsync(
            IReadOnlyCollection<string> asked, CancellationToken batchCt)
        {
            stopping?.Cancel();
            return failure is null
                ? Task.FromResult(
                    new SiteBatchReading(
                        new HashSet<string>(StringComparer.Ordinal),
                        new HashSet<string>(StringComparer.Ordinal)))
                : Task.FromException<SiteBatchReading>(failure);
        }

        return SyncPreviewJob.RunAsync(
            provider.GetRequiredService<IServiceScopeFactory>(),
            (_, _) => Task.FromResult<SyncPreviewAiming?>(
                registers == SyncRegisters.Sites
                    ? new SyncPreviewAiming(
                        WhisparrGeneration.V2, registers, Held: null, HeldSitesAsync)
                    : new SyncPreviewAiming(
                        WhisparrGeneration.V3, registers, HeldAsync, HeldSites: null)),
            log,
            stopping?.Token ?? TestCt);
    }

    private static Task<SyncPreviewView?> RunSitesAsync(
        IReadOnlyList<LibrarySiteIdentity> studios,
        SiteNumbers source,
        HeldSites instance,
        CancellationToken ct,
        SyncPreviewCache? cache = null,
        ILogger? log = null)
    {
        var provider = Scopes(
            StubLibraryIdentities.OfSites(studios, UnidentifiedStudios),
            cache ?? new SyncPreviewCache(TimeProvider.System));

        return SyncPreviewJob.RunAsync(
            provider.GetRequiredService<IServiceScopeFactory>(),
            (_, _) => Task.FromResult<SyncPreviewAiming?>(
                new SyncPreviewAiming(
                    WhisparrGeneration.V2,
                    SyncRegisters.Sites,
                    Held: null,
                    (asked, batchCt) => global::WhisparrSync.WhisparrSync.ReduceHeldSitesAsync(
                        source, instance.AskAsync, asked, batchCt))),
            log ?? NullLogger.Instance,
            ct);
    }

    private static Task<SyncPreviewView?> RunAsync(
        IReadOnlyList<string> identifiers, HeldScenes instance, SyncPreviewCache? cache = null)
    {
        var provider = Scopes(
            StubLibraryIdentities.OfScenes(identifiers, UnidentifiedScenes),
            cache ?? new SyncPreviewCache(TimeProvider.System));

        return SyncPreviewJob.RunAsync(
            provider.GetRequiredService<IServiceScopeFactory>(),
            (_, _) => Task.FromResult<SyncPreviewAiming?>(
                new SyncPreviewAiming(
                    WhisparrGeneration.V3,
                    SyncRegisters.Scenes,
                    instance.AskAsync,
                    HeldSites: null)),
            NullLogger.Instance,
            TestCt);
    }

    private static ServiceProvider Scopes(
        ILibrarySceneIdentityPort identities, SyncPreviewCache cache)
        => new ServiceCollection()
            .AddSingleton(identities)
            .AddSingleton(cache)
            .BuildServiceProvider();

    /// <summary>Keeps one event id's lines, as a sink would write them.</summary>
    private sealed class RecordingLogger(int kept) : ILogger
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);

            if (eventId.Id == kept)
            {
                Lines.Add(formatter(state, exception));
            }
        }
    }

    /// <summary>What an instance holds, recording every batch it was asked about.</summary>
    private sealed class HeldScenes(IEnumerable<string> held, int? failOnCall = null)
    {
        private readonly HashSet<string> _held = new(held, StringComparer.OrdinalIgnoreCase);

        public List<IReadOnlyCollection<string>> Asked { get; } = [];

        public void NowHolds(IEnumerable<string> more) => _held.UnionWith(more);

        public Task<IReadOnlySet<string>> AskAsync(
            IReadOnlyCollection<string> foreignIds, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Asked.Add([.. foreignIds]);
            return Asked.Count == failOnCall
                ? throw new HttpRequestException("nothing answered")
                : Task.FromResult<IReadOnlySet<string>>(
                    foreignIds.Where(_held.Contains).ToHashSet(StringComparer.Ordinal));
        }
    }

    /// <summary>Which sites an instance holds, recording every batch of numbers it was asked about.</summary>
    private sealed class HeldSites(
        IEnumerable<int> held, int? failOnCall = null, Action? onAsk = null)
    {
        private readonly HashSet<int> _held = [.. held];

        public List<IReadOnlyCollection<int>> Asked { get; } = [];

        public Task<IReadOnlySet<int>> AskAsync(
            IReadOnlyCollection<int> siteNumbers, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Asked.Add([.. siteNumbers]);
            onAsk?.Invoke();

            return Asked.Count == failOnCall
                ? throw new HttpRequestException("nothing answered")
                : Task.FromResult<IReadOnlySet<int>>(siteNumbers.Where(_held.Contains).ToHashSet());
        }
    }

    /// <summary>
    /// A metadata source answering each identifier, recording how many resolves were outstanding at
    /// once.
    /// </summary>
    /// <remarks>
    /// With <paramref name="waitsForCompany"/> a resolve is held open until as many are outstanding
    /// as the bound allows, so what the caller bounds is observable rather than timed. The wait ends
    /// on its own where that never happens, so a caller resolving one at a time fails the assertion
    /// rather than hanging.
    /// </remarks>
    private sealed class SiteNumbers(
        IReadOnlyDictionary<string, WhisparrSiteNumber> answers, bool waitsForCompany = false)
        : ISiteNumberPort
    {
        private readonly TaskCompletionSource _company =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly Lock _gate = new();
        private int _live;

        public List<string> Resolved { get; } = [];

        public int MaxInFlight { get; private set; }

        public async Task<WhisparrSiteNumber> ResolveSiteNumberAsync(
            string storedSiteId, CancellationToken ct)
        {
            var live = Interlocked.Increment(ref _live);
            lock (_gate)
            {
                Resolved.Add(storedSiteId);
                MaxInFlight = Math.Max(MaxInFlight, live);
            }

            if (live >= SyncPreviewJob.MetadataResolvesInFlight)
            {
                _company.TrySetResult();
            }

            if (waitsForCompany)
            {
                await Task.WhenAny(_company.Task, Task.Delay(TimeSpan.FromMilliseconds(250), ct))
                    .ConfigureAwait(false);
            }

            Interlocked.Decrement(ref _live);
            return answers.TryGetValue(storedSiteId, out var answer)
                ? answer
                : throw new InvalidOperationException(
                    $"This source was given no answer for {storedSiteId}, so nothing under test "
                        + "should be resolving it.");
        }
    }
}
