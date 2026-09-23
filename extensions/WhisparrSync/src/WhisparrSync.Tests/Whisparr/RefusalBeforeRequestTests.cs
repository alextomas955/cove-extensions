using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using WhisparrSync.Connection;
using WhisparrSync.Contracts;
using WhisparrSync.Import;
using WhisparrSync.Options;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Whisparr;

// Each emptiness assertion is paired with a send taken through the same double, so an empty log
// is evidence rather than the only thing the case could report.
public sealed class RefusalBeforeRequestTests
{
    private const string V3StatusFixture = "whisparr-v3-3.3.8.1097-system-status.json";
    private const string V2StatusFixture = "whisparr-v2-2.2.0.231-system-status.json";
    private const string StoredAddress = "http://whisparr-v3:6969";
    private const string V2Address = "http://whisparr-v2:6969";
    private const string StoredKey = "7c7c7c7c7c7c7c7c7c7c7c7c7c7c7c7c";

    private const int Burst = 10;

    private static readonly DateTimeOffset Midnight = new(2026, 8, 31, 0, 0, 0, TimeSpan.Zero);

    // The control the other cases rest on: the recorder reports the address and key it sent, not
    // merely that something was sent.
    [Fact]
    public async Task APathThatDoesSendRecordsTheAddressAndKeyItSent()
    {
        var sent = new StatusRecordingHandler(V3StatusFixture);
        var runner = await RunnerOverAsync(sent, StoredAddress, StoredKey);

        var view = await runner.TestStoredAsync(TestCt);

        Assert.Equal(ConnectionFailureKind.Connected, view.Kind);
        var call = Assert.Single(sent.Calls);
        Assert.Equal(new Uri(StoredAddress + "/api/v3/system/status"), call.Target);
        Assert.Equal(StoredKey, call.ApiKey);
    }

    // Taken against a real generation gap: no route on v2 adds a catalogue item, so the v2 instance
    // declares no missing-scene role and the v2 recorder declares none either.
    [Fact]
    public async Task ACapabilityTheGenerationDoesNotHoldIsRefusedWithNothingSent()
    {
        var client = RecordingWhisparrCore.ReportingV2(V2StatusFixture);
        var sent = new StatusRecordingHandler(V2StatusFixture);
        var runner = await RunnerOverAsync(sent, V2Address, StoredKey, WhisparrGeneration.V2);

        Assert.IsNotAssignableFrom<IWhisparrMissingSceneActing>(client);
        Assert.DoesNotContain(
            typeof(IWhisparrMissingSceneActing), typeof(WhisparrV2Instance).GetInterfaces());
        Assert.DoesNotContain(
            WhisparrCapability.RegisterMissingScenes,
            GenerationCapabilities.CapabilitiesOf(WhisparrGeneration.V2));
        Assert.Empty(client.Verbs);
        Assert.Empty(sent.Calls);

        // The same runtime, driven down a path that does send.
        Assert.Equal(ConnectionFailureKind.Connected, (await runner.TestStoredAsync(TestCt)).Kind);
        Assert.Single(sent.Calls);
    }

