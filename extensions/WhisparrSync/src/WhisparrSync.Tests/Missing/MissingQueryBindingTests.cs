using Cove.Core.Auth;
using Cove.Extensions.Shared;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging.Abstractions;
using WhisparrSync.Contracts;
using WhisparrSync.Missing;
using WhisparrSync.Monitoring;
using WhisparrSync.Options;
using WhisparrSync.Providers;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Missing;

/// <summary>
/// That what the browser puts in the address reaches the provider's own request.
/// </summary>
/// <remarks>
/// Asserted against the COMPOSED provider request rather than the handler's own arguments. A route
/// that binds every value and then drops it passes an argument-level assertion and answers an
/// unfiltered page that looks entirely correct.
/// <para>
/// The filter encoding is the one the surface writes. A key and value that serialise one way in the
/// browser and parse another way here fail silently, so both directions are driven from the one
/// declaration.
/// </para>
/// </remarks>
public sealed class MissingQueryBindingTests
{
    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ThePageNumberReachesTheComposedProviderRequest()
    {
        var catalogue = await DriveAsync(page: 3);

        Assert.Equal(3, Composed(catalogue).Page);
    }

    [Fact]
    public async Task TheSortValueReachesTheComposedProviderRequest()
    {
        var catalogue = await DriveAsync(sort: "TITLE");

        Assert.Equal("TITLE", Composed(catalogue).Sort);
    }

    /// <summary>
    /// The title search narrows the whole catalogue rather than the page that loaded, which is what
    /// keeps the count line beside it truthful.
    /// </summary>
    [Fact]
    public async Task TheTitleSearchReachesTheComposedProviderRequest()
    {
        var catalogue = await DriveAsync(q: "pool");

        Assert.Equal("pool", Composed(catalogue).TitleSearch);
    }

    [Fact]
    public async Task AFacetSelectionReachesTheComposedProviderRequest()
    {
        var catalogue = await DriveAsync(filters: "performer:mia");

        Assert.Equal("mia", Composed(catalogue).Filters["performer"]);
    }

    /// <summary>
    /// The form the surface writes is the form this reads. Each pair is its own percent-encoded
    /// segment, so a provider value carrying either separator survives.
    /// </summary>
    [Theory]
    [InlineData("year", "2024")]
    [InlineData("studio", "a,b")]
    [InlineData("tag", "a:b")]
    public async Task AValueCarryingASeparatorSurvivesTheRoundTrip(string key, string value)
    {
        var written = MissingFilterForm.Write(
            new Dictionary<string, string>(StringComparer.Ordinal) { [key] = value });

        var catalogue = await DriveAsync(filters: written);

        Assert.Equal(value, Composed(catalogue).Filters[key]);
    }

    [Fact]
    public async Task SeveralSelectionsAllReachTheComposedProviderRequest()
    {
        var written = MissingFilterForm.Write(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["performer"] = "mia",
                ["year"] = "2024",
            });

        var catalogue = await DriveAsync(filters: written);

