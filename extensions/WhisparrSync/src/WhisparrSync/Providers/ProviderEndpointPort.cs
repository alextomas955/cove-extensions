using Cove.Core.Interfaces;
using WhisparrSync.Contracts;
using WhisparrSync.Import;
using WhisparrSync.Options;

namespace WhisparrSync.Providers;

/// <summary>The metadata provider one generation reads its catalogue from.</summary>
/// <param name="IdentityEndpoint">
/// The endpoint spelling identity rows are written under, which is the configured one rather than
/// the address the provider's own API is served at.
/// </param>
/// <param name="ApiKey">The credential the host holds for that source.</param>
/// <param name="MaxRequestsPerMinute">The rate the host is configured to allow.</param>
public sealed record ResolvedProvider(
    string IdentityEndpoint, string ApiKey, int MaxRequestsPerMinute);

/// <summary>Where a provider serves its own API, which is not where its identity is spelled.</summary>
/// <remarks>
/// ThePornDB's catalogue is REST at this address while Cove stamps a remote-id row under
/// <see cref="IdentityEndpoint.ThePornDb"/>. An ownership match keys on that stored spelling by
/// exact string, so this address may never stand in for it: it matches no stored row, and every
/// scene the library holds would read as missing.
/// <para>
/// <see cref="EndpointMatchGuard.SameSource"/> does not catch the substitution. It compares
/// registrable domains, and both addresses share one, so the two spellings read as the same source
/// there while the stored rows carry only one of them.
/// </para>
/// </remarks>
internal static class ProviderApiBase
{
    /// <summary>Where ThePornDB serves the REST API this product reads a catalogue from.</summary>
    internal const string ThePornDb = "https://api.theporndb.net";
}

/// <summary>Which metadata server the connected generation reads from, or that there is none.</summary>
/// <remarks>
/// The configuration is optional: a host that registers none must still load the extension, so an
/// absent configuration is answered as a refusal.
/// <para>
/// Nothing in the host applies <see cref="MetadataServerInstance.MaxRequestsPerMinute"/> to an
/// outbound request, so the number is a rate this product paces itself to rather than one it is held
/// to.
/// </para>
/// </remarks>
internal sealed class ProviderEndpointPort(CoveConfiguration? config)
{
    /// <summary>
    /// The server <paramref name="generation"/> identifies against, or null where the host names
    /// none.
    /// </summary>
    /// <remarks>
    /// Matched on the source rather than on the string, because the host resolves an endpoint to a
    /// source on its registrable domain and two spellings of one provider are one source.
    /// </remarks>
    internal ResolvedProvider? Resolve(
        WhisparrGeneration generation, MetadataProviderEndpoints preferred)
    {
        ArgumentNullException.ThrowIfNull(preferred);

        var servers = config?.Scraping?.MetadataServers;
        if (servers is null || servers.Count == 0)
        {
            return null;
        }

        var identityEndpoint = IdentityEndpoint.Resolve(
            generation, preferred, [.. servers.Select(server => server.Endpoint)]);

        var matched = servers.FirstOrDefault(
            server => EndpointMatchGuard.SameSource(server.Endpoint, identityEndpoint));

        return matched is null || string.IsNullOrWhiteSpace(matched.ApiKey)
            ? null
            : new ResolvedProvider(identityEndpoint, matched.ApiKey, matched.MaxRequestsPerMinute);
    }
}
