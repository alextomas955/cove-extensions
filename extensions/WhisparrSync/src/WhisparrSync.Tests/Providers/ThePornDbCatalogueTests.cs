using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cove.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using WhisparrSync.Contracts;
using WhisparrSync.Import;
using WhisparrSync.Monitoring;
using WhisparrSync.Options;
using WhisparrSync.Providers;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Providers;

/// <summary>
/// What this catalogue puts on the wire, and what it makes of pages the provider really served.
/// </summary>
/// <remarks>
/// Every page here is a recording of a live read. The provider's paging metadata is what the client
/// depends on, and a document written to suit the client would have agreed with whatever it did.
/// <para>
/// The provider refuses a malformed request with a status outside the success range and a body
/// naming the field, so the cases covering a refusal answer both.
/// </para>
/// </remarks>
public sealed class ThePornDbCatalogueTests
{
    private const string PageFixture = "theporndb-2026-09-scenes-page.json";
    private const string CeilingFixture = "theporndb-2026-09-scenes-at-ceiling.json";
    private const string ShortPageFixture = "theporndb-2026-09-scenes-short-first-page.json";
    private const string VersionFixture = "theporndb-2026-09-openapi-version.json";
    private const string LookupFixture = "theporndb-2026-09-name-lookups.json";

    private const string ConfiguredSpelling = "https://theporndb.net/graphql";
    private const string SomeKey = "0e2e0e2e0e2e0e2e0e2e0e2e0e2e0e2e";
    private const string StudioUuid = "e3b61b3e-0c20-4bea-9441-b88430ed6317";

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    /// <summary>
    /// Each recording states when it was taken and which revision of the provider's own document it
    /// was taken against. That revision moved twice while this work was being planned, so a fixture
    /// replaced by a hand-written document is reported here rather than passing quietly.
    /// </summary>
    [Theory]
    [InlineData(VersionFixture)]
    [InlineData(PageFixture)]
    [InlineData(CeilingFixture)]
    [InlineData(ShortPageFixture)]
    public void EveryFixtureStatesItsOwnProvenance(string fixture)
    {
        var document = JsonDocument.Parse(ProbeFixtures.Read(fixture)).RootElement;

        Assert.Equal("2026-09-06", document.GetProperty("recordedOn").GetString());
        Assert.NotEmpty(document.GetProperty("documentVersion").GetString()!);
        Assert.StartsWith(
            "https://api.theporndb.net",
            document.GetProperty("recordedAgainst").GetString()!,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The version the tests read is the one the provider's own document carried, not one copied
    /// into a plan. It moved from v3.24.738 through v3.24.744 to what was recorded here.
    /// </summary>
    [Fact]
    public void TheRecordedPagesWereTakenAgainstTheRecordedDocumentRevision()
    {
        var declared = JsonDocument.Parse(ProbeFixtures.Read(VersionFixture))
            .RootElement.GetProperty("documentVersion")
            .GetString();

        foreach (var fixture in (string[])[PageFixture, CeilingFixture, ShortPageFixture])
        {
            Assert.Equal(
                declared,
                JsonDocument.Parse(ProbeFixtures.Read(fixture))
                    .RootElement.GetProperty("documentVersion")
                    .GetString());
        }
    }

    /// <summary>
    /// A page larger than the provider's own is refused with a status outside the success range, so
    /// asking for one reads as a catalogue that could not be reached at all.
    /// </summary>
    [Fact]
    public async Task NoComposedRequestAsksForMoreThanTheProvidersLargestPage()
    {
        var (catalogue, handler) = CatalogueOver(Recorded(PageFixture));

        await catalogue.ReadPageAsync(TagPage(perPage: 500), TestCt);

        Assert.Contains(
            $"per_page={ThePornDbCatalogue.MaxPerPage}", handler.Targets[0], StringComparison.Ordinal);
        Assert.DoesNotContain("per_page=500", handler.Targets[0], StringComparison.Ordinal);
    }

    /// <summary>
    /// The scene route refuses the identifier Cove stores, so one read converts it. A uuid reaching
    /// the scene route is a permanently unreachable catalogue rather than an error a reader sees.
    /// </summary>
    [Fact]
    public async Task AStudioUuidIsResolvedOnceAndTheSceneRouteCarriesTheNumericIdentifier()
    {
        var (catalogue, handler) = CatalogueOver(RecordedSite(), Recorded(PageFixture));

        await catalogue.ReadPageAsync(StudioPage(), TestCt);

        Assert.Equal(2, handler.Targets.Count);
        Assert.Equal($"/sites/{StudioUuid}", handler.Targets[0]);
        Assert.Contains("site_id=92", handler.Targets[1], StringComparison.Ordinal);
        Assert.DoesNotContain(StudioUuid, handler.Targets[1], StringComparison.Ordinal);
    }

    /// <summary>
    /// The provider serves no more than its ceiling and clamps a page past the last one, so a size
    /// at the ceiling is a floor and the pager is fed the provider's own last page.
    /// </summary>
    [Fact]
    public async Task AtTheCeilingTheSizeIsAFloorAndTheLastPageIsTheProvidersOwn()
    {
        var served = Response(CeilingFixture);
        var (catalogue, _) = CatalogueOver(served.ToJsonString());

        var page = await PageFrom(catalogue, TagPage(), TestCt);

        Assert.True(page.SizeIsLowerBound);
        Assert.Equal(ThePornDbCatalogue.CatalogueCeiling, page.CatalogueSize);
        Assert.Equal(served["meta"]!["last_page"]!.GetValue<int>(), page.LastPage);
        Assert.Equal(served["meta"]!["from"]!.GetValue<int>(), page.RangeFrom);
        Assert.Equal(served["meta"]!["to"]!.GetValue<int>(), page.RangeTo);
    }

    /// <summary>A catalogue below the ceiling reports an exact size and is not marked as a floor.</summary>
    [Fact]
    public async Task BelowTheCeilingTheSizeIsExact()
    {
        var served = Response(PageFixture);
        var (catalogue, _) = CatalogueOver(served.ToJsonString());

        var page = await PageFrom(catalogue, StudioPage(providerEntityId: "92"), TestCt);

        Assert.False(page.SizeIsLowerBound);
        Assert.Equal(served["meta"]!["total"]!.GetValue<int>(), page.CatalogueSize);
        Assert.NotEqual(page.Scenes.Count, page.CatalogueSize);
    }

    /// <summary>
    /// A page carrying fewer rows than it asked for is not the end of the catalogue here. The row
    /// count is dropped to the number a short page was once measured at while the metadata stays
    /// exactly as the provider served it, so what is exercised is the client's rule rather than a
    /// page the provider never served.
    /// </summary>
    [Fact]
    public async Task APageShorterThanItsOwnRangeIsNotReadAsTheEndOfTheCatalogue()
    {
        var served = Response(ShortPageFixture);
        var rows = served["data"]!.AsArray();
        while (rows.Count > 39)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        var (catalogue, _) = CatalogueOver(served.ToJsonString());

        var page = await PageFrom(catalogue, TagPage(), TestCt);

        Assert.Equal(39, page.Scenes.Count);
        Assert.Equal(served["meta"]!["last_page"]!.GetValue<int>(), page.LastPage);
        Assert.True(page.LastPage > 1);
    }

    [Fact]
    public async Task ARecordedPageProjectsEveryScenesFields()
    {
        var served = Response(PageFixture);
        var (catalogue, _) = CatalogueOver(served.ToJsonString());

        var page = await PageFrom(catalogue, StudioPage(providerEntityId: "92"), TestCt);

        var rows = served["data"]!.AsArray();
        Assert.Equal(rows.Count, page.Scenes.Count);
        Assert.All(page.Scenes, scene => Assert.NotEmpty(scene.ProviderSceneId));
        Assert.All(page.Scenes, scene => Assert.NotEmpty(scene.Title));

        Assert.Equal(rows[0]!["id"]!.GetValue<string>(), page.Scenes[0].ProviderSceneId);
        Assert.Equal(rows[0]!["title"]!.GetValue<string>(), page.Scenes[0].Title);
        Assert.Equal(rows[0]!["date"]!.GetValue<string>(), page.Scenes[0].ReleaseDate);
        Assert.Equal(rows[0]!["site"]!["name"]!.GetValue<string>(), page.Scenes[0].StudioName);
        Assert.Equal(rows[0]!["performers"]!.AsArray().Count, page.Scenes[0].Performers.Count);
        Assert.Equal(rows[0]!["tags"]!.AsArray().Count, page.Scenes[0].Tags.Count);
        Assert.All(page.Scenes[0].Performers, performer => Assert.NotNull(performer.ImageUrl));
        Assert.NotNull(page.Scenes[0].CoverUrl);
    }

    /// <summary>
    /// A refused request carries the provider's own message. Read as a page it would be a catalogue
    /// listing nothing, which is a wrong answer a reader would read as right.
    /// </summary>
    [Fact]
    public async Task ARefusalIsNotReadAsAnEmptyCatalogue()
    {
        var (catalogue, _) = CatalogueOver(
            HttpStatusCode.UnprocessableEntity,
            """{"message":"The year field must be a number.","errors":{"year":["The year field must be a number."]}}""");

        Assert.Null(await catalogue.ReadCatalogueSizeAsync(TagPage(), TestCt));
    }

    /// <summary>
    /// The catalogue is read from the provider's API address while ownership is matched on the
    /// spelling the host is configured with. Keyed on the API address instead, an ownership match
    /// would find no stored row and every scene the library holds would read as missing.
    /// </summary>
    /// <remarks>
    /// The registrable-domain comparison does not separate the two addresses, so what is asserted is
    /// the value the identity carries rather than that the comparison would have refused it.
    /// </remarks>
    [Fact]
    public async Task TheApiAddressNeverStandsInForTheIdentitySpelling()
    {
        var (catalogue, handler) = CatalogueOver(Recorded(PageFixture));
        var identity = new ProviderEndpointPort(Configured())
            .Resolve(WhisparrGeneration.V2, new MetadataProviderEndpoints())!;

        await catalogue.ReadPageAsync(TagPage(), TestCt);

        Assert.Equal(ConfiguredSpelling, identity.IdentityEndpoint);
        Assert.NotEqual(ProviderApiBase.ThePornDb, identity.IdentityEndpoint);
        Assert.True(
            EndpointMatchGuard.SameSource(ProviderApiBase.ThePornDb, identity.IdentityEndpoint));
        Assert.StartsWith("/scenes", handler.Targets[0], StringComparison.Ordinal);
    }

    /// <summary>
    /// The credential travels in the request's own header and never in the address. A query string
    /// is written to a proxy log and to a browser history; a header is not.
    /// </summary>
    [Fact]
    public async Task TheCredentialTravelsInTheHeaderAndNeverInTheAddress()
    {
        var (catalogue, handler) = CatalogueOver(Recorded(PageFixture));

        await catalogue.ReadPageAsync(TagPage(), TestCt);

        Assert.DoesNotContain(SomeKey, handler.Targets[0], StringComparison.Ordinal);
        Assert.DoesNotContain("api_key=", handler.Targets[0], StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A tag has no route carrying an identifier segment, so the identifier is the numeric one the
    /// scene route reads out of the parameter's own key.
    /// </summary>
    [Fact]
    public async Task ATagCatalogueCarriesTheProvidersNumericIdentifier()
    {
        var (catalogue, handler) = CatalogueOver(Recorded(CeilingFixture));

        await catalogue.ReadPageAsync(TagPage(), TestCt);

        Assert.Single(handler.Targets);
        Assert.Contains("tags%5B70%5D=70", handler.Targets[0], StringComparison.Ordinal);
    }

    /// <summary>
    /// A tag page is offered no menu and costs no request. The only menu this provider fills is
    /// tags, which on a tag page would narrow a tag to itself.
    /// </summary>
    [Fact]
    public async Task ATagPageIsOfferedNoMenu()
    {
        var (catalogue, handler) = CatalogueOver(TagsMenuAnswer);

        Assert.Empty(await catalogue.ListFacetMenusAsync(WhisparrEntityKind.Tag, "70", TestCt));
        Assert.Empty(handler.Requests);
    }

    /// <summary>A studio page is offered the tags menu, filled by asking the provider.</summary>
    [Fact]
    public async Task AStudioPageIsOfferedTheTagsMenu()
    {
        var (catalogue, handler) = CatalogueOver(TagsMenuAnswer, NoScenes, NoScenes);

        var menus = await catalogue.ListFacetMenusAsync(WhisparrEntityKind.Studio, "92", TestCt);

        var menu = Assert.Single(menus);
        Assert.Equal(ThePornDbCatalogue.TagFacetKey, menu.Key);
        Assert.Equal("70", Assert.Single(menu.Values).Value);
        Assert.StartsWith("/tags", handler.Targets[0], StringComparison.Ordinal);
    }

    /// <summary>
    /// The year menu carries every year between the two edges of the entity's own catalogue, newest
    /// first, and each value is the year the provider's own parameter takes.
    /// </summary>
    [Fact]
    public async Task AYearMenuCarriesEveryYearTheCatalogueSpans()
    {
        var (catalogue, handler) = CatalogueOver(
            TagsMenuAnswer, SceneDated("2019-04-02"), SceneDated("2016-11-30"));

        var menus = await catalogue.ListFacetMenusAsync(WhisparrEntityKind.Studio, "92", TestCt);

        var years = Assert.Single(menus, menu => menu.Key == ThePornDbCatalogue.YearKey);
        Assert.Equal("Year", years.Label);
        Assert.False(years.IsTypeAhead);
        Assert.Equal(
            ["2019", "2018", "2017", "2016"], years.Values.Select(value => value.Value));
        Assert.Equal(years.Values.Select(value => value.Value), years.Values.Select(value => value.Label));

        // Both edges are read under the provider's own orderings, one row each.
        Assert.Contains($"orderBy={ThePornDbCatalogue.NewestFirst}", handler.Targets[1], StringComparison.Ordinal);
        Assert.Contains($"orderBy={ThePornDbCatalogue.OldestFirst}", handler.Targets[2], StringComparison.Ordinal);
        Assert.All(
            handler.Targets.Skip(1),
            target => Assert.Contains("per_page=1", target, StringComparison.Ordinal));
    }

    /// <summary>
    /// An edge that answers no year leaves the menu out. A year list is only meaningful over a
    /// catalogue whose extent was read, and a half-read extent would offer years at a guess.
    /// </summary>
    [Theory]
    [InlineData(NoScenes)]
    [InlineData("""{"data":[{"id":"a","date":"0001-01-01"}],"meta":{"total":1}}""")]
    public async Task AnUnreadableEdgeLeavesTheYearMenuOut(string edge)
    {
        var (catalogue, _) = CatalogueOver(TagsMenuAnswer, SceneDated("2019-04-02"), edge);

        var menus = await catalogue.ListFacetMenusAsync(WhisparrEntityKind.Studio, "92", TestCt);

        Assert.DoesNotContain(menus, menu => menu.Key == ThePornDbCatalogue.YearKey);
    }

    /// <summary>The year the surface chose reaches the provider, so it narrows the catalogue.</summary>
    [Fact]
    public async Task TheYearReachesTheProvidersOwnParameter()
    {
        var (catalogue, handler) = CatalogueOver(Recorded(CeilingFixture));

        await catalogue.ReadPageAsync(
            TagPage(
                filters: new Dictionary<string, string> { [ThePornDbCatalogue.YearKey] = "2015" }),
            TestCt);

        Assert.Contains("year=2015", handler.Targets[0], StringComparison.Ordinal);

        var (without, plain) = CatalogueOver(Recorded(CeilingFixture));
        await without.ReadPageAsync(TagPage(), TestCt);
        Assert.DoesNotContain("year=", plain.Targets[0], StringComparison.Ordinal);
    }

    /// <summary>
    /// One registration of the seam. A second, whichever provider it named, would be shadowed rather
    /// than failing, so it would be dead wiring a reader takes for the live path.
    /// </summary>
    [Fact]
    public void TheSeamIsRegisteredExactlyOnce()
    {
        var services = new ServiceCollection();

        services.AddMissingProviders();

        Assert.Single(
            services, registration => registration.ServiceType == typeof(IProviderCatalogue));
    }

    /// <summary>
    /// The whole composed request against the live service, on a machine that holds a credential
    /// for it. Every other case here answers from a recording, so this is the only one that reports
    /// a parameter the provider stopped accepting.
    /// </summary>
    /// <remarks>
    /// Skipped where no credential is present, which is every machine but a maintainer's.
    /// </remarks>
    [Fact]
    public async Task ALiveStudioPageArrivesWhereACredentialIsPresent()
    {
        var key = Environment.GetEnvironmentVariable("THEPORNDB_API_KEY");
        Assert.SkipWhen(
            string.IsNullOrWhiteSpace(key), "no ThePornDB credential is present on this machine");

        var catalogue = new ThePornDbCatalogue(
            new HttpClient { Timeout = WhisparrClient.RequestTimeout },
            new ProviderEndpointPort(Configured(key!)),
            new OptionsStore(new FakeStore()),
            new ProviderPacer(),
            NullLogger.Instance);

        var page = await PageFrom(catalogue, StudioPage(), TestCt);

        Assert.NotEmpty(page.Scenes);
        Assert.True(page.CatalogueSize > page.Scenes.Count);
        Assert.All(page.Scenes, scene => Assert.NotEmpty(scene.Title));
    }

    /// <summary>One page of the tags route, in the shape the provider serves it.</summary>
    private const string TagsMenuAnswer = """{"data":[{"id":70,"name":"Anal"}],"meta":{"total":1}}""";

    /// <summary>A scenes page listing nothing.</summary>
    private const string NoScenes = """{"data":[],"meta":{"total":0}}""";

    /// <summary>One row of the scenes route, carrying the release date the edge read looks at.</summary>
    private static string SceneDated(string date)
        => $$$"""
            {"data":[{"id":"a-scene","title":"A scene","date":"{{{date}}}"}],"meta":{"total":1}}
            """;

    private static JsonObject Response(string fixture)
        => JsonNode.Parse(ProbeFixtures.Read(fixture))!["response"]!.DeepClone().AsObject();

    private static string Recorded(string fixture) => Response(fixture).ToJsonString();

    private static string RecordedSite()
        => JsonNode.Parse(ProbeFixtures.Read(LookupFixture))!["cases"]!["siteByUuid"]!["response"]!
            .DeepClone()
            .ToJsonString();

    private static ProviderCatalogueRequest TagPage(
        int perPage = 40, IReadOnlyDictionary<string, string>? filters = null)
        => new(
            WhisparrEntityKind.Tag,
            "70",
            1,
            perPage,
            null,
            null,
            filters ?? new Dictionary<string, string>());

    private static ProviderCatalogueRequest StudioPage(string providerEntityId = StudioUuid)
        => new(
            WhisparrEntityKind.Studio,
            providerEntityId,
            1,
            40,
            null,
            null,
            new Dictionary<string, string>());

    private static CoveConfiguration Configured(string key = SomeKey)
    {
        var config = new CoveConfiguration();
        config.Scraping.MetadataServers.Add(
            new MetadataServerInstance
            {
                Endpoint = ConfiguredSpelling,
                ApiKey = key,
                Name = "ThePornDB",

                // Zero paces nothing, so these cases do not wait on a limiter to settle a question
                // about a composed request.
                MaxRequestsPerMinute = 0,
            });

        return config;
    }

    private static (ThePornDbCatalogue Catalogue, BodyRecordingHandler Handler) CatalogueOver(
        params string[] answers)
        => CatalogueOver(
            BodyRecordingHandler.AnsweringInTurn(
                [.. answers.Select(answer => (HttpStatusCode.OK, answer))]));

    private static (ThePornDbCatalogue Catalogue, BodyRecordingHandler Handler) CatalogueOver(
        HttpStatusCode status, string answer)
        => CatalogueOver(BodyRecordingHandler.Answering(status, answer));

    private static (ThePornDbCatalogue Catalogue, BodyRecordingHandler Handler) CatalogueOver(
        BodyRecordingHandler handler)
    {
        var catalogue = new ThePornDbCatalogue(
            new HttpClient(handler),
            new ProviderEndpointPort(Configured()),
            new OptionsStore(new FakeStore()),
            new ProviderPacer(),
            NullLogger.Instance);

        return (catalogue, handler);
    }

    // A page the provider really served. The answer carries none where nothing arrived, so a case
    // about what a page projects states that a page arrived before reading one.
    private static async Task<ProviderCataloguePage> PageFrom(
        ThePornDbCatalogue catalogue, ProviderCatalogueRequest request, CancellationToken ct)
    {
        var answer = await catalogue.ReadPageAsync(request, ct);

        Assert.NotNull(answer.Page);
        return answer.Page;
    }
}
