namespace WhisparrSync.Identity;

/// <summary>
/// Whether two metadata endpoints name the same source, by the host's own rule.
/// </summary>
/// <remarks>
/// Pure, and a transcription of the host's rule, which lives in private methods of its own
/// assembly. Comparing the two strings instead would report no identity for a video the host treats
/// as identified, and write a second identity row for a source that already has one.
/// <para>
/// The host reduces a host name to its last two labels, which treats a multi-label public suffix as
/// two labels. The simplification is kept, not corrected: a more correct rule here would disagree
/// with the host on exactly the inputs the simplification covers.
/// </para>
/// </remarks>
public static class EndpointMatchGuard
{
    /// <summary>Whether <paramref name="a"/> and <paramref name="b"/> name the same source.</summary>
    public static bool SameSource(string? a, string? b)
    {
        if (string.Equals(Normalise(a), Normalise(b), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var domainA = RegistrableDomain(a);
        return domainA.Length > 0
            && string.Equals(domainA, RegistrableDomain(b), StringComparison.OrdinalIgnoreCase);
    }

    internal static string Normalise(string? endpoint)
        => endpoint?.Trim().TrimEnd('/') ?? string.Empty;

    // The last two labels of the endpoint's host, the whole host where it has two or fewer, or
    // blank where there is no host. An input that is not an absolute URL is retried with a scheme
    // prepended, so a bare host name answers rather than reading as blank.
    internal static string RegistrableDomain(string? endpoint)
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
        return labels.Length <= 2 ? host : $"{labels[^2]}.{labels[^1]}";
    }
}
