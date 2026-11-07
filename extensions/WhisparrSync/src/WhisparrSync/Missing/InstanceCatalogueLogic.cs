using System.Globalization;
using WhisparrSync.Providers;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Missing;

/// <summary>One page of an entity's catalogue, as the instance's own scene list pages into.</summary>
internal sealed record InstanceCataloguePage(
    IReadOnlyList<WhisparrCatalogueScene> Scenes,
    int CatalogueSize,
    int LastPage,
    int RangeFrom,
    int RangeTo);

/// <summary>
/// Turns the scenes the instance lists for one entity into the page a reader asked for.
/// </summary>
/// <remarks>
/// Everything here is arithmetic over a list already in hand, so narrowing, ordering and paging cost
/// no request. The list is one entity's catalogue and is bounded by that entity, never by the
/// library.
/// </remarks>
internal static class InstanceCatalogueLogic
{
    internal const string NewestFirst = "release-desc";
    internal const string OldestFirst = "release-asc";
    internal const string TitleAscending = "title-asc";

    /// <summary>The facet key a performer narrowing travels under.</summary>
    internal const string PerformerFacet = "performer";

    /// <summary>The facet key a tag narrowing travels under.</summary>
    internal const string TagFacet = "tag";

    /// <summary>The orderings this source offers, which it applies itself.</summary>
    internal static IReadOnlyList<ProviderSortOption> Sorts { get; } =
    [
        new ProviderSortOption(NewestFirst, "Newest first"),
        new ProviderSortOption(OldestFirst, "Oldest first"),
        new ProviderSortOption(TitleAscending, "Title A-Z"),
    ];

    /// <summary>One page of the catalogue, after narrowing and ordering.</summary>
    internal static InstanceCataloguePage PageOf(
        IReadOnlyList<WhisparrCatalogueScene> scenes,
        int page,
        int perPage,
        string? sort,
        string? titleSearch,
        IReadOnlyDictionary<string, string> filters)
    {
        ArgumentNullException.ThrowIfNull(scenes);
        ArgumentNullException.ThrowIfNull(filters);

        var narrowed = Ordered(Narrowed(scenes, titleSearch, filters), sort);
        var size = narrowed.Count;

        // Counted from one, and a page past the end answers empty rather than the last page again:
        // a pager that re-serves a page silently repeats scenes a reader has already acted on.
        var bounded = Math.Max(1, perPage);
        var lastPage = size == 0 ? 1 : (size + bounded - 1) / bounded;
        var at = Math.Max(1, page);
        var skipped = (at - 1) * bounded;

        var taken = skipped >= size
            ? []
            : narrowed.Skip(skipped).Take(bounded).ToArray();

        return new InstanceCataloguePage(
            taken,
            size,
            lastPage,
            taken.Length == 0 ? 0 : skipped + 1,
            skipped + taken.Length);
    }

    /// <summary>
    /// The facet menus this catalogue fills, read off the scenes themselves.
    /// </summary>
    /// <remarks>
    /// The values are every value the entity's own catalogue carries, so a menu is whole rather than
    /// a page of one, and a facet the catalogue carries no value for is not offered at all.
    /// </remarks>
    internal static IReadOnlyList<ProviderFacetMenu> FacetsOf(
        IReadOnlyList<WhisparrCatalogueScene> scenes)
    {
        ArgumentNullException.ThrowIfNull(scenes);

        var performers = new Dictionary<string, string>(StringComparer.Ordinal);
        var tags = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var scene in scenes)
        {
            foreach (var performer in scene.Performers)
            {
                performers.TryAdd(performer.ForeignId, performer.Name);
            }

            foreach (var tag in scene.Tags)
            {
                tags.Add(tag);
            }
        }

        var menus = new List<ProviderFacetMenu>(2);
        if (performers.Count > 0)
        {
            var values = performers
                .OrderBy(entry => entry.Value, StringComparer.OrdinalIgnoreCase)
                .Select(entry => new ProviderFacetValue(entry.Key, entry.Value))
                .ToArray();
            menus.Add(new ProviderFacetMenu(PerformerFacet, "Performers", values, values.Length));
        }

        if (tags.Count > 0)
        {
            var values = tags.Select(tag => new ProviderFacetValue(tag, tag)).ToArray();
            menus.Add(new ProviderFacetMenu(TagFacet, "Tags", values, values.Length));
        }

        return menus;
    }

    private static List<WhisparrCatalogueScene> Narrowed(
        IReadOnlyList<WhisparrCatalogueScene> scenes,
        string? titleSearch,
        IReadOnlyDictionary<string, string> filters)
    {
        var needle = titleSearch?.Trim();
        filters.TryGetValue(PerformerFacet, out var performer);
        filters.TryGetValue(TagFacet, out var tag);

        var kept = new List<WhisparrCatalogueScene>(scenes.Count);
        foreach (var scene in scenes)
        {
            if (needle is { Length: > 0 }
                && !scene.Title.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (performer is { Length: > 0 }
                && !scene.Performers.Any(
                    named => string.Equals(named.ForeignId, performer, StringComparison.Ordinal)))
            {
                continue;
            }

            if (tag is { Length: > 0 }
                && !scene.Tags.Any(
                    named => string.Equals(named, tag, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            kept.Add(scene);
        }

        return kept;
    }

    // A scene the instance carries no date for sorts last under both date orderings rather than
    // first under one of them, so an undated scene never leads a page.
    private static List<WhisparrCatalogueScene> Ordered(
        List<WhisparrCatalogueScene> scenes, string? sort)
        => sort switch
        {
            OldestFirst =>
            [
                .. scenes
                    .OrderBy(scene => DateOf(scene) is null)
                    .ThenBy(scene => DateOf(scene))
                    .ThenBy(scene => scene.Title, StringComparer.OrdinalIgnoreCase),
            ],
            TitleAscending =>
            [
                .. scenes.OrderBy(scene => scene.Title, StringComparer.OrdinalIgnoreCase),
            ],
            _ =>
            [
                .. scenes
                    .OrderBy(scene => DateOf(scene) is null)
                    .ThenByDescending(scene => DateOf(scene))
                    .ThenBy(scene => scene.Title, StringComparer.OrdinalIgnoreCase),
            ],
        };

    private static DateOnly? DateOf(WhisparrCatalogueScene scene)
        => DateOnly.TryParse(
            scene.ReleaseDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;
}
