using Cove.Core.Auth;
using Microsoft.AspNetCore.Http;
using WhisparrSync.Connection;
using WhisparrSync.Contracts;
using WhisparrSync.Options;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;
using static Cove.Extensions.Shared.Testing.HttpResultUnwrap;

namespace WhisparrSync.Tests.Api;

/// <summary>
/// The settings read and write, driven through the handlers the routes call.
/// </summary>
/// <remarks>
/// Every case starts from a store and a credential port of its own, so a test that writes cannot
/// change what another one reads.
/// </remarks>
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
        var credentials = new RecordingCredentialPort().Holding(WhisparrGeneration.V3, StoredKey);
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

    /// <summary>
    /// The never-verified state and a verified-then-failed state are different answers, so the page can
    /// say which one it is holding.
    /// </summary>
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
        var credentials = new RecordingCredentialPort().Holding(WhisparrGeneration.V3, StoredKey);

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

    /// <summary>
    /// A trailing separator and letter case do not move an address, so neither discards the reading.
    /// </summary>
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
        var credentials = new RecordingCredentialPort().Holding(WhisparrGeneration.V3, StoredKey);

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
        var credentials = new RecordingCredentialPort().Holding(WhisparrGeneration.V3, StoredKey);

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

    /// <summary>
    /// A writer that commits while the registration is in flight keeps its value, in the stored blob
    /// and in the answer the page is given.
    /// </summary>
    /// <remarks>
    /// The competing writer is the production secret-position write, which is what a delivery arriving
    /// during a registration performs, and it lands on the very connection record this handler read
    /// before its outbound call.
    /// </remarks>
    [Fact]
    public async Task ARegistrationKeepsAWriteCommittedWhileItWasInFlight()
    {
        var (_, options) = await SeededAsync();
        using var gate = new OptionsWriteGate();
        var credentials = new RecordingCredentialPort().Holding(WhisparrGeneration.V3, StoredKey);

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
            await global::WhisparrSync.WhisparrSync.SaveSettingsAsync(
                request,
                Configure(),
                options,
                new OptionsWriteGate(),
                credentials,
                new FixedClock(Now),
                TestCt));

    private static async Task<CallbackView> RegisterAsync(
        OptionsStore options,
        OptionsWriteGate gate,
        RecordingCredentialPort credentials,
        IWhisparrNotificationPort notifications)
        => ValueOf<CallbackView>(
            await global::WhisparrSync.WhisparrSync.RegisterCallbackAsync(
                new RegisterCallbackRequest(null),
                RequestFrom(CoveOrigin),
                Configure(),
                ExtensionId,
                options,
                gate,
                credentials,
                new MintedSecretPort(),
                notifications,
                new FixedClock(Now),
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
