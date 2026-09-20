using Cove.Core.Interfaces;
using WhisparrSync.Contracts;
using WhisparrSync.Identity;
using WhisparrSync.Options;

namespace WhisparrSync.Providers;

/// <summary>The metadata provider one generation reads its catalogue from.</summary>
/// <remarks>
/// IdentityEndpoint is the configured spelling identity rows are written under, not the address the
/// provider's own API is served at.
/// </remarks>
public sealed record ResolvedProvider(
    string IdentityEndpoint, string ApiKey, int MaxRequestsPerMinute);

// Cove stamps remote-id rows under IdentityEndpoint.ThePornDb, and an ownership match keys on that
// stored spelling by exact string. This API address must never stand in for it, or every scene in
// the library reads as missing. EndpointMatchGuard.SameSource does not catch the substitution: it
// compares registrable domains, which both addresses share.
internal static class ProviderApiBase
{
    internal const string ThePornDb = "https://api.theporndb.net";
}

// Nothing in the host applies MaxRequestsPerMinute to an outbound request, so it is a rate this
// product paces itself to.
internal sealed class ProviderEndpointPort(CoveConfiguration? config)
{
    // Matched on the source, not on the string: the host resolves an endpoint to a source by
    // registrable domain, so two spellings of one provider are one source.
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
