using Cove.Core.Auth;
using Microsoft.AspNetCore.Http;
using WhisparrSync.Connection;
using WhisparrSync.Contracts;
using WhisparrSync.Options;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;
using static Cove.Extensions.Shared.Testing.HttpResultUnwrap;

namespace WhisparrSync.Tests.Api;

// Every case starts from a store and a credential port of its own, so a case that writes cannot
// change what another one reads.
public sealed class SettingsEndpointTests
{
    private const string StoredKey = "3f3f3f3f3f3f3f3f3f3f3f3f3f3f3f3f";
    private const string StoredAddress = "http://whisparr-v3:6969";
    private const string ExtensionId = "com.alextomas955.whisparrsync";
    private const string CoveOrigin = "http://cove.example:8080";
    private static readonly DateTimeOffset Verified = new(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AReadAsAConfigureTierCallerAnswersWithTheStoredConnections()
    {
        var (store, options) = await SeededAsync();
        var credentials = new RecordingCredentialPort().Holding(WhisparrGeneration.V3, StoredAddress, StoredKey);
        var writesBefore = store.SetCallCount;

        var view = await ReadAsync(options, credentials, Configure());

        Assert.Equal(WhisparrGeneration.V3, view.SelectedGeneration);
        Assert.Equal(StoredAddress, view.V3.Address);
        Assert.True(view.V3.KeyIsSet);
        Assert.Equal("3.3.8.1097", view.V3.RecordedVersion);
        Assert.Equal(Verified, view.V3.VersionVerifiedAtUtc);
        Assert.False(view.V2.KeyIsSet);
        Assert.Equal("", view.V2.Address);
        Assert.Equal(writesBefore, store.SetCallCount);
    }

    // Never verified and verified-then-failed are different answers, so the page can say which it
    // is holding.
    [Fact]
    public async Task AGenerationNeverTestedReportsNoVerifiedInstantRatherThanAnOldOne()
    {
        var (_, options) = await SeededAsync();

        var view = await ReadAsync(options, new RecordingCredentialPort(), Configure());

        Assert.Null(view.V2.RecordedVersion);
        Assert.Null(view.V2.VersionVerifiedAtUtc);
        Assert.Null(view.V2.LastReachableAtUtc);
    }

    [Fact]
    public async Task AReadHoldingOnlyTheLibraryReadTierIsRefusedAndDisclosesNothing()
    {
        var (store, options) = await SeededAsync();
        var credentials = new RecordingCredentialPort().Holding(WhisparrGeneration.V3, StoredAddress, StoredKey);

        var result = await global::WhisparrSync.WhisparrSync.ReadSettingsAsync(
            FakePrincipalAccessor.WithPermissions(Permissions.VideosRead), options, credentials, TestCt);

        Assert.Equal(403, StatusOf(result));
        Assert.Empty(credentials.Reads);
        Assert.Empty(store.GetKeys);
    }

    [Fact]
    public async Task ASaveThatMovesTheAddressDiscardsTheReadingTakenAgainstTheOldOne()
    {
        var (_, options) = await SeededAsync();

        var view = await SaveAsync(
            options,
            new RecordingCredentialPort(),
            new WhisparrSyncSettingsSaveRequest(
                WhisparrGeneration.V3,
                new WhisparrSyncGenerationSaveRequest("http://whisparr-somewhere-else:6969", KeyWriteSignal.Keep, null),
                null));

        Assert.Equal("http://whisparr-somewhere-else:6969", view.V3.Address);
        Assert.Null(view.V3.RecordedVersion);
        Assert.Null(view.V3.VersionVerifiedAtUtc);
        Assert.Null(view.V3.LastReachableAtUtc);
    }

    [Theory]
    [InlineData(StoredAddress)]
    [InlineData(StoredAddress + "/")]
    [InlineData("HTTP://WHISPARR-V3:6969")]
    [InlineData("  " + StoredAddress + "  ")]
    public async Task ASaveThatDoesNotMoveTheAddressKeepsTheReading(string address)
    {
        var (_, options) = await SeededAsync();

        var view = await SaveAsync(
            options,
            new RecordingCredentialPort(),
            new WhisparrSyncSettingsSaveRequest(
                WhisparrGeneration.V3,
                new WhisparrSyncGenerationSaveRequest(address, KeyWriteSignal.Keep, null),
                null));

        Assert.Equal("3.3.8.1097", view.V3.RecordedVersion);
        Assert.Equal(Verified, view.V3.VersionVerifiedAtUtc);
        Assert.Equal(StoredAddress, view.V3.Address);
    }

    [Fact]
    public async Task ASaveOfOneGenerationLeavesTheOtherAlone()
    {
        var (_, options) = await SeededAsync();

        var view = await SaveAsync(
            options,
            new RecordingCredentialPort(),
            new WhisparrSyncSettingsSaveRequest(
                WhisparrGeneration.V2,
                null,
                new WhisparrSyncGenerationSaveRequest("http://whisparr-v2:6969", KeyWriteSignal.Keep, null)));

        Assert.Equal(WhisparrGeneration.V2, view.SelectedGeneration);
        Assert.Equal("http://whisparr-v2:6969", view.V2.Address);
        Assert.Equal(StoredAddress, view.V3.Address);
        Assert.Equal("3.3.8.1097", view.V3.RecordedVersion);
    }

    [Theory]
    [InlineData(KeyWriteSignal.Keep, null)]
    [InlineData(KeyWriteSignal.Replace, null)]
    [InlineData(KeyWriteSignal.Replace, "")]
    [InlineData(KeyWriteSignal.Replace, "   ")]
    public async Task ASaveCarryingNoKeyKeepsTheStoredOne(KeyWriteSignal signal, string? submitted)
    {
        var (_, options) = await SeededAsync();
        var credentials = new RecordingCredentialPort().Holding(WhisparrGeneration.V3, StoredAddress, StoredKey);

        var view = await SaveAsync(
            options,
            credentials,
            new WhisparrSyncSettingsSaveRequest(
                WhisparrGeneration.V3,
                new WhisparrSyncGenerationSaveRequest(StoredAddress, signal, submitted),
                null));

        Assert.True(view.V3.KeyIsSet);
        Assert.Equal(StoredKey, await credentials.ReadAsync(WhisparrGeneration.V3, TestCt));
        Assert.All(credentials.Writes, write => Assert.Equal(CredentialWriteKind.Keep, write.Kind));
    }

    [Fact]
    public async Task AnExplicitClearRemovesTheStoredKey()
    {
        var (_, options) = await SeededAsync();
        var credentials = new RecordingCredentialPort().Holding(WhisparrGeneration.V3, StoredAddress, StoredKey);

        var view = await SaveAsync(
            options,
            credentials,
            new WhisparrSyncSettingsSaveRequest(
                WhisparrGeneration.V3,
                new WhisparrSyncGenerationSaveRequest(StoredAddress, KeyWriteSignal.Clear, null),
                null));

        Assert.False(view.V3.KeyIsSet);
        Assert.Null(await credentials.ReadAsync(WhisparrGeneration.V3, TestCt));
    }

    [Fact]
    public async Task AReplacementIsWrittenAgainstTheGenerationItNames()
    {
        var (_, options) = await SeededAsync();
        var credentials = new RecordingCredentialPort();

        var view = await SaveAsync(
            options,
            credentials,
            new WhisparrSyncSettingsSaveRequest(
                WhisparrGeneration.V2,
                null,
                new WhisparrSyncGenerationSaveRequest("http://whisparr-v2:6969", KeyWriteSignal.Replace, "v2-key")));

        Assert.True(view.V2.KeyIsSet);
        Assert.False(view.V3.KeyIsSet);
        Assert.Contains(
            credentials.Writes,
            write => write is (WhisparrGeneration.V2, CredentialWriteKind.Replace, "v2-key"));
        Assert.DoesNotContain(
            credentials.Writes,
            write => write.Generation == WhisparrGeneration.V3 && write.Kind != CredentialWriteKind.Keep);
    }

    // The competing writer is the production secret-position write, which a delivery arriving
    // during a registration performs. It lands on the connection record this handler read before
    // its outbound call.
    [Fact]
    public async Task ARegistrationKeepsAWriteCommittedWhileItWasInFlight()
    {
        var (_, options) = await SeededAsync();
        using var gate = new OptionsWriteGate();
        var credentials = new RecordingCredentialPort().Holding(WhisparrGeneration.V3, StoredAddress, StoredKey);

        var view = await RegisterAsync(
            options,
            gate,
            credentials,
            new DeliveringNotificationPort(options, gate, CallbackSecretPosition.Address));

        // The blob first, then the answer: a handler that stored both and still answered from its
        // pre-network local would pass an answer-first assertion for the wrong reason.
        var stored = await options.LoadAsync(TestCt);
        Assert.Equal(CallbackSecretPosition.Address, stored.V3?.LastCallbackSecretPosition);
        Assert.Equal(RegistrationStatus.Registered, stored.V3?.CallbackRegistration);

        Assert.Equal(RegistrationStatus.Registered, view.Status);
        Assert.Equal(CallbackSecretPosition.Address, view.LastEventSecretPosition);
    }

    // Whether the host locks itself down turns on the address Whisparr calls, where the call comes
    // from and the host's own trusted-host list, none of which this product can read. So the risk is
    // reported and the registration still goes ahead: refusing on it blocked the setups the host
    // would have allowed, including every containerised one.
    [Fact]
    public async Task ARegistrationGoesAheadAndReportsThatCoveMightLockItselfDown()
    {
        var (_, options) = await SeededAsync();
        using var gate = new OptionsWriteGate();

        var view = await RegisterAsync(
            options,
            gate,
            new RecordingCredentialPort().Holding(WhisparrGeneration.V3, StoredAddress, StoredKey),
            new DeliveringNotificationPort(options, gate, CallbackSecretPosition.OutOfBand),
            wouldLockDown: true);

        Assert.False(view.RegistrationIsSafe);
        Assert.Equal(RegistrationStatus.Registered, view.Status);
    }

    // A Cove with sign-in off and no owner account yet is one nobody can be locked out of: the host
    // lets an outside call through untouched so first-run setup can be finished from elsewhere. A
    // product that refused on the sign-in setting alone would block that Cove for no gain, which is
    // what the containerised suite runs against.
    [Fact]
    public async Task ARegistrationGoesAheadWhereNoLockdownWouldFollow()
    {
        var (_, options) = await SeededAsync();
        using var gate = new OptionsWriteGate();

        var view = await RegisterAsync(
            options,
            gate,
            new RecordingCredentialPort().Holding(WhisparrGeneration.V3, StoredAddress, StoredKey),
            new DeliveringNotificationPort(options, gate, CallbackSecretPosition.OutOfBand),
            wouldLockDown: false);

        Assert.True(view.RegistrationIsSafe);
        Assert.Equal(RegistrationStatus.Registered, view.Status);
    }

    // The field the manifest reads is filled at load, so without a refresh on save a generation
    // switched through the page keeps the surfaces of the one before it until the extension loads
    // again.
    [Fact]
    public async Task ASaveChangesWhatTheNextManifestReadRegistersOnTheSameInstance()
    {
        var (_, options) = await SeededAsync();
        using var gate = new OptionsWriteGate();
        var credentials = new RecordingCredentialPort();
        var extension = WhisparrSyncFixture.Create();

        await SaveOnAsync(extension, options, gate, credentials, WhisparrGeneration.V3);
        var afterV3 = SlotsOf(extension);

        await SaveOnAsync(extension, options, gate, credentials, WhisparrGeneration.V2);
        var afterV2 = SlotsOf(extension);

        Assert.Contains("video-card-content", afterV3);
        Assert.DoesNotContain("video-card-content", afterV2);
        Assert.Contains("studio-card-footer", afterV3);
        Assert.Contains("studio-card-footer", afterV2);
    }

    private static IReadOnlyList<string> SlotsOf(global::WhisparrSync.WhisparrSync extension)
        => [.. extension.GetUIManifest().Slots.Select(slot => slot.Slot)];

    private static async Task SaveOnAsync(
        global::WhisparrSync.WhisparrSync extension,
        OptionsStore options,
        OptionsWriteGate gate,
        RecordingCredentialPort credentials,
        WhisparrGeneration generation)
    {
        var credential = new WhisparrSyncGenerationSaveRequest(
            "http://whisparr:6969", KeyWriteSignal.Replace, StoredKey);
        var saved = await extension.SaveSettingsAsync(
            new WhisparrSyncSettingsSaveRequest(
                generation,
                generation == WhisparrGeneration.V3 ? credential : null,
                generation == WhisparrGeneration.V2 ? credential : null),
            Configure(),
            options,
            gate,
            credentials,
            new FixedClock(Now),
            TestCt);

        Assert.Equal(generation, ValueOf<WhisparrSyncSettingsView>(saved).SelectedGeneration);
    }

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    private static FakePrincipalAccessor Configure()
        => FakePrincipalAccessor.WithPermissions(Permissions.ExtensionsConfigure);

    private static int StatusOf(IResult result)
        => Assert.IsAssignableFrom<IStatusCodeHttpResult>(Unwrap(result)).StatusCode ?? 0;

    private static T ValueOf<T>(IResult result)
        => Assert.IsType<T>(Assert.IsAssignableFrom<IValueHttpResult>(Unwrap(result)).Value);

    private static async Task<WhisparrSyncSettingsView> ReadAsync(
        OptionsStore options, RecordingCredentialPort credentials, FakePrincipalAccessor principal)
        => ValueOf<WhisparrSyncSettingsView>(
            await global::WhisparrSync.WhisparrSync.ReadSettingsAsync(principal, options, credentials, TestCt));

    private static async Task<WhisparrSyncSettingsView> SaveAsync(
        OptionsStore options, RecordingCredentialPort credentials, WhisparrSyncSettingsSaveRequest request)
        => ValueOf<WhisparrSyncSettingsView>(
            await WhisparrSyncFixture.Create().SaveSettingsAsync(
                request,
                Configure(),
                options,
                new OptionsWriteGate(),
                credentials,
                new FixedClock(Now),
                TestCt));

    private sealed class Lockdown(bool wouldLockDown) : IHostLockdownPort
    {
        public Task<bool> WouldLockDownAsync(CancellationToken ct) => Task.FromResult(wouldLockDown);
    }

    private static async Task<CallbackView> RegisterAsync(
        OptionsStore options,
        OptionsWriteGate gate,
        RecordingCredentialPort credentials,
        IWhisparrNotificationPort notifications,
        bool wouldLockDown = false)
        => ValueOf<CallbackView>(
            await global::WhisparrSync.WhisparrSync.RegisterCallbackAsync(
                new RegisterCallbackRequest(null),
                RequestFrom(CoveOrigin),
                Configure(),
                ExtensionId,
                new CallbackAddressing(
                    options,
                    new MintedSecretPort(),
                    new Lockdown(wouldLockDown),
                    new FixedClock(Now)),
                new CallbackRegistering(
                    gate,
                    credentials,
                    notifications,
                    new RegistrationGate()),
                TestCt));

    private static DefaultHttpContext RequestFrom(string origin)
    {
        var at = new Uri(origin);
        var http = new DefaultHttpContext();
        http.Request.Scheme = at.Scheme;
        http.Request.Host = new HostString(at.Authority);
        return http;
    }

    // A v3 connection that has been tested once, and a v2 that has never been configured.
    private static async Task<(FakeStore Store, OptionsStore Options)> SeededAsync()
    {
        var store = new FakeStore();
        var options = new OptionsStore(store);
        await options.SaveAsync(
            new WhisparrSyncOptions
            {
                SelectedGeneration = WhisparrGeneration.V3,
                V3 = new WhisparrSyncGenerationConnection
                {
                    Address = StoredAddress,
                    RecordedVersion = "3.3.8.1097",
                    VersionVerifiedAtUtc = Verified,
                    LastReachableAtUtc = Verified,
                },
            },
            TestCt);

        store.GetKeys.Clear();
        return (store, options);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
