using WhisparrSync.Contracts;
using WhisparrSync.Providers;

namespace WhisparrSync.Tests.TestSupport;

// An identifier this was not given an answer for throws rather than answering null, so a pass that
// asked about the wrong scene cannot pass. Null is an answer here and is configured explicitly: it
// is the provider naming no number for a scene it does know about. The capability set is a real
// provider's, so a case cannot ask for a combination no provider has. Every other member of the
// seam throws: one answering an empty page would let a pass reaching the wrong seam look like one
// that found nothing.
internal sealed class RecordingProviderCatalogue : IProviderCatalogue
{
    private readonly Dictionary<string, int?> _numbers;

    internal RecordingProviderCatalogue(IReadOnlyDictionary<string, int?> numbers)
    {
        ArgumentNullException.ThrowIfNull(numbers);
        _numbers = new Dictionary<string, int?>(numbers, StringComparer.Ordinal);
    }

    public List<string> Resolved { get; } = [];

    // Set for a case whose subject is a provider that stopped answering part way through a site. The
    // call is still recorded, because what the pass asked about is the fact under test. A factory
    // rather than an instance, so a case can also stop the run at the moment the read is made and
    // raise the shape that stop arrives in.
    public Func<Exception>? Unreachable { get; set; }

    public string ProviderName => "StashDB";

    public IReadOnlyList<ProviderSortOption> Sorts => throw Unasked(nameof(Sorts));

    public string DefaultSort => throw Unasked(nameof(DefaultSort));

    public Task<int?> ResolveNumericSceneIdAsync(string providerSceneId, CancellationToken ct)
    {
        Resolved.Add(providerSceneId);

        if (Unreachable is { } raised)
        {
            throw raised();
        }

        return _numbers.TryGetValue(providerSceneId, out var number)
            ? Task.FromResult(number)
            : throw new InvalidOperationException(
                $"Unexpected scene resolution: {providerSceneId}. Configure its answer explicitly.");
    }

    public Task<ProviderSiteNumber> ResolveNumericSiteIdAsync(
        string providerSiteId, CancellationToken ct)
        => throw Unasked(nameof(ResolveNumericSiteIdAsync));

    public string? SceneAddress(string providerSceneId) => throw Unasked(nameof(SceneAddress));

    public Task<ProviderCatalogueAnswer> ReadPageAsync(
        ProviderCatalogueRequest request, CancellationToken ct)
        => throw Unasked(nameof(ReadPageAsync));

    public Task<int?> ReadCatalogueSizeAsync(ProviderCatalogueRequest request, CancellationToken ct)
        => throw Unasked(nameof(ReadCatalogueSizeAsync));

    public Task<ProviderIdentityLookup> LookUpByNameAsync(
        WhisparrEntityKind kind, string name, IReadOnlyList<string> aliases, CancellationToken ct)
        => throw Unasked(nameof(LookUpByNameAsync));

    public Task<IReadOnlyList<ProviderFacetMenu>> ListFacetMenusAsync(
        WhisparrEntityKind kind, string providerEntityId, CancellationToken ct)
        => throw Unasked(nameof(ListFacetMenusAsync));

    public Task<ProviderFacetSearch> SearchFacetValuesAsync(
        WhisparrEntityKind kind,
        string providerEntityId,
        string facetKey,
        string fragment,
        CancellationToken ct)
        => throw Unasked(nameof(SearchFacetValuesAsync));

    private static InvalidOperationException Unasked(string member)
        => new($"Unexpected provider call: {member}. Nothing on this path reads it.");
}
