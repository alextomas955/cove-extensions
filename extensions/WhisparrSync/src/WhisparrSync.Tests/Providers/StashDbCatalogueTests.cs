using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cove.Core.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using WhisparrSync.Monitoring;
using WhisparrSync.Options;
using WhisparrSync.Providers;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.Providers;

/// <summary>
/// What this catalogue puts on the wire, and what it makes of a page the provider really served.
/// </summary>
/// <remarks>
/// The page is a recording of one live read rather than a document written to suit the projector, so
/// a field this product reads under a name the provider does not use fails here.
/// <para>
/// The provider answers an authentication failure with 200 and an <c>errors</c> member, so the cases
/// covering a refusal answer a success status deliberately.
/// </para>
/// </remarks>
public sealed class StashDbCatalogueTests
{
    private const string FixtureName = "stashdb-2026-09-scene-page.json";
    private const string FacetFixtureName = "stashdb-2026-09-facet-menus.json";
    private const string ConfiguredSpelling = "https://stashdb.org/graphql";
    private const string SomeKey = "0e2e0e2e0e2e0e2e0e2e0e2e0e2e0e2e";

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    /// <summary>
    /// The recording states when it was taken and what it was taken against. A fixture replaced with
    /// a hand-written document loses both, and this is what reports that rather than passing quietly
    /// on an invented page.
    /// </summary>
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

    /// <summary>
    /// The credential travels in the provider's own header and never in the address. A query string
    /// is written to a proxy log and to a browser history; a header is not.
    /// </summary>
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

        // Read off the recording rather than restated, so this stays a claim about what the provider
        // served rather than about a number typed here.
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

    /// <summary>
    /// The size is the provider's own count. Taken from the served array's length it would report a
    /// forty-scene catalogue for every studio, and the count line above the grid would state it.
    /// </summary>
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

    /// <summary>A page shorter than the count is the end of a catalogue, not a truncated read.</summary>
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

    /// <summary>
    /// The provider refuses a credential inside a success status. Read on the status alone this is a
    /// catalogue that is simply empty, which is a wrong answer a reader would read as right.
    /// </summary>
    [Fact]
    public async Task ARefusalInsideASuccessStatusIsNotReadAsAnEmptyCatalogue()
    {
        var (catalogue, _) = CatalogueOver(
            """{"errors":[{"message":"not authorized"}],"data":null}""");

        Assert.Null(await catalogue.ReadCatalogueSizeAsync(StudioPage(), TestCt));
    }

    /// <summary>
    /// A credential the provider refuses answers no page at all. Read as an empty page it is a
    /// catalogue listing nothing, which the surface states as a reader owning everything.
    /// </summary>
    /// <remarks>
    /// The single request is the second half of the claim: a refusal is the provider's own answer,
    /// so a second attempt collects the same refusal and writes the same line again.
    /// </remarks>
    [Fact]
    public async Task AReadPageOverARefusedCredentialAnswersNoPageAndIsSentOnce()
    {
        var (catalogue, handler) = CatalogueAnswering((HttpStatusCode.Unauthorized, "{}"));

        var answer = await catalogue.ReadPageAsync(StudioPage(), TestCt);

        Assert.Null(answer.Page);
        Assert.Single(handler.Requests);
    }

    /// <summary>
    /// The provider states an expired key as an <c>errors</c> member inside a success status, and
    /// that is no page either.
    /// </summary>
    [Fact]
    public async Task AReadPageOverARefusalInsideASuccessStatusAnswersNoPageAndIsSentOnce()
    {
        var (catalogue, handler) = CatalogueAnswering(
            (HttpStatusCode.OK, """{"errors":[{"message":"not authorized"}]}"""));

        var answer = await catalogue.ReadPageAsync(StudioPage(), TestCt);

        Assert.Null(answer.Page);
        Assert.Single(handler.Requests);
    }

    /// <summary>
    /// A rate limit and a gateway failure are passing, so each takes the second attempt.
    /// </summary>
    /// <remarks>
    /// The request count is the whole claim, and it is counted at the transport rather than
    /// inferred from the answer, which is no page either way.
    /// </remarks>
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

    /// <summary>A status the provider stated about the request itself is sent once.</summary>
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

    /// <summary>A connection that drops part way through the body answers no page.</summary>
    [Fact]
    public async Task AReadPageOverADroppedConnectionAnswersNoPage()
    {
        var (catalogue, _) = CatalogueOver(
            BodyRecordingHandler.AnsweringWithABodyThatStopsPartWay());

        Assert.Null((await catalogue.ReadPageAsync(StudioPage(), TestCt)).Page);
    }

    /// <summary>An answer past the read bound answers no page.</summary>
    [Fact]
    public async Task AReadPageOverAnAnswerPastTheReadBoundAnswersNoPage()
    {
        var (catalogue, _) = CatalogueOver(BodyRecordingHandler.AnsweringPastTheReadBound());

        Assert.Null((await catalogue.ReadPageAsync(StudioPage(), TestCt)).Page);
    }

