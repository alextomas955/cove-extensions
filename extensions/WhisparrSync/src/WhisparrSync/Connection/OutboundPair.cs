using WhisparrSync.Options;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Connection;

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
        ArgumentNullException.ThrowIfNull(credentials);

        var generation = stored.SelectedGeneration;
        var held = await credentials.ReadConnectionAsync(generation, ct).ConfigureAwait(false);
        var apiKey = held?.ApiKey;
        var address = string.IsNullOrWhiteSpace(held?.Address)
            ? stored.ConnectionFor(generation)?.Address
            : held.Address;

        return ConnectionTester.TryReadConnection(address, apiKey, out var baseAddress, out _)
            ? new WhisparrBinding(generation, baseAddress, apiKey)
            : null;
    }
}
