using WhisparrSync.Monitoring;

namespace WhisparrSync.Providers;

/// <summary>One catalogue scene, as a provider reports it.</summary>
/// <param name="ProviderSceneId">The identifier the provider issued.</param>
/// <param name="Title">The title as the provider spells it.</param>
/// <param name="ReleaseDate">The release date the provider carries, or null where it carries none.</param>
/// <param name="CoverUrl">The provider's own cover address, or null where it offers none.</param>
/// <param name="StudioName">The studio the provider names, or null where it names none.</param>
/// <param name="Description">The provider's own description, or null where it carries none.</param>
/// <param name="Performers">The performers the provider names on the scene.</param>
/// <param name="Tags">The tags the provider names on the scene.</param>
public sealed record ProviderScene(
    string ProviderSceneId,
    string Title,
    string? ReleaseDate,
    string? CoverUrl,
    string? StudioName,
    string? Description,
    IReadOnlyList<ProviderPerformer> Performers,
    IReadOnlyList<string> Tags);

/// <summary>One performer a provider names on a scene.</summary>
/// <param name="ProviderPerformerId">The identifier the provider issued.</param>
/// <param name="Name">The name as the provider spells it.</param>
/// <param name="ImageUrl">The provider's own picture, or null where it offers none.</param>
public sealed record ProviderPerformer(string ProviderPerformerId, string Name, string? ImageUrl);

/// <summary>What one page of a provider's catalogue is asked for.</summary>
/// <remarks>
/// <paramref name="Sort"/> and every filter value are opaque strings the provider itself issued.
/// Nothing outside the provider slice reads or composes one, so this product holds no model of a
/// provider's ordering or filtering vocabulary that could disagree with the provider's own.
/// </remarks>
/// <param name="Kind">Which kind of entity the catalogue is for.</param>
/// <param name="ProviderEntityId">The entity's identifier in the provider's own namespace.</param>
/// <param name="Page">Which page to read, counted from one.</param>
/// <param name="PerPage">How many scenes a page is read in.</param>
/// <param name="Sort">The ordering to read under, or null for the provider's own.</param>
/// <param name="TitleSearch">A title search over the whole catalogue, or null for none.</param>
/// <param name="Filters">Facet selections, keyed by the keys the provider itself issued.</param>
public sealed record ProviderCatalogueRequest(
    WhisparrEntityKind Kind,
    string ProviderEntityId,
    int Page,
    int PerPage,
    string? Sort,
    string? TitleSearch,
    IReadOnlyDictionary<string, string> Filters);

/// <summary>One page of a provider's catalogue.</summary>
/// <param name="Scenes">The scenes the page carries, in the order the provider served them.</param>
/// <param name="CatalogueSize">How many scenes the provider lists for the entity.</param>
/// <param name="SizeIsLowerBound">
/// <paramref name="CatalogueSize"/> is a floor rather than a count.
/// </param>
/// <param name="LastPage">
/// The last page the provider will serve. Reported by the provider rather than derived from
/// <paramref name="CatalogueSize"/>, because a provider that clamps a page number past its own
/// ceiling re-serves the last page and a derived count would offer pages that silently repeat.
/// </param>
/// <param name="RangeFrom">The first position this page covers, as the provider reports it.</param>
/// <param name="RangeTo">The last position this page covers, as the provider reports it.</param>
public sealed record ProviderCataloguePage(
    IReadOnlyList<ProviderScene> Scenes,
    int CatalogueSize,
    bool SizeIsLowerBound,
    int LastPage,
    int RangeFrom,
    int RangeTo);

/// <summary>One value a provider's facet menu offers.</summary>
/// <param name="Value">The opaque string the provider itself issued.</param>
/// <param name="Label">How the value reads.</param>
public sealed record ProviderFacetValue(string Value, string Label);

/// <summary>One facet menu a provider fills.</summary>
/// <param name="Key">The opaque key the provider itself issued.</param>
/// <param name="Label">How the menu reads.</param>
/// <param name="Values">The values offered, covering the whole catalogue.</param>
/// <param name="IsTypeAhead">
/// The menu is longer than one page, so the surface asks the provider as the user types rather than
/// rendering the values here as a fixed list.
/// </param>
public sealed record ProviderFacetMenu(
    string Key, string Label, IReadOnlyList<ProviderFacetValue> Values, bool IsTypeAhead);

