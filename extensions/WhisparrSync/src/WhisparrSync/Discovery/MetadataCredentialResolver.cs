namespace WhisparrSync.Discovery;

/// <summary>
/// A metadata server projected from Cove's <c>Scraping.MetadataServers</c> — the neutral resolver input. Declared
/// extension-local (referencing no Cove type) so <see cref="MetadataCredentialResolver"/> and its L0 test stay in
/// the bare-CI compile leg.
/// </summary>
internal sealed record MetadataServerCandidate(string Endpoint, string ApiKey, int MaxRequestsPerMinute);

/// <summary>
/// A resolved outbound credential for a direct-metadata read: the endpoint + key to call plus the host
/// rate-limit hint, all carried from the matching Cove metadata server.
/// </summary>
internal sealed record ResolvedMetadataCredential(string Endpoint, string ApiKey, int? MaxRequestsPerMinute);

/// <summary>
/// Pure credential resolution for a direct-metadata provider: reuse the credential Cove already stores
/// for the matching metadata server — Cove is the SOLE source, there is no extension-side key. No I/O — the
/// caller projects Cove's config into <see cref="MetadataServerCandidate"/>s and executes the decision.
/// </summary>
internal static class MetadataCredentialResolver
{
    /// <summary>
    /// Resolves the credential for <paramref name="targetEndpoint"/> from Cove's configured metadata servers:
    /// the first <paramref name="coveServers"/> entry whose endpoint reduces to the same registrable domain AND
    /// has a non-empty key (carrying its own endpoint/key/rpm), else null.
    /// </summary>
    public static ResolvedMetadataCredential? Resolve(
        IReadOnlyList<MetadataServerCandidate> coveServers, string targetEndpoint)
    {
        var targetDomain = GetRegistrableDomain(targetEndpoint);
        if (targetDomain.Length == 0)
        {
            return null;
        }

        foreach (var server in coveServers)
        {
            if (string.IsNullOrEmpty(server.ApiKey))
            {
                continue;
            }

            var domain = GetRegistrableDomain(server.Endpoint);
            if (domain.Length > 0 && string.Equals(domain, targetDomain, StringComparison.OrdinalIgnoreCase))
            {
                return new ResolvedMetadataCredential(server.Endpoint, server.ApiKey, server.MaxRequestsPerMinute);
            }
        }

        return null;
    }

    // Mirrors Cove's MetadataServerService.GetRegistrableDomain: the last two host labels, lowercased, tolerating
    // a scheme-less bare host. Empty on an unparseable input so a bad endpoint fails closed (empty never matches).
    private static string GetRegistrableDomain(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return string.Empty;
        }

        var trimmed = endpoint.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
        {
            Uri.TryCreate("https://" + trimmed, UriKind.Absolute, out uri);
        }

        var host = uri?.Host;
        if (string.IsNullOrEmpty(host))
        {
            return string.Empty;
        }

        var labels = host.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var domain = labels.Length <= 2 ? host : $"{labels[^2]}.{labels[^1]}";
        return domain.ToLowerInvariant();
    }
}
