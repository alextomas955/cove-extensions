using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WhisparrSync.Contracts;
using WhisparrSync.Library;
using WhisparrSync.Monitoring;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Library;

// The request counts are asserted beside the answers because every answer here is also derivable
// from a shape that reads the instance's whole catalogue.
public sealed class LibraryStatusPortTests
{
    private const string ForeignId = "5ee16943-0da6-4ee4-94c1-54172e3d0b7e";

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AnUnresolvedIdentityIsNoReadingAndNoRequest()
    {
        var reading = new RecordingEntityReading(status: 200, body: Monitored);

        var rows = await ReadAsync(reading, Nothing, [1, 2]);

        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Null(row.Reading));
        Assert.Equal(0, reading.Calls);
    }

    // An instance holding no entry is a different fact from an entry it holds and does not monitor.
    [Fact]
    public async Task AnAbsentEntityIsNotPresentAndNotMonitored()
    {
        var reading = new RecordingEntityReading(status: 404, body: "");

        var rows = await ReadAsync(reading, Resolving, [1]);

        Assert.Equal(new LibraryCardReading(false, false, false), rows[0].Reading);
    }

    [Fact]
    public async Task AHeldAndMonitoredEntityIsPresentAndMonitored()
    {
        var reading = new RecordingEntityReading(status: 200, body: Monitored);

        var rows = await ReadAsync(reading, Resolving, [1]);

        Assert.Equal(new LibraryCardReading(false, true, true), rows[0].Reading);
    }

    [Fact]
    public async Task AHeldAndUnmonitoredEntityIsPresentAndNotMonitored()
    {
        var reading = new RecordingEntityReading(status: 200, body: """{"monitored":false}""");

        var rows = await ReadAsync(reading, Resolving, [1]);

        Assert.Equal(new LibraryCardReading(false, true, false), rows[0].Reading);
    }

    // Both members stay unestablished, so the badge draws the unknown state rather than one of the
    // four a reader would act on.
    [Fact]
    public async Task AnUnreadableAnswerEstablishesNeither()
    {
        var reading = new RecordingEntityReading(status: 500, body: "");

        var rows = await ReadAsync(reading, Resolving, [1]);

        Assert.Equal(new LibraryCardReading(false, null, null), rows[0].Reading);
    }

    [Fact]
    public async Task AConnectionThatDroppedIsOneUnestablishedCardAndNoMore()
    {
        var reading = new RecordingEntityReading(status: 200, body: Monitored, throwOnCall: 1);

        var rows = await ReadAsync(reading, Resolving, [1, 2]);

        Assert.Equal(new LibraryCardReading(false, null, null), rows[0].Reading);
        Assert.Equal(new LibraryCardReading(false, true, true), rows[1].Reading);
    }

    // Nothing grows with the library. A page costs at most one request per card it was given.
    [Fact]
    public async Task APageCostsAtMostOneRequestPerRequestedCard()
    {
        var reading = new RecordingEntityReading(status: 200, body: Monitored);
        var requested = Enumerable.Range(1, 40).ToArray();

        var rows = await ReadAsync(reading, Resolving, requested);

        Assert.Equal(requested.Length, reading.Calls);
        Assert.Equal(requested, rows.Select(row => row.CoveId));
    }

    // The exclusion read comes first, because exclusion is tested before a state is derived. One
    // read per card would put a second request against a third party on every card of the page.
    [Fact]
    public async Task APageOfScenesCostsOneExclusionReadAndOneStatusReadPerIdentifier()
    {
        var reading = new RecordingSceneReading(status: 200, body: HeldAndMonitored);

        var readings = await ReadScenesAsync(reading, Excluding(reading), SceneIdentities(1, 2, 3));

        Assert.Equal(1, reading.ExclusionReads);
        Assert.Equal(3, reading.SceneReads);
        Assert.Equal([1, 2, 3], readings.Keys.Order());
    }

    [Fact]
    public async Task AHeldAndMonitoredSceneIsPresentAndMonitored()
    {
        var reading = new RecordingSceneReading(status: 200, body: HeldAndMonitored);

        var readings = await ReadScenesAsync(reading, Excluding(reading), SceneIdentities(1));

        Assert.Equal(new LibraryCardReading(false, true, true), readings[1]);
    }

    // An empty list means the instance holds no entry. It also establishes that no file is held,
    // because an instance holding no entry has nothing to hold a file for.
    [Fact]
    public async Task AnEmptyListIsAnAbsenceAndEstablishesNoFlag()
    {
        var reading = new RecordingSceneReading(status: 200, body: "[]");

        var readings = await ReadScenesAsync(reading, Excluding(reading), SceneIdentities(1));

        Assert.Equal(new LibraryCardReading(false, false, null, false), readings[1]);
    }

    [Fact]
    public async Task AHeldSceneWithAFileReadsAsInLibrary()
    {
        var reading = new RecordingSceneReading(status: 200, body: HeldWithAFile);

        var readings = await ReadScenesAsync(reading, Excluding(reading), SceneIdentities(1));

        Assert.Equal(new LibraryCardReading(false, true, true, true), readings[1]);
    }

    // Reading an absent file flag as false would count the scene among the ones the instance holds
    // no file for, on a fact nothing answered.
    [Fact]
    public async Task AHeldSceneWithNoFileFlagEstablishesNothingAboutAFile()
    {
        var reading = new RecordingSceneReading(status: 200, body: HeldAndMonitored);

        var readings = await ReadScenesAsync(reading, Excluding(reading), SceneIdentities(1));

        Assert.Null(readings[1].InLibrary);
    }

    [Fact]
    public async Task AnUnreadableSceneAnswerEstablishesNeither()
    {
        var reading = new RecordingSceneReading(status: 500, body: "");

        var readings = await ReadScenesAsync(reading, Excluding(reading), SceneIdentities(1));

        Assert.Equal(new LibraryCardReading(false, null, null), readings[1]);
    }

    // Exclusion and presence arrive from different reads and the badge tests exclusion first.
    // Folding them would report the scene as one the instance was never offered.
    [Fact]
    public async Task AnExcludedSceneCarriesTheExclusionBesideAnAbsence()
    {
        var reading = new RecordingSceneReading(status: 404, body: "");

        var readings = await ReadScenesAsync(
            reading, Excluding(reading, SceneIdentifier(1)), SceneIdentities(1));

        Assert.Equal(new LibraryCardReading(true, false, null, false), readings[1]);
    }

    // A role answering an empty set would report every scene as one the user has not excluded,
    // which is a fact no instance answered.
    [Fact]
    public async Task AGenerationHoldingNoExclusionRoleAsksNothingAndExcludesNothing()
    {
        var reading = new RecordingSceneReading(status: 200, body: HeldAndMonitored);

        var readings = await ReadScenesAsync(reading, NoExclusionRole, SceneIdentities(1));

        Assert.Equal(0, reading.ExclusionReads);
        Assert.Equal(new LibraryCardReading(false, true, true), readings[1]);
    }

    [Fact]
    public async Task AConnectionThatDroppedIsOneUnestablishedSceneAndNoMore()
    {
        var reading = new RecordingSceneReading(
            status: 200, body: HeldAndMonitored, throwOnCall: 1);

        var readings = await ReadScenesAsync(reading, Excluding(reading), SceneIdentities(1, 2));

        Assert.Equal(new LibraryCardReading(false, null, null), readings[1]);
        Assert.Equal(new LibraryCardReading(false, true, true), readings[2]);
    }

    // An instance that accepts the connection and then hangs reaches the port as a cancellation
    // nobody asked for. Letting it escape would fail the whole page.
    [Fact]
    public async Task AReadThatOutlivedTheClientsTimeoutIsOneUnestablishedCardAndNoMore()
    {
        var reading = new RecordingEntityReading(
            status: 200, body: Monitored, throwOnCall: 1, failure: () => new TaskCanceledException());

        var rows = await ReadAsync(reading, Resolving, [1, 2]);

        Assert.Equal(new LibraryCardReading(false, null, null), rows[0].Reading);
        Assert.Equal(new LibraryCardReading(false, true, true), rows[1].Reading);
    }

    [Fact]
    public async Task AReadThatOutlivedTheClientsTimeoutIsOneUnestablishedSceneAndNoMore()
    {
        var reading = new RecordingSceneReading(
            status: 200,
            body: HeldAndMonitored,
            throwOnCall: 1,
            failure: () => new TaskCanceledException());

        var readings = await ReadScenesAsync(reading, Excluding(reading), SceneIdentities(1, 2));

        Assert.Equal(new LibraryCardReading(false, null, null), readings[1]);
        Assert.Equal(new LibraryCardReading(false, true, true), readings[2]);
    }

    // A shutdown is not a verdict about the instance, so it leaves the port rather than reading as
    // a card nothing could be established about.
    [Fact]
    public async Task AShutdownLeavesThePortRatherThanReadingAsAnUnestablishedCard()
    {
        using var stopping = new CancellationTokenSource();
        await stopping.CancelAsync();
        var reading = new RecordingEntityReading(status: 200, body: Monitored);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await new LibraryStatusPort(Resolving, NullLogger.Instance)
                .ReadEntityCardsAsync(
                    reading.AnswerAsync,
                    NoEntityBatch,
                    WhisparrEntityKind.Studio,
                    WhisparrGeneration.V3,
                    Instance,
                    ApiKey,
                    [1],
                    stopping.Token));
    }

    [Fact]
    public async Task AShutdownLeavesTheScenePathRatherThanReadingAsAnUnestablishedScene()
    {
        using var stopping = new CancellationTokenSource();
        await stopping.CancelAsync();
        var reading = new RecordingSceneReading(status: 200, body: HeldAndMonitored);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await new LibraryStatusPort(Nothing, NullLogger.Instance)
                .ReadSceneCardsAsync(
                    reading,
                    new Capability<IWhisparrSceneExclusionReading>(reading, null),
                    NoSceneBatch,
                    Instance,
                    ApiKey,
                    WhisparrGeneration.V3,
                    SceneIdentities(1),
                    stopping.Token));
    }

    private static string Monitored => """{"id":7,"monitored":true}""";

    // The per-scene route answers a list, of one row where the instance holds the scene.
    private static string HeldAndMonitored => """[{"id":9,"monitored":true}]""";

    private static string HeldWithAFile => """[{"id":9,"monitored":true,"hasFile":true}]""";

    private static string SceneIdentifier(int coveId) => $"scene-{coveId}";

    private static IReadOnlyList<LibraryCardIdentity> SceneIdentities(params int[] coveIds)
        => [.. coveIds.Select(coveId => new LibraryCardIdentity(coveId, SceneIdentifier(coveId)))];

    // V2 registers no exclusion role at all.
    private static Capability<IWhisparrSceneExclusionReading> NoExclusionRole
        => GenerationCapabilities.For(WhisparrGeneration.V2)
            .Obtain<IWhisparrSceneExclusionReading>();

    // Every entity is named by one identifier.
    private static IEntityIdentityPort Resolving => new FakeIdentities(IdentityResolution.At(ForeignId));

    // No entity is named at all, a library holding no usable link.
    private static IEntityIdentityPort Nothing => new FakeIdentities(IdentityResolution.Unmatched);

    // One line per contained card. The count is bounded by the page size the route accepts.
    [Fact]
    public async Task AContainedCardLeavesOneLineNamingTheFailureAndTheHost()
    {
        var recorded = new RecordingLogger();
        var reading = new RecordingEntityReading(status: 200, body: Monitored, throwOnCall: 1);

        await ReadAsync(reading, Resolving, [1, 2], recorded);

        Assert.Single(recorded.ContainedLines);
        Assert.Contains("HttpRequestException", recorded.ContainedLines[0], StringComparison.Ordinal);
        Assert.Contains(Instance.Host, recorded.ContainedLines[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACardThatAnsweredLeavesNoLine()
    {
        var recorded = new RecordingLogger();
        var reading = new RecordingEntityReading(status: 200, body: Monitored);

        await ReadAsync(reading, Resolving, [1, 2], recorded);

        Assert.Empty(recorded.ContainedLines);
    }

    [Fact]
    public async Task AContainedSceneLeavesOneLineNamingTheFailureAndTheHost()
    {
        var recorded = new RecordingLogger();
        var reading = new RecordingSceneReading(
            status: 200, body: HeldAndMonitored, throwOnCall: 1);

        await ReadScenesAsync(reading, Excluding(reading), SceneIdentities(1, 2), recorded);

        Assert.Single(recorded.ContainedLines);
        Assert.Contains(Instance.Host, recorded.ContainedLines[0], StringComparison.Ordinal);
    }

    // The instance every case reads from. Its host is what a contained line names.
    private static Uri Instance => new("http://whisparr.invalid");

    private static string ApiKey => "0e2e0e2e0e2e0e2e0e2e0e2e0e2e0e2e";

    // The generation registers no batch role, so every card is asked about on its own. What the
    // batch path does instead has tests of its own.
    private static Capability<IWhisparrEntityBatchReading> NoEntityBatch { get; } = new(
        null, new CapabilityRefusal(WhisparrCapability.ReadEntityCardsInBatch, WhisparrGeneration.V2));

    private static Capability<IWhisparrSceneBatchReading> NoSceneBatch { get; } = new(
        null, new CapabilityRefusal(WhisparrCapability.ReadSceneCardsInBatch, WhisparrGeneration.V2));

    private static async Task<IReadOnlyList<LibraryStatusRow>> ReadAsync(
        RecordingEntityReading reading,
        IEntityIdentityPort identities,
        IReadOnlyList<int> coveIds,
        ILogger? log = null)
        => (await new LibraryStatusPort(identities, log ?? NullLogger.Instance)
            .ReadEntityCardsAsync(
                reading.AnswerAsync,
                NoEntityBatch,
                WhisparrEntityKind.Studio,
                WhisparrGeneration.V3,
                Instance,
                ApiKey,
                coveIds,
                TestCt)).Rows;

    private static Capability<IWhisparrSceneExclusionReading> Excluding(
        RecordingSceneReading reading, params string[] excluded)
    {
        reading.Excludes(excluded);
        return new Capability<IWhisparrSceneExclusionReading>(reading, null);
    }

    private static async Task<IReadOnlyDictionary<int, LibraryCardReading>> ReadScenesAsync(
        RecordingSceneReading reading,
        Capability<IWhisparrSceneExclusionReading> exclusions,
        IReadOnlyList<LibraryCardIdentity> identities,
        ILogger? log = null)
        => (await new LibraryStatusPort(Nothing, log ?? NullLogger.Instance).ReadSceneCardsAsync(
            reading,
            exclusions,
            NoSceneBatch,
            Instance,
            ApiKey,
            WhisparrGeneration.V3,
            identities,
            TestCt)).Readings;

    private sealed class FakeIdentities(IdentityResolution answer) : IEntityIdentityPort
    {
        public Task<IdentityResolution> ResolveAsync(
            WhisparrEntityKind kind, int coveId, WhisparrGeneration generation, CancellationToken ct)
            => Task.FromResult(answer);
    }

    // Both scene roles sit on one recorder, so the counts a case asserts come from the same object
    // and cannot describe two seams that were never used together.
    private sealed class RecordingSceneReading(
        int status, string body, int? throwOnCall = null, Func<Exception>? failure = null)
        : IWhisparrSceneStatusReading, IWhisparrSceneExclusionReading
    {
        private IReadOnlySet<string> _excluded = new HashSet<string>(StringComparer.Ordinal);

        public int SceneReads { get; private set; }

        public int ExclusionReads { get; private set; }

        public void Excludes(params string[] excluded)
            => _excluded = new HashSet<string>(excluded, StringComparer.Ordinal);

        public Task<WhisparrResponse> ReadEntityPresenceAsync(
            Uri baseAddress,
            string apiKey,
            WhisparrEntityKind kind,
            string foreignId,
            CancellationToken ct)
            => throw new InvalidOperationException(
                "The scene card path probes no entity: a page of cards names no entity they sit under.");

        public Task<WhisparrResponse> ReadSceneByRemoteIdAsync(
            Uri baseAddress, string apiKey, string remoteId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            SceneReads++;

            return SceneReads == throwOnCall
                ? throw (failure?.Invoke() ?? new HttpRequestException("nothing answered"))
                : Task.FromResult(new WhisparrResponse(status, "application/json", body));
        }

        public Task<IReadOnlySet<string>> ReduceHeldScenesAsync(
            Uri baseAddress,
            string apiKey,
            IReadOnlyCollection<string> foreignIds,
            CancellationToken ct)
            => throw new InvalidOperationException(
                "This surface asks about one scene at a time and never about a batch of them.");

        public Task<IReadOnlySet<string>> ReduceExclusionsAsync(
            Uri baseAddress,
            string apiKey,
            IReadOnlyCollection<string> providerSceneIds,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            ExclusionReads++;

            return Task.FromResult<IReadOnlySet<string>>(
                new HashSet<string>(providerSceneIds.Where(_excluded.Contains), StringComparer.Ordinal));
        }

        public Task<SceneExclusionLookup> FindSceneExclusionAsync(
            Uri baseAddress, string apiKey, string foreignId, CancellationToken ct)
            => throw new NotSupportedException(
                "The card path asks about a page of scenes at once and never for one exclusion "
                    + "row's own identifier.");
    }

    // Keeps the contained-request lines, by event id, as a sink would write them.
    private sealed class RecordingLogger : ILogger
    {
        private const int ContainedRequestEventId = 2117;

        private readonly List<string> _lines = [];

        public List<string> ContainedLines => _lines;

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

            if (eventId.Id == ContainedRequestEventId)
            {
                _lines.Add(formatter(state, exception));
            }
        }
    }

    private sealed class RecordingEntityReading(
        int status, string body, int? throwOnCall = null, Func<Exception>? failure = null)
    {
        public int Calls { get; private set; }

        public Task<WhisparrResponse> AnswerAsync(string foreignId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Calls++;
            Assert.Equal(ForeignId, foreignId);

            return Calls == throwOnCall
                ? throw (failure?.Invoke() ?? new HttpRequestException("nothing answered"))
                : Task.FromResult(new WhisparrResponse(status, "application/json", body));
        }
    }
}
