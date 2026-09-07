using WhisparrSync.Contracts;

namespace WhisparrSync.Discovery;

/// <summary>
/// Normalizes the untrusted <see cref="DiscoveryQueryRequest"/> a browser sends into the internal
/// <see cref="DiscoveryQuery"/>, and translates the sort vocabulary between the wire literals and the enum.
/// </summary>
/// <remarks>
/// Every rule here DROPS a value it cannot trust and lets the read succeed with that axis unapplied — a filter id
/// of the wrong shape, a year no release date can carry, an unrecognized sort literal. A read that refused would
/// turn a hand-edited url into an error page; a dropped axis is reported back through the capability projection
/// the response carries.
/// <para>
/// The shape checks belong to this extension, never to the provider. ThePornDB answers an unknown query parameter
/// with a 200 and ignores it (verified live — eleven plausible parameters each left the total unchanged), which
/// makes a provider 200 no evidence at all about a malformed id.
/// </para>
/// </remarks>
internal static class DiscoveryQueryGuard
{
    // Outside this span a "year" is a data error or a probe, never a release year a user means to filter on.
    private const int MinYear = 1880;
    private const int MaxYear = 2200;

    // A ThePornDB numeric id is digits only; the length bound keeps an absurd string out of a url fragment.
    private const int MaxTpdbIdLength = 12;

    // The control's own order, which the projected capability list preserves.
    private static readonly DiscoverySortMode[] SortOrder =
        [DiscoverySortMode.Newest, DiscoverySortMode.Oldest, DiscoverySortMode.Title];

    /// <summary>
    /// The trusted query for an untrusted request body. A null request, an unrecognized sort, a filter id of the
    /// wrong shape for <paramref name="isV2"/>'s provider, and an out-of-range year each fall back to the shipped
    /// default for that one dimension; the read itself always succeeds.
    /// </summary>
    public static DiscoveryQuery Normalize(DiscoveryQueryRequest? request, bool isV2)
        => request is null
            ? DiscoveryQuery.Default
            : new DiscoveryQuery(
                ParseSort(request.Sort),
                FilterId(request.StudioId, isV2),
                FilterId(request.PerformerId, isV2),
                FilterId(request.TagId, isV2),
                ClampYear(request.Year));

    /// <summary>Whether a value has StashDB's id shape — an RFC-4122 8-4-4-4-12 hex UUID.</summary>
    public static bool IsStashDbFilterId(string value) => Guid.TryParseExact(value, "D", out _);

    /// <summary>Whether a value has ThePornDB's filter-id shape — one or more ASCII digits, bounded in length.</summary>
    public static bool IsTpdbFilterId(string value)
    {
        if (value.Length is 0 or > MaxTpdbIdLength)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The camelCase wire literals for a source's declared server-side sorts, in the sort control's own order.
    /// This is the one place the <see cref="DiscoverySortMode"/> enum and the <see cref="DiscoverySortModes"/>
    /// literals are mapped, in both directions.
    /// </summary>
    public static string[] SortWireNames(IReadOnlySet<DiscoverySortMode> modes)
        => [.. SortOrder.Where(modes.Contains).Select(WireName)];

    /// <summary>
    /// The camelCase wire literals for a set of facet axes, in the control order the tab renders them in. The one
    /// place the <see cref="DiscoveryFacetAxis"/> enum and the <see cref="DiscoveryFacetAxes"/> literals are
    /// mapped, matching <see cref="SortWireNames"/>'s role on the sort vocabulary.
    /// </summary>
    public static string[] FacetWireNames(IReadOnlySet<DiscoveryFacetAxis> axes)
        => [.. FacetOrder.Where(axes.Contains).Select(WireName)];

    // The control's own order, which the projected axis lists preserve.
    private static readonly DiscoveryFacetAxis[] FacetOrder =
        [DiscoveryFacetAxis.Studio, DiscoveryFacetAxis.Performer, DiscoveryFacetAxis.Tag, DiscoveryFacetAxis.Year];

    private static string WireName(DiscoveryFacetAxis axis)
        => axis switch
        {
            DiscoveryFacetAxis.Studio => DiscoveryFacetAxes.Studio,
            DiscoveryFacetAxis.Performer => DiscoveryFacetAxes.Performer,
            DiscoveryFacetAxis.Tag => DiscoveryFacetAxes.Tag,
            _ => DiscoveryFacetAxes.Year,
        };

    // An unrecognized literal falls back to the shipped default, mirroring the client's own parseSortMode: a
    // hand-edited url yields the default view, never a 400.
    private static DiscoverySortMode ParseSort(string? value)
        => value switch
        {
            DiscoverySortModes.Oldest => DiscoverySortMode.Oldest,
            DiscoverySortModes.Title => DiscoverySortMode.Title,
            _ => DiscoverySortMode.Newest,
        };

    private static string WireName(DiscoverySortMode mode)
        => mode switch
        {
            DiscoverySortMode.Oldest => DiscoverySortModes.Oldest,
            DiscoverySortMode.Title => DiscoverySortModes.Title,
            _ => DiscoverySortModes.Newest,
        };

    private static string? FilterId(string? value, bool isV2)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        var wellShaped = isV2 ? IsTpdbFilterId(trimmed) : IsStashDbFilterId(trimmed);
        return wellShaped ? trimmed : null;
    }

    private static int? ClampYear(int? value)
        => value is { } year && year >= MinYear && year <= MaxYear ? year : null;
}
