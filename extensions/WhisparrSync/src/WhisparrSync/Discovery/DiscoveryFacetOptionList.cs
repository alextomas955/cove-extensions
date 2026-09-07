using System.Globalization;
using WhisparrSync.Contracts;

namespace WhisparrSync.Discovery;

/// <summary>
/// Builds a facet axis's selectable <see cref="FacetOption"/> list from the {id, label} pairs a catalogue page
/// carries. Pure — no I/O, no provider knowledge; each source projects its own row shape into pairs and this
/// decides distinctness and order.
/// </summary>
/// <remarks>
/// Distinctness is by LABEL, case-insensitively: two provider ids sharing a display name collapse to one option
/// carrying the first id encountered. That collapse already happens on the client, which derives the same lists
/// from row names alone, and its cost is that only one of the two can be narrowed on by id.
/// </remarks>
internal static class DiscoveryFacetOptionList
{
    /// <summary>
    /// The distinct options in <paramref name="pairs"/>, ordered by label. Null when nothing survives — an axis
    /// with no options is absent, never an empty array. A pair with an empty id or an empty label is dropped:
    /// an option a filter cannot be built from is not selectable.
    /// </summary>
    /// <param name="pairs">The {id, label} pairs the page's rows carry, in encounter order.</param>
    /// <param name="descending">Reverses the label order, for an axis whose values read best newest-first.</param>
    internal static FacetOption[]? Build(IEnumerable<(string? Id, string? Label)> pairs, bool descending = false)
    {
        var byLabel = new SortedDictionary<string, FacetOption>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, label) in pairs)
        {
            if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(label))
            {
                byLabel.TryAdd(label, new FacetOption(id, label));
            }
        }

        if (byLabel.Count == 0)
        {
            return null;
        }

        return descending ? [.. byLabel.Values.Reverse()] : [.. byLabel.Values];
    }

    /// <summary>
    /// The year axis's options from a page's release dates — the leading four digits of each, most recent first.
    /// </summary>
    /// <remarks>
    /// Descending matches the client's own year ordering and the newest-first default sort, keeping the control's
    /// option order the same whichever end filled it.
    /// </remarks>
    internal static FacetOption[]? Years(IEnumerable<string?> releaseDates)
        => Build(releaseDates
            .Select(LeadingYear)
            .Where(year => year is not null)
            .Select(year => year!.Value.ToString(CultureInfo.InvariantCulture))
            .Select(year => ((string?)year, (string?)year)),
            descending: true);

    /// <summary>
    /// The leading four digits of a release date, or null when it carries none.
    /// </summary>
    /// <remarks>
    /// Neither provider guarantees a full ISO date — StashDB returns bare <c>"1970"</c> and <c>"2016-01"</c> — and
    /// the client reads a year off a row by the same leading-four-digit rule, keeping the two ends of the wire
    /// agreed on what a partial date means.
    /// </remarks>
    internal static int? LeadingYear(string? releaseDate)
        => releaseDate is { Length: >= 4 }
            && int.TryParse(releaseDate.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var year)
            ? year
            : null;
}