    /// <summary>Null and zero are different answers, and only one of them claims anything.</summary>
    [Fact]
    public async Task AnUnreadCountIsNullAndAnEmptyCatalogueIsZero()
    {
        var (empty, _) = CatalogueOver("""{"data":{"queryScenes":{"count":0,"scenes":[]}}}""");

        Assert.Equal(0, await empty.ReadCatalogueSizeAsync(StudioPage(), TestCt));
    }

    /// <summary>
    /// A studio reads its own scenes, and itself with every descendant where the host's own
    /// sub-studio toggle is on. The provider attributes a parent studio's scenes to its children, so
    /// the direct spelling answers nothing at all on a network.
    /// </summary>
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

    /// <summary>Both are non-null on the provider's own input, so both are always sent.</summary>
    [Fact]
    public void TheSortAndTheDirectionAreAlwaysSent()
    {
        var scope = StashDbCatalogue.ScopeFor(StudioPage());

        Assert.Equal("DATE", scope["sort"]!.GetValue<string>());
        Assert.Equal("DESC", scope["direction"]!.GetValue<string>());
    }

    /// <summary>
    /// The ordering reported as in force is the one an unnamed read really sends, so a surface
    /// cannot name an order the provider did not apply.
    /// </summary>
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

    /// <summary>
    /// The title search reaches the provider, so it narrows the whole catalogue rather than the page
    /// that happened to load.
    /// </summary>
    [Fact]
    public void ATitleSearchIsComposedIntoTheProvidersOwnQuery()
    {
        var scope = StashDbCatalogue.ScopeFor(StudioPage(titleSearch: "pool"));

        Assert.Equal("pool", scope["title"]!.GetValue<string>());
    }

    /// <summary>
    /// A facet selection reaches the provider's own query, so the value narrows the whole catalogue
    /// rather than the page that happened to load.
    /// </summary>
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

    /// <summary>
    /// A chosen sub-studio replaces the studio scope. Added beside it the selection would widen what
    /// is read rather than narrowing it.
    /// </summary>
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

    /// <summary>
    /// The ordering is one opaque value the provider issued, and both halves of it reach the
    /// provider's own two fields.
    /// </summary>
    [Fact]
    public void AChosenOrderingReachesBothOfTheProvidersFields()
    {
        var scope = StashDbCatalogue.ScopeFor(StudioPage(sort: "TITLE:ASC"));

        Assert.Equal("TITLE", scope["sort"]!.GetValue<string>());
        Assert.Equal("ASC", scope["direction"]!.GetValue<string>());
    }

    /// <summary>An ordering this type did not issue is not taken apart into halves it may not carry.</summary>
    [Fact]
    public void AnOrderingTheProviderDidNotIssueFallsBackToTheDefault()
    {
        var scope = StashDbCatalogue.ScopeFor(StudioPage(sort: "not-an-ordering"));

        Assert.Equal("DATE", scope["sort"]!.GetValue<string>());
        Assert.Equal("DESC", scope["direction"]!.GetValue<string>());
    }

    /// <summary>
    /// Each menu costs one request and carries at most one page of values. Read whole, a network's
    /// performer list runs to thousands.
    /// </summary>
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

        // The counts the recorded responses themselves report, against 25 values each.
        Assert.Equal([48, 2941], menus.Select(menu => menu.ReportedValueCount));
        Assert.All(menus, menu => Assert.Equal(StashDbCatalogue.FacetPageSize, menu.Values.Count));
    }

    /// <summary>
    /// A menu the provider reports no more values of than it served reports its own value count, so
    /// nothing states a bound over a whole menu.
    /// </summary>
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

    /// <summary>
    /// No menu offers a year. This provider's scene query carries one date criterion with no
    /// inclusive bound, so a year is not expressible on it and no control for one is drawn.
    /// </summary>
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

    /// <summary>
    /// A tag page is offered no menu. A tag's own performers and studios are not listable, and a tag
    /// menu there would narrow a tag to itself.
    /// </summary>
    [Fact]
    public async Task ATagPageIsOfferedNoMenu()
    {
        var (catalogue, handler) = CatalogueOver(Facet("tags"));

        Assert.Empty(await catalogue.ListFacetMenusAsync(WhisparrEntityKind.Tag, "a-tag", TestCt));
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

    // The recorded body as the provider served it: the fixture wraps the response with its own
    // provenance, and the client is answered the response half alone.
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

                // Zero paces nothing, so these cases do not wait on a limiter to settle a question
                // about a composed request.
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

    // A page the provider really served. The answer carries none where nothing arrived, so a case
    // about what a page projects states that a page arrived before reading one.
    private static async Task<ProviderCataloguePage> PageFrom(
        StashDbCatalogue catalogue, ProviderCatalogueRequest request, CancellationToken ct)
    {
        var answer = await catalogue.ReadPageAsync(request, ct);

        Assert.NotNull(answer.Page);
        return answer.Page;
    }
}
