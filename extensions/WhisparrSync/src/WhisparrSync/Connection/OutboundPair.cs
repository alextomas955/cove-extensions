using WhisparrSync.Contracts;
using WhisparrSync.Options;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Connection;

// What one resolution established. Missing names the setting that was empty and is null once a
// binding was built, so a caller cannot report a refusal a resolution did not make. Address is the
// rebuilt form and is present on a refusal the key caused, so that refusal can echo the instance it
// would have reached.
internal readonly record struct OutboundResolution(
    WhisparrBinding? Binding, ConnectionSetting? Missing, Uri? Address);

// The one place the address and the key an outbound request is built from are resolved.
internal static class OutboundPair
{
    // The address comes from the row that holds the key, so the two cannot be observed from either
    // side of a save that changed both and one instance's key cannot be posted to another. A row
    // written before the address was stored there carries none, and the stored options answer for
    // that installation until its next save.
    // Null rather than a binding over an empty pair, so an unconfigured connection reaches nothing
    // that could make a request.
    internal static async Task<WhisparrBinding?> ResolveAsync(
        WhisparrSyncOptions stored, ICredentialPort credentials, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stored);

        return (await ResolveAsync(stored, credentials, stored.SelectedGeneration, ct)
            .ConfigureAwait(false)).Binding;
    }

    // Takes the generation rather than reading the selected one, for a caller answering for the
    // generation it was handed: a delivery names the generation that sent it, and a job carries the
    // one it was aimed at.
    internal static async Task<OutboundResolution> ResolveAsync(
        WhisparrSyncOptions stored,
        ICredentialPort credentials,
        WhisparrGeneration generation,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(credentials);

        var held = await credentials.ReadConnectionAsync(generation, ct).ConfigureAwait(false);
        var apiKey = held?.ApiKey;
        var address = string.IsNullOrWhiteSpace(held?.Address)
            ? stored.ConnectionFor(generation)?.Address
            : held.Address;

        return ConnectionTester.TryReadConnection(address, apiKey, out var baseAddress, out var missing)
            ? new OutboundResolution(new WhisparrBinding(generation, baseAddress, apiKey), null, baseAddress)
            : new OutboundResolution(null, missing, baseAddress);
    }
}
