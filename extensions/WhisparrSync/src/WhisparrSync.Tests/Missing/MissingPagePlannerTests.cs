using Microsoft.Extensions.Logging.Abstractions;
using WhisparrSync.Contracts;
using WhisparrSync.Missing;
using WhisparrSync.Monitoring;
using WhisparrSync.Providers;
using WhisparrSync.Tests.TestSupport;
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

        await planner.PlanAsync(Request(), Context(), NullLogger.Instance, TestCt);

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

        var view = await planner.PlanAsync(Request(), Context(), NullLogger.Instance, TestCt);

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

        var view = await planner.PlanAsync(Request(), Context(), NullLogger.Instance, TestCt);

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

        var view = await planner.PlanAsync(Request(), Context(withProvider: false), NullLogger.Instance, TestCt);

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

        await planner.PlanAsync(Request() with { MenusAlreadyHeld = true }, Context(), NullLogger.Instance, TestCt);

        Assert.Equal(0, catalogue.MenuReads);
    }

    [Fact]
    public async Task TheViewCarriesWhatTheCatalogueOfferedAsMenusAndSorts()
    {
        var catalogue = new RecordingCatalogue(ScenesNamed("a"))
        {
            Menus = [new ProviderFacetMenu("year", "Year", [new ProviderFacetValue("2024", "2024")], 1)],
        };
        var planner = PlannerOver(catalogue);

        var view = await planner.PlanAsync(Request(), Context(), NullLogger.Instance, TestCt);

        Assert.Equal("year", Assert.Single(view.Facets).Key);
        Assert.Equal("DATE", Assert.Single(view.Sorts).Value);
    }

    /// <summary>
    /// A caller naming no ordering still reads an ordered page, so the view names the ordering
    /// the provider applied rather than echoing the caller's silence back as nothing.
    /// </summary>
    [Fact]
    public async Task APageReadUnderNoNamedOrderingReportsTheProvidersOwn()
    {
        var catalogue = new RecordingCatalogue(ScenesNamed("a"));
        var planner = PlannerOver(catalogue);

        var view = await planner.PlanAsync(Request(), Context(), NullLogger.Instance, TestCt);

        Assert.Equal(catalogue.DefaultSort, view.SortInForce);
        Assert.Contains(view.Sorts, offered => offered.Value == view.SortInForce);
    }

    [Fact]
    public async Task AnOrderingTheCallerNamedIsTheOneReported()
    {
        var catalogue = new RecordingCatalogue(ScenesNamed("a"));
        var planner = PlannerOver(catalogue);

        var view = await planner.PlanAsync(
            Request() with { Sort = "TITLE:ASC" }, Context(), NullLogger.Instance, TestCt);

        Assert.Equal("TITLE:ASC", view.SortInForce);
    }

    /// <summary>Nothing was read, so nothing was ordered.</summary>
    [Fact]
    public async Task ARefusedPageNamesNoOrdering()
    {
        var catalogue = new RecordingCatalogue(ScenesNamed("a"));
        var planner = PlannerOver(catalogue, identity: null);

        var view = await planner.PlanAsync(
            Request() with { Sort = "TITLE:ASC" }, Context(), NullLogger.Instance, TestCt);

        Assert.Equal(MissingRefusalKind.NoProviderIdForEntity, view.Refusal);
        Assert.Null(view.SortInForce);
    }

    /// <summary>
    /// A generation keeping no per-scene record states that rather than offering a retry, because no
    /// retry could establish a status.
    /// </summary>
    [Fact]
    public async Task AGenerationHoldingNoStatusRoleStatesThatNothingCanEstablishOne()
    {
        var planner = PlannerOver(new RecordingCatalogue(ScenesNamed("a")));

        var view = await planner.PlanAsync(Request(), Context(withStatusRole: false), NullLogger.Instance, TestCt);

        Assert.True(view.StatusIsPermanentlyAbsent);
        Assert.False(view.StatusWasRead);
        Assert.Equal(MissingRefusalKind.WhisparrKeepsNoSceneRecords, view.Refusal);
    }

    private static MissingPageRequest Request()
        => new(
            WhisparrEntityKind.Studio,
            7,
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
            withStatusRole ? new StubStatusReading() : null,
            ExclusionReading: null);

    /// <summary>
    /// The card carries where its scene is shown, composed by the source that answered. A browser
    /// composing one would hold a pattern per source, and the wrong one on the other generation.
    /// </summary>
    [Fact]
    public async Task ACardCarriesTheAddressTheSourceNamedForItsScene()
    {
        var catalogue = new RecordingCatalogue(ScenesNamed("a", "b"))
        {
            Address = id => $"https://a.source.invalid/scenes/{id}",
        };

        var view = await PlannerOver(catalogue)
            .PlanAsync(Request(), Context(), NullLogger.Instance, TestCt);

        Assert.Equal(
            ["https://a.source.invalid/scenes/a", "https://a.source.invalid/scenes/b"],
            view.Cards.Select(card => card.SceneUrl));
    }

    /// <summary>A source that names no address leaves the card with none, rather than a guess.</summary>
    [Fact]
    public async Task ACardFromASourceThatNamesNoAddressCarriesNone()
    {
        var view = await PlannerOver(new RecordingCatalogue(ScenesNamed("a")))
            .PlanAsync(Request(), Context(), NullLogger.Instance, TestCt);

        Assert.Null(Assert.Single(view.Cards).SceneUrl);
    }

    private static List<ProviderScene> ScenesNamed(params string[] ids)
        => [.. ids.Select(id => new ProviderScene(id, id, null, null, null, null, [], []))];

    /// <summary>
    /// The page names the source that answered it, and a page that answered nothing names the
    /// source it would have read. A name held by the surface would say one provider on both
    /// generations.
    /// </summary>
    [Fact]
    public async Task EveryPageNamesTheSourceItWasReadFrom()
    {
        var catalogue = new RecordingCatalogue(ScenesNamed("a"))
        {
            Capabilities = ProviderCapabilities.ForThePornDb(new object()),
        };

        var answered = await PlannerOver(catalogue)
            .PlanAsync(Request(), Context(), NullLogger.Instance, TestCt);
        var refused = await PlannerOver(catalogue, identity: null)
            .PlanAsync(Request(), Context(), NullLogger.Instance, TestCt);

        Assert.Equal("ThePornDB", answered.ProviderName);
        Assert.Equal(MissingRefusalKind.NoProviderIdForEntity, refused.Refusal);
        Assert.Equal("ThePornDB", refused.ProviderName);
    }

    /// <summary>
    /// A fragment reaches the source under the entity the page is for, and the values it matched
    /// come back as rows a menu can offer.
    /// </summary>
    [Fact]
    public async Task AFragmentReachesTheSourceAndItsValuesComeBackAsMenuRows()
    {
        var catalogue = new RecordingCatalogue(ScenesNamed("a"))
        {
            FacetSearch = ProviderFacetSearch.Matched(
                [new ProviderFacetValue("t-1", "Anal Sex")], 64),
        };
        var planner = PlannerOver(catalogue);

        var found = await planner.SearchFacetValuesAsync(
            new MissingFacetSearchRequest(WhisparrEntityKind.Studio, 7, "tags", "ana"),
            WhisparrGeneration.V3,
            TestCt);

        Assert.Equal(MissingFacetSearchOutcome.Matched, found.Outcome);
        Assert.Equal("t-1", Assert.Single(found.Values).Value);
        Assert.Equal(64, found.ReportedValueCount);
        Assert.Equal("ana", catalogue.SearchedFor);
        Assert.Equal("tags", catalogue.SearchedFacet);
        Assert.Equal("a-studio", catalogue.SearchedEntityId);
    }

    /// <summary>
    /// A lookup that answered nothing is not an absence. Answered as a match of nothing it would
    /// state that a value the source holds does not exist, which is what a lookup is for.
    /// </summary>
    [Fact]
    public async Task ALookupThatAnsweredNothingIsHeldApartFromAMatchOfNothing()
    {
        var unread = new RecordingCatalogue(ScenesNamed("a"))
        {
            FacetSearch = ProviderFacetSearch.NotReached,
        };
        var matched = new RecordingCatalogue(ScenesNamed("a"))
        {
            FacetSearch = ProviderFacetSearch.Matched([], 0),
        };

        var refused = await PlannerOver(unread).SearchFacetValuesAsync(
            new MissingFacetSearchRequest(WhisparrEntityKind.Studio, 7, "tags", "zz"),
            WhisparrGeneration.V3,
            TestCt);
        var none = await PlannerOver(matched).SearchFacetValuesAsync(
            new MissingFacetSearchRequest(WhisparrEntityKind.Studio, 7, "tags", "zz"),
            WhisparrGeneration.V3,
            TestCt);

        Assert.Equal(MissingFacetSearchOutcome.NoAnswer, refused.Outcome);
        Assert.Equal(MissingFacetSearchOutcome.Matched, none.Outcome);
        Assert.Empty(none.Values);
    }

    /// <summary>
    /// A facet the source cannot search says so, and the surface keeps narrowing the values it
    /// already holds for that one.
    /// </summary>
    [Fact]
    public async Task AFacetTheSourceCannotSearchSaysSo()
    {
        var catalogue = new RecordingCatalogue(ScenesNamed("a"))
        {
            FacetSearch = ProviderFacetSearch.NotSearchable,
        };

        var answer = await PlannerOver(catalogue).SearchFacetValuesAsync(
            new MissingFacetSearchRequest(WhisparrEntityKind.Studio, 7, "year", "201"),
            WhisparrGeneration.V3,
            TestCt);

        Assert.Equal(MissingFacetSearchOutcome.NotSearchable, answer.Outcome);
        Assert.Empty(answer.Values);
    }

    /// <summary>An entity the source names nothing for is asked nothing, and states no absence.</summary>
    [Fact]
    public async Task AnUnresolvedIdentityAsksTheSourceNothingAndStatesNoAbsence()
    {
        var catalogue = new RecordingCatalogue(ScenesNamed("a"));

        var answer = await PlannerOver(catalogue, identity: null).SearchFacetValuesAsync(
            new MissingFacetSearchRequest(WhisparrEntityKind.Studio, 7, "tags", "ana"),
            WhisparrGeneration.V3,
            TestCt);

        Assert.Equal(MissingFacetSearchOutcome.NoAnswer, answer.Outcome);
        Assert.Null(catalogue.SearchedFor);
    }

    private static MissingPagePlanner PlannerOver(
        RecordingCatalogue catalogue,
        StubOwned? owned = null,
        string? identity = "a-studio")
        => new(
            new MissingIdentityResolver(new StubIdentities(identity), catalogue, new StubEntityNames()),
            catalogue,
            owned ?? new StubOwned(),
            new SceneStatusPort(),
            new SceneExclusionPort());

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

        public Task<IReadOnlySet<string>> ReduceHeldScenesAsync(
            Uri baseAddress,
            string apiKey,
            IReadOnlyCollection<string> foreignIds,
            CancellationToken ct)
            => throw new InvalidOperationException(
                "This surface asks about one scene at a time and never about a batch of them.");
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

        public ProviderFacetSearch FacetSearch { get; init; } = ProviderFacetSearch.NotReached;

        public string? SearchedFor { get; private set; }

        public string? SearchedFacet { get; private set; }

        public string? SearchedEntityId { get; private set; }

        public IReadOnlyList<ProviderSortOption> Sorts { get; } =
            [new ProviderSortOption("DATE", "Newest first")];

        public string DefaultSort { get; init; } = "DATE";

        /// <summary>Where this catalogue says one of its scenes is shown.</summary>
        public Func<string, string?> Address { get; init; } = _ => null;

        public ProviderCapabilitySet Capabilities { get; init; } =
            ProviderCapabilities.ForStashDb(new object());

        public string? SceneAddress(string providerSceneId) => Address(providerSceneId);

        public Task<ProviderCatalogueAnswer> ReadPageAsync(
            ProviderCatalogueRequest request, CancellationToken ct)
        {
            PageReads++;
            Requests.Add(request);
            return Task.FromResult(
                ProviderCatalogueAnswer.Answered(
                    new ProviderCataloguePage(
                        scenes,
                        catalogueSize == 0 ? scenes.Count : catalogueSize,
                        SizeIsLowerBound: false,
                        LastPage: 1,
                        rangeFrom,
                        rangeTo)));
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

        public Task<int?> ResolveNumericSceneIdAsync(string providerSceneId, CancellationToken ct)
            => Task.FromResult<int?>(null);

        public Task<IReadOnlyList<ProviderFacetMenu>> ListFacetMenusAsync(
            WhisparrEntityKind kind, string providerEntityId, CancellationToken ct)
        {
            MenuReads++;
            return Task.FromResult(Menus);
        }

        public Task<ProviderFacetSearch> SearchFacetValuesAsync(
            WhisparrEntityKind kind,
            string providerEntityId,
            string facetKey,
            string fragment,
            CancellationToken ct)
        {
            SearchedFor = fragment;
            SearchedFacet = facetKey;
            SearchedEntityId = providerEntityId;
            return Task.FromResult(FacetSearch);
        }
    }
}