    // All three sets are written out, so a capability added later fails here rather than passing
    // over a case nothing drives. A capability held by neither generation is not a gap: it is
    // declared as soon as a role expresses it and held once a generation can register one.
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
                WhisparrCapability.ReadSceneStatus,
                WhisparrCapability.ReadSceneExclusions,
                WhisparrCapability.SearchScene,
                WhisparrCapability.MonitorScene,
                WhisparrCapability.ExcludeScene,
                WhisparrCapability.RegisterOwnedSites,
                WhisparrCapability.ReadSiteSceneRows,
                WhisparrCapability.ReadHeldSites,
                WhisparrCapability.ReadEntityCardsInBatch,
                WhisparrCapability.ReadSceneCardsInBatch,
                WhisparrCapability.TrackEntityCatalogue,
                WhisparrCapability.ReadEntityCatalogue,
                WhisparrCapability.ReadInstanceFilesystem,
            ],
            Enum.GetValues<WhisparrCapability>());
        Assert.Equal(
            [
                WhisparrCapability.OutOfBandCallbackSecret,
                WhisparrCapability.MonitorStudio,
                WhisparrCapability.MonitorPerformer,
                WhisparrCapability.RegisterMissingScenes,
                WhisparrCapability.ReflectOwnedFiles,
                WhisparrCapability.SearchMonitored,
                WhisparrCapability.ReadSceneStatus,
                WhisparrCapability.ReadSceneExclusions,
                WhisparrCapability.SearchScene,
                WhisparrCapability.MonitorScene,
                WhisparrCapability.ExcludeScene,
                WhisparrCapability.ReadEntityCardsInBatch,
                WhisparrCapability.ReadSceneCardsInBatch,
                WhisparrCapability.TrackEntityCatalogue,
                WhisparrCapability.ReadEntityCatalogue,
                WhisparrCapability.ReadInstanceFilesystem,
            ],
            GenerationCapabilities.CapabilitiesOf(WhisparrGeneration.V3));
        Assert.Equal(
            [
                WhisparrCapability.OutOfBandCallbackSecret,
                WhisparrCapability.MonitorStudio,
                WhisparrCapability.ReflectOwnedFiles,
                WhisparrCapability.SearchMonitored,
                WhisparrCapability.MonitorScene,
                WhisparrCapability.RegisterOwnedSites,
                WhisparrCapability.ReadSiteSceneRows,
                WhisparrCapability.ReadHeldSites,
                WhisparrCapability.ReadEntityCardsInBatch,
                WhisparrCapability.TrackEntityCatalogue,
                WhisparrCapability.ReadEntityCatalogue,
                WhisparrCapability.ReadInstanceFilesystem,
            ],
            GenerationCapabilities.CapabilitiesOf(WhisparrGeneration.V2));
    }

    [Theory]
    [InlineData("", null, ConnectionSetting.Address)]
    [InlineData("", StoredKey, ConnectionSetting.Address)]
    [InlineData(StoredAddress, null, ConnectionSetting.ApiKey)]
    public async Task ARefusalTakenBeforeAnythingWasConfiguredSendsNothing(
        string address, string? apiKey, ConnectionSetting missing)
    {
        var sent = new StatusRecordingHandler(V3StatusFixture);
        var runner = await RunnerOverAsync(sent, address, apiKey);

        var view = await runner.TestStoredAsync(TestCt);

        Assert.Empty(sent.Calls);
        Assert.Equal(ConnectionFailureKind.NotConfigured, view.Kind);
        Assert.Equal(missing, view.MissingSetting);
    }

    [Fact]
    public async Task ATransientTestOfAnUnconfiguredPairSendsNothing()
    {
        var sent = new StatusRecordingHandler(V3StatusFixture);
        var runner = await RunnerOverAsync(sent, StoredAddress, StoredKey);

        var view = await runner.TestTransientAsync(" ", " ", TestCt);

        Assert.Empty(sent.Calls);
        Assert.Equal(ConnectionFailureKind.NotConfigured, view.Kind);

        Assert.Equal(
            ConnectionFailureKind.Connected,
            (await runner.TestTransientAsync(StoredAddress, StoredKey, TestCt)).Kind);
        Assert.Single(sent.Calls);
    }

    // A read that reaches the client re-pays the client's timeout and retry inside the inbound
    // request pipeline, so an uncached burst during an outage is a burst of stalls.
    [Fact]
    public async Task ABurstAgainstAnUnreachableInstanceProbesItOnce()
    {
        var unreachable = new UnreachableRootFolders(RecordingWhisparrCore.Reporting(V3StatusFixture));
        var roots = await RootPortOverAsync(unreachable, StoredAddress, StoredKey, new MovableClock(Midnight));

        for (var delivery = 0; delivery < Burst; delivery++)
        {
            Assert.Null(await roots.ReadAsync(WhisparrGeneration.V3, TestCt));
        }

        Assert.Equal(1, unreachable.Attempts);
    }

    [Fact]
    public async Task ABurstAgainstAnUnconfiguredConnectionSendsNothing()
    {
        var client = RecordingWhisparrCore.Reporting(V3StatusFixture);
        var unconfigured = await RootPortOverAsync(client, "", null, new MovableClock(Midnight));

        for (var delivery = 0; delivery < Burst; delivery++)
        {
            Assert.Null(await unconfigured.ReadAsync(WhisparrGeneration.V3, TestCt));
        }

        Assert.Empty(client.Notifications);

        var configured = await RootPortOverAsync(
            client, StoredAddress, StoredKey, new MovableClock(Midnight));
        await configured.ReadAsync(WhisparrGeneration.V3, TestCt);
        Assert.Single(client.Notifications);
    }

    // The control for the burst case above: without this, that assertion would pass against a
    // reading held for ever, which is a recovered instance nothing notices.
    [Fact]
    public async Task AnUnreachableInstanceIsAskedAgainOnceTheHeldReadingRunsOut()
    {
        var unreachable = new UnreachableRootFolders(RecordingWhisparrCore.Reporting(V3StatusFixture));
        var clock = new MovableClock(Midnight);
        var roots = await RootPortOverAsync(unreachable, StoredAddress, StoredKey, clock);

        await roots.ReadAsync(WhisparrGeneration.V3, TestCt);
        await roots.ReadAsync(WhisparrGeneration.V3, TestCt);
        Assert.Equal(1, unreachable.Attempts);

        clock.Advance(ReportedRootCache.NothingToReadLifetime);
        await roots.ReadAsync(WhisparrGeneration.V3, TestCt);

        Assert.Equal(2, unreachable.Attempts);
    }

    // The cross-root guard can be applied to an instance that declares none, and cannot be applied
    // to a list nobody read. Collapsing the two lets an outage read as a settled fact about the
    // instance, and an import made on it copies the bytes in full.
    [Fact]
    public async Task AnInstanceDeclaringNoRootIsHeldApartFromOneThatCouldNotBeRead()
    {
        var declaring = RecordingWhisparrCore.Reporting(V3StatusFixture);
        declaring.Answering(
            nameof(IWhisparrClient.ReadRootFoldersAsync), RecordingWhisparrCore.Json(200, "[]"));
        var roots = await RootPortOverAsync(
            declaring, StoredAddress, StoredKey, new MovableClock(Midnight));

        Assert.Empty((await roots.ReadAsync(WhisparrGeneration.V3, TestCt))!);
    }

    // The trade the two constants make: how long an outage keeps a recovered instance invisible
    // against how often a burst re-probes one that is still down.
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
        if (apiKey is null)
        {
            credentials.HoldingAddressOnly(WhisparrGeneration.V3, address);
        }
        else
        {
            credentials.Holding(WhisparrGeneration.V3, address, apiKey);
        }

        return new ReportedRootPort(
            new FixedInstanceFactory(client),
            credentials,
            new ReportedRootCache(clock),
            NullLogger.Instance);
    }

    private sealed class UnreachableRootFolders(RecordingWhisparrCore inner) : IWhisparrClient
    {
        public int Attempts { get; private set; }


        public Task<WhisparrResponse> ReadRootFoldersAsync(CancellationToken ct)
        {
            Attempts++;
            throw new HttpRequestException("the instance answered nothing");
        }

        public Task<WhisparrResponse> ReadNotificationSchemaAsync(CancellationToken ct)
            => inner.ReadNotificationSchemaAsync(ct);

        public Task<WhisparrResponse> ListNotificationsAsync(CancellationToken ct)
            => inner.ListNotificationsAsync(ct);

        public Task<WhisparrResponse> ReadQualityProfilesAsync(CancellationToken ct)
            => inner.ReadQualityProfilesAsync(ct);

        public Task<WhisparrResponse> ReadHistoryAsync(
            int page,
            int pageSize,
            CancellationToken ct)
            => inner.ReadHistoryAsync(page, pageSize, ct);

        public Task<WhisparrResponse> ReadCommandAsync(
            int commandId, CancellationToken ct)
            => inner.ReadCommandAsync(commandId, ct);

        public Task<WhisparrResponse> CreateNotificationAsync(
            JsonNode body, CancellationToken ct)
            => inner.CreateNotificationAsync(body, ct);

        public Task<WhisparrResponse> UpdateNotificationAsync(
            int id, JsonNode body, CancellationToken ct)
            => inner.UpdateNotificationAsync(id, body, ct);
    }

    private sealed class MovableClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    // The whole runtime the outbound path runs through, with the recording client at the seam every
    // request would leave by.
    // The status read the tester makes is composed by the generated client and sent through the
    // transport, so what a case reads back off it is the request that left rather than a call log.
    private sealed class StatusRecordingHandler(string statusFixture) : HttpMessageHandler
    {
        public List<(Uri? Target, string? ApiKey)> Calls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls.Add((
                request.RequestUri,
                request.Headers.TryGetValues(WhisparrTransport.ApiKeyHeader, out var keys)
                    ? keys.FirstOrDefault()
                    : null));

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    ProbeFixtures.Read(statusFixture), Encoding.UTF8, "application/json"),
            });
        }
    }

    private static async Task<ConnectionTestRunner> RunnerOverAsync(
        StatusRecordingHandler sent,
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
        if (apiKey is null)
        {
            credentials.HoldingAddressOnly(generation, address);
        }
        else
        {
            credentials.Holding(generation, address, apiKey);
        }

        var http = new HttpClient(sent);
        WhisparrTransport.Configure(http);

        return new ConnectionTestRunner(
            new ConnectionTester(
                TestWhisparrClient.TransportOver(http, sent),
                NullLogger<ConnectionTester>.Instance),
            options,
            new OptionsWriteGate(),
            credentials,
            TimeProvider.System);
    }
}
