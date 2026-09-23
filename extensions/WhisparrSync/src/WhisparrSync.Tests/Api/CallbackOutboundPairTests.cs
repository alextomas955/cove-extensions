using Cove.Core.Auth;
using Microsoft.AspNetCore.Http;
using WhisparrSync.Connection;
using WhisparrSync.Contracts;
using WhisparrSync.Options;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;
using static Cove.Extensions.Shared.Testing.HttpResultUnwrap;

namespace WhisparrSync.Tests.Api;

// A registration writes this product's callback secret into the instance it reaches. The address and
// the key are two writes in two stores, so a registration taking one from either side of a save that
// moved both would plant the secret, and present the key, at the instance the user is moving away
// from.
public sealed class CallbackOutboundPairTests
{
    private const string StoredAddress = "http://whisparr-v3:6969";
    private const string MovedAddress = "http://whisparr-elsewhere:6969";
    private const string MovedKey = "8d5d8d5d8d5d8d5d8d5d8d5d8d5d8d5d";
    private const string ExtensionId = "com.alextomas955.whisparrsync";
    private const string CoveOrigin = "http://cove.example:8080";

    private static readonly DateTimeOffset Now = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    [Fact]
    public async Task NoRegistrationCarryingTheSecretReachesTheAddressTheRowLeftBehind()
    {
        var notifications = new RecordingNotificationPort();
        var credentials = new RecordingCredentialPort()
            .Holding(WhisparrGeneration.V3, MovedAddress, MovedKey);

        await RegisterCallbackAsync(credentials, notifications);

        Assert.DoesNotContain(
            notifications.Registrations,
            sent => ConnectionTester.IsSameAddress(
                StoredAddress, sent.Binding.BaseAddress.ToString()));
    }

    // Paired with the case above, so an empty registration log is not what satisfies it.
    [Fact]
    public async Task TheRegistrationBindsToTheAddressHeldBesideTheKeyItSends()
    {
        var notifications = new RecordingNotificationPort();
        var credentials = new RecordingCredentialPort()
            .Holding(WhisparrGeneration.V3, MovedAddress, MovedKey);

        await RegisterCallbackAsync(credentials, notifications);

        var sent = Assert.Single(notifications.Registrations);
        Assert.True(ConnectionTester.IsSameAddress(
            MovedAddress, sent.Binding.BaseAddress.ToString()));
        Assert.Equal(MovedKey, sent.Binding.ApiKey);
    }

    // The row is the only source of the address. The stored blob names one here, so a registration
    // that still consulted it would present this row's secret to that instance.
    [Fact]
    public async Task ARowCarryingNoAddressRegistersNothing()
    {
        var notifications = new RecordingNotificationPort();
        var credentials = new RecordingCredentialPort().Holding(WhisparrGeneration.V3, MovedKey);

        await RegisterCallbackAsync(credentials, notifications);

        Assert.Empty(notifications.Registrations);
    }

    [Fact]
    public async Task AConnectionWithNoKeyNamesTheKeyAndRegistersNothing()
    {
        var notifications = new RecordingNotificationPort();

        var view = await RegisterCallbackAsync(
            new RecordingCredentialPort().HoldingAddressOnly(WhisparrGeneration.V3, StoredAddress),
            notifications);

        Assert.Equal(ConnectionSetting.ApiKey, view.MissingSetting);
        Assert.Empty(notifications.Registrations);
    }

    private static async Task<CallbackView> RegisterCallbackAsync(
        ICredentialPort credentials, IWhisparrNotificationPort notifications)
    {
        var options = new OptionsStore(new FakeStore());
        await options.SaveAsync(
            new WhisparrSyncOptions { SelectedGeneration = WhisparrGeneration.V3 }.WithConnectionFor(
                WhisparrGeneration.V3,
                new WhisparrSyncGenerationConnection { Address = StoredAddress }),
            TestCt);

        using var gate = new OptionsWriteGate();
        using var registrations = new RegistrationGate();
        return ValueOf(
            await global::WhisparrSync.WhisparrSync.RegisterCallbackAsync(
                new RegisterCallbackRequest(null),
                RequestFrom(CoveOrigin),
                FakePrincipalAccessor.WithPermissions(Permissions.ExtensionsConfigure),
                ExtensionId,
                options,
                gate,
                credentials,
                new MintedSecretPort(),
                notifications,
                registrations,
                new OpenLockdown(),
                new FixedClock(Now),
                TestCt));
    }

    private static CallbackView ValueOf(IResult result)
        => Assert.IsType<CallbackView>(
            Assert.IsAssignableFrom<IValueHttpResult>(Unwrap(result)).Value);

    private static DefaultHttpContext RequestFrom(string origin)
    {
        var at = new Uri(origin);
        var http = new DefaultHttpContext();
        http.Request.Scheme = at.Scheme;
        http.Request.Host = new HostString(at.Authority);
        return http;
    }

    // Records the pair and the secret each registration carried, so a case can say which instance
    // the secret was written into.
    private sealed class RecordingNotificationPort : IWhisparrNotificationPort
    {
        public List<(WhisparrBinding Binding, string CallbackAddress, string Secret)> Registrations { get; } = [];

        public Task<CallbackRegistrationOutcome> RegisterAsync(
            WhisparrBinding binding, string callbackAddress, string secret, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(binding);
            Registrations.Add((binding, callbackAddress, secret));
            return Task.FromResult(
                new CallbackRegistrationOutcome(
                    RegistrationStatus.Registered, callbackAddress, Created: true, Refusal: null));
        }

        public Task<CallbackRegistrationOutcome> ReadAsync(
            WhisparrBinding binding, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class OpenLockdown : IHostLockdownPort
    {
        public Task<bool> WouldLockDownAsync(CancellationToken ct) => Task.FromResult(false);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
