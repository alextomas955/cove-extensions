using Microsoft.Extensions.Logging.Abstractions;
using WhisparrSync.Connection;
using WhisparrSync.Contracts;
using WhisparrSync.Options;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.Connection;

/// <summary>
/// Which of the two ways a connection test is asked for may record what it learned, and what an
/// unconfigured connection answers before anything leaves.
/// </summary>
/// <remarks>
/// The outbound seam is a double that records its arguments, so a case asserting that no request was
/// made is asserting on the absence of a call rather than on an inspection of the code.
/// </remarks>
public sealed class StoredConnectionTestTests
{
    private const string StoredAddress = "http://whisparr-v3:6969";
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
        Assert.Equal(StoredAddress, call.Address);
        Assert.Equal(StoredKey, call.ApiKey);

        var stored = await ConnectionAsync(options);
        Assert.Equal("3.3.8.1097", stored.RecordedVersion);
        Assert.Equal(Now, stored.VersionVerifiedAtUtc);
        Assert.Equal(Now, stored.LastReachableAtUtc);
    }

    /// <summary>
    /// A transient test describes an instance the user may only be considering, so a success there is
    /// not a reading of the stored one.
    /// </summary>
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

    /// <summary>
    /// A transient test aimed at the stored address did reach the stored instance, so what it says
    /// about reachability is true of it — but it still read no version of the stored connection.
    /// </summary>
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

    /// <summary>
    /// The two recorded lines measure different things: an instance that turned the key down was still
    /// reached.
    /// </summary>
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

    /// <summary>With both settings empty the refusal names one of them, the same one on every run.</summary>
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

    /// <summary>
    /// A writer that commits while the probe is in flight keeps its value, and the reading is still
    /// recorded.
    /// </summary>
    /// <remarks>
    /// The competing writer is the production secret-position write, which lands on the very
    /// connection record this path read before it asked the instance anything.
    /// </remarks>
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

    /// <summary>
    /// The version an instance reports is stored shortened however long the answer was, so the one
    /// write that records a reading cannot be how an oversized value reaches the blob.
    /// </summary>
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

    /// <summary>
    /// The three readings the answering instance chooses are shortened where the view is projected,
    /// so the body the settings page reads cannot be as large as the body that arrived.
    /// </summary>
    /// <remarks>
    /// The ordinary document is the control: a projection that dropped all three would satisfy the
    /// length assertions on their own. The echoed version is held to the ceiling the stored reading
    /// uses, because the stored reading is this value.
    /// </remarks>
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

    /// <summary>The view a status document naming those three readings is projected into.</summary>
    /// <remarks>
    /// Driven through the real tester over the recording client seam, so the projection under test is
    /// the one the route reaches rather than a view a double assembled.
    /// </remarks>
    private static async Task<ConnectionTestView> ViewOfAsync(
        string version, string branch, string appName)
    {
        var client = new RecordingWhisparrClient(RecordingWhisparrClient.Json(
            200,
            $$"""
            { "appName": "{{appName}}", "version": "{{version}}", "branch": "{{branch}}" }
            """));

        return await new ConnectionTester(client, NullLogger<ConnectionTester>.Instance)
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

    /// <summary>
    /// A tester that commits the production secret-position write before it answers, which is what a
    /// delivery arriving during a probe does.
    /// </summary>
    /// <remarks>
    /// The delivery is attributed to the generation this fixture stores and selects, which is the
    /// connection the probe under test read before it asked the instance anything.
    /// </remarks>
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

        public Task ApplyAsync(
            WhisparrGeneration generation, CredentialWrite write, DateTimeOffset nowUtc, CancellationToken ct)
            => inner.ApplyAsync(generation, write, nowUtc, ct);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
