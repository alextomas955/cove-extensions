using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cove.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using WhisparrSync.Contracts;
using WhisparrSync.Identity;
using WhisparrSync.Options;
using WhisparrSync.Providers;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Providers;

// Every page here is a recording of a live read. The client depends on the provider's paging
// metadata, and a document written to suit the client would agree with whatever it did.
// ThePornDB refuses a malformed request with a status outside the success range and a body naming
// the field, so the refusal cases answer both.
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

    // The provider's document revision moves often, so every page has to come from the same one.
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

    // ThePornDB refuses a page larger than its own maximum, which reads as a catalogue that could
    // not be reached.
    [Fact]
    public async Task NoComposedRequestAsksForMoreThanTheProvidersLargestPage()
    {
        var (catalogue, handler) = CatalogueOver(Recorded(PageFixture));

        await catalogue.ReadPageAsync(TagPage(perPage: 500), TestCt);

        Assert.Contains(
            $"per_page={ThePornDbCatalogue.MaxPerPage}", handler.Targets[0], StringComparison.Ordinal);
        Assert.DoesNotContain("per_page=500", handler.Targets[0], StringComparison.Ordinal);
    }

    // ThePornDB's scene route refuses the uuid Cove stores, so one read converts it to a number.
    // A uuid reaching that route is an unreachable catalogue rather than an error a reader sees.
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

    [Fact]
    public async Task TheReportedDefaultOrderingIsTheOneAnUnnamedReadSends()
    {
        var (catalogue, handler) = CatalogueOver(RecordedSite(), Recorded(PageFixture));

        await catalogue.ReadPageAsync(StudioPage(), TestCt);

        Assert.Contains(
            $"orderBy={catalogue.DefaultSort}", handler.Targets[1], StringComparison.Ordinal);
        Assert.Contains(catalogue.Sorts, offered => offered.Value == catalogue.DefaultSort);
    }

    // ThePornDB serves no more than its ceiling and clamps a page past the last one, so a size at
    // the ceiling is a floor.
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

    // The rows are cut to a count a short page was measured at while the metadata stays as the
    // provider served it, so the client's rule is what is exercised.
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

    [Fact]
    public async Task ARefusalIsNotReadAsAnEmptyCatalogue()
    {
        var (catalogue, _) = CatalogueOver(
            HttpStatusCode.UnprocessableEntity,
            """{"message":"The year field must be a number.","errors":{"year":["The year field must be a number."]}}""");

        Assert.Null(await catalogue.ReadCatalogueSizeAsync(TagPage(), TestCt));
    }

    // No page rather than an empty one. The surface reads an empty page as a catalogue that listed
    // nothing. A tag already carries the number the scene route takes, so the page costs one read
    // and the request count means something.
    [Fact]
    public async Task AReadPageOverARefusedCredentialAnswersNoPageAndIsSentOnce()
    {
        var (catalogue, handler) = CatalogueOver(HttpStatusCode.Unauthorized, "{}");

        var answer = await catalogue.ReadPageAsync(TagPage(), TestCt);

        Assert.Null(answer.Page);
        Assert.Single(handler.Requests);
    }

    // ThePornDB serves its message member inside a success status as well as outside one.
    [Fact]
    public async Task AReadPageOverARefusalInsideASuccessStatusAnswersNoPageAndIsSentOnce()
    {
        var (catalogue, handler) = CatalogueOver(
            HttpStatusCode.OK, """{"message":"Unauthenticated."}""");

        var answer = await catalogue.ReadPageAsync(TagPage(), TestCt);

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
        var (catalogue, handler) = CatalogueOver(status, "{}");

        var answer = await catalogue.ReadPageAsync(TagPage(), TestCt);

        Assert.Null(answer.Page);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task AReadPageOverAStatedStatusIsSentOnce(HttpStatusCode status)
    {
        var (catalogue, handler) = CatalogueOver(status, "{}");

        var answer = await catalogue.ReadPageAsync(TagPage(), TestCt);

        Assert.Null(answer.Page);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task AReadPageOverADroppedConnectionAnswersNoPage()
    {
        var (catalogue, _) = CatalogueOver(
            BodyRecordingHandler.AnsweringWithABodyThatStopsPartWay());

        Assert.Null((await catalogue.ReadPageAsync(TagPage(), TestCt)).Page);
    }

    [Fact]
    public async Task AReadPageOverAnAnswerPastTheReadBoundAnswersNoPage()
    {
        var (catalogue, _) = CatalogueOver(BodyRecordingHandler.AnsweringPastTheReadBound());

        Assert.Null((await catalogue.ReadPageAsync(TagPage(), TestCt)).Page);
    }

    // The catalogue is read from the API address while ownership is matched on the configured
    // spelling. Keyed on the API address, an ownership match would find no stored row and every
    // scene in the library would read as missing. The registrable-domain comparison does not
    // separate the two addresses, so the assertion is on the value the identity carries.
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

    // A query string reaches a proxy log and a browser history. A header does not.
    [Fact]
    public async Task TheCredentialTravelsInTheHeaderAndNeverInTheAddress()
    {
        var (catalogue, handler) = CatalogueOver(Recorded(PageFixture));

        await catalogue.ReadPageAsync(TagPage(), TestCt);

        Assert.DoesNotContain(SomeKey, handler.Targets[0], StringComparison.Ordinal);
        Assert.DoesNotContain("api_key=", handler.Targets[0], StringComparison.OrdinalIgnoreCase);
    }

    // A tag has no route with an identifier segment, so its number travels in the parameter key.
    [Fact]
    public async Task ATagCatalogueCarriesTheProvidersNumericIdentifier()
    {
        var (catalogue, handler) = CatalogueOver(Recorded(CeilingFixture));

        await catalogue.ReadPageAsync(TagPage(), TestCt);

        Assert.Single(handler.Targets);
        Assert.Contains("tags%5B70%5D=70", handler.Targets[0], StringComparison.Ordinal);
    }

    // The only menu this provider fills is tags, which on a tag page would narrow a tag to itself.
    [Fact]
    public async Task ATagPageIsOfferedNoMenu()
    {
        var (catalogue, handler) = CatalogueOver(TagsMenuAnswer);

        Assert.Empty(await catalogue.ListFacetMenusAsync(WhisparrEntityKind.Tag, "70", TestCt));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task AStudioPageIsOfferedTheTagsMenu()
    {
        var (catalogue, handler) = CatalogueOver(TagsMenuAnswer, NoScenes, NoScenes);

        var menus = await catalogue.ListFacetMenusAsync(WhisparrEntityKind.Studio, "92", TestCt);

        var menu = Assert.Single(menus);
        Assert.Equal(ThePornDbCatalogue.TagFacetKey, menu.Key);
        Assert.Equal("70", Assert.Single(menu.Values).Value);
        Assert.Equal(menu.Values.Count, menu.ReportedValueCount);
        Assert.StartsWith("/tags", handler.Targets[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATagsMenuCarriesTheTotalTheProviderReported()
    {
        var (catalogue, _) = CatalogueOver(BoundedTagsMenuAnswer, NoScenes, NoScenes);

        var menus = await catalogue.ListFacetMenusAsync(WhisparrEntityKind.Studio, "92", TestCt);

        var menu = Assert.Single(menus, item => item.Key == ThePornDbCatalogue.TagFacetKey);
        Assert.Equal(2, menu.Values.Count);
        Assert.Equal(400, menu.ReportedValueCount);
    }

    // The years span the two edges of the entity's own catalogue, so both edges are read.
    [Fact]
    public async Task AYearMenuCarriesEveryYearTheCatalogueSpans()
    {
        var (catalogue, handler) = CatalogueOver(
            TagsMenuAnswer, SceneDated("2019-04-02"), SceneDated("2016-11-30"));

        var menus = await catalogue.ListFacetMenusAsync(WhisparrEntityKind.Studio, "92", TestCt);

        var years = Assert.Single(menus, menu => menu.Key == ThePornDbCatalogue.YearKey);
        Assert.Equal("Year", years.Label);
        Assert.Equal(years.Values.Count, years.ReportedValueCount);
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

    // A half-read extent would offer years at a guess.
    [Theory]
    [InlineData(NoScenes)]
    [InlineData("""{"data":[{"id":"a","date":"0001-01-01"}],"meta":{"total":1}}""")]
    public async Task AnUnreadableEdgeLeavesTheYearMenuOut(string edge)
    {
        var (catalogue, _) = CatalogueOver(TagsMenuAnswer, SceneDated("2019-04-02"), edge);

        var menus = await catalogue.ListFacetMenusAsync(WhisparrEntityKind.Studio, "92", TestCt);

        Assert.DoesNotContain(menus, menu => menu.Key == ThePornDbCatalogue.YearKey);
    }

    // The fragment reaches the provider's own search parameter, so a value the menu never carried
    // is still found.
    [Fact]
    public async Task ATagFragmentReachesTheProvidersOwnSearchParameter()
    {
        var (catalogue, handler) = CatalogueOver(BoundedTagsMenuAnswer);

        var answer = await catalogue.SearchFacetValuesAsync(
            WhisparrEntityKind.Studio, "92", ThePornDbCatalogue.TagFacetKey, "ana", TestCt);

        Assert.True(answer.IsSearchable);
        Assert.Equal(["70", "71"], answer.Values?.Select(value => value.Value));
        Assert.Equal(400, answer.ReportedValueCount);
        Assert.StartsWith("/tags", handler.Targets[0], StringComparison.Ordinal);
        Assert.Contains("q=ana", handler.Targets[0], StringComparison.Ordinal);

        // The route serves thirty rows a page and declares no page size, so none is named.
        Assert.DoesNotContain("per_page=", handler.Targets[0], StringComparison.Ordinal);
    }

    // Reported as the same answer, a failed read would state that a value does not exist.
    [Fact]
    public async Task AMatchOfNothingAndAReadThatAnsweredNothingAreDifferentAnswers()
    {
        var (matched, _) = CatalogueOver("""{"data":[],"meta":{"total":0}}""");
        var (refused, _) = CatalogueOver(HttpStatusCode.InternalServerError, "{}");

        var none = await matched.SearchFacetValuesAsync(
            WhisparrEntityKind.Studio, "92", ThePornDbCatalogue.TagFacetKey, "zz", TestCt);
        var unread = await refused.SearchFacetValuesAsync(
            WhisparrEntityKind.Studio, "92", ThePornDbCatalogue.TagFacetKey, "zz", TestCt);

        Assert.Empty(none.Values!);
        Assert.Null(unread.Values);
        Assert.True(unread.IsSearchable);
    }

    // The year menu is derived from the catalogue's two date edges, not from a value list.
    [Fact]
    public async Task TheYearMenuAnswersThatItCannotBeSearched()
    {
        var (catalogue, handler) = CatalogueOver(TagsMenuAnswer);

        var answer = await catalogue.SearchFacetValuesAsync(
            WhisparrEntityKind.Studio, "92", ThePornDbCatalogue.YearKey, "201", TestCt);

        Assert.False(answer.IsSearchable);
        Assert.Null(answer.Values);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ASearchCarriesAtMostOneBoundedSetOfValues()
    {
        var rows = string.Join(
            ',',
            Enumerable.Range(1, ThePornDbCatalogue.FacetPageSize + 5)
                .Select(at => $"{{\"id\":{at},\"name\":\"Tag {at}\"}}"));
        var (catalogue, _) = CatalogueOver(
            $"{{\"data\":[{rows}],\"meta\":{{\"total\":34}}}}");

        var answer = await catalogue.SearchFacetValuesAsync(
            WhisparrEntityKind.Studio, "92", ThePornDbCatalogue.TagFacetKey, "tag", TestCt);

        Assert.Equal(ThePornDbCatalogue.FacetPageSize, answer.Values?.Count);
        Assert.Equal(34, answer.ReportedValueCount);
    }

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

    // A second registration would be shadowed rather than failing, leaving dead wiring a reader
    // takes for the live path.
    [Fact]
    public void TheSeamIsRegisteredExactlyOnce()
    {
        var services = new ServiceCollection();

        services.AddMissingProviders();

        Assert.Single(
            services,
            registration => registration.ServiceType == typeof(ProviderCatalogueSource));
    }

    // Every other case answers from a recording, so this is the only one that reports a parameter
    // the provider stopped accepting. It is skipped where no credential is present.
    [Fact]
    public async Task ALiveStudioPageArrivesWhereACredentialIsPresent()
    {
        var key = Environment.GetEnvironmentVariable("THEPORNDB_API_KEY");
        Assert.SkipWhen(
            string.IsNullOrWhiteSpace(key), "no ThePornDB credential is present on this machine");

        var catalogue = new ThePornDbCatalogue(
            new HttpClient { Timeout = WhisparrTransport.RequestTimeout },
            new ProviderEndpointPort(Configured(key!)),
            new OptionsStore(new FakeStore()),
            new ProviderPacer(),
            NullLogger.Instance);

        var page = await PageFrom(catalogue, StudioPage(), TestCt);

        Assert.NotEmpty(page.Scenes);
        Assert.True(page.CatalogueSize > page.Scenes.Count);
        Assert.All(page.Scenes, scene => Assert.NotEmpty(scene.Title));
    }

    private const string TagsMenuAnswer = """{"data":[{"id":70,"name":"Anal"}],"meta":{"total":1}}""";

    // A tags page the provider reports far more values for than it served.
    private const string BoundedTagsMenuAnswer =
        """{"data":[{"id":70,"name":"Anal"},{"id":71,"name":"Solo"}],"meta":{"total":400}}""";

    private const string NoScenes = """{"data":[],"meta":{"total":0}}""";

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

    private static int RecordedSiteNumber()
        => JsonNode.Parse(ProbeFixtures.Read(LookupFixture))!["cases"]!["siteByUuid"]!["response"]!
            ["data"]!["id"]!.GetValue<int>();

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

                // Zero paces nothing, so no case waits on the limiter.
                MaxRequestsPerMinute = 0,
            });

        return config;
    }

    // The row comes from the recorded page, which carries both identifiers as the provider served
    // them, wrapped the way the single-scene route wraps one row.
    [Fact]
    public async Task AStoredSceneIdentifierResolvesToTheProvidersOwnNumber()
    {
        var row = Response(PageFixture)["data"]!.AsArray()[0]!;
        var storedId = row["id"]!.GetValue<string>();
        var expected = row["_id"]!.GetValue<int>();
        var (catalogue, handler) = CatalogueOver(SingleScene(row));

        var resolved = await catalogue.ResolveNumericSceneIdAsync(storedId, TestCt);

        Assert.Equal(expected, resolved);
        Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
        Assert.EndsWith($"/scenes/{storedId}", handler.Requests[0].Path, StringComparison.Ordinal);
    }

    // Zero would be a number v2 then looks a row up by.
    [Fact]
    public async Task AnAnswerCarryingNoNumberResolvesToNothing()
    {
        var row = Response(PageFixture)["data"]!.AsArray()[0]!.DeepClone();
        var storedId = row["id"]!.GetValue<string>();
        row.AsObject().Remove("_id");
        var (catalogue, _) = CatalogueOver(SingleScene(row));

        Assert.Null(await catalogue.ResolveNumericSceneIdAsync(storedId, TestCt));
    }

    // The row is a recording, so the number asserted is the one the provider really issued.
    [Fact]
    public async Task AStoredSiteIdentifierResolvesToTheProvidersOwnNumber()
    {
        var (catalogue, handler) = CatalogueOver(HttpStatusCode.OK, RecordedSite());

        var resolved = await catalogue.ResolveNumericSiteIdAsync(StudioUuid, TestCt);

        Assert.Equal(RecordedSiteNumber(), resolved.Number);
        Assert.True(resolved.WasReached);
        Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
        Assert.EndsWith($"/sites/{StudioUuid}", handler.Requests[0].Path, StringComparison.Ordinal);
    }

    // Naming no such site is the provider's own answer, so a second attempt collects it again.
    [Fact]
    public async Task ASiteTheProviderNamesNoneForIsStatedAsThat()
    {
        var (catalogue, handler) = CatalogueOver(HttpStatusCode.NotFound, "{}");

        var resolved = await catalogue.ResolveNumericSiteIdAsync(StudioUuid, TestCt);

        Assert.Equal(ProviderSiteNumber.NamesNone, resolved);
        Assert.Single(handler.Requests);
    }

    // Counted together, a site nothing is known about would be reported as one the provider has no
    // number for.
    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task AReadThatNeverArrivedIsHeldApartFromASiteTheProviderNamesNoneFor(
        HttpStatusCode status)
    {
        var (catalogue, _) = CatalogueOver(status, "{}");

        var resolved = await catalogue.ResolveNumericSiteIdAsync(StudioUuid, TestCt);

        Assert.Equal(ProviderSiteNumber.NotReached, resolved);
        Assert.NotEqual(ProviderSiteNumber.NamesNone, resolved);
        Assert.False(resolved.WasReached);
    }

    [Fact]
    public async Task AConnectionThatFailsResolvesToAReadThatNeverArrived()
    {
        var (catalogue, _) = CatalogueOver(
            BodyRecordingHandler.AnsweringWithABodyThatStopsPartWay());

        Assert.Equal(
            ProviderSiteNumber.NotReached,
            await catalogue.ResolveNumericSiteIdAsync(StudioUuid, TestCt));
    }

    // A rate limiter is not the provider answering about the site.
    [Fact]
    public async Task ARateLimitedReadIsIssuedAgainAndTheSecondAnswerSettlesIt()
    {
        var (catalogue, handler) = CatalogueOver(
            BodyRecordingHandler.AnsweringInTurn(
                (HttpStatusCode.TooManyRequests, "{}"), (HttpStatusCode.OK, RecordedSite())));

        var resolved = await catalogue.ResolveNumericSiteIdAsync(StudioUuid, TestCt);

        Assert.Equal(RecordedSiteNumber(), resolved.Number);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task ARateLimitedReadOnEveryAttemptIsStillAReadThatNeverArrived()
    {
        var (catalogue, handler) = CatalogueOver(HttpStatusCode.TooManyRequests, "{}");

        var resolved = await catalogue.ResolveNumericSiteIdAsync(StudioUuid, TestCt);

        Assert.Equal(ProviderSiteNumber.NotReached, resolved);
        Assert.Equal(2, handler.Requests.Count);
    }

    // Zero is no identifier on this provider, so it is not a number to carry across.
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"data\":{}}")]
    [InlineData("{\"data\":{\"id\":0}}")]
    public async Task AnAnswerCarryingNoUsableNumberIsAReadThatEstablishedNothing(string answered)
    {
        var (catalogue, _) = CatalogueOver(HttpStatusCode.OK, answered);

        Assert.Equal(
            ProviderSiteNumber.NotReached,
            await catalogue.ResolveNumericSiteIdAsync(StudioUuid, TestCt));
    }

    private static string SingleScene(JsonNode row)
        => new JsonObject { ["data"] = row.DeepClone() }.ToJsonString();

    // The identifier this product carries is the API's own, and nothing measured says it addresses
    // a page on the provider's site. A composed address answering 404 is worse than no link.
    [Fact]
    public void NoSceneIsGivenAnAddress()
    {
        var (catalogue, _) = CatalogueOver(HttpStatusCode.OK, "{}");

        Assert.Null(catalogue.SceneAddress("2846feb8-f7da-4312-a3a7-a32d32d3b865"));
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

    // The answer carries no page where nothing arrived, so a case about projection states that a
    // page arrived before reading one.
    private static async Task<ProviderCataloguePage> PageFrom(
        ThePornDbCatalogue catalogue, ProviderCatalogueRequest request, CancellationToken ct)
    {
        var answer = await catalogue.ReadPageAsync(request, ct);

        Assert.NotNull(answer.Page);
        return answer.Page;
    }
}
