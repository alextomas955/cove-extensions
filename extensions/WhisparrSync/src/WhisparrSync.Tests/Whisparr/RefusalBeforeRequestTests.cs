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
        var client = RecordingWhisparrClient.Reporting(V3StatusFixture);
        var runner = await RunnerOverAsync(client, StoredAddress, StoredKey);

        var view = await runner.TestStoredAsync(TestCt);

        Assert.Equal(ConnectionFailureKind.Connected, view.Kind);
        var call = Assert.Single(client.Calls);
        Assert.Equal(new Uri(StoredAddress + "/"), call.BaseAddress);
        Assert.Equal(StoredKey, call.ApiKey);
    }

    // Taken against a real generation gap rather than a set built holding nothing: no route on v2
    // adds a catalogue item, so its set holds no missing-scene role.
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
                WhisparrCapability.ReadInstanceFilesystem,
            ],
            GenerationCapabilities.For(WhisparrGeneration.V3).Held);
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
                WhisparrCapability.ReadInstanceFilesystem,
            ],
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

    // A read that reaches the client re-pays the client's timeout and retry inside the inbound
    // request pipeline, so an uncached burst during an outage is a burst of stalls.
    [Fact]
    public async Task ABurstAgainstAnUnreachableInstanceProbesItOnce()
    {
        var unreachable = new UnreachableRootFolders(RecordingWhisparrClient.Reporting(V3StatusFixture));
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
        var client = RecordingWhisparrClient.Reporting(V3StatusFixture);
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

    // The cross-root guard can be applied to an instance that declares none, and cannot be applied
    // to a list nobody read. Collapsing the two lets an outage read as a settled fact about the
    // instance, and an import made on it copies the bytes in full.
    [Fact]
    public async Task AnInstanceDeclaringNoRootIsHeldApartFromOneThatCouldNotBeRead()
    {
        var declaring = RecordingWhisparrClient.Reporting(V3StatusFixture);
        declaring.Answering(
            nameof(IWhisparrClient.ReadRootFoldersAsync), RecordingWhisparrClient.Json(200, "[]"));
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
        if (apiKey is not null)
        {
            credentials.Holding(WhisparrGeneration.V3, apiKey);
        }

        return new ReportedRootPort(
            client, options, credentials, new ReportedRootCache(clock), NullLogger.Instance);
    }

    private sealed class UnreachableRootFolders(RecordingWhisparrClient inner) : IWhisparrClient
    {
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

        public Task<WhisparrResponse> ReadCommandAsync(
            Uri baseAddress, string apiKey, int commandId, CancellationToken ct)
            => inner.ReadCommandAsync(baseAddress, apiKey, commandId, ct);

        public Task<WhisparrResponse> CreateNotificationAsync(
            Uri baseAddress, string apiKey, JsonNode body, CancellationToken ct)
            => inner.CreateNotificationAsync(baseAddress, apiKey, body, ct);

        public Task<WhisparrResponse> UpdateNotificationAsync(
            Uri baseAddress, string apiKey, int id, JsonNode body, CancellationToken ct)
            => inner.UpdateNotificationAsync(baseAddress, apiKey, id, body, ct);
    }

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
