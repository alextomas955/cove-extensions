using Microsoft.Extensions.Logging.Abstractions;
using WhisparrSync.Contracts;
using WhisparrSync.Missing;
using WhisparrSync.Monitoring;
using WhisparrSync.Providers;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Missing;

public sealed class MissingPagePlannerTests
{
    private const string StashDb = "https://stashdb.org/graphql";

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    [Fact]
    public async Task OnePageIsOneInstanceRead()
    {
        var instance = new RecordingInstance(ScenesNamed("a", "b", "c"));

        await PlannerOver()
            .PlanAsync(Request(), Context(instance), NullLogger.Instance, TestCt);

        Assert.Equal(1, instance.Reads);
    }

    // The count beside the tab and the page under it are the same read, so opening a tab costs the
    // instance one request rather than two.
    [Fact]
    public async Task TheCountAndThePageShareOneRead()
    {
        var instance = new RecordingInstance(ScenesNamed("a", "b"));
        var planner = PlannerOver();

        await planner.CountAsync(Request(), Context(instance), TestCt);
        await planner.PlanAsync(Request(), Context(instance), NullLogger.Instance, TestCt);

        Assert.Equal(1, instance.Reads);
    }

    // The figure is what is missing, not the size of a catalogue: what the library already holds has
    // left the set before anything is counted.
    [Fact]
    public async Task ScenesTheLibraryHoldsLeaveTheSetAndTheCount()
    {
        var instance = new RecordingInstance(
            ScenesNamed([.. Enumerable.Range(0, 40).Select(index => $"scene-{index}")]));
        var owned = new StubOwned("scene-0", "scene-1", "scene-2", "scene-3", "scene-4");
        var planner = PlannerOver(owned: owned);

        var view = await planner.PlanAsync(Request(), Context(instance), NullLogger.Instance, TestCt);
        var count = await planner.CountAsync(Request(), Context(instance), TestCt);

        Assert.Equal(35, view.Cards.Count);
        Assert.Equal(35, view.CatalogueSize);
        Assert.Equal(35, count.Count);
        Assert.Equal(1, view.RangeFrom);
        Assert.Equal(35, view.RangeTo);
    }

    // An entity the instance has never been told about is a different answer from one it holds and
    // lists nothing under.
    [Fact]
    public async Task AnEntityTheInstanceDoesNotHoldSaysSoRatherThanListingNothing()
    {
        var instance = new RecordingInstance(WhisparrCatalogueRefusal.EntityNotHeld);

        var view = await PlannerOver()
            .PlanAsync(Request(), Context(instance), NullLogger.Instance, TestCt);

        Assert.Equal(MissingRefusalKind.EntityNotInWhisparr, view.Refusal);
        Assert.Empty(view.Cards);
    }

    [Fact]
    public async Task AnInstanceThatDidNotAnswerIsHeldApartFromAnEntityItDoesNotHold()
    {
        var instance = new RecordingInstance(WhisparrCatalogueRefusal.NotReached);

        var view = await PlannerOver()
            .PlanAsync(Request(), Context(instance), NullLogger.Instance, TestCt);

        Assert.Equal(MissingRefusalKind.WhisparrCatalogueNotRead, view.Refusal);
        Assert.Empty(view.Cards);
    }

    [Fact]
    public async Task AnEmptyCatalogueIsAMeasurementAndNotARefusal()
    {
        var instance = new RecordingInstance([]);

        var view = await PlannerOver()
            .PlanAsync(Request(), Context(instance), NullLogger.Instance, TestCt);

        Assert.Equal(MissingRefusalKind.None, view.Refusal);
        Assert.Empty(view.Cards);
        Assert.Equal(0, view.CatalogueSize);
    }

    // The state is the instance's own flag on the row the card came from, so no status is asked for
    // afterwards and no card can carry one read for a different scene.
    [Fact]
    public async Task ACardCarriesTheMonitoredFlagOffItsOwnRow()
    {
        var instance = new RecordingInstance(
        [
            Scene("a", monitored: true),
            Scene("b", monitored: false),
        ]);

        var view = await PlannerOver()
            .PlanAsync(Request(), Context(instance), NullLogger.Instance, TestCt);

        Assert.Equal(
            [MissingSceneState.Monitored, MissingSceneState.Unmonitored],
            view.Cards.Select(card => card.State));
    }

    [Fact]
    public async Task AnUnresolvedIdentityRefusesAndAsksTheInstanceNothing()
    {
        var instance = new RecordingInstance(ScenesNamed("a"));

        var view = await PlannerOver(identity: null)
            .PlanAsync(Request(), Context(instance), NullLogger.Instance, TestCt);

        Assert.Equal(MissingRefusalKind.NoProviderIdForEntity, view.Refusal);
        Assert.Empty(view.Cards);
        Assert.Equal(0, instance.Reads);
    }

    [Fact]
    public async Task AnUnresolvableEntitysCountIsNullRatherThanZero()
    {
        var instance = new RecordingInstance(ScenesNamed("a"));

        var count = await PlannerOver(identity: null)
            .CountAsync(Request(), Context(instance), TestCt);

        Assert.Null(count.Count);
        Assert.Equal(0, instance.Reads);
    }