        Assert.Equal("mia", Composed(catalogue).Filters["performer"]);
        Assert.Equal("2024", Composed(catalogue).Filters["year"]);
    }

    /// <summary>The toolbar can only offer a menu the answer carries.</summary>
    [Fact]
    public async Task TheAnswerCarriesTheMenusTheCatalogueFilledAndTheSortsItOffers()
    {
        var catalogue = new RecordingCatalogue
        {
            Menus =
            [
                new ProviderFacetMenu(
                    "performer", "Performer", [new ProviderFacetValue("mia", "Mia")], 1),
            ],
        };

        var view = await PlanOverAsync(catalogue);

        var menu = Assert.Single(view.Facets);
        Assert.Equal("performer", menu.Key);
        Assert.Equal("mia", Assert.Single(menu.Values).Value);
        Assert.Equal("DATE", Assert.Single(view.Sorts).Value);
    }

    [Fact]
    public async Task ACatalogueOfferingNoMenuAnswersAnEmptyList()
    {
        var view = await PlanOverAsync(new RecordingCatalogue());

        Assert.Empty(view.Facets);
    }

    /// <summary>
    /// A page size above the bound is refused rather than clamped. Clamped silently the answer would
    /// describe a different page from the one asked for.
    /// </summary>
    [Theory]
    [InlineData(41)]
    [InlineData(1000)]
    public async Task APageSizeAboveTheBoundIsRefused(int perPage)
    {
        var answered = await WhisparrSync.ReadMissingPageAsync(
            "studio", 7, 1, perPage, null, null, null, null,
            FakePrincipalAccessor.WithPermissions(Permissions.VideosRead),
            new OptionsStore(new FakeStore()),
            new RecordingCredentialPort(),
            new RecordingWhisparrClient(new WhisparrResponse(200, "application/json", "[]")),
            new ProviderEndpointPort(null),
            PlannerOver(new RecordingCatalogue()),
            NullLogger.Instance,
            TestCt);

        Assert.IsType<BadRequest>(answered.Result);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task APageBelowOneIsRefused(int page)
    {
        var answered = await WhisparrSync.ReadMissingPageAsync(
            "studio", 7, page, 40, null, null, null, null,
            FakePrincipalAccessor.WithPermissions(Permissions.VideosRead),
            new OptionsStore(new FakeStore()),
            new RecordingCredentialPort(),
            new RecordingWhisparrClient(new WhisparrResponse(200, "application/json", "[]")),
            new ProviderEndpointPort(null),
            PlannerOver(new RecordingCatalogue()),
            NullLogger.Instance,
            TestCt);

        Assert.IsType<BadRequest>(answered.Result);
    }

    /// <summary>
    /// The typed fragment reaches the source, and the values it matched come back for the menu to
    /// offer.
    /// </summary>
    [Fact]
    public async Task AFacetFragmentReachesTheSourceThroughTheRoute()
    {
        var catalogue = new RecordingCatalogue();

        var answered = await WhisparrSync.ReadMissingFacetValuesAsync(
            "studio", 7, "tags", " ana ",
            FakePrincipalAccessor.WithPermissions(Permissions.VideosRead),
            new OptionsStore(new FakeStore()),
            PlannerOver(catalogue),
            NullLogger.Instance,
            TestCt);

        var view = Assert.IsType<Ok<MissingFacetSearchView>>(answered.Result).Value;
        Assert.NotNull(view);
        Assert.Equal(MissingFacetSearchOutcome.Matched, view.Outcome);
        Assert.Equal("t-1", Assert.Single(view.Values).Value);
        Assert.Equal(64, view.ReportedValueCount);

        // Trimmed, so a trailing space does not become a fragment the source matches nothing for.
        Assert.Equal("ana", Assert.Single(catalogue.Fragments));
    }

    /// <summary>
    /// A fragment below the bound is answered without asking the source. One character matches most
    /// of a list, so the request buys nothing and the menu keeps narrowing what it holds.
    /// </summary>
    [Theory]
    [InlineData("a")]
    [InlineData(" ")]
    [InlineData("")]
    public async Task AFragmentBelowTheBoundAsksTheSourceNothing(string fragment)
    {
        var catalogue = new RecordingCatalogue();

        var answered = await WhisparrSync.ReadMissingFacetValuesAsync(
            "studio", 7, "tags", fragment,
            FakePrincipalAccessor.WithPermissions(Permissions.VideosRead),
            new OptionsStore(new FakeStore()),
            PlannerOver(catalogue),
            NullLogger.Instance,
            TestCt);

        var view = Assert.IsType<Ok<MissingFacetSearchView>>(answered.Result).Value;
        Assert.NotNull(view);
        Assert.Equal(MissingFacetSearchOutcome.FragmentTooShort, view.Outcome);
        Assert.Empty(view.Values);
        Assert.Empty(catalogue.Fragments);
    }

    /// <summary>A caller who may not read the library is refused before anything is asked.</summary>
    [Fact]
    public async Task AFacetLookupIsRefusedWithoutTheReadPermission()
    {
        var catalogue = new RecordingCatalogue();

        var answered = await WhisparrSync.ReadMissingFacetValuesAsync(
            "studio", 7, "tags", "ana",
            FakePrincipalAccessor.WithPermissions(),
            new OptionsStore(new FakeStore()),
            PlannerOver(catalogue),
            NullLogger.Instance,
            TestCt);

        Assert.IsType<ForbiddenCode>(answered.Result);
        Assert.Empty(catalogue.Fragments);
    }

    private static ProviderCatalogueRequest Composed(RecordingCatalogue catalogue)
        => Assert.Single(catalogue.Requests);

    // The planner is driven directly rather than through the route, because the route resolves a
    // connection from stored options and the claim here is about what the query string becomes.
    private static async Task<RecordingCatalogue> DriveAsync(
        int page = 1, string? sort = null, string? q = null, string? filters = null)
    {
        var catalogue = new RecordingCatalogue();
        await PlanOverAsync(catalogue, page, sort, q, filters);
        return catalogue;
    }

    private static async Task<MissingPageView> PlanOverAsync(
        RecordingCatalogue catalogue,
        int page = 1,
        string? sort = null,
        string? q = null,
        string? filters = null)
    {
        var request = new MissingPageRequest(
            WhisparrEntityKind.Studio,
            7,
            page,
            PerPage: 40,
            sort,
            q,
            MissingFilterForm.Read(filters),
            MenusAlreadyHeld: false);

        var context = new MissingPageContext(
            new Uri("http://whisparr.invalid:6969"),
            "0e2e0e2e0e2e0e2e",
            WhisparrGeneration.V3,
            new ResolvedProvider("https://stashdb.org/graphql", "a-key", 240),
            StatusReading: null,
            ExclusionReading: null);

        return await PlannerOver(catalogue).PlanAsync(request, context, NullLogger.Instance, TestCt);
    }

    private static MissingPagePlanner PlannerOver(RecordingCatalogue catalogue)
        => new(
            new MissingIdentityResolver(new StubIdentities(), catalogue, new StubEntityNames()),
            catalogue,
            new StubOwned(),
            new SceneStatusPort(),
            new SceneExclusionPort());

    private sealed class StubIdentities : IEntityIdentityPort
    {
        public Task<IdentityResolution> ResolveAsync(
            WhisparrEntityKind kind, int coveId, WhisparrGeneration generation, CancellationToken ct)
            => Task.FromResult(IdentityResolution.At("a-studio"));
    }

    private sealed class StubOwned : IOwnedScenePort
    {
        public Task<IReadOnlySet<string>> ReadOwnedAsync(
            string identityEndpoint, IReadOnlyList<string> providerSceneIds, CancellationToken ct)
            => Task.FromResult<IReadOnlySet<string>>(new HashSet<string>(StringComparer.Ordinal));
    }

    private sealed class RecordingCatalogue : IProviderCatalogue
    {
        public List<ProviderCatalogueRequest> Requests { get; } = [];

        public List<string> Fragments { get; } = [];

        /// <summary>Every scene this was asked to resolve to a number, in order.</summary>
        public List<string> Resolutions { get; } = [];

        public IReadOnlyList<ProviderFacetMenu> Menus { get; init; } = [];

        public IReadOnlyList<ProviderSortOption> Sorts { get; } =
            [new ProviderSortOption("DATE", "Newest first")];

        public string DefaultSort { get; init; } = "DATE";

        public string? SceneAddress(string providerSceneId) => null;

        public ProviderCapabilitySet Capabilities { get; } =
            ProviderCapabilities.ForStashDb(new object());

        public Task<ProviderCatalogueAnswer> ReadPageAsync(
            ProviderCatalogueRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(
                ProviderCatalogueAnswer.Answered(
                    new ProviderCataloguePage([], 0, SizeIsLowerBound: false, 1, 1, 0)));
        }

        public Task<int?> ReadCatalogueSizeAsync(
            ProviderCatalogueRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult<int?>(0);
        }

        public Task<ProviderIdentityLookup> LookUpByNameAsync(
            WhisparrEntityKind kind,
            string name,
            IReadOnlyList<string> aliases,
            CancellationToken ct)
            => Task.FromResult(ProviderIdentityLookup.Unmatched);

        // This stub names itself StashDB, which issues no number of its own for a scene, so the ask
        // is recorded and answered null rather than answered with a number nothing measured.
        public Task<int?> ResolveNumericSceneIdAsync(string providerSceneId, CancellationToken ct)
        {
            Resolutions.Add(providerSceneId);
            return Task.FromResult<int?>(null);
        }

        public Task<IReadOnlyList<ProviderFacetMenu>> ListFacetMenusAsync(
            WhisparrEntityKind kind, string providerEntityId, CancellationToken ct)
            => Task.FromResult(Menus);

        public Task<ProviderFacetSearch> SearchFacetValuesAsync(
            WhisparrEntityKind kind,
            string providerEntityId,
            string facetKey,
            string fragment,
            CancellationToken ct)
        {
            Fragments.Add(fragment);
            return Task.FromResult(
                ProviderFacetSearch.Matched([new ProviderFacetValue("t-1", "Anal Sex")], 64));
        }
    }
}
