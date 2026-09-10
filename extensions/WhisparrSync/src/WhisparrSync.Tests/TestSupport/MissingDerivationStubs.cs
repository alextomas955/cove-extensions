using WhisparrSync.Contracts;
using WhisparrSync.Missing;
using WhisparrSync.Monitoring;
using WhisparrSync.Providers;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.TestSupport;

/// <summary>A provider catalogue that records what it was asked and answers what it was given.</summary>
/// <remarks>
/// The counts are the point: a case claiming that a refusal reaches no provider asserts a zero here
/// rather than an empty result, which an implementation that called and discarded would also produce.
/// </remarks>
internal sealed class StubProviderCatalogue(
    List<ProviderScene>? scenes = null, ProviderIdentityLookup? lookup = null) : IProviderCatalogue
{
    /// <summary>How many page reads this was asked for.</summary>
    public int PageReads { get; private set; }

    /// <summary>How many catalogue-size reads this was asked for.</summary>
    public int SizeReads { get; private set; }

    /// <summary>Every name lookup this was asked for, in order.</summary>
    public List<(WhisparrEntityKind Kind, string Name, IReadOnlyList<string> Aliases)> Lookups { get; }
        = [];

    public IReadOnlyList<ProviderSortOption> Sorts { get; } =
        [new ProviderSortOption("DATE", "Newest first")];

    public string DefaultSort => "DATE";

    public ProviderCapabilitySet Capabilities { get; } = ProviderCapabilities.ForStashDb(new object());

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

/// <summary>
/// A provider catalogue holding several pages, which narrows by title the way a source does.
/// </summary>
/// <remarks>
/// Held apart from <see cref="StubProviderCatalogue"/>, which answers one page and reports one. A
/// walk over a catalogue is only observable where a second page exists, and a narrowing that narrows
/// nothing would let a run over the whole catalogue pass for a run over the narrowed one.
/// </remarks>
internal sealed class PagedProviderCatalogue(List<ProviderScene> scenes, int perPage)
    : IProviderCatalogue
{
    /// <summary>Every page read this was asked for, in order.</summary>
    public List<ProviderCatalogueRequest> Requests { get; } = [];

    public IReadOnlyList<ProviderSortOption> Sorts { get; } =
        [new ProviderSortOption("DATE", "Newest first")];

    public string DefaultSort => "DATE";

    public ProviderCapabilitySet Capabilities { get; } = ProviderCapabilities.ForStashDb(new object());

    /// <summary>The scenes a title search leaves, in the source's own order.</summary>
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

/// <summary>An identity table holding one identifier, or none.</summary>
internal sealed class StubEntityIdentities(string? foreignId) : IEntityIdentityPort
{
    public Task<IdentityResolution> ResolveAsync(
        WhisparrEntityKind kind, int coveId, WhisparrGeneration generation, CancellationToken ct)
        => Task.FromResult(
            foreignId is null ? IdentityResolution.Unmatched : IdentityResolution.At(foreignId));
}

/// <summary>A library holding none of a page's scenes.</summary>
internal sealed class StubOwnedScenes(params string[] owned) : IOwnedScenePort
{
    public Task<IReadOnlySet<string>> ReadOwnedAsync(
        string identityEndpoint, IReadOnlyList<string> providerSceneIds, CancellationToken ct)
        => Task.FromResult<IReadOnlySet<string>>(owned.ToHashSet(StringComparer.Ordinal));
}

/// <summary>An instance answering a fixed status, counting the two reads apart.</summary>
/// <remarks>
/// The two counts are held apart because the claim they serve is about which of them was issued: a
/// kind the instance publishes no entity for has no probe to spend, and a total would hide that.
/// </remarks>
internal sealed class StubSceneStatusReading(
    int presence = 200, string sceneAnswer = "[]", bool unreachable = false)
    : IWhisparrSceneStatusReading
{
    /// <summary>How many entity probes this was asked for.</summary>
    public int PresenceReads { get; private set; }

    /// <summary>How many per-scene reads this was asked for.</summary>
    public int SceneReads { get; private set; }

    public Task<WhisparrResponse> ReadEntityPresenceAsync(
        Uri baseAddress, string apiKey, WhisparrEntityKind kind, string foreignId, CancellationToken ct)
    {
        PresenceReads++;
        return unreachable
            ? throw new HttpRequestException("The instance was not reached.")
            : Task.FromResult(new WhisparrResponse(presence, "application/json", "{}"));
    }

    public Task<WhisparrResponse> ReadSceneByRemoteIdAsync(
        Uri baseAddress, string apiKey, string remoteId, CancellationToken ct)
    {
        SceneReads++;
        return unreachable
            ? throw new HttpRequestException("The instance was not reached.")
            : Task.FromResult(new WhisparrResponse(200, "application/json", sceneAnswer));
    }
}
