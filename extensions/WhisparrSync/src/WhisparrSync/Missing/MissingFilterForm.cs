namespace WhisparrSync.Missing;

/// <summary>How facet selections travel between the browser and this product.</summary>
/// <remarks>
/// One encoding, declared here and mirrored by the surface's own URL module. A key and value that
/// serialise one way in the browser and parse another way here answer an unfiltered page that looks
/// correct, so the form is pinned by a test on both sides rather than described in prose.
/// <para>
/// Each pair is its own percent-encoded segment, so a provider value carrying either separator
/// survives the round trip.
/// </para>
/// </remarks>
internal static class MissingFilterForm
{
    /// <summary>The separator between one selection and the next.</summary>
    internal const char PairSeparator = ',';

    /// <summary>The separator between a key and its value.</summary>
    internal const char KeySeparator = ':';

    /// <summary>The selections <paramref name="raw"/> carries.</summary>
    /// <remarks>
    /// A malformed segment is skipped rather than refused. A shared link is edited by hand, and a
    /// broken one should narrow by what it does say instead of answering an error.
    /// </remarks>
    internal static IReadOnlyDictionary<string, string> Read(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var filters = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in raw.Split(PairSeparator))
        {
            var at = pair.IndexOf(KeySeparator, StringComparison.Ordinal);
            if (at <= 0)
            {
                continue;
            }

            var key = Uri.UnescapeDataString(pair[..at]);
            var value = Uri.UnescapeDataString(pair[(at + 1)..]);
            if (key.Length > 0 && value.Length > 0)
            {
                filters[key] = value;
            }
        }

        return filters;
    }

    /// <summary>How <paramref name="filters"/> is written, in the surface's own order.</summary>
    /// <remarks>
    /// Sorted by key, so one selection produces one address however the map was built up.
    /// </remarks>
    internal static string Write(IReadOnlyDictionary<string, string> filters)
    {
        ArgumentNullException.ThrowIfNull(filters);

        return string.Join(
            PairSeparator,
            filters
                .Where(pair => pair.Key.Length > 0 && pair.Value.Length > 0)
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair =>
                    $"{Uri.EscapeDataString(pair.Key)}{KeySeparator}{Uri.EscapeDataString(pair.Value)}"));
    }
}
