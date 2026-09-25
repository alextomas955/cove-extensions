using WhisparrSync.Contracts;

namespace WhisparrSync.Providers;

/// <summary>One catalogue scene, as a provider reports it.</summary>
/// <remarks>A null member is one the provider carries no value for.</remarks>
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
public sealed record ProviderPerformer(string ProviderPerformerId, string Name, string? ImageUrl);

/// <summary>What one page of a provider's catalogue is asked for.</summary>
/// <remarks>
/// Page is counted from one. Sort, and every filter key and value, are opaque strings the provider
/// itself issued: this product composes none of its own.
/// </remarks>
public sealed record ProviderCatalogueRequest(
    WhisparrEntityKind Kind,
    string ProviderEntityId,
    int Page,
    int PerPage,
    string? Sort,
    string? TitleSearch,
    IReadOnlyDictionary<string, string> Filters);

/// <summary>One page of a provider's catalogue.</summary>
/// <remarks>
/// Scenes keep the order the provider served them. LastPage is reported by the provider, never
/// derived from CatalogueSize: a provider clamps a page number past its own ceiling and re-serves
/// the last page, so a derived count would offer pages that silently repeat.
/// </remarks>
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

    public ProviderCataloguePage? Page { get; }

    public static ProviderCatalogueAnswer NotReached { get; } = new((ProviderCataloguePage?)null);

    public static ProviderCatalogueAnswer Answered(ProviderCataloguePage page) => new(page);
}

/// <summary>One value a provider's facet menu offers.</summary>
public sealed record ProviderFacetValue(string Value, string Label);

/// <summary>One facet menu a provider fills.</summary>
/// <remarks>
/// ReportedValueCount is how many values the provider says the menu holds, which may exceed the
/// number carried. A count equal to the number carried is a whole menu.
/// </remarks>
public sealed record ProviderFacetMenu(
    string Key,
    string Label,
    IReadOnlyList<ProviderFacetValue> Values,
    int ReportedValueCount);

/// <summary>What a provider answered when one facet's values were searched.</summary>
/// <remarks>
/// Three answers. A search that matched nothing carries an empty list; a read that answered nothing
/// carries no list at all. A facet the provider has no value list to search is the third answer,
/// not a flag a caller tests before asking.
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

    public IReadOnlyList<ProviderFacetValue>? Values { get; }

    // How many values the provider says match, which may exceed the number carried.
    public int ReportedValueCount { get; }

    public bool IsSearchable { get; }

    public static ProviderFacetSearch NotReached { get; } = new(null, 0, true);

    public static ProviderFacetSearch NotSearchable { get; } = new(null, 0, false);

    public static ProviderFacetSearch Matched(
        IReadOnlyList<ProviderFacetValue> values, int reported) => new(values, reported, true);
}

/// <summary>One ordering a provider offers.</summary>
public sealed record ProviderSortOption(string Value, string Label);

/// <summary>What a provider is known to call one entity, or why it has no name for it.</summary>
/// <remarks>
/// A lookup that never reached the provider names no entity and states no absence. The two are held
/// apart so an implementation cannot answer one for the other. Two exact matches would leave the
/// choice to match order, so ambiguity is its own answer and counts as no identifier.
/// </remarks>
public sealed record ProviderIdentityLookup
{
    private ProviderIdentityLookup(string? providerEntityId, bool isAmbiguous, bool wasReached)
    {
        ProviderEntityId = providerEntityId;
        IsAmbiguous = isAmbiguous;
        WasReached = wasReached;
    }

    public string? ProviderEntityId { get; }

    public bool IsAmbiguous { get; }

    // The provider answered, whatever it answered.
    public bool WasReached { get; }

    public static ProviderIdentityLookup NotReached { get; } = new(null, false, false);

    public static ProviderIdentityLookup Unmatched { get; } = new(null, false, true);

    public static ProviderIdentityLookup Ambiguous { get; } = new(null, true, true);

    public static ProviderIdentityLookup Matched(string providerEntityId)
        => new(providerEntityId, false, true);
}

/// <summary>The number a provider issues for one site, or why it issues none.</summary>
/// <remarks>
/// A read that never arrived issues no number and states no absence. The two are held apart so an
/// implementation cannot answer one for the other. A rate-limited read is not-reached: counted as a
/// site the provider names none for, a throttled site would move silently into the column of sites
/// nothing is held for.
/// </remarks>
public sealed record ProviderSiteNumber
{
    private ProviderSiteNumber(int? number, bool wasReached)
    {
        Number = number;
        WasReached = wasReached;
    }

