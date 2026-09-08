using WhisparrSync.Contracts;
using WhisparrSync.Library;
using WhisparrSync.Monitoring;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Library;

/// <summary>
/// What each card on a page reads as, and how many requests a page of them costs.
/// </summary>
/// <remarks>
/// The two facts a badge acts on differently are the point: an entity the instance holds no entry
/// for, and one nothing could be established about. A port collapsing them would draw a state for a
/// card nothing answered for.
/// <para>
/// The cost claim is asserted rather than the answers alone, because every value here is also
/// derivable from a shape that reads the instance's whole catalogue.
/// </para>
/// </remarks>
public sealed class LibraryStatusPortTests
{
    private const string ForeignId = "5ee16943-0da6-4ee4-94c1-54172e3d0b7e";

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    /// <summary>
    /// A card the library names no identifier for costs no request and carries no reading.
    /// </summary>
    [Fact]
    public async Task AnUnresolvedIdentityIsNoReadingAndNoRequest()
    {
        var reading = new RecordingEntityReading(status: 200, body: Monitored);

        var rows = await ReadAsync(reading, Nothing, [1, 2]);

        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Null(row.Reading));
        Assert.Equal(0, reading.Calls);
    }

    /// <summary>
    /// An instance holding no entry answers an absence, which is a different fact from an entry it
    /// holds and does not monitor.
    /// </summary>
    [Fact]
    public async Task AnAbsentEntityIsNotPresentAndNotMonitored()
    {
        var reading = new RecordingEntityReading(status: 404, body: "");

        var rows = await ReadAsync(reading, Resolving, [1]);

        Assert.Equal(new LibraryCardReading(false, false, false), rows[0].Reading);
    }

    /// <summary>An entity the instance holds and monitors answers both.</summary>
    [Fact]
    public async Task AHeldAndMonitoredEntityIsPresentAndMonitored()
    {
        var reading = new RecordingEntityReading(status: 200, body: Monitored);

        var rows = await ReadAsync(reading, Resolving, [1]);

        Assert.Equal(new LibraryCardReading(false, true, true), rows[0].Reading);
    }

    /// <summary>An entity the instance holds and does not monitor answers presence alone.</summary>
    [Fact]
    public async Task AHeldAndUnmonitoredEntityIsPresentAndNotMonitored()
    {
        var reading = new RecordingEntityReading(status: 200, body: """{"monitored":false}""");

        var rows = await ReadAsync(reading, Resolving, [1]);

        Assert.Equal(new LibraryCardReading(false, true, false), rows[0].Reading);
    }

    /// <summary>
    /// An answer nothing could be established from carries both as unestablished, so the badge draws
    /// the unknown state rather than one of the four a reader would act on.
    /// </summary>
    [Fact]
    public async Task AnUnreadableAnswerEstablishesNeither()
    {
        var reading = new RecordingEntityReading(status: 500, body: "");

        var rows = await ReadAsync(reading, Resolving, [1]);

        Assert.Equal(new LibraryCardReading(false, null, null), rows[0].Reading);
    }

    /// <summary>A dropped connection is contained per card and leaves the rest of the page alone.</summary>
    [Fact]
    public async Task AConnectionThatDroppedIsOneUnestablishedCardAndNoMore()
    {
        var reading = new RecordingEntityReading(status: 200, body: Monitored, throwOnCall: 1);

        var rows = await ReadAsync(reading, Resolving, [1, 2]);

        Assert.Equal(new LibraryCardReading(false, null, null), rows[0].Reading);
        Assert.Equal(new LibraryCardReading(false, true, true), rows[1].Reading);
    }

    /// <summary>
    /// Nothing grows with the library: a page costs at most one request per card it was given, in the
    /// order it was given them.
    /// </summary>
    [Fact]
    public async Task APageCostsAtMostOneRequestPerRequestedCard()
    {
        var reading = new RecordingEntityReading(status: 200, body: Monitored);
        var requested = Enumerable.Range(1, 40).ToArray();

        var rows = await ReadAsync(reading, Resolving, requested);

        Assert.Equal(requested.Length, reading.Calls);
        Assert.Equal(requested, rows.Select(row => row.CoveId));
    }

    /// <summary>
    /// A page of scenes costs one exclusion read for the whole set and one status read per
    /// identifier.
    /// </summary>
    /// <remarks>
    /// The exclusion read comes first, because exclusion is tested before a state is derived. A read
    /// per card would put a second request against a third party on every card of the page.
    /// </remarks>
    [Fact]
    public async Task APageOfScenesCostsOneExclusionReadAndOneStatusReadPerIdentifier()
    {
        var reading = new RecordingSceneReading(status: 200, body: HeldAndMonitored);

        var readings = await ReadScenesAsync(reading, Excluding(reading), SceneIdentities(1, 2, 3));

        Assert.Equal(1, reading.ExclusionReads);
        Assert.Equal(3, reading.SceneReads);
        Assert.Equal([1, 2, 3], readings.Keys.Order());
    }

    /// <summary>A scene the instance holds and monitors reads as both.</summary>
    [Fact]
    public async Task AHeldAndMonitoredSceneIsPresentAndMonitored()
    {
        var reading = new RecordingSceneReading(status: 200, body: HeldAndMonitored);

        var readings = await ReadScenesAsync(reading, Excluding(reading), SceneIdentities(1));

        Assert.Equal(new LibraryCardReading(false, true, true), readings[1]);
    }

    /// <summary>
    /// An instance answering an empty list holds no entry, which is a different fact from an entry it
    /// holds and does not monitor.
    /// </summary>
    [Fact]
    public async Task AnEmptyListIsAnAbsenceAndEstablishesNoFlag()
    {
        var reading = new RecordingSceneReading(status: 200, body: "[]");

        var readings = await ReadScenesAsync(reading, Excluding(reading), SceneIdentities(1));

        Assert.Equal(new LibraryCardReading(false, false, null), readings[1]);
    }

    /// <summary>An answer nothing could be read from establishes neither member.</summary>
    [Fact]
    public async Task AnUnreadableSceneAnswerEstablishesNeither()
    {
        var reading = new RecordingSceneReading(status: 500, body: "");

        var readings = await ReadScenesAsync(reading, Excluding(reading), SceneIdentities(1));

        Assert.Equal(new LibraryCardReading(false, null, null), readings[1]);
    }

    /// <summary>
    /// An excluded scene reads as excluded even where the instance holds no entry for it.
    /// </summary>
    /// <remarks>
    /// The two facts arrive from different reads, and the badge tests exclusion first. Folding them
    /// would report the scene as one the instance was never offered.
    /// </remarks>
    [Fact]
    public async Task AnExcludedSceneCarriesTheExclusionBesideAnAbsence()
    {
        var reading = new RecordingSceneReading(status: 404, body: "");

        var readings = await ReadScenesAsync(
            reading, Excluding(reading, SceneIdentifier(1)), SceneIdentities(1));

        Assert.Equal(new LibraryCardReading(true, false, null), readings[1]);
    }

    /// <summary>
    /// A generation registering no exclusion role sends no exclusion read and excludes nothing.
    /// </summary>
    /// <remarks>
    /// Obtained by absence rather than by asking which generation is connected. A member answering an
    /// empty set would report every scene as one the instance's user has not excluded, which is a
    /// fact no instance answered.
    /// </remarks>
    [Fact]
    public async Task AGenerationHoldingNoExclusionRoleAsksNothingAndExcludesNothing()
    {
        var reading = new RecordingSceneReading(status: 200, body: HeldAndMonitored);

        var readings = await ReadScenesAsync(reading, NoExclusionRole, SceneIdentities(1));

        Assert.Equal(0, reading.ExclusionReads);
        Assert.Equal(new LibraryCardReading(false, true, true), readings[1]);
    }

    /// <summary>A dropped connection is contained per scene and leaves the rest of the page alone.</summary>
    [Fact]
    public async Task AConnectionThatDroppedIsOneUnestablishedSceneAndNoMore()
    {
        var reading = new RecordingSceneReading(
            status: 200, body: HeldAndMonitored, throwOnCall: 1);

        var readings = await ReadScenesAsync(reading, Excluding(reading), SceneIdentities(1, 2));

        Assert.Equal(new LibraryCardReading(false, null, null), readings[1]);
        Assert.Equal(new LibraryCardReading(false, true, true), readings[2]);
    }

    /// <summary>
    /// A read that outlived the client's own timeout is contained per card, the way a dropped
    /// connection is.
    /// </summary>
    /// <remarks>
    /// This is the common failure for a page of sequential reads: an instance that accepts the
    /// connection and then hangs. It reaches the port as a cancellation nobody asked for, and
    /// escaping it would answer the whole page with a failure the route declares no result for.
    /// </remarks>
    [Fact]
    public async Task AReadThatOutlivedTheClientsTimeoutIsOneUnestablishedCardAndNoMore()
    {
        var reading = new RecordingEntityReading(
            status: 200, body: Monitored, throwOnCall: 1, failure: () => new TaskCanceledException());

        var rows = await ReadAsync(reading, Resolving, [1, 2]);

        Assert.Equal(new LibraryCardReading(false, null, null), rows[0].Reading);
        Assert.Equal(new LibraryCardReading(false, true, true), rows[1].Reading);
    }

    /// <summary>The same for a scene, which is the path a page of forty reads takes.</summary>
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

    /// <summary>
    /// A shutdown is not a verdict about the instance, so it leaves the port rather than being
    /// recorded as a card nothing could be established about.
    /// </summary>
    [Fact]
    public async Task AShutdownLeavesThePortRatherThanReadingAsAnUnestablishedCard()
    {
        using var stopping = new CancellationTokenSource();
        await stopping.CancelAsync();
        var reading = new RecordingEntityReading(status: 200, body: Monitored);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await new LibraryStatusPort(Resolving).ReadEntityCardsAsync(
                reading.AnswerAsync,
                WhisparrEntityKind.Studio,
                WhisparrGeneration.V3,
                [1],
                stopping.Token));
    }

    /// <summary>The same for a scene.</summary>
    [Fact]
    public async Task AShutdownLeavesTheScenePathRatherThanReadingAsAnUnestablishedScene()
    {
        using var stopping = new CancellationTokenSource();
        await stopping.CancelAsync();
        var reading = new RecordingSceneReading(status: 200, body: HeldAndMonitored);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await new LibraryStatusPort(Nothing).ReadSceneCardsAsync(
                reading,
                new Capability<IWhisparrSceneExclusionReading>(reading, null),
                new Uri("http://whisparr.invalid"),
                "0e2e0e2e0e2e0e2e0e2e0e2e0e2e0e2e",
                SceneIdentities(1),
                stopping.Token));
    }

    private static string Monitored => """{"id":7,"monitored":true}""";

    /// <summary>The per-scene route answers a list, of one row where the instance holds the scene.</summary>
    private static string HeldAndMonitored => """[{"id":9,"monitored":true}]""";

    /// <summary>The identifier the scene named by <paramref name="coveId"/> is known by.</summary>
    private static string SceneIdentifier(int coveId) => $"scene-{coveId}";

    private static IReadOnlyList<LibraryCardIdentity> SceneIdentities(params int[] coveIds)
        => [.. coveIds.Select(coveId => new LibraryCardIdentity(coveId, SceneIdentifier(coveId)))];

    /// <summary>The generation registers no exclusion role at all.</summary>
    private static Capability<IWhisparrSceneExclusionReading> NoExclusionRole
        => GenerationCapabilities.For(WhisparrGeneration.V2)
            .Obtain<IWhisparrSceneExclusionReading>();

    /// <summary>Every entity is named by one identifier.</summary>
    private static IEntityIdentityPort Resolving => new FakeIdentities(IdentityResolution.At(ForeignId));

    /// <summary>No entity is named at all, which is a library holding no usable link.</summary>
    private static IEntityIdentityPort Nothing => new FakeIdentities(IdentityResolution.Unmatched);

    private static async Task<IReadOnlyList<LibraryStatusRow>> ReadAsync(
        RecordingEntityReading reading, IEntityIdentityPort identities, IReadOnlyList<int> coveIds)
        => await new LibraryStatusPort(identities).ReadEntityCardsAsync(
            reading.AnswerAsync,
            WhisparrEntityKind.Studio,
            WhisparrGeneration.V3,
            coveIds,
            TestCt);

    /// <summary>The exclusion role, answering <paramref name="excluded"/> and counting its reads.</summary>
    private static Capability<IWhisparrSceneExclusionReading> Excluding(
        RecordingSceneReading reading, params string[] excluded)
    {
        reading.Excludes(excluded);
        return new Capability<IWhisparrSceneExclusionReading>(reading, null);
    }

    private static async Task<IReadOnlyDictionary<int, LibraryCardReading>> ReadScenesAsync(
        RecordingSceneReading reading,
        Capability<IWhisparrSceneExclusionReading> exclusions,
        IReadOnlyList<LibraryCardIdentity> identities)
        => await new LibraryStatusPort(Nothing).ReadSceneCardsAsync(
            reading,
            exclusions,
            new Uri("http://whisparr.invalid"),
            "0e2e0e2e0e2e0e2e0e2e0e2e0e2e0e2e",
            identities,
            TestCt);

    private sealed class FakeIdentities(IdentityResolution answer) : IEntityIdentityPort
    {
        public Task<IdentityResolution> ResolveAsync(
            WhisparrEntityKind kind, int coveId, WhisparrGeneration generation, CancellationToken ct)
            => Task.FromResult(answer);
    }

    /// <summary>
    /// One answer for every scene, with the two reads the scene path issues counted apart.
    /// </summary>
    /// <remarks>
    /// Both roles on one recorder, so the counts a case asserts come from the same object and cannot
    /// describe two seams that were never used together.
    /// </remarks>
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
    }

    /// <summary>One answer for every card, and a count of how many were asked for.</summary>
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
