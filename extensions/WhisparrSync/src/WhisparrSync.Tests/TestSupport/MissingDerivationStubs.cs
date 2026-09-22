using WhisparrSync.Contracts;
using WhisparrSync.Missing;
using WhisparrSync.Monitoring;
using WhisparrSync.Providers;

namespace WhisparrSync.Tests.TestSupport;

// The counts are the point: a case claiming that a refusal reaches no provider asserts a zero here
// rather than an empty result, which an implementation that called and discarded would also
// produce.
internal sealed class StubProviderCatalogue(
    List<ProviderScene>? scenes = null, ProviderIdentityLookup? lookup = null) : IProviderCatalogue
{
    public int PageReads { get; private set; }

    public int SizeReads { get; private set; }

    public List<(WhisparrEntityKind Kind, string Name, IReadOnlyList<string> Aliases)> Lookups { get; }
        = [];

    public List<string> Resolutions { get; } = [];

    public IReadOnlyList<ProviderSortOption> Sorts { get; } =
        [new ProviderSortOption("DATE", "Newest first")];

    public string DefaultSort => "DATE";

    public ProviderCapabilitySet Capabilities { get; } = ProviderCapabilities.ForStashDb(new object());

    public string? SceneAddress(string providerSceneId) => null;

    public Task<ProviderCatalogueAnswer> ReadPageAsync(
        ProviderCatalogueRequest request, CancellationToken ct)
    {
        PageReads++;
        var listed = scenes ?? [];
        return Task.FromResult(
            ProviderCatalogueAnswer.Answered(
                new ProviderCataloguePage(
                    listed,
                    listed.Count,
                    SizeIsLowerBound: false,
                    LastPage: 1,
                    RangeFrom: 1,
                    RangeTo: 40)));
    }

    public Task<int?> ReadCatalogueSizeAsync(ProviderCatalogueRequest request, CancellationToken ct)
    {
        SizeReads++;
        return Task.FromResult<int?>(scenes?.Count ?? 0);
    }

    public Task<ProviderIdentityLookup> LookUpByNameAsync(
        WhisparrEntityKind kind, string name, IReadOnlyList<string> aliases, CancellationToken ct)
    {
        Lookups.Add((kind, name, aliases));
        return Task.FromResult(lookup ?? ProviderIdentityLookup.Unmatched);
    }

    // This stub names itself StashDB, which issues no number of its own for a scene. Answering one
    // would let a case pass against a provider that cannot resolve one.
    public Task<int?> ResolveNumericSceneIdAsync(string providerSceneId, CancellationToken ct)
    {
        Resolutions.Add(providerSceneId);
        return Task.FromResult<int?>(null);
    }

    // This stub names itself StashDB, which issues no number of its own for a site either.
    public Task<ProviderSiteNumber> ResolveNumericSiteIdAsync(
        string providerSiteId, CancellationToken ct)
        => Task.FromResult(ProviderSiteNumber.NotReached);

    public Task<IReadOnlyList<ProviderFacetMenu>> ListFacetMenusAsync(
        WhisparrEntityKind kind, string providerEntityId, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<ProviderFacetMenu>>([]);

    public Task<ProviderFacetSearch> SearchFacetValuesAsync(
        WhisparrEntityKind kind,
        string providerEntityId,
        string facetKey,
        string fragment,
        CancellationToken ct)
        => Task.FromResult(ProviderFacetSearch.NotReached);
}

// Held apart from StubProviderCatalogue, which answers one page and reports one. A walk over a
// catalogue is only observable where a second page exists, and a narrowing that narrows nothing
// would let a run over the whole catalogue pass for a run over the narrowed one.
internal sealed class PagedProviderCatalogue(List<ProviderScene> scenes, int perPage)
    : IProviderCatalogue
{
    public List<ProviderCatalogueRequest> Requests { get; } = [];

    public IReadOnlyList<ProviderSortOption> Sorts { get; } =
        [new ProviderSortOption("DATE", "Newest first")];

    public string DefaultSort => "DATE";

    public ProviderCapabilitySet Capabilities { get; } = ProviderCapabilities.ForStashDb(new object());

    public string? SceneAddress(string providerSceneId) => null;

    public List<ProviderScene> Matching(string? titleSearch)
        => titleSearch is null
            ? scenes
            : [.. scenes.Where(scene => scene.Title.Contains(titleSearch, StringComparison.Ordinal))];

    public Task<ProviderCatalogueAnswer> ReadPageAsync(
        ProviderCatalogueRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        Requests.Add(request);

        var matching = Matching(request.TitleSearch);
        var lastPage = Math.Max(1, (matching.Count + perPage - 1) / perPage);
        var from = (request.Page - 1) * perPage;
        var served = from >= matching.Count ? [] : matching.GetRange(from, Math.Min(perPage, matching.Count - from));

        return Task.FromResult(
            ProviderCatalogueAnswer.Answered(
                new ProviderCataloguePage(
                    served,
                    matching.Count,
                    SizeIsLowerBound: false,
                    lastPage,
                    RangeFrom: from + 1,
                    RangeTo: from + served.Count)));
    }

    public Task<int?> ReadCatalogueSizeAsync(ProviderCatalogueRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Task.FromResult<int?>(Matching(request.TitleSearch).Count);
    }

    public Task<ProviderIdentityLookup> LookUpByNameAsync(
        WhisparrEntityKind kind, string name, IReadOnlyList<string> aliases, CancellationToken ct)
        => Task.FromResult(ProviderIdentityLookup.Unmatched);

    public Task<int?> ResolveNumericSceneIdAsync(string providerSceneId, CancellationToken ct)
        => Task.FromResult<int?>(null);

    public Task<ProviderSiteNumber> ResolveNumericSiteIdAsync(
        string providerSiteId, CancellationToken ct)
        => Task.FromResult(ProviderSiteNumber.NotReached);

    public Task<IReadOnlyList<ProviderFacetMenu>> ListFacetMenusAsync(
        WhisparrEntityKind kind, string providerEntityId, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<ProviderFacetMenu>>([]);

    public Task<ProviderFacetSearch> SearchFacetValuesAsync(
        WhisparrEntityKind kind,
        string providerEntityId,
        string facetKey,
        string fragment,
        CancellationToken ct)
        => Task.FromResult(ProviderFacetSearch.NotReached);
}

internal sealed class StubEntityIdentities(string? foreignId) : IEntityIdentityPort
{
    public Task<IdentityResolution> ResolveAsync(
        WhisparrEntityKind kind, int coveId, WhisparrGeneration generation, CancellationToken ct)
        => Task.FromResult(
            foreignId is null ? IdentityResolution.Unmatched : IdentityResolution.At(foreignId));
}

internal sealed class StubOwnedScenes(params string[] owned) : IOwnedScenePort
{
    public Task<IReadOnlySet<string>> ReadOwnedAsync(
        string identityEndpoint, IReadOnlyList<string> providerSceneIds, CancellationToken ct)
        => Task.FromResult<IReadOnlySet<string>>(owned.ToHashSet(StringComparer.Ordinal));
}