    public int? Number { get; }

    // The provider answered, whatever it answered.
    public bool WasReached { get; }

    public static ProviderSiteNumber NotReached { get; } = new(null, false);

    public static ProviderSiteNumber NamesNone { get; } = new(null, true);

    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="number"/> is not positive. Zero is no identifier on either provider, and a
    /// caller handed one would address a row by it.
    /// </exception>
    public static ProviderSiteNumber Numbered(int number)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(number);
        return new ProviderSiteNumber(number, true);
    }
}

/// <summary>The metadata provider Cove is configured with, as this product reads it.</summary>
/// <remarks>
/// No member takes a caller-supplied path, verb or query string, so aiming the stored credential at
/// an address of someone's choosing is not expressible rather than being a value a validation step
/// has to refuse.
/// </remarks>
public interface IProviderCatalogue
{
    /// <summary>The orderings this provider offers, in the order they are shown.</summary>
    IReadOnlyList<ProviderSortOption> Sorts { get; }

    /// <summary>The ordering this provider reads under when a caller names none.</summary>
    /// <remarks>One of <see cref="Sorts"/>, stated by the provider rather than assumed.</remarks>
    string DefaultSort { get; }

    /// <summary>What this provider is called, for a reader being told where a page came from.</summary>
    string ProviderName { get; }

    /// <summary>
    /// Where this provider shows the scene <paramref name="providerSceneId"/> names, or null where
    /// no public address can be composed from the identifier the provider issued.
    /// </summary>
    string? SceneAddress(string providerSceneId);

    /// <summary>One page of the catalogue <paramref name="request"/> names.</summary>
    Task<ProviderCatalogueAnswer> ReadPageAsync(
        ProviderCatalogueRequest request, CancellationToken ct);

    /// <summary>
    /// How many scenes the provider lists for the catalogue <paramref name="request"/> names, or null
    /// where it could not be answered.
    /// </summary>
    /// <remarks>
    /// Null and zero are different answers. Zero is a catalogue the provider lists nothing in; null
    /// is no measurement at all.
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
    /// The provider's own numeric id for the scene <paramref name="providerSceneId"/> names, or null
    /// where it has none for it.
    /// </summary>
    /// <remarks>
    /// One read per scene, nothing held between calls: a cache here would answer for a source the
    /// host was reconfigured away from. Null and a refusal are not distinguished, so a scene reads
    /// as unnumbered in both cases.
    /// </remarks>
    Task<int?> ResolveNumericSceneIdAsync(string providerSceneId, CancellationToken ct);

    /// <summary>
    /// The provider's own numeric id for the site <paramref name="providerSiteId"/> names.
    /// </summary>
    /// <remarks>
    /// One read per site, nothing held between calls: a cache here would answer for a source the
    /// host was reconfigured away from. A provider that issues no number of its own answers
    /// <c>ProviderSiteNumber.None</c> without sending anything.
    /// </remarks>
    Task<ProviderSiteNumber> ResolveNumericSiteIdAsync(string providerSiteId, CancellationToken ct);

    /// <summary>
    /// The facet menus this provider fills for the <paramref name="kind"/> entity
    /// <paramref name="providerEntityId"/> names.
    /// </summary>
    /// <remarks>
    /// Asked of the provider, so the values cover the whole catalogue rather than the scenes one
    /// page happened to carry.
    /// </remarks>
    Task<IReadOnlyList<ProviderFacetMenu>> ListFacetMenusAsync(
        WhisparrEntityKind kind, string providerEntityId, CancellationToken ct);

    /// <summary>
    /// The values of the facet <paramref name="facetKey"/> names that match
    /// <paramref name="fragment"/>, for the <paramref name="kind"/> entity
    /// <paramref name="providerEntityId"/> names.
    /// </summary>
    /// <remarks>
    /// Asked of the provider, so a value the menu was never handed is reachable. Bounded to one
    /// page of values, with the provider's own count of the matches carried beside them. A facet
    /// this provider derives rather than lists answers that it cannot be searched, and the caller
    /// keeps narrowing the values it already holds.
    /// </remarks>
    Task<ProviderFacetSearch> SearchFacetValuesAsync(
        WhisparrEntityKind kind,
        string providerEntityId,
        string facetKey,
        string fragment,
        CancellationToken ct);
}
