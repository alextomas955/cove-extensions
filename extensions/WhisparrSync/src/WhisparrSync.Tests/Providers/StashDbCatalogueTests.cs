using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cove.Core.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using WhisparrSync.Contracts;
using WhisparrSync.Options;
using WhisparrSync.Providers;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.Providers;

// The page is a recording of one live read, not a document written to suit the projector.
// StashDB answers an authentication failure with 200 and an errors member, so the refusal cases
// answer a success status on purpose.
public sealed class StashDbCatalogueTests
{
    private const string FixtureName = "stashdb-2026-09-scene-page.json";
    private const string FacetFixtureName = "stashdb-2026-09-facet-menus.json";
    private const string ConfiguredSpelling = "https://stashdb.org/graphql";
    private const string SomeKey = "0e2e0e2e0e2e0e2e0e2e0e2e0e2e0e2e";

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    [Fact]
    public void TheFixtureStatesItsOwnProvenance()
    {
        var fixture = JsonDocument.Parse(ProbeFixtures.Read(FixtureName)).RootElement;

        Assert.Equal("2026-09-06", fixture.GetProperty("recordedOn").GetString());
        Assert.Equal(ConfiguredSpelling, fixture.GetProperty("recordedAgainst").GetString());
    }

    [Fact]
    public async Task OnePageIsOneOutboundCall()
    {
        var (catalogue, handler) = CatalogueOver(RecordedPage());

        await catalogue.ReadPageAsync(StudioPage(), TestCt);

        Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
    }

