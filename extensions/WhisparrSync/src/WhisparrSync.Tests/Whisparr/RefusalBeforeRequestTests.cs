using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using WhisparrSync.Connection;
using WhisparrSync.Contracts;
using WhisparrSync.Import;
using WhisparrSync.Monitoring;
using WhisparrSync.Options;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Whisparr;

/// <summary>
/// What leaves for Whisparr when a request is refused, driven through the real client seam.
/// </summary>
/// <remarks>
/// Every case here runs against a double that records the arguments of every request, and each
/// emptiness assertion is paired with a send taken through the SAME double, so an empty log is
/// evidence rather than the only thing this test could ever report.
/// </remarks>
public sealed class RefusalBeforeRequestTests
{
    private const string V3StatusFixture = "whisparr-v3-3.3.8.1097-system-status.json";
    private const string V2StatusFixture = "whisparr-v2-2.2.0.231-system-status.json";
    private const string StoredAddress = "http://whisparr-v3:6969";
    private const string V2Address = "http://whisparr-v2:6969";
    private const string StoredKey = "7c7c7c7c7c7c7c7c7c7c7c7c7c7c7c7c";

    /// <summary>How many deliveries one burst stands for.</summary>
    private const int Burst = 10;

    private static readonly DateTimeOffset Midnight = new(2026, 8, 31, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// The control the emptiness assertions rest on: this recorder reports a send, with the address
    /// and key that were sent rather than the fact that something was.
    /// </summary>
    [Fact]
    public async Task APathThatDoesSendRecordsTheAddressAndKeyItSent()
    {
        var client = RecordingWhisparrClient.Reporting(V3StatusFixture);
        var runner = await RunnerOverAsync(client, StoredAddress, StoredKey);

        var view = await runner.TestStoredAsync(TestCt);

        Assert.Equal(ConnectionFailureKind.Connected, view.Kind);
        var call = Assert.Single(client.Calls);
        Assert.Equal(new Uri(StoredAddress + "/"), call.BaseAddress);
        Assert.Equal(StoredKey, call.ApiKey);
    }

    /// <summary>
    /// A capability the connected generation does not hold is refused with nothing sent, and the same
    /// client is then shown to record a request, so the empty log above is a fact about the refusal.
    /// </summary>
    /// <remarks>
    /// Taken against a real generation gap rather than against a set built holding nothing: no route
    /// on the older generation adds a catalogue item at all, so its set holds no missing-scene role and
    /// the refusal under test is the one a user actually reaches. What is asserted is the property
    /// CAP-2 asks for: obtaining a role that is absent produces a refusal and nothing leaves.
    /// </remarks>
    [Fact]
    public async Task ACapabilityTheSetDoesNotHoldIsRefusedWithNothingSent()
    {
        var client = RecordingWhisparrClient.Reporting(V2StatusFixture);
        var runner = await RunnerOverAsync(client, V2Address, StoredKey, WhisparrGeneration.V2);

        var refusal = GenerationCapabilities.For(WhisparrGeneration.V2, WhisparrRoleSet.From(client))
            .Obtain<IWhisparrMissingSceneActing>()
            .Match<CapabilityRefusal?>(_ => null, refused => refused);

        Assert.NotNull(refusal);
        Assert.Equal(WhisparrCapability.RegisterMissingScenes, refusal.Capability);
        Assert.Equal(WhisparrGeneration.V2, refusal.Generation);
        Assert.Empty(client.Calls);

        // The same client, driven down a path that does send.
        Assert.Equal(ConnectionFailureKind.Connected, (await runner.TestStoredAsync(TestCt)).Kind);
        Assert.Single(client.Calls);
    }

    /// <summary>
    /// The capability vocabulary, and what each generation holds of it.
    /// </summary>
    /// <remarks>
    /// All three sets are written out, so a capability added later fails here rather than passing over
    /// a case nothing drives, and a generation that begins holding one fails here rather than gaining
    /// it in silence.
    /// <para>
    /// A member held by neither generation is not a gap here. A capability names what a caller can be
    /// refused under, so it is declared as soon as a role expresses it and held only once some
    /// generation has an implementation to register; until then it reads as refused, which is what it
    /// is.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheCapabilityVocabularyAndWhatEachGenerationHoldsAreWrittenDown()
    {
        Assert.Equal(
            [
                WhisparrCapability.OutOfBandCallbackSecret,
                WhisparrCapability.MonitorStudio,
                WhisparrCapability.MonitorPerformer,
                WhisparrCapability.RegisterMissingScenes,
                WhisparrCapability.ReflectOwnedFiles,
                WhisparrCapability.SearchMonitored,
            ],
            Enum.GetValues<WhisparrCapability>());
        Assert.Equal(
            [
                WhisparrCapability.OutOfBandCallbackSecret,
                WhisparrCapability.MonitorStudio,
                WhisparrCapability.MonitorPerformer,
            ],
            GenerationCapabilities.For(WhisparrGeneration.V3).Held);
        Assert.Equal(
            [WhisparrCapability.OutOfBandCallbackSecret, WhisparrCapability.MonitorStudio],
            GenerationCapabilities.For(WhisparrGeneration.V2).Held);
    }

    [Theory]
    [InlineData("", null, ConnectionSetting.Address)]
    [InlineData("", StoredKey, ConnectionSetting.Address)]
    [InlineData(StoredAddress, null, ConnectionSetting.ApiKey)]
    public async Task ARefusalTakenBeforeAnythingWasConfiguredSendsNothing(
        string address, string? apiKey, ConnectionSetting missing)
    {
        var client = RecordingWhisparrClient.Reporting(V3StatusFixture);
        var runner = await RunnerOverAsync(client, address, apiKey);

        var view = await runner.TestStoredAsync(TestCt);

        Assert.Empty(client.Calls);
        Assert.Equal(ConnectionFailureKind.NotConfigured, view.Kind);
        Assert.Equal(missing, view.MissingSetting);
    }

    /// <summary>
    /// The same refusal on the transient path, whose address and key come from the request rather than
    /// from what is stored.
    /// </summary>
    [Fact]
    public async Task ATransientTestOfAnUnconfiguredPairSendsNothing()
    {
        var client = RecordingWhisparrClient.Reporting(V3StatusFixture);
        var runner = await RunnerOverAsync(client, StoredAddress, StoredKey);

        var view = await runner.TestTransientAsync(" ", " ", TestCt);

        Assert.Empty(client.Calls);
        Assert.Equal(ConnectionFailureKind.NotConfigured, view.Kind);

        Assert.Equal(
            ConnectionFailureKind.Connected,
            (await runner.TestTransientAsync(StoredAddress, StoredKey, TestCt)).Kind);
        Assert.Single(client.Calls);
    }

    /// <summary>
    /// A burst of deliveries arriving while the instance is unreachable costs one outbound probe.
    /// </summary>
    /// <remarks>
    /// A read that reaches the client re-pays the client's own timeout and retry, and it does so
    /// inside the inbound request pipeline. Uncached, a burst during an outage is therefore a burst of
    /// stalls rather than a burst of refusals.
    /// </remarks>
    [Fact]
    public async Task ABurstAgainstAnUnreachableInstanceProbesItOnce()
    {
        var unreachable = new UnreachableRootFolders(RecordingWhisparrClient.Reporting(V3StatusFixture));
        var roots = await RootPortOverAsync(unreachable, StoredAddress, StoredKey, new MovableClock(Midnight));

        for (var delivery = 0; delivery < Burst; delivery++)
        {
            Assert.Empty(await roots.ReadAsync(WhisparrGeneration.V3, TestCt));
        }

        Assert.Equal(1, unreachable.Attempts);
    }

    /// <summary>A burst against an unconfigured connection reaches no request at all.</summary>
    /// <remarks>
    /// Paired with a send taken through the SAME double, so the empty log is a fact about the refusal
    /// rather than the only thing this case could report.
    /// </remarks>
    [Fact]
    public async Task ABurstAgainstAnUnconfiguredConnectionSendsNothing()
    {
        var client = RecordingWhisparrClient.Reporting(V3StatusFixture);
        var unconfigured = await RootPortOverAsync(client, "", null, new MovableClock(Midnight));

        for (var delivery = 0; delivery < Burst; delivery++)
        {
            Assert.Empty(await unconfigured.ReadAsync(WhisparrGeneration.V3, TestCt));
        }

        Assert.Empty(client.Notifications);

        var configured = await RootPortOverAsync(
            client, StoredAddress, StoredKey, new MovableClock(Midnight));
        await configured.ReadAsync(WhisparrGeneration.V3, TestCt);
        Assert.Single(client.Notifications);
    }

    /// <summary>Once the held reading has run out, the instance is asked again.</summary>
    /// <remarks>
    /// The discriminating control for the burst above: without this, that assertion would equally
    /// pass against a reading held for ever, which is a recovered instance nothing ever notices.
    /// </remarks>
    [Fact]
    public async Task AnUnreachableInstanceIsAskedAgainOnceTheHeldReadingRunsOut()
    {
        var unreachable = new UnreachableRootFolders(RecordingWhisparrClient.Reporting(V3StatusFixture));
        var clock = new MovableClock(Midnight);
        var roots = await RootPortOverAsync(unreachable, StoredAddress, StoredKey, clock);

        await roots.ReadAsync(WhisparrGeneration.V3, TestCt);
        await roots.ReadAsync(WhisparrGeneration.V3, TestCt);
        Assert.Equal(1, unreachable.Attempts);

        clock.Advance(ReportedRootCache.NothingToReadLifetime);
        await roots.ReadAsync(WhisparrGeneration.V3, TestCt);

        Assert.Equal(2, unreachable.Attempts);
    }

    /// <summary>
    /// A reading the instance did not give is held for less time than one it did.
    /// </summary>
    /// <remarks>
    /// The trade the two constants make: how long an outage keeps a recovered instance invisible
    /// against how often a burst re-probes one that is still down.
    /// </remarks>
    [Fact]
    public void AReadingTheInstanceDidNotGiveIsHeldForLessTime()
        => Assert.True(ReportedRootCache.NothingToReadLifetime < ReportedRootCache.Lifetime);

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    // The whole runtime the reported-root read runs through, with the client at the seam every
    // request would leave by.
    private static async Task<IReportedRootPort> RootPortOverAsync(
        IWhisparrClient client, string address, string? apiKey, TimeProvider clock)
    {
        var options = new OptionsStore(new FakeStore());
        await options.SaveAsync(
            new WhisparrSyncOptions
            {
                SelectedGeneration = WhisparrGeneration.V3,
                V3 = new WhisparrSyncGenerationConnection { Address = address },
            },
            TestCt);

        var credentials = new RecordingCredentialPort();
        if (apiKey is not null)
        {
            credentials.Holding(WhisparrGeneration.V3, apiKey);
        }

        return new ReportedRootPort(
            client, options, credentials, new ReportedRootCache(clock), NullLogger.Instance);
    }

    /// <summary>A client whose root-folder read never arrives, counting what it was asked for.</summary>
    private sealed class UnreachableRootFolders(RecordingWhisparrClient inner) : IWhisparrClient
    {
        /// <summary>How many root-folder reads were attempted against the instance.</summary>
        public int Attempts { get; private set; }

        public Task<WhisparrResponse> ReadRootFoldersAsync(
            Uri baseAddress, string apiKey, CancellationToken ct)
        {
            Attempts++;
            throw new HttpRequestException("the instance answered nothing");
        }

        public Task<WhisparrResponse> ReadStatusAsync(Uri baseAddress, string apiKey, CancellationToken ct)
            => inner.ReadStatusAsync(baseAddress, apiKey, ct);

        public Task<WhisparrResponse> ReadNotificationSchemaAsync(
            Uri baseAddress, string apiKey, CancellationToken ct)
            => inner.ReadNotificationSchemaAsync(baseAddress, apiKey, ct);

        public Task<WhisparrResponse> ListNotificationsAsync(
            Uri baseAddress, string apiKey, CancellationToken ct)
            => inner.ListNotificationsAsync(baseAddress, apiKey, ct);

        public Task<WhisparrResponse> ReadQualityProfilesAsync(
            Uri baseAddress, string apiKey, CancellationToken ct)
            => inner.ReadQualityProfilesAsync(baseAddress, apiKey, ct);

        public Task<WhisparrResponse> ReadHistoryAsync(
            Uri baseAddress,
            string apiKey,
            WhisparrGeneration generation,
            int page,
            int pageSize,
            CancellationToken ct)
            => inner.ReadHistoryAsync(baseAddress, apiKey, generation, page, pageSize, ct);

        public Task<WhisparrResponse> CreateNotificationAsync(
            Uri baseAddress, string apiKey, JsonNode body, CancellationToken ct)
            => inner.CreateNotificationAsync(baseAddress, apiKey, body, ct);

        public Task<WhisparrResponse> UpdateNotificationAsync(
            Uri baseAddress, string apiKey, int id, JsonNode body, CancellationToken ct)
            => inner.UpdateNotificationAsync(baseAddress, apiKey, id, body, ct);
    }

    /// <summary>A clock the case moves by hand, so a lifetime is exercised without waiting one.</summary>
    private sealed class MovableClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    // The whole runtime the outbound path runs through, with the recording client at the seam every
    // request would leave by.
    private static async Task<ConnectionTestRunner> RunnerOverAsync(
        IWhisparrClient client,
        string address,
        string? apiKey,
        WhisparrGeneration generation = WhisparrGeneration.V3)
    {
        var connection = new WhisparrSyncGenerationConnection { Address = address };
        var options = new OptionsStore(new FakeStore());
        await options.SaveAsync(
            new WhisparrSyncOptions
            {
                SelectedGeneration = generation,
                V3 = generation == WhisparrGeneration.V3 ? connection : null,
                V2 = generation == WhisparrGeneration.V2 ? connection : null,
            },
            TestCt);

        var credentials = new RecordingCredentialPort();
        if (apiKey is not null)
        {
            credentials.Holding(generation, apiKey);
        }

        return new ConnectionTestRunner(
            new ConnectionTester(client, NullLogger<ConnectionTester>.Instance),
            options,
            new OptionsWriteGate(),
            credentials,
            TimeProvider.System);
    }
}
