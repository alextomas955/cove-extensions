using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using WhisparrSync.Connection;
using WhisparrSync.Contracts;
using WhisparrSync.Options;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Connection;

public sealed class StoredConnectionTestTests
{
    private const string StoredAddress = "http://whisparr-v3:6969";
    private const string HeldAddress = "http://whisparr-moved:6969";
    private const string StoredKey = "3f3f3f3f3f3f3f3f3f3f3f3f3f3f3f3f";
    private static readonly DateTimeOffset Verified = new(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ASuccessfulStoredTestRecordsTheVersionAndWhenItWasVerified()
    {
        var options = await SeededAsync(recordedVersion: null, verifiedAt: null, lastReachableAt: null);
        var tester = RecordingConnectionTester.Connected("3.3.8.1097");
        var runner = NewRunner(tester, options, KeyPort());

        var view = await runner.TestStoredAsync(TestCt);

        Assert.Equal(ConnectionFailureKind.Connected, view.Kind);
        var call = Assert.Single(tester.Calls);
        // The rebuilt form, which is what drops any credentials embedded in the stored address.
        Assert.Equal(StoredAddress + "/", call.Address);
        Assert.Equal(StoredKey, call.ApiKey);

        var stored = await ConnectionAsync(options);
        Assert.Equal("3.3.8.1097", stored.RecordedVersion);
        Assert.Equal(Now, stored.VersionVerifiedAtUtc);
        Assert.Equal(Now, stored.LastReachableAtUtc);
    }

    // Every outbound request resolves its address from the row that holds the key, so the stored
    // test has to probe that address as well. A blob address left behind by a save that moved the
    // instance names a connection nothing sends to, and a success against it would be read as one.
    [Fact]
    public async Task TheStoredTestProbesTheAddressHeldWithTheKey()
    {
        var options = await SeededAsync(recordedVersion: null, verifiedAt: null, lastReachableAt: null);
        var tester = RecordingConnectionTester.Connected("3.3.8.1097");
        var held = new RecordingCredentialPort().Holding(WhisparrGeneration.V3, HeldAddress, StoredKey);

        await NewRunner(tester, options, held).TestStoredAsync(TestCt);

        var call = Assert.Single(tester.Calls);
        Assert.Equal(HeldAddress + "/", call.Address);
        Assert.Equal(StoredKey, call.ApiKey);
    }

    // A transient test describes an instance the user may only be considering, so a success there
    // is not a reading of the stored one.
    [Fact]
    public async Task ASuccessfulTransientTestLeavesTheRecordedVersionAlone()
    {
        var options = await SeededAsync("3.3.8.1097", Verified, Verified);
        var runner = NewRunner(RecordingConnectionTester.Connected("9.9.9.9999"), options, KeyPort());

        await runner.TestTransientAsync("http://whisparr-somewhere-else:6969", "another-key", TestCt);

        var stored = await ConnectionAsync(options);
        Assert.Equal("3.3.8.1097", stored.RecordedVersion);
        Assert.Equal(Verified, stored.VersionVerifiedAtUtc);
        Assert.Equal(Verified, stored.LastReachableAtUtc);
    }

    // A transient test aimed at the stored address did reach the stored instance, so reachability
    // is true of it. It still read no version of the stored connection.
    [Fact]
    public async Task ATransientTestOfTheStoredAddressRecordsReachabilityOnly()
    {
        var options = await SeededAsync("3.3.8.1097", Verified, Verified);
        var runner = NewRunner(RecordingConnectionTester.Connected("9.9.9.9999"), options, KeyPort());

        await runner.TestTransientAsync(StoredAddress + "/", "a-key-typed-in-the-field", TestCt);

        var stored = await ConnectionAsync(options);
        Assert.Equal(Now, stored.LastReachableAtUtc);
        Assert.Equal("3.3.8.1097", stored.RecordedVersion);
        Assert.Equal(Verified, stored.VersionVerifiedAtUtc);
    }

    // An instance that turned the key down was still reached, so the two recorded lines differ.
    [Fact]
    public async Task ARejectedKeyRecordsReachabilityAndLeavesTheVersionReading()
    {
        var options = await SeededAsync("3.3.8.1097", Verified, Verified);
        var runner = NewRunner(RecordingConnectionTester.KeyRejected(), options, KeyPort());

        var view = await runner.TestStoredAsync(TestCt);

        Assert.Equal(ConnectionFailureKind.KeyRejected, view.Kind);
        var stored = await ConnectionAsync(options);
        Assert.Equal(Now, stored.LastReachableAtUtc);
        Assert.Equal("3.3.8.1097", stored.RecordedVersion);
        Assert.Equal(Verified, stored.VersionVerifiedAtUtc);
    }

    [Fact]
    public async Task AnInstanceThatAnsweredNothingRecordsNothing()
    {
        var options = await SeededAsync("3.3.8.1097", Verified, Verified);
        var runner = NewRunner(RecordingConnectionTester.Unreachable(), options, KeyPort());

        await runner.TestStoredAsync(TestCt);

        var stored = await ConnectionAsync(options);
        Assert.Equal(Verified, stored.LastReachableAtUtc);
        Assert.Equal(Verified, stored.VersionVerifiedAtUtc);
    }

    [Fact]
    public async Task AnUnsetAddressRefusesByNamingTheAddressAndMakesNoRequest()
    {
        var options = await SeededAsync(null, null, null, address: "");
        var tester = RecordingConnectionTester.Connected("3.3.8.1097");

        var view = await NewRunner(tester, options, KeyPort()).TestStoredAsync(TestCt);

        Assert.Equal(ConnectionFailureKind.NotConfigured, view.Kind);
        Assert.Equal(ConnectionSetting.Address, view.MissingSetting);
        Assert.Empty(tester.Calls);
    }

    [Fact]
    public async Task AGenerationNothingConfiguredRefusesByNamingTheAddress()
    {
        var options = new OptionsStore(new FakeStore());
        var tester = RecordingConnectionTester.Connected("3.3.8.1097");

        var view = await NewRunner(tester, options, KeyPort()).TestStoredAsync(TestCt);

        Assert.Equal(ConnectionSetting.Address, view.MissingSetting);
        Assert.Empty(tester.Calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AnUnsetKeyRefusesByNamingTheKeyAndMakesNoRequest(string? key)
    {
        var options = await SeededAsync(null, null, null);
        var tester = RecordingConnectionTester.Connected("3.3.8.1097");

        var view = await NewRunner(tester, options, WithRawKey(new RecordingCredentialPort(), key))
            .TestStoredAsync(TestCt);

        Assert.Equal(ConnectionFailureKind.NotConfigured, view.Kind);
        Assert.Equal(ConnectionSetting.ApiKey, view.MissingSetting);
        // The echoed address is the rebuilt form, which is what drops any credentials embedded in it.
        Assert.Equal(StoredAddress + "/", view.Address);
        Assert.Empty(tester.Calls);
    }

    // With both settings empty the refusal names the address, the same one on every run.
    [Fact]
    public async Task WithNeitherSettingSetTheRefusalNamesTheAddress()
    {
        var options = await SeededAsync(null, null, null, address: "");
        var tester = RecordingConnectionTester.Connected("3.3.8.1097");

        var view = await NewRunner(tester, options, WithRawKey(new RecordingCredentialPort(), null))
            .TestStoredAsync(TestCt);

        Assert.Equal(ConnectionSetting.Address, view.MissingSetting);
        Assert.Empty(tester.Calls);
    }

    [Theory]
    [InlineData(StoredAddress, StoredAddress + "/", true)]
    [InlineData(StoredAddress, "HTTP://WHISPARR-V3:6969", true)]
    [InlineData(StoredAddress, "  " + StoredAddress + "  ", true)]
    [InlineData(StoredAddress, StoredAddress + ":6970", false)]
    [InlineData(StoredAddress, "https://whisparr-v3:6969", false)]
    public void ATrailingSeparatorAndLetterCaseAreNotAChangeOfAddress(
        string stored, string typed, bool same)
        => Assert.Equal(same, ConnectionTester.IsSameAddress(stored, typed));

    // The competing writer is the production secret-position write. It lands on the connection
    // record this path read before it asked the instance anything.
    [Fact]
    public async Task AStoredTestKeepsAWriteCommittedWhileTheProbeWasInFlight()
    {
        var options = await SeededAsync(recordedVersion: null, verifiedAt: null, lastReachableAt: null);
        using var gate = new OptionsWriteGate();
        var tester = new DeliveringConnectionTester(
            RecordingConnectionTester.Connected("3.3.8.1097"),
            options,
            gate,
            CallbackSecretPosition.OutOfBand);

        var view = await new ConnectionTestRunner(tester, options, gate, KeyPort(), new FixedClock(Now))
            .TestStoredAsync(TestCt);

        Assert.Equal(ConnectionFailureKind.Connected, view.Kind);
        var stored = await ConnectionAsync(options);
        Assert.Equal(CallbackSecretPosition.OutOfBand, stored.LastCallbackSecretPosition);
        Assert.Equal("3.3.8.1097", stored.RecordedVersion);
        Assert.Equal(Now, stored.VersionVerifiedAtUtc);
        Assert.Equal(Now, stored.LastReachableAtUtc);
    }

    // Shortening here keeps the one write that records a reading from putting an oversized value
    // in the settings blob.
    [Fact]
    public async Task AnOverLongReportedVersionIsStoredShortened()
    {
        var options = await SeededAsync(recordedVersion: null, verifiedAt: null, lastReachableAt: null);
        var reported = new string('x', WhisparrSyncGenerationConnection.RecordedVersionMaxLength * 4);
        var runner = NewRunner(RecordingConnectionTester.Connected(reported), options, KeyPort());

        await runner.TestStoredAsync(TestCt);

        var stored = await ConnectionAsync(options);
        Assert.Equal(
            WhisparrSyncGenerationConnection.RecordedVersionMaxLength,
            stored.RecordedVersion?.Length);
        Assert.Equal(Now, stored.VersionVerifiedAtUtc);
    }

    // The instance chooses these three readings, so the projection shortens them. The ordinary
    // document is the control: a projection that dropped all three would satisfy the length
    // assertions on their own.
    [Fact]
    public async Task TheEchoedReadingsAreShortenedWhereTheViewIsProjected()
    {
        var overLong = new string('x', ConnectionTester.ReportedNameMaxLength * 4);

        var shortened = await ViewOfAsync("3." + overLong, overLong, overLong);

        Assert.Equal(
            WhisparrSyncGenerationConnection.RecordedVersionMaxLength, shortened.Version?.Length);
        Assert.Equal(ConnectionTester.ReportedNameMaxLength, shortened.Branch?.Length);
        Assert.Equal(ConnectionTester.ReportedNameMaxLength, shortened.OtherApplication?.Length);

        var ordinary = await ViewOfAsync("3.3.8.1097", "eros", "Radarr");

        Assert.Equal("3.3.8.1097", ordinary.Version);
        Assert.Equal("eros", ordinary.Branch);
        Assert.Equal("Radarr", ordinary.OtherApplication);
    }

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    // Driven through the real tester over the recording client seam, so the projection under test
    // is the one the route reaches rather than a view a double assembled.
    private static async Task<ConnectionTestView> ViewOfAsync(
        string version, string branch, string appName)
    {
        var handler = BodyRecordingHandler.Answering(
            HttpStatusCode.OK,
            $$"""
            { "appName": "{{appName}}", "version": "{{version}}", "branch": "{{branch}}" }
            """);
        using var http = new HttpClient(handler);
        WhisparrTransport.Configure(http);

        return await new ConnectionTester(
                TestWhisparrClient.TransportOver(http, handler),
                NullLogger<ConnectionTester>.Instance)
            .TestAsync(StoredAddress, StoredKey, TestCt);
    }

    private static ConnectionTestRunner NewRunner(
        IWhisparrConnectionTester tester, OptionsStore options, ICredentialPort credentials)
        => new(tester, options, new OptionsWriteGate(), credentials, new FixedClock(Now));

    private static RecordingCredentialPort KeyPort()
        => new RecordingCredentialPort().Holding(WhisparrGeneration.V3, StoredKey);

    // A key the port answers with verbatim, including the blank spellings a stored row cannot hold but
    // a hand-edited one could.
    private static ICredentialPort WithRawKey(RecordingCredentialPort port, string? key)
        => key is null ? port : new RawKeyPort(port, key);

    private static async Task<WhisparrSyncGenerationConnection> ConnectionAsync(OptionsStore options)
    {
        var connection = (await options.LoadAsync(TestCt)).ConnectionFor(WhisparrGeneration.V3);
        Assert.NotNull(connection);
        return connection;
    }

    private static async Task<OptionsStore> SeededAsync(
        string? recordedVersion,
        DateTimeOffset? verifiedAt,
        DateTimeOffset? lastReachableAt,
        string address = StoredAddress)
    {
        var options = new OptionsStore(new FakeStore());
        await options.SaveAsync(
            new WhisparrSyncOptions
            {
                SelectedGeneration = WhisparrGeneration.V3,
                V3 = new WhisparrSyncGenerationConnection
                {
                    Address = address,
                    RecordedVersion = recordedVersion,
                    VersionVerifiedAtUtc = verifiedAt,
                    LastReachableAtUtc = lastReachableAt,
                },
            },
            TestCt);

        return options;
    }

    // Commits the production secret-position write before answering, as a delivery arriving during
    // a probe does. The write is attributed to the generation this fixture stores and selects.
    private sealed class DeliveringConnectionTester(
        IWhisparrConnectionTester inner,
        OptionsStore options,
        OptionsWriteGate gate,
        CallbackSecretPosition position) : IWhisparrConnectionTester
    {
        public async Task<ConnectionTestView> TestAsync(
            string? address, string? apiKey, CancellationToken ct)
        {
            await global::WhisparrSync.WhisparrSync.RecordSecretPositionAsync(
                options, gate, WhisparrGeneration.V3, position, ct);
            return await inner.TestAsync(address, apiKey, ct);
        }
    }

    private sealed class RawKeyPort(ICredentialPort inner, string key) : ICredentialPort
    {
        public Task<string?> ReadAsync(WhisparrGeneration generation, CancellationToken ct)
            => Task.FromResult<string?>(key);

        public Task<bool> HasKeyAsync(WhisparrGeneration generation, CancellationToken ct)
            => inner.HasKeyAsync(generation, ct);

        public async Task<WhisparrStoredConnection?> ReadConnectionAsync(
            WhisparrGeneration generation, CancellationToken ct)
        {
            var held = await inner.ReadConnectionAsync(generation, ct);
            return new WhisparrStoredConnection(held?.Address ?? "", key);
        }

        public Task ApplyAsync(
            WhisparrGeneration generation,
            CredentialWrite write,
            string address,
            DateTimeOffset nowUtc,
            CancellationToken ct)
            => inner.ApplyAsync(generation, write, address, nowUtc, ct);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