    [Fact]
    public async Task AHostNamingNoProviderRefusesWithoutAskingAnything()
    {
        var instance = new RecordingInstance(ScenesNamed("a"));

        var view = await PlannerOver()
            .PlanAsync(
                Request(), Context(instance, withProvider: false), NullLogger.Instance, TestCt);

        Assert.Equal(MissingRefusalKind.NoMetadataProviderConfigured, view.Refusal);
        Assert.Equal(0, instance.Reads);
    }

    [Fact]
    public async Task MenusComeOffTheScenesAndAreOmittedWhenTheCallerHoldsThem()
    {
        var instance = new RecordingInstance(
        [
            Scene("a", performer: "Ada Byron", tag: "Drama"),
            Scene("b", performer: "Ada Byron", tag: "Comedy"),
        ]);
        var planner = PlannerOver();

        var view = await planner.PlanAsync(Request(), Context(instance), NullLogger.Instance, TestCt);
        var held = await planner.PlanAsync(
            Request() with { MenusAlreadyHeld = true },
            Context(instance),
            NullLogger.Instance,
            TestCt);

        Assert.Equal(["performer", "tag"], view.Facets.Select(menu => menu.Key));
        Assert.Equal(["Ada Byron"], view.Facets[0].Values.Select(value => value.Label));
        Assert.Equal(["Comedy", "Drama"], view.Facets[1].Values.Select(value => value.Label));
        Assert.Empty(held.Facets);
    }

    [Fact]
    public async Task APageUnderNoNamedOrderingLeadsWithTheNewestScene()
    {
        var instance = new RecordingInstance(
        [
            Scene("older", date: "2021-01-01"),
            Scene("newer", date: "2024-06-01"),
            Scene("undated"),
        ]);

        var view = await PlannerOver()
            .PlanAsync(Request(), Context(instance), NullLogger.Instance, TestCt);

        Assert.Equal(["newer", "older", "undated"], view.Cards.Select(card => card.ProviderSceneId));
        Assert.Equal(InstanceCatalogueLogic.NewestFirst, view.SortInForce);
    }

    [Fact]
    public async Task AnOrderingTheCallerNamedIsTheOneApplied()
    {
        var instance = new RecordingInstance(
        [
            Scene("b-scene", date: "2024-06-01"),
            Scene("a-scene", date: "2021-01-01"),
        ]);

        var view = await PlannerOver().PlanAsync(
            Request() with { Sort = InstanceCatalogueLogic.TitleAscending },
            Context(instance),
            NullLogger.Instance,
            TestCt);

        Assert.Equal(["a-scene", "b-scene"], view.Cards.Select(card => card.ProviderSceneId));
        Assert.Equal(InstanceCatalogueLogic.TitleAscending, view.SortInForce);
    }

    [Fact]
    public async Task APageTurnMovesThroughTheSetWithoutRepeatingIt()
    {
        var instance = new RecordingInstance(ScenesNamed("a", "b", "c", "d", "e"));
        var planner = PlannerOver();

        var first = await planner.PlanAsync(
            Request() with { PerPage = 2 }, Context(instance), NullLogger.Instance, TestCt);
        var second = await planner.PlanAsync(
            Request() with { Page = 2, PerPage = 2 }, Context(instance), NullLogger.Instance, TestCt);
        var past = await planner.PlanAsync(
            Request() with { Page = 9, PerPage = 2 }, Context(instance), NullLogger.Instance, TestCt);

        Assert.Equal(3, first.LastPage);
        Assert.Equal(2, first.Cards.Count);
        Assert.Empty(
            first.Cards.Select(card => card.ProviderSceneId)
                .Intersect(second.Cards.Select(card => card.ProviderSceneId)));
        Assert.Empty(past.Cards);
    }

