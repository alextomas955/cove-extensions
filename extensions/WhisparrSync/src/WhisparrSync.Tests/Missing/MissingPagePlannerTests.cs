using WhisparrSync.Contracts;
using WhisparrSync.Missing;
using WhisparrSync.Monitoring;
using WhisparrSync.Providers;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Missing;

/// <summary>
/// The derivation: one provider read, the owned rows removed, and a status for what remains.
/// </summary>
/// <remarks>
/// The call count is asserted alongside the answer. Every value here is also producible by a shape
/// that reads several pages or walks the library, so a correct answer alone says nothing about cost.
/// </remarks>
public sealed class MissingPagePlannerTests
{
    private const string StashDb = "https://stashdb.org/graphql";

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    [Fact]
    public async Task OnePageIsOneProviderRead()
    {
        var catalogue = new RecordingCatalogue(ScenesNamed("a", "b", "c"));
        var planner = PlannerOver(catalogue);

        await planner.PlanAsync(Request(), Context(), TestCt);

        Assert.Equal(1, catalogue.PageReads);
    }

    /// <summary>
    /// A page is never topped back up, and the range stays the provider's own. Derived from the card
    /// count the count line would read one to thirty-five above a page the provider called one to
    /// forty.
    /// </summary>
    [Fact]
    public async Task OwnedScenesLeaveThePageAndTheRangeStaysTheProvidersOwn()
    {
        var scenes = ScenesNamed([.. Enumerable.Range(0, 40).Select(index => $"scene-{index}")]);
        var catalogue = new RecordingCatalogue(scenes, catalogueSize: 3941, rangeFrom: 1, rangeTo: 40);
        var owned = new StubOwned("scene-0", "scene-1", "scene-2", "scene-3", "scene-4");
        var planner = PlannerOver(catalogue, owned);

        var view = await planner.PlanAsync(Request(), Context(), TestCt);

        Assert.Equal(35, view.Cards.Count);
        Assert.Equal(1, view.RangeFrom);
        Assert.Equal(40, view.RangeTo);
        Assert.Equal(3941, view.CatalogueSize);
        Assert.Equal(1, catalogue.PageReads);
    }

    /// <summary>An entity the provider names nothing for is a refusal taken before any request.</summary>
    [Fact]
    public async Task AnUnresolvedIdentityRefusesAndAsksTheProviderNothing()
    {
        var catalogue = new RecordingCatalogue(ScenesNamed("a"));
        var planner = PlannerOver(catalogue, identity: null);

        var view = await planner.PlanAsync(Request(), Context(), TestCt);

        Assert.Equal(MissingRefusalKind.NoProviderIdForEntity, view.Refusal);
        Assert.Empty(view.Cards);
        Assert.Equal(0, catalogue.PageReads);
    }

    /// <summary>
    /// Zero is a claim and null is an abstention. A count of zero for an entity nothing could be
    /// resolved for would state that the provider lists nothing, which was never measured.
    /// </summary>
    [Fact]
    public async Task AnUnresolvableEntitysCountIsNullRatherThanZero()
    {
        var catalogue = new RecordingCatalogue(ScenesNamed("a"));
        var planner = PlannerOver(catalogue, identity: null);

        var count = await planner.CountAsync(Request(), Context(), TestCt);

        Assert.Null(count.Count);
        Assert.Equal(0, catalogue.SizeReads);
    }

    [Fact]
    public async Task AHostNamingNoProviderRefusesWithoutAskingAnything()
    {
        var catalogue = new RecordingCatalogue(ScenesNamed("a"));
        var planner = PlannerOver(catalogue);

        var view = await planner.PlanAsync(Request(), Context(withProvider: false), TestCt);

        Assert.Equal(MissingRefusalKind.NoMetadataProviderConfigured, view.Refusal);
        Assert.Equal(0, catalogue.PageReads);
    }

    /// <summary>
    /// The menus are read when the tab opens and not again per page, a menu read being one provider
    /// call per menu.
    /// </summary>
    [Fact]
    public async Task MenusTheCallerAlreadyHoldsAreNotReadAgain()
    {
        var catalogue = new RecordingCatalogue(ScenesNamed("a"));
        var planner = PlannerOver(catalogue);

        await planner.PlanAsync(Request() with { MenusAlreadyHeld = true }, Context(), TestCt);

        Assert.Equal(0, catalogue.MenuReads);
    }

    [Fact]
    public async Task TheViewCarriesWhatTheCatalogueOfferedAsMenusAndSorts()
    {
        var catalogue = new RecordingCatalogue(ScenesNamed("a"))
        {
            Menus = [new ProviderFacetMenu("year", "Year", [new ProviderFacetValue("2024", "2024")], false)],
        };
        var planner = PlannerOver(catalogue);

        var view = await planner.PlanAsync(Request(), Context(), TestCt);

        Assert.Equal("year", Assert.Single(view.Facets).Key);
        Assert.Equal("DATE", Assert.Single(view.Sorts).Value);
    }

