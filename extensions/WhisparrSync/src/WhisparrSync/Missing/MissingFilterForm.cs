namespace WhisparrSync.Missing;

// The encoding is mirrored by the surface's own URL module and pinned by a test on both sides. A
// key or value that serialises one way in the browser and parses another way here answers an
// unfiltered page that looks correct. Each pair is its own percent-encoded segment, so a value
// carrying either separator survives the round trip.
internal static class MissingFilterForm
{
    internal const char PairSeparator = ',';

    internal const char KeySeparator = ':';

    // A malformed segment is skipped rather than refused: a hand-edited link narrows by what it
    // does say instead of answering an error.
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

    // Sorted by key, so one selection produces one address however the map was built up.
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
