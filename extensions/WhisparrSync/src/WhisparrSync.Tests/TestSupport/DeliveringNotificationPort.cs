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
        WhisparrGeneration generation,
        Uri baseAddress,
        string apiKey,
        string callbackAddress,
        string secret,
        CancellationToken ct)
    {
        await global::WhisparrSync.WhisparrSync.RecordSecretPositionAsync(
            options, gate, generation, position, ct);
        return new CallbackRegistrationOutcome(
            RegistrationStatus.Registered, callbackAddress, Created: true, Refusal: null);
    }

    public Task<CallbackRegistrationOutcome> ReadAsync(
        Uri baseAddress, string apiKey, CancellationToken ct)
        => throw new NotSupportedException();
}

// Refuses every call, so a test using it proves the instance was never contacted. An empty list on
// a recording double would prove the same thing only if the assertion were written, and a later
// edit that dropped the assertion would still pass.
internal sealed class UncontactableNotificationPort : IWhisparrNotificationPort
{
    public Task<CallbackRegistrationOutcome> RegisterAsync(
        WhisparrGeneration generation,
        Uri baseAddress,
        string apiKey,
        string callbackAddress,
        string secret,
        CancellationToken ct)
        => throw new InvalidOperationException(
            "the instance was contacted by a registration that should have been refused first.");

    public Task<CallbackRegistrationOutcome> ReadAsync(
        Uri baseAddress, string apiKey, CancellationToken ct)
        => throw new NotSupportedException();
}

internal sealed class MintedSecretPort : ICallbackSecretPort
{
    private const string Secret = "9c1f6b2e4a8d0357";

    public Task<string?> ReadAsync(CancellationToken ct) => Task.FromResult<string?>(Secret);

    public Task<string> EnsureAsync(DateTimeOffset nowUtc, CancellationToken ct)
        => Task.FromResult(Secret);
}