    /// <summary>
    /// A generation keeping no per-scene record states that rather than offering a retry, because no
    /// retry could establish a status.
    /// </summary>
    [Fact]
    public async Task AGenerationHoldingNoStatusRoleStatesThatNothingCanEstablishOne()
    {
        var planner = PlannerOver(new RecordingCatalogue(ScenesNamed("a")));

        var view = await planner.PlanAsync(Request(), Context(withStatusRole: false), TestCt);

        Assert.True(view.StatusIsPermanentlyAbsent);
        Assert.False(view.StatusWasRead);
        Assert.Equal(MissingRefusalKind.WhisparrKeepsNoSceneRecords, view.Refusal);
    }

    private static MissingPageRequest Request()
        => new(
            WhisparrEntityKind.Studio,
            7,
            EntityName: null,
            Aliases: [],
            Page: 1,
            PerPage: 40,
            Sort: null,
            TitleSearch: null,
            Filters: new Dictionary<string, string>(),
            MenusAlreadyHeld: false);

    // The two absences are named rather than passed as null, so a case asking for "no provider"
    // cannot silently get the default one.
    private static MissingPageContext Context(
        bool withProvider = true, bool withStatusRole = true)
        => new(
            new Uri("http://whisparr.invalid:6969"),
            "0e2e0e2e0e2e0e2e",
            WhisparrGeneration.V3,
            withProvider ? new ResolvedProvider(StashDb, "a-key", 240) : null,
            withStatusRole ? new StubStatusReading() : null);

    private static List<ProviderScene> ScenesNamed(params string[] ids)
        => [.. ids.Select(id => new ProviderScene(id, id, null, null, null, null, [], []))];

    private static MissingPagePlanner PlannerOver(
        RecordingCatalogue catalogue,
        StubOwned? owned = null,
        string? identity = "a-studio")
        => new(
            new MissingIdentityResolver(new StubIdentities(identity), catalogue),
            catalogue,
            owned ?? new StubOwned(),
            new SceneStatusPort());

    private sealed class StubIdentities(string? foreignId) : IEntityIdentityPort
    {
        public Task<IdentityResolution> ResolveAsync(
            WhisparrEntityKind kind, int coveId, WhisparrGeneration generation, CancellationToken ct)
            => Task.FromResult(
                foreignId is null ? IdentityResolution.Unmatched : IdentityResolution.At(foreignId));
    }

    private sealed class StubOwned(params string[] owned) : IOwnedScenePort
    {
        public Task<IReadOnlySet<string>> ReadOwnedAsync(
            string identityEndpoint, IReadOnlyList<string> providerSceneIds, CancellationToken ct)
            => Task.FromResult<IReadOnlySet<string>>(owned.ToHashSet(StringComparer.Ordinal));
    }

    private sealed class StubStatusReading : IWhisparrSceneStatusReading
    {
        public Task<WhisparrResponse> ReadEntityPresenceAsync(
            Uri baseAddress,
            string apiKey,
            WhisparrEntityKind kind,
            string foreignId,
            CancellationToken ct)
            => Task.FromResult(new WhisparrResponse(404, "application/json", "{}"));

        public Task<WhisparrResponse> ReadSceneByRemoteIdAsync(
            Uri baseAddress, string apiKey, string remoteId, CancellationToken ct)
            => Task.FromResult(new WhisparrResponse(200, "application/json", "[]"));
    }

    private sealed class RecordingCatalogue(
        List<ProviderScene> scenes,
        int catalogueSize = 0,
        int rangeFrom = 1,
        int rangeTo = 40) : IProviderCatalogue
    {
        public int PageReads { get; private set; }

        public int SizeReads { get; private set; }

        public int MenuReads { get; private set; }

        public List<ProviderCatalogueRequest> Requests { get; } = [];

        public IReadOnlyList<ProviderFacetMenu> Menus { get; init; } = [];

        public IReadOnlyList<ProviderSortOption> Sorts { get; } =
            [new ProviderSortOption("DATE", "Newest first")];

        public ProviderCapabilitySet Capabilities { get; } = ProviderCapabilities.ForStashDb(new object());

        public Task<ProviderCataloguePage> ReadPageAsync(
            ProviderCatalogueRequest request, CancellationToken ct)
        {
            PageReads++;
            Requests.Add(request);
            return Task.FromResult(
                new ProviderCataloguePage(
                    scenes,
                    catalogueSize == 0 ? scenes.Count : catalogueSize,
                    SizeIsLowerBound: false,
                    LastPage: 1,
                    rangeFrom,
                    rangeTo));
        }

        public Task<int?> ReadCatalogueSizeAsync(
            ProviderCatalogueRequest request, CancellationToken ct)
        {
            SizeReads++;
            Requests.Add(request);
            return Task.FromResult<int?>(catalogueSize);
        }

        public Task<ProviderIdentityLookup> LookUpByNameAsync(
            WhisparrEntityKind kind,
            string name,
            IReadOnlyList<string> aliases,
            CancellationToken ct)
            => Task.FromResult(ProviderIdentityLookup.Unmatched);

        public Task<IReadOnlyList<ProviderFacetMenu>> ListFacetMenusAsync(
            WhisparrEntityKind kind, string providerEntityId, CancellationToken ct)
        {
            MenuReads++;
            return Task.FromResult(Menus);
        }
    }
}
