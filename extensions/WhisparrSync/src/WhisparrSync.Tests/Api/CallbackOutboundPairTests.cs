using Cove.Core.Auth;
using Microsoft.AspNetCore.Http;
using WhisparrSync.Connection;
using WhisparrSync.Contracts;
using WhisparrSync.Options;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;
using static Cove.Extensions.Shared.Testing.HttpResultUnwrap;

namespace WhisparrSync.Tests.Api;

// A registration writes this product's callback secret into the instance it reaches. The address
// and the key are two writes in two stores, so a registration reading one from either side of a
// save that moved both would plant the secret at the instance the user is moving away from.
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

    [Fact]
    public async Task TheRegisteredAddressCarriesNoneOfTheCredentialsTheEditedOneDid()
    {
        var notifications = new RecordingNotificationPort();
        var credentials = new RecordingCredentialPort()
            .Holding(WhisparrGeneration.V3, MovedAddress, MovedKey);

        await RegisterCallbackAsync(
            credentials, notifications, "http://someone:hunter2@cove.example:8080");

        var sent = Assert.Single(notifications.Registrations);
        Assert.StartsWith("http://cove.example:8080/", sent.CallbackAddress, StringComparison.Ordinal);
        Assert.DoesNotContain("someone", sent.CallbackAddress, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", sent.CallbackAddress, StringComparison.Ordinal);
    }

    // The registration round trip is the longest window another writer of this record has. A save
    // committed in it moves the address, so the status the call came back with describes an
    // instance the row no longer names.
    [Fact]
    public async Task ARegistrationWhoseAddressMovedUnderTheCallRecordsNoStatus()
    {
        var options = new OptionsStore(new FakeStore());
        var credentials = new RecordingCredentialPort()
            .Holding(WhisparrGeneration.V3, StoredAddress, MovedKey);
        var notifications = new MovingNotificationPort(credentials);

        await RegisterCallbackAsync(credentials, notifications, options: options);

        Assert.Single(notifications.Registrations);
        var stored = (await options.LoadAsync(TestCt)).ConnectionFor(WhisparrGeneration.V3);
        Assert.Equal(RegistrationStatus.NotCheckedYet, stored!.CallbackRegistration);
    }

    // The control: with the row still naming the instance the call reached, the status is recorded.
    [Fact]
    public async Task ARegistrationTheRowStillNamesRecordsItsStatus()
    {
        var options = new OptionsStore(new FakeStore());
        var credentials = new RecordingCredentialPort()
            .Holding(WhisparrGeneration.V3, StoredAddress, MovedKey);

        await RegisterCallbackAsync(credentials, new RecordingNotificationPort(), options: options);

        var stored = (await options.LoadAsync(TestCt)).ConnectionFor(WhisparrGeneration.V3);
        Assert.Equal(RegistrationStatus.Registered, stored!.CallbackRegistration);
    }

    private static async Task<CallbackView> RegisterCallbackAsync(
        ICredentialPort credentials,
        IWhisparrNotificationPort notifications,
        string? editedAddress = null,
        OptionsStore? options = null)
    {
        options ??= new OptionsStore(new FakeStore());
        await options.SaveAsync(
            new WhisparrSyncOptions { SelectedGeneration = WhisparrGeneration.V3 }.WithConnectionFor(
                WhisparrGeneration.V3,
                new WhisparrSyncGenerationConnection()),
            TestCt);

        using var gate = new OptionsWriteGate();
        using var registrations = new RegistrationGate();
        return ValueOf(
            await global::WhisparrSync.WhisparrSync.RegisterCallbackAsync(
                new RegisterCallbackRequest(editedAddress),
                TestRequest.From(CoveOrigin),
                FakePrincipalAccessor.WithPermissions(Permissions.ExtensionsConfigure),
                ExtensionId,
                new CallbackAddressing(
                    options,
                    new MintedSecretPort(),
                    new Lockdown(wouldLockDown: false),
                    new FixedClock(Now)),
                new CallbackRegistering(
                    gate,
                    credentials,
                    notifications,
                    registrations),
                TestCt));
    }

    private static CallbackView ValueOf(IResult result)
        => Assert.IsType<CallbackView>(
            Assert.IsAssignableFrom<IValueHttpResult>(Unwrap(result)).Value);

    // Moves the stored address while the registration is in flight, as a settings save committed in
    // that window does.
    private sealed class MovingNotificationPort(RecordingCredentialPort credentials)
        : IWhisparrNotificationPort
    {
        private readonly RecordingNotificationPort _inner = new();

        public IReadOnlyList<(WhisparrBinding Binding, string CallbackAddress, string Secret)>
            Registrations => _inner.Registrations;

        public Task<CallbackRegistrationOutcome> RegisterAsync(
            WhisparrBinding binding, string callbackAddress, string secret, CancellationToken ct)
        {
            credentials.Holding(WhisparrGeneration.V3, MovedAddress, MovedKey);
            return _inner.RegisterAsync(binding, callbackAddress, secret, ct);
        }

        public Task<CallbackRegistrationOutcome> ReadAsync(
            WhisparrBinding binding, CancellationToken ct) => throw new NotSupportedException();
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
}
