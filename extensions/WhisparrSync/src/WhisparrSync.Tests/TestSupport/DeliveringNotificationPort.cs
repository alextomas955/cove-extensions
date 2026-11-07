using WhisparrSync.Connection;
using WhisparrSync.Contracts;
using WhisparrSync.Options;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.TestSupport;

// Commits the production secret-position write before it answers the registration, which is what a
// delivery arriving mid-registration does. The competing write is the real one, so it lands on the
// same connection record a delivery lands on.
internal sealed class DeliveringNotificationPort(
    OptionsStore options, OptionsWriteGate gate, CallbackSecretPosition position)
    : IWhisparrNotificationPort
{
    public async Task<CallbackRegistrationOutcome> RegisterAsync(
        WhisparrBinding binding, string callbackAddress, string secret, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(binding);

        await global::WhisparrSync.WhisparrSync.RecordSecretPositionAsync(
            options, gate, binding.Generation, position, ct);
        return new CallbackRegistrationOutcome(
            RegistrationStatus.Registered, callbackAddress, Created: true, Refusal: null);
    }

    public Task<CallbackRegistrationOutcome> ReadAsync(WhisparrBinding binding, CancellationToken ct)
        => throw new NotSupportedException();
}

internal sealed class MintedSecretPort : ICallbackSecretPort
{
    private const string Secret = "9c1f6b2e4a8d0357";

    public Task<string?> ReadAsync(CancellationToken ct) => Task.FromResult<string?>(Secret);

    public Task<string> EnsureAsync(DateTimeOffset nowUtc, CancellationToken ct)
        => Task.FromResult(Secret);
}