/// <summary>One ordering a provider offers.</summary>
/// <param name="Value">The opaque string the provider itself issued.</param>
/// <param name="Label">How the option reads.</param>
public sealed record ProviderSortOption(string Value, string Label);

/// <summary>What a provider is known to call one entity, or why it has no name for it.</summary>
/// <remarks>
/// A three-way answer rather than a nullable identifier. Two exact matches leaves the choice to
/// match order, so ambiguity is answered as itself and counts as no identifier; a near match is a
/// wrong answer under a name a reader would read as right.
/// </remarks>
/// <param name="ProviderEntityId">The identifier the provider issued, or null.</param>
/// <param name="IsAmbiguous">Several entities matched exactly.</param>
public sealed record ProviderIdentityLookup(string? ProviderEntityId, bool IsAmbiguous)
{
    /// <summary>The provider names no entity matching exactly.</summary>
    public static ProviderIdentityLookup Unmatched { get; } = new(null, false);

    /// <summary>The provider names several entities matching exactly.</summary>
    public static ProviderIdentityLookup Ambiguous { get; } = new(null, true);

    /// <summary>The provider names exactly one entity, as <paramref name="providerEntityId"/>.</summary>
    public static ProviderIdentityLookup Matched(string providerEntityId)
        => new(providerEntityId, false);
}

/// <summary>The metadata provider Cove is configured with, as this product reads it.</summary>
/// <remarks>
/// Narrow in the same sense this product's instance client is: no member takes a caller-supplied
/// path, verb or query string, so aiming the stored credential at an address of someone's choosing
/// is not expressible rather than being a value a validation step has to refuse.
/// <para>
/// Every ordering and filter value that crosses this seam is one the provider itself issued. This
/// product composes none, so it holds no vocabulary of its own that a provider could contradict.
/// </para>
/// </remarks>
public interface IProviderCatalogue
{
    /// <summary>The orderings this provider offers, in the order they are shown.</summary>
    IReadOnlyList<ProviderSortOption> Sorts { get; }

    /// <summary>What this provider can honour.</summary>
    /// <remarks>
    /// A capability the provider lacks holds no role, so a sort option, a facet or a year filter it
    /// cannot honour is an absent registration rather than a value a caller has to test for.
    /// </remarks>
    ProviderCapabilitySet Capabilities { get; }

    /// <summary>One page of the catalogue <paramref name="request"/> names.</summary>
    /// <exception cref="HttpRequestException">The request produced no response.</exception>
    Task<ProviderCataloguePage> ReadPageAsync(
        ProviderCatalogueRequest request, CancellationToken ct);

    /// <summary>
    /// How many scenes the provider lists for the catalogue <paramref name="request"/> names, or null
    /// where it could not be answered.
    /// </summary>
    /// <remarks>
    /// Null and zero are different answers. Zero is a catalogue the provider lists nothing in; null
    /// is no measurement at all, and nothing is claimed about the provider from it.
    /// </remarks>
    Task<int?> ReadCatalogueSizeAsync(ProviderCatalogueRequest request, CancellationToken ct);

    /// <summary>
    /// What the provider calls the <paramref name="kind"/> entity named <paramref name="name"/>.
    /// </summary>
    /// <remarks>
    /// Matched exactly, never near. <paramref name="aliases"/> are offered to a provider that
    /// matches on them and ignored by one that does not.
    /// </remarks>
    Task<ProviderIdentityLookup> LookUpByNameAsync(
        WhisparrEntityKind kind, string name, IReadOnlyList<string> aliases, CancellationToken ct);

    /// <summary>
    /// The facet menus this provider fills for the <paramref name="kind"/> entity
    /// <paramref name="providerEntityId"/> names.
    /// </summary>
    /// <remarks>
    /// Filled by asking the provider, so the values cover the whole catalogue rather than the scenes
    /// one page happened to carry.
    /// </remarks>
    Task<IReadOnlyList<ProviderFacetMenu>> ListFacetMenusAsync(
        WhisparrEntityKind kind, string providerEntityId, CancellationToken ct);
}