    [Fact]
    public async Task ARefusedPageNamesNoOrdering()
    {
        var instance = new RecordingInstance(ScenesNamed("a"));

        var view = await PlannerOver(identity: null).PlanAsync(
            Request() with { Sort = InstanceCatalogueLogic.TitleAscending },
            Context(instance),
            NullLogger.Instance,
            TestCt);

        Assert.Equal(MissingRefusalKind.NoProviderIdForEntity, view.Refusal);
        Assert.Null(view.SortInForce);
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

    // The absence is named rather than passed as null, so a case asking for "no provider" cannot
    // silently get the default one.
    private static MissingPageContext Context(
        IWhisparrEntityCatalogueReading instance, bool withProvider = true)
        => new(
            new WhisparrBinding(
                WhisparrGeneration.V3, new Uri("http://whisparr.invalid:6969"), "0e2e0e2e0e2e0e2e"),
            withProvider ? new ResolvedProvider(StashDb, "a-key", 240) : null,
            ExclusionReading: null,
            instance);

    [Fact]
    public async Task ACardCarriesTheAddressTheSourceNamedForItsScene()
    {
        var catalogue = new RecordingCatalogue([])
        {
            Address = id => $"https://a.source.invalid/scenes/{id}",
        };
        var instance = new RecordingInstance([Scene("a"), Scene("b")]);

        var view = await PlannerOver(catalogue)
            .PlanAsync(Request(), Context(instance), NullLogger.Instance, TestCt);

        Assert.Equal(
            ["https://a.source.invalid/scenes/a", "https://a.source.invalid/scenes/b"],
            view.Cards.Select(card => card.SceneUrl));
    }

    [Fact]
    public async Task ACardFromASourceThatNamesNoAddressCarriesNone()
    {
        var instance = new RecordingInstance(ScenesNamed("a"));

        var view = await PlannerOver(new RecordingCatalogue([]))
            .PlanAsync(Request(), Context(instance), NullLogger.Instance, TestCt);

        Assert.Null(Assert.Single(view.Cards).SceneUrl);
    }

    private static List<WhisparrCatalogueScene> ScenesNamed(params string[] ids)
        => [.. ids.Select(id => Scene(id))];

    [Fact]
    public async Task EveryPageNamesTheSourceItWasReadFrom()
    {
        var catalogue = new RecordingCatalogue([])
        {
            Capabilities = ProviderCapabilities.ForThePornDb(new object()),
        };
        var instance = new RecordingInstance(ScenesNamed("a"));

        var answered = await PlannerOver(catalogue)
            .PlanAsync(Request(), Context(instance), NullLogger.Instance, TestCt);
        var refused = await PlannerOver(catalogue, identity: null)
            .PlanAsync(Request(), Context(instance), NullLogger.Instance, TestCt);

        Assert.Equal("ThePornDB", answered.ProviderName);
        Assert.Equal(MissingRefusalKind.NoProviderIdForEntity, refused.Refusal);
        Assert.Equal("ThePornDB", refused.ProviderName);
    }

    [Fact]
    public async Task AFragmentReachesTheSourceAndItsValuesComeBackAsMenuRows()
    {
        var catalogue = new RecordingCatalogue([])
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

    [Fact]
    public async Task ALookupThatAnsweredNothingIsHeldApartFromAMatchOfNothing()
    {
        var unread = new RecordingCatalogue([])
        {
            FacetSearch = ProviderFacetSearch.NotReached,
        };
        var matched = new RecordingCatalogue([])
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

    [Fact]
    public async Task AFacetTheSourceCannotSearchSaysSo()
    {
        var catalogue = new RecordingCatalogue([])
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

    [Fact]
    public async Task AnUnresolvedIdentityAsksTheSourceNothingAndStatesNoAbsence()
    {
        var catalogue = new RecordingCatalogue([]);

        var answer = await PlannerOver(catalogue, identity: null).SearchFacetValuesAsync(
            new MissingFacetSearchRequest(WhisparrEntityKind.Studio, 7, "tags", "ana"),
            WhisparrGeneration.V3,
            TestCt);

        Assert.Equal(MissingFacetSearchOutcome.NoAnswer, answer.Outcome);
        Assert.Null(catalogue.SearchedFor);
    }

    // The instance's catalogue reaches the planner through the context, so it is not named here.
    private static MissingPagePlanner PlannerOver(
        RecordingCatalogue? catalogue = null,
        StubOwned? owned = null,
        string? identity = "a-studio")
    {
        var source = TestProviderCatalogues.Naming(catalogue ?? new RecordingCatalogue([]));
        return new MissingPagePlanner(
            new MissingIdentityResolver(
                new StubIdentities(identity), source, new StubEntityNames()),
            source,
            owned ?? new StubOwned(),
            new InstanceCatalogueCache(TimeProvider.System));
    }

    private static WhisparrCatalogueScene Scene(
        string id,
        bool monitored = false,
        string? date = null,
        string? performer = null,
        string? tag = null)
        => new(
            id,
            id,
            date,
            null,
            null,
            null,
            performer is null ? [] : [new WhisparrCataloguePerformer(performer, performer, null)],
            tag is null ? [] : [tag],
            monitored,
            HasFile: false);

    // Answers one list, or one refusal, and counts what it was asked.
    private sealed class RecordingInstance : IWhisparrEntityCatalogueReading
    {
        private readonly WhisparrEntityCatalogue _answer;

        public RecordingInstance(IReadOnlyList<WhisparrCatalogueScene> scenes)
            => _answer = WhisparrEntityCatalogue.Listing(scenes);

        public RecordingInstance(WhisparrCatalogueRefusal refusal)
            => _answer = WhisparrEntityCatalogue.Refused(refusal);

        public int Reads { get; private set; }

        public string? AskedAbout { get; private set; }

        public Task<WhisparrEntityCatalogue> ReadEntityCatalogueAsync(
            WhisparrEntityKind kind,
            string foreignId,
            CancellationToken ct)
        {
            Reads++;
            AskedAbout = foreignId;
            return Task.FromResult(_answer);
        }
    }

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

        public Task<ProviderSiteNumber> ResolveNumericSiteIdAsync(
            string providerSiteId, CancellationToken ct)
            => Task.FromResult(ProviderSiteNumber.NotReached);

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