    // A query string reaches a proxy log and a browser history. A header does not.
    [Fact]
    public async Task TheCredentialTravelsInTheHeaderAndNeverInTheAddress()
    {
        var (catalogue, handler) = CatalogueOver(RecordedPage());

        await catalogue.ReadPageAsync(StudioPage(), TestCt);

        Assert.DoesNotContain(SomeKey, handler.Targets[0], StringComparison.Ordinal);
        Assert.DoesNotContain("apikey=", handler.Targets[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ARecordedPageProjectsEveryScenesFields()
    {
        var (catalogue, _) = CatalogueOver(RecordedPage());

        var page = await PageFrom(catalogue, StudioPage(), TestCt);

        Assert.Equal(40, page.Scenes.Count);
        Assert.All(page.Scenes, scene => Assert.NotEmpty(scene.ProviderSceneId));
        Assert.All(page.Scenes, scene => Assert.NotEmpty(scene.Title));

        // Read off the recording, so the claim stays about what the provider served.
        var served = RecordedScenes();
        Assert.Equal(served[0].GetProperty("id").GetString(), page.Scenes[0].ProviderSceneId);
        Assert.Equal(served[0].GetProperty("title").GetString(), page.Scenes[0].Title);
        Assert.Equal(
            served[0].GetProperty("studio").GetProperty("name").GetString(),
            page.Scenes[0].StudioName);
        Assert.Equal(
            served[0].GetProperty("images")[0].GetProperty("url").GetString(),
            page.Scenes[0].CoverUrl);
        Assert.Equal(
            served[0].GetProperty("performers").GetArrayLength(),
            page.Scenes[0].Performers.Count);
        Assert.Equal(served[0].GetProperty("tags").GetArrayLength(), page.Scenes[0].Tags.Count);
    }

    // Taken from the served array's length the size would read as forty scenes for every studio.
    [Fact]
    public async Task TheCatalogueSizeIsTheProvidersCountAndNotThePagesLength()
    {
        var (catalogue, _) = CatalogueOver(RecordedPage());

        var page = await PageFrom(catalogue, StudioPage(), TestCt);

        var served = JsonDocument.Parse(ProbeFixtures.Read(FixtureName))
            .RootElement.GetProperty("response")
            .GetProperty("queryScenes");

        Assert.Equal(served.GetProperty("count").GetInt32(), page.CatalogueSize);
        Assert.NotEqual(page.Scenes.Count, page.CatalogueSize);
    }

    // A page shorter than the count is the end of a catalogue, not a truncated read.
    [Fact]
    public async Task ACountLargerThanTheServedArrayStillAnswersTheCount()
    {
        var (catalogue, _) = CatalogueOver(
            """{"data":{"queryScenes":{"count":3941,"scenes":[]}}}""");

        var page = await PageFrom(catalogue, StudioPage(), TestCt);

        Assert.Equal(3941, page.CatalogueSize);
        Assert.Empty(page.Scenes);
        Assert.False(page.SizeIsLowerBound);
    }

    [Fact]
    public async Task ARefusalInsideASuccessStatusIsNotReadAsAnEmptyCatalogue()
    {
        var (catalogue, _) = CatalogueOver(
            """{"errors":[{"message":"not authorized"}],"data":null}""");

        Assert.Null(await catalogue.ReadCatalogueSizeAsync(StudioPage(), TestCt));
    }

    // No page rather than an empty one. The surface reads an empty page as a catalogue that listed
    // nothing. A refusal is the provider's own answer, so a second attempt collects the same one.
    [Fact]
    public async Task AReadPageOverARefusedCredentialAnswersNoPageAndIsSentOnce()
    {
        var (catalogue, handler) = CatalogueAnswering((HttpStatusCode.Unauthorized, "{}"));

        var answer = await catalogue.ReadPageAsync(StudioPage(), TestCt);

        Assert.Null(answer.Page);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task AReadPageOverARefusalInsideASuccessStatusAnswersNoPageAndIsSentOnce()
    {
        var (catalogue, handler) = CatalogueAnswering(
            (HttpStatusCode.OK, """{"errors":[{"message":"not authorized"}]}"""));

        var answer = await catalogue.ReadPageAsync(StudioPage(), TestCt);

        Assert.Null(answer.Page);
        Assert.Single(handler.Requests);
    }

    // A rate limit and a gateway failure are passing, so each takes a second attempt.
    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task AReadPageOverAPassingFailureIsSentTwice(HttpStatusCode status)
    {
        var (catalogue, handler) = CatalogueAnswering((status, "{}"));

        var answer = await catalogue.ReadPageAsync(StudioPage(), TestCt);

        Assert.Null(answer.Page);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task AReadPageOverAStatedStatusIsSentOnce(HttpStatusCode status)
    {
        var (catalogue, handler) = CatalogueAnswering((status, "{}"));

        var answer = await catalogue.ReadPageAsync(StudioPage(), TestCt);

        Assert.Null(answer.Page);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task AReadPageOverADroppedConnectionAnswersNoPage()
    {
        var (catalogue, _) = CatalogueOver(
            BodyRecordingHandler.AnsweringWithABodyThatStopsPartWay());

        Assert.Null((await catalogue.ReadPageAsync(StudioPage(), TestCt)).Page);
    }

    [Fact]
    public async Task AReadPageOverAnAnswerPastTheReadBoundAnswersNoPage()
    {
        var (catalogue, _) = CatalogueOver(BodyRecordingHandler.AnsweringPastTheReadBound());

        Assert.Null((await catalogue.ReadPageAsync(StudioPage(), TestCt)).Page);
    }

    [Fact]
    public async Task AnUnreadCountIsNullAndAnEmptyCatalogueIsZero()
    {
        var (empty, _) = CatalogueOver("""{"data":{"queryScenes":{"count":0,"scenes":[]}}}""");

        Assert.Equal(0, await empty.ReadCatalogueSizeAsync(StudioPage(), TestCt));
    }

    // StashDB attributes a parent studio's scenes to its children, so on a network the direct
    // spelling answers nothing.
    [Fact]
    public void TheSubStudioToggleChoosesBetweenTheTwoStudioSpellings()
    {
        var direct = StashDbCatalogue.ScopeFor(StudioPage());
        var withChildren = StashDbCatalogue.ScopeFor(
            StudioPage(
                new Dictionary<string, string>
                {
                    [StashDbCatalogue.IncludeSubStudiosKey] = "true",
                }));

        Assert.Null(direct["parentStudio"]);
        Assert.NotNull(direct["studios"]);
        Assert.Equal("INCLUDES", direct["studios"]!["modifier"]!.GetValue<string>());

        Assert.Equal("a-studio", withChildren["parentStudio"]!.GetValue<string>());
        Assert.Null(withChildren["studios"]);
    }

    // Both are non-null on the provider's own input type, so both are always sent.
    [Fact]
    public void TheSortAndTheDirectionAreAlwaysSent()
    {
        var scope = StashDbCatalogue.ScopeFor(StudioPage());

        Assert.Equal("DATE", scope["sort"]!.GetValue<string>());
        Assert.Equal("DESC", scope["direction"]!.GetValue<string>());
    }

    [Fact]
    public void TheReportedDefaultOrderingIsTheOneAnUnnamedReadSends()
    {
        var scope = StashDbCatalogue.ScopeFor(StudioPage());
        var sort = scope["sort"]!.GetValue<string>();
        var direction = scope["direction"]!.GetValue<string>();
        var (catalogue, _) = CatalogueOver("{}");

        Assert.Equal($"{sort}:{direction}", catalogue.DefaultSort);
        Assert.Contains(catalogue.Sorts, offered => offered.Value == catalogue.DefaultSort);
    }

    // Composed into the provider's query, a search narrows the whole catalogue rather than the page
    // that happened to load. The same holds for the facet cases below.
    [Fact]
    public void ATitleSearchIsComposedIntoTheProvidersOwnQuery()
    {
        var scope = StashDbCatalogue.ScopeFor(StudioPage(titleSearch: "pool"));

        Assert.Equal("pool", scope["title"]!.GetValue<string>());
    }

    [Fact]
    public void AFacetSelectionIsComposedIntoTheProvidersOwnQuery()
    {
        var chosen = StashDbCatalogue.ScopeFor(
            StudioPage(
                new Dictionary<string, string>
                {
                    [StashDbCatalogue.PerformerFacetKey] = "a-performer",
                }));

        Assert.Equal(
            "a-performer",
            chosen[StashDbCatalogue.PerformerFacetKey]!["value"]![0]!.GetValue<string>());
        Assert.Null(StashDbCatalogue.ScopeFor(StudioPage())[StashDbCatalogue.PerformerFacetKey]);
    }

    // Added beside the studio scope rather than replacing it, the selection would widen the read.
    [Fact]
    public void AChosenSubStudioReplacesTheStudioScope()
    {
        var chosen = StashDbCatalogue.ScopeFor(
            StudioPage(
                new Dictionary<string, string>
                {
                    [StashDbCatalogue.IncludeSubStudiosKey] = "true",
                    [StashDbCatalogue.SubStudioFacetKey] = "a-child",
                }));

        Assert.Null(chosen["parentStudio"]);
        Assert.Equal(
            "a-child", chosen[StashDbCatalogue.SubStudioFacetKey]!["value"]![0]!.GetValue<string>());
    }

    [Fact]
    public void AChosenOrderingReachesBothOfTheProvidersFields()
    {
        var scope = StashDbCatalogue.ScopeFor(StudioPage(sort: "TITLE:ASC"));

        Assert.Equal("TITLE", scope["sort"]!.GetValue<string>());
        Assert.Equal("ASC", scope["direction"]!.GetValue<string>());
    }

    [Fact]
    public void AnOrderingTheProviderDidNotIssueFallsBackToTheDefault()
    {
        var scope = StashDbCatalogue.ScopeFor(StudioPage(sort: "not-an-ordering"));

        Assert.Equal("DATE", scope["sort"]!.GetValue<string>());
        Assert.Equal("DESC", scope["direction"]!.GetValue<string>());
    }

    // A network's performer list runs to thousands, so a menu carries at most one page.
    [Fact]
    public async Task EachFacetMenuIsOneRequestAndAtMostOnePageOfValues()
    {
        var (catalogue, handler) = CatalogueOverEach(
            Facet("studioPerformers"), Facet("subStudios"), Facet("tags"));

        var menus = await catalogue.ListFacetMenusAsync(
            WhisparrEntityKind.Studio, "a-studio", TestCt);

        // A network has no performers of its own, so that menu is absent rather than empty.
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(2, menus.Count);
        Assert.All(
            menus, menu => Assert.True(menu.Values.Count <= StashDbCatalogue.FacetPageSize));
        Assert.All(menus, menu => Assert.True(menu.ReportedValueCount > menu.Values.Count));

        // The counts the recorded responses report, against 25 values each.
        Assert.Equal([48, 2941], menus.Select(menu => menu.ReportedValueCount));
        Assert.All(menus, menu => Assert.Equal(StashDbCatalogue.FacetPageSize, menu.Values.Count));
    }

    [Fact]
    public async Task AMenuTheProviderReportsNoMoreOfCarriesItsOwnValueCount()
    {
        var (catalogue, _) = CatalogueOverEach(Facet("shortPerformerMenu"), "{}", "{}");

        var menus = await catalogue.ListFacetMenusAsync(
            WhisparrEntityKind.Studio, "a-studio", TestCt);

        var menu = Assert.Single(menus);
        Assert.NotEmpty(menu.Values);
        Assert.Equal(menu.Values.Count, menu.ReportedValueCount);
    }

    // StashDB's scene query carries one date criterion with no inclusive bound, so a year is not
    // expressible and no control for one is drawn.
    [Theory]
    [InlineData(WhisparrEntityKind.Studio)]
    [InlineData(WhisparrEntityKind.Performer)]
    [InlineData(WhisparrEntityKind.Tag)]
    public async Task NoMenuOffersAYear(WhisparrEntityKind kind)
    {
        var (catalogue, _) = CatalogueOverEach(
            Facet("studioPerformers"), Facet("subStudios"), Facet("tags"));

        var menus = await catalogue.ListFacetMenusAsync(kind, "an-entity", TestCt);

        Assert.DoesNotContain(
            menus,
            menu => menu.Key.Contains("year", StringComparison.OrdinalIgnoreCase)
                || menu.Label.Contains("year", StringComparison.OrdinalIgnoreCase));
    }

    // A tag's own performers and studios are not listable, and a tag menu there would narrow a tag
    // to itself.
    [Fact]
    public async Task ATagPageIsOfferedNoMenu()
    {
        var (catalogue, handler) = CatalogueOver(Facet("tags"));

        Assert.Empty(await catalogue.ListFacetMenusAsync(WhisparrEntityKind.Tag, "a-tag", TestCt));
        Assert.Empty(handler.Requests);
    }

    // The fragment travels on the query the menu is filled from, so a value the menu never carried
    // is still found.
    [Fact]
    public async Task AFragmentTravelsAsTheProvidersOwnNameCriterion()
    {
        var (catalogue, handler) = CatalogueOver(Facet("tags"));

        var answer = await catalogue.SearchFacetValuesAsync(
            WhisparrEntityKind.Studio, "a-studio", StashDbCatalogue.TagFacetKey, "ana", TestCt);

        Assert.True(answer.IsSearchable);
        Assert.NotEmpty(answer.Values!);
        Assert.True(answer.ReportedValueCount > answer.Values!.Count);

        var input = JsonNode.Parse(handler.Requests[0].Body)!["variables"]!["input"]!;
        Assert.Equal("ana", input["name"]!.GetValue<string>());
        Assert.Equal(StashDbCatalogue.FacetPageSize, input["per_page"]!.GetValue<int>());

        // StashDB was measured ignoring its alias criterion, so a filter sent under that name is
        // dropped in silence.
        Assert.Null(input["alias"]);
    }

    // Reported as the same answer, a failed read would state that a value does not exist.
    [Fact]
    public async Task AMatchOfNothingAndAReadThatAnsweredNothingAreDifferentAnswers()
    {
        var (matched, _) = CatalogueOver(
            """{"data":{"queryTags":{"count":0,"tags":[]}}}""");
        var (refused, _) = CatalogueOver(
            """{"errors":[{"message":"invalid"}]}""");

        var none = await matched.SearchFacetValuesAsync(
            WhisparrEntityKind.Studio, "a-studio", StashDbCatalogue.TagFacetKey, "zz", TestCt);
        var unread = await refused.SearchFacetValuesAsync(
            WhisparrEntityKind.Studio, "a-studio", StashDbCatalogue.TagFacetKey, "zz", TestCt);

        Assert.Empty(none.Values!);
        Assert.Null(unread.Values);
        Assert.True(unread.IsSearchable);
    }

    [Theory]
    [InlineData(WhisparrEntityKind.Tag, StashDbCatalogue.TagFacetKey)]
    [InlineData(WhisparrEntityKind.Performer, StashDbCatalogue.PerformerFacetKey)]
    [InlineData(WhisparrEntityKind.Studio, ThePornDbCatalogue.YearKey)]
    public async Task AFacetThisPageIsOfferedNoMenuOfIsNotSearchable(
        WhisparrEntityKind kind, string facetKey)
    {
        var (catalogue, handler) = CatalogueOver(Facet("tags"));

        var answer = await catalogue.SearchFacetValuesAsync(kind, "an-entity", facetKey, "an", TestCt);

        Assert.False(answer.IsSearchable);
        Assert.Null(answer.Values);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void TheFacetFixtureStatesItsOwnProvenance()
    {
        var fixture = JsonDocument.Parse(ProbeFixtures.Read(FacetFixtureName)).RootElement;

        Assert.Equal("2026-09-06", fixture.GetProperty("recordedOn").GetString());
        Assert.Equal(ConfiguredSpelling, fixture.GetProperty("recordedAgainst").GetString());
    }

    private static string Facet(string label)
        => JsonNode.Parse(ProbeFixtures.Read(FacetFixtureName))!["cases"]![label]!["response"]!
            .DeepClone()
            .ToJsonString();

    private static JsonElement[] RecordedScenes()
        => [.. JsonDocument.Parse(ProbeFixtures.Read(FixtureName))
            .RootElement.GetProperty("response")
            .GetProperty("queryScenes")
            .GetProperty("scenes")
            .EnumerateArray()];

    // The fixture wraps the response with its own provenance, so the client is answered the
    // response half alone.
    private static string RecordedPage()
    {
        var response = JsonNode.Parse(ProbeFixtures.Read(FixtureName))!["response"]!;
        return new JsonObject { ["data"] = response.DeepClone() }.ToJsonString();
    }

    private static ProviderCatalogueRequest StudioPage(
        IReadOnlyDictionary<string, string>? filters = null,
        string? titleSearch = null,
        string? sort = null)
        => new(
            WhisparrEntityKind.Studio,
            "a-studio",
            1,
            40,
            sort,
            titleSearch,
            filters ?? new Dictionary<string, string>());

    // Measured 2026-09-09: a real identifier under this path redirected to the sign-in page
    // carrying the same path back, so the route resolves.
    [Fact]
    public void ASceneIsAddressedOnTheSiteRatherThanWhereTheCatalogueIsRead()
    {
        var (catalogue, _) = CatalogueOver("{}");

        Assert.Equal(
            "https://stashdb.org/scenes/3ac7838f-0e3f-4f19-9d2b-7c1a5b9e2f10",
            catalogue.SceneAddress("3ac7838f-0e3f-4f19-9d2b-7c1a5b9e2f10"));
    }

    [Fact]
    public void AnIdentifierCannotWidenThePathItIsPlacedIn()
    {
        var (catalogue, _) = CatalogueOver("{}");

        Assert.Equal("https://stashdb.org/scenes/..%2Fusers", catalogue.SceneAddress("../users"));
    }

    private static (StashDbCatalogue Catalogue, BodyRecordingHandler Handler) CatalogueOverEach(
        params string[] answers)
        => CatalogueOver(
            BodyRecordingHandler.AnsweringInTurn(
                [.. answers.Select(answer => (HttpStatusCode.OK, answer))]));

    private static (StashDbCatalogue Catalogue, BodyRecordingHandler Handler) CatalogueAnswering(
        params (HttpStatusCode Status, string Answer)[] answers)
        => CatalogueOver(BodyRecordingHandler.AnsweringInTurn(answers));

    private static (StashDbCatalogue Catalogue, BodyRecordingHandler Handler) CatalogueOver(
        string answer)
        => CatalogueOver(BodyRecordingHandler.Answering(HttpStatusCode.OK, answer));

    private static (StashDbCatalogue Catalogue, BodyRecordingHandler Handler) CatalogueOver(
        BodyRecordingHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri(ConfiguredSpelling) };

        var config = new CoveConfiguration();
        config.Scraping.MetadataServers.Add(
            new MetadataServerInstance
            {
                Endpoint = ConfiguredSpelling,
                ApiKey = SomeKey,
                Name = "stashdb",

                // Zero paces nothing, so no case waits on the limiter.
                MaxRequestsPerMinute = 0,
            });

        var catalogue = new StashDbCatalogue(
            http,
            new ProviderEndpointPort(config),
            new OptionsStore(new FakeStore()),
            new ProviderPacer(),
            NullLogger.Instance);

        return (catalogue, handler);
    }

    // The answer carries no page where nothing arrived, so a case about projection states that a
    // page arrived before reading one.
    private static async Task<ProviderCataloguePage> PageFrom(
        StashDbCatalogue catalogue, ProviderCatalogueRequest request, CancellationToken ct)
    {
        var answer = await catalogue.ReadPageAsync(request, ct);

        Assert.NotNull(answer.Page);
        return answer.Page;
    }
}
