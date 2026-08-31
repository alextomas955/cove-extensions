namespace WhisparrSync.Import;

/// <summary>
/// Whether two metadata endpoints name the same source, by the host's own rule.
/// </summary>
/// <remarks>
/// Pure. The host decides this with two private methods inside its own assembly, so an extension
/// cannot call the rule and has to carry a transcription of it. Comparing the two strings instead
/// would answer "no identity" for a video the host itself treats as identified, and would write a
/// second identity row for a source that already has one.
/// <para>
/// The host reduces a host name to its last two labels, which treats a multi-label public suffix as
/// two labels. The simplification is kept rather than corrected: the two implementations have to
/// agree about what one source is, and a more correct rule here would disagree with the host on
/// exactly the inputs the simplification covers.
/// </para>
/// </remarks>
public static class EndpointMatchGuard
{
    /// <summary>Whether <paramref name="a"/> and <paramref name="b"/> name the same source.</summary>
    /// <remarks>
    /// Two arms, in this order: the normalised strings compared case-insensitively, then the
    /// registrable domains. The second arm requires <paramref name="a"/> to have a domain at all, so
    /// a blank reaches an answer only through the first.
    /// </remarks>
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

    /// <summary>An endpoint with surrounding whitespace and any trailing separator removed.</summary>
    internal static string Normalise(string? endpoint)
        => endpoint?.Trim().TrimEnd('/') ?? string.Empty;

    /// <summary>
    /// The last two labels of <paramref name="endpoint"/>'s host, or the whole host when it has two
    /// or fewer. Blank when there is no host to read.
    /// </summary>
    /// <remarks>
    /// An input that is not an absolute URL is retried with a scheme prepended, so a bare host name
    /// answers rather than reading as blank.
    /// </remarks>
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
