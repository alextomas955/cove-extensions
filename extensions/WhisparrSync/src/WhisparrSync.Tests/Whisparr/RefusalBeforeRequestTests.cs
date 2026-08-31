using Microsoft.Extensions.Logging.Abstractions;
using WhisparrSync.Connection;
using WhisparrSync.Contracts;
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
    /// The refusal is taken against a set built holding nothing rather than against a generation,
    /// because no generation this product manages refuses the one capability it declares today. What
    /// is asserted is still the property CAP-2 asks for: obtaining a role that is absent produces a
    /// refusal and nothing leaves.
    /// </remarks>
    [Fact]
    public async Task ACapabilityTheSetDoesNotHoldIsRefusedWithNothingSent()
    {
        var client = RecordingWhisparrClient.Reporting(V2StatusFixture);
        var runner = await RunnerOverAsync(client, V2Address, StoredKey, WhisparrGeneration.V2);

        var refusal = new WhisparrCapabilitySet(WhisparrGeneration.V2, [])
            .Obtain<IOutOfBandSecretRegistration>()
            .Match<CapabilityRefusal?>(_ => null, refused => refused);

        Assert.NotNull(refusal);
        Assert.Equal(WhisparrGeneration.V2, refusal.Generation);
        Assert.Empty(client.Calls);

        // The same client, driven down a path that does send.
        Assert.Equal(ConnectionFailureKind.Connected, (await runner.TestStoredAsync(TestCt)).Kind);
        Assert.Single(client.Calls);
    }

    /// <summary>
    /// The capability vocabulary, and which generations hold each member.
    /// </summary>
    /// <remarks>
    /// Both generations hold the one member today, by carriers neither shares with the other. The
    /// vocabulary is written out so a capability added later fails here rather than passing over a
    /// case nothing drives.
    /// </remarks>
    [Fact]
    public void EveryCapabilityThisProductDeclaresIsHeldBySomeGeneration()
    {
        Assert.Equal([WhisparrCapability.OutOfBandCallbackSecret], Enum.GetValues<WhisparrCapability>());
        Assert.Equal(
            [WhisparrCapability.OutOfBandCallbackSecret],
            GenerationCapabilities.For(WhisparrGeneration.V3).Held);
        Assert.Equal(
            [WhisparrCapability.OutOfBandCallbackSecret],
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

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

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
            credentials,
            TimeProvider.System);
    }
}
