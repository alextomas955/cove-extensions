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

/// <summary>What a provider answered when one page of its catalogue was asked for.</summary>
/// <remarks>
/// No page means nothing arrived and nothing is claimed about the catalogue. A page listing no
/// scenes means the provider answered and lists nothing.
/// </remarks>
public sealed record ProviderCatalogueAnswer
{
    private ProviderCatalogueAnswer(ProviderCataloguePage? page) => Page = page;

    /// <summary>The page the provider served, or null where it served none.</summary>
    public ProviderCataloguePage? Page { get; }

    /// <summary>The provider was not reached, or refused.</summary>
    public static ProviderCatalogueAnswer NotReached { get; } = new((ProviderCataloguePage?)null);

    /// <summary>The provider served <paramref name="page"/>.</summary>
    public static ProviderCatalogueAnswer Answered(ProviderCataloguePage page) => new(page);
}

/// <summary>One value a provider's facet menu offers.</summary>
/// <param name="Value">The opaque string the provider itself issued.</param>
/// <param name="Label">How the value reads.</param>
public sealed record ProviderFacetValue(string Value, string Label);

/// <summary>One facet menu a provider fills.</summary>
/// <param name="Key">The opaque key the provider itself issued.</param>
/// <param name="Label">How the menu reads.</param>
/// <param name="Values">The values offered, covering the whole catalogue.</param>
/// <param name="ReportedValueCount">
/// How many values the provider says the menu holds, however many <paramref name="Values"/> carries.
/// A count equal to the number of values carried is a whole menu.
/// </param>
public sealed record ProviderFacetMenu(
    string Key,
    string Label,
    IReadOnlyList<ProviderFacetValue> Values,
    int ReportedValueCount);

/// <summary>What a provider answered when one facet's values were searched.</summary>
/// <remarks>
/// Three answers and no fourth. A search that matched nothing carries an empty list, which is a
/// measurement; a read that answered nothing carries no list at all and measures nothing. A facet
/// the provider has no value list to search is a third answer rather than a flag a caller tests
/// before asking.
/// </remarks>
public sealed record ProviderFacetSearch
{
    private ProviderFacetSearch(
        IReadOnlyList<ProviderFacetValue>? values, int reportedValueCount, bool isSearchable)
    {
        Values = values;
        ReportedValueCount = reportedValueCount;
        IsSearchable = isSearchable;
    }

    /// <summary>The values the provider matched, or null where it matched none.</summary>
    public IReadOnlyList<ProviderFacetValue>? Values { get; }

    /// <summary>
    /// How many values the provider says match, however many <see cref="Values"/> carries.
    /// </summary>
    public int ReportedValueCount { get; }

    /// <summary>The provider searches this facet at all.</summary>
    public bool IsSearchable { get; }

    /// <summary>The provider was not reached, or refused.</summary>
    public static ProviderFacetSearch NotReached { get; } = new(null, 0, true);

    /// <summary>The provider holds no value list it can search for this facet.</summary>
    public static ProviderFacetSearch NotSearchable { get; } = new(null, 0, false);

    /// <summary>The provider matched <paramref name="values"/>, of <paramref name="reported"/>.</summary>
    public static ProviderFacetSearch Matched(
        IReadOnlyList<ProviderFacetValue> values, int reported) => new(values, reported, true);
}

/// <summary>One ordering a provider offers.</summary>
/// <param name="Value">The opaque string the provider itself issued.</param>
/// <param name="Label">How the option reads.</param>
public sealed record ProviderSortOption(string Value, string Label);

/// <summary>What a provider is known to call one entity, or why it has no name for it.</summary>
/// <remarks>
/// A lookup that never reached the provider names no entity and states no absence, and the two are
/// held apart here so an implementation cannot answer one for the other. Two exact matches leaves
/// the choice to match order, so ambiguity is answered as itself and counts as no identifier; a
/// near match is a wrong answer under a name a reader would read as right.
/// </remarks>
public sealed record ProviderIdentityLookup
{
    private ProviderIdentityLookup(string? providerEntityId, bool isAmbiguous, bool wasReached)
    {
        ProviderEntityId = providerEntityId;
        IsAmbiguous = isAmbiguous;
        WasReached = wasReached;
    }

    /// <summary>The identifier the provider issued, or null.</summary>
    public string? ProviderEntityId { get; }

    /// <summary>Several entities matched exactly.</summary>
    public bool IsAmbiguous { get; }

    /// <summary>The provider answered, whatever it answered.</summary>
    public bool WasReached { get; }

    /// <summary>The provider was not reached, or refused.</summary>
    public static ProviderIdentityLookup NotReached { get; } = new(null, false, false);

    /// <summary>The provider names no entity matching exactly.</summary>
    public static ProviderIdentityLookup Unmatched { get; } = new(null, false, true);

    /// <summary>The provider names several entities matching exactly.</summary>
    public static ProviderIdentityLookup Ambiguous { get; } = new(null, true, true);

    /// <summary>The provider names exactly one entity, as <paramref name="providerEntityId"/>.</summary>
    public static ProviderIdentityLookup Matched(string providerEntityId)
        => new(providerEntityId, false, true);
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

    /// <summary>The ordering this provider reads under when a caller names none.</summary>
    /// <remarks>
    /// One of <see cref="Sorts"/>, because a page is always served in some order and a caller that
    /// named none still has one in force. The provider states it rather than a caller assuming it.
    /// </remarks>
    string DefaultSort { get; }

    /// <summary>What this provider can honour.</summary>
    /// <remarks>
    /// A capability the provider lacks holds no role, so a sort option, a facet or a year filter it
    /// cannot honour is an absent registration rather than a value a caller has to test for.
    /// </remarks>
    ProviderCapabilitySet Capabilities { get; }

    /// <summary>
    /// Where this provider shows the scene <paramref name="providerSceneId"/> names, or null where
    /// it publishes no address a reader can open.
    /// </summary>
    /// <remarks>
    /// Composed here rather than in the browser, because the pattern belongs to the provider and a
    /// browser composing one would hold a copy per source. A provider whose public address cannot
    /// be composed from the identifier it issued answers null, and the card that carries it is then
    /// not a link at all.
    /// </remarks>
    string? SceneAddress(string providerSceneId);

    /// <summary>One page of the catalogue <paramref name="request"/> names.</summary>
    /// <remarks>
    /// A read that answered nothing carries no page, so a failure is stated at this seam rather than
    /// encoded as a catalogue listing nothing.
    /// </remarks>
    Task<ProviderCatalogueAnswer> ReadPageAsync(
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
    /// matches on them and ignored by one that does not. A lookup that did not reach the provider
    /// carries no name and states no absence, so a refusal is not answered as an entity the
    /// provider has no name for.
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

    /// <summary>
    /// The values of the facet <paramref name="facetKey"/> names that match
    /// <paramref name="fragment"/>, for the <paramref name="kind"/> entity
    /// <paramref name="providerEntityId"/> names.
    /// </summary>
    /// <remarks>
    /// Asked of the provider, so a value the menu was never handed is reachable. Bounded to one page
    /// of values, and the provider's own count of the matches is carried beside them so a bound can
    /// still be stated.
    /// <para>
    /// A facet this provider derives rather than lists answers that it cannot be searched, and the
    /// caller keeps narrowing the values it holds for that one.
    /// </para>
    /// </remarks>
    Task<ProviderFacetSearch> SearchFacetValuesAsync(
        WhisparrEntityKind kind,
        string providerEntityId,
        string facetKey,
        string fragment,
        CancellationToken ct);
}
