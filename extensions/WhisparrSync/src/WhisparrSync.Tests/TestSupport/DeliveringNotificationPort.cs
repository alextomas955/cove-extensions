using WhisparrSync.Connection;
using WhisparrSync.Contracts;
using WhisparrSync.Options;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.TestSupport;

/// <summary>
/// A notification port that commits the production secret-position write before it answers the
/// registration, which is what a delivery arriving mid-registration does.
/// </summary>
/// <remarks>
/// The competing write is the real one rather than a stand-in, so what it lands on is exactly what a
/// delivery lands on: the connection record the registration handler read before its outbound call.
/// </remarks>
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

/// <summary>A secret port answering with one secret it never has to mint.</summary>
internal sealed class MintedSecretPort : ICallbackSecretPort
{
    private const string Secret = "9c1f6b2e4a8d0357";

    public Task<string?> ReadAsync(CancellationToken ct) => Task.FromResult<string?>(Secret);

    public Task<string> EnsureAsync(DateTimeOffset nowUtc, CancellationToken ct)
        => Task.FromResult(Secret);
}
