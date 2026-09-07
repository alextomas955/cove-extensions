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
/// What each provider is asked when an entity carries no stored identifier, and what it makes of the
/// answer.
/// </summary>
/// <remarks>
/// The library holds no tag identifier at all, so a tag page always takes this path. Every answer
/// here is a recording of a live call: the two behaviours the lookup depends on, a canonical tag
/// found only through its aliases and a common name matching several entities exactly, are the
/// provider's own rather than a document written to produce them.
/// </remarks>
public sealed class ProviderNameLookupTests
{
    private const string StashDbFixture = "stashdb-2026-09-name-lookups.json";
    private const string ThePornDbFixture = "theporndb-2026-09-name-lookups.json";

    private const string StashDbSpelling = "https://stashdb.org/graphql";
    private const string ThePornDbSpelling = "https://theporndb.net/graphql";
    private const string SomeKey = "0e2e0e2e0e2e0e2e0e2e0e2e0e2e0e2e";

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    [Fact]
    public void BothFixturesStateWhenAndWhereTheyWereRecorded()
    {
        var stashDb = JsonDocument.Parse(ProbeFixtures.Read(StashDbFixture)).RootElement;
        var thePornDb = JsonDocument.Parse(ProbeFixtures.Read(ThePornDbFixture)).RootElement;

        Assert.Equal("2026-09-06", stashDb.GetProperty("recordedOn").GetString());
        Assert.Equal(StashDbSpelling, stashDb.GetProperty("recordedAgainst").GetString());
        Assert.Equal("2026-09-06", thePornDb.GetProperty("recordedOn").GetString());
        Assert.Equal(
            "https://api.theporndb.net", thePornDb.GetProperty("recordedAgainst").GetString());
    }

    [Fact]
    public async Task AStudioNamedExactlyResolvesOnStashDb()
    {
        var catalogue = StashDbOver(StashDb("findStudioExact"));

        var found = await catalogue.LookUpByNameAsync(
            WhisparrEntityKind.Studio, "Brazzers", [], TestCt);

        Assert.False(found.IsAmbiguous);
        Assert.Equal(
            StashDbAnswer("findStudioExact")["findStudio"]!["id"]!.GetValue<string>(),
            found.ProviderEntityId);
    }

    [Fact]
    public async Task AStudioTheProviderDoesNotNameResolvesToNothingOnStashDb()
    {
        var catalogue = StashDbOver(StashDb("findStudioAbsent"));

        var found = await catalogue.LookUpByNameAsync(
            WhisparrEntityKind.Studio, "ZZZ No Such Studio Here 12345", [], TestCt);

        Assert.Null(found.ProviderEntityId);
        Assert.False(found.IsAmbiguous);
    }

    /// <summary>
    /// The alias-aware query is what a tag is asked for. The plain one answers null for a word that
    /// is an alias of a canonical tag, and reporting no catalogue for a very common tag would be a
    /// false negative under a name a reader would read as right.
    /// </summary>
    [Fact]
    public async Task ATagIsAskedForThroughTheAliasAwareQueryOnStashDb()
    {
        var (catalogue, handler) = StashDbRecording(StashDb("findTagOrAlias"));

        var found = await catalogue.LookUpByNameAsync(WhisparrEntityKind.Tag, "Anal", [], TestCt);

        Assert.Equal(
            StashDbAnswer("findTagOrAlias")["findTagOrAlias"]!["id"]!.GetValue<string>(),
            found.ProviderEntityId);
        Assert.Contains("findTagOrAlias(", handler.Requests[0].Body, StringComparison.Ordinal);
        Assert.DoesNotContain("findTag(name", handler.Requests[0].Body, StringComparison.Ordinal);
    }

    /// <summary>
    /// The plain query really does answer null for that word, so the choice of query is what decides
    /// whether the tag is found at all.
    /// </summary>
    [Fact]
    public void ThePlainTagQueryAnsweredNothingForTheSameWord()
    {
        var plain = JsonDocument.Parse(StashDb("findTagPlain"))
            .RootElement.GetProperty("data")
            .GetProperty("findTag");

        Assert.Equal(JsonValueKind.Null, plain.ValueKind);
        Assert.Equal(
            "Anal Sex", StashDbAnswer("findTagOrAlias")["findTagOrAlias"]!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task APerformerNamedExactlyOnceResolvesOnStashDb()
    {
        var catalogue = StashDbOver(StashDb("performerExact"));

        var found = await catalogue.LookUpByNameAsync(
            WhisparrEntityKind.Performer, "Mia Malkova", [], TestCt);

        Assert.NotNull(found.ProviderEntityId);
        Assert.False(found.IsAmbiguous);
    }

    /// <summary>
    /// Several performers really do carry one name, separated only by the provider's own
    /// disambiguation. A coin flip between them is a wrong identifier under a name that reads right.
    /// </summary>
    [Fact]
    public async Task SeveralExactMatchesResolveToNoIdentifierOnStashDb()
    {
        var catalogue = StashDbOver(StashDb("performerDuplicate"));

        var found = await catalogue.LookUpByNameAsync(
            WhisparrEntityKind.Performer, "Jessica", [], TestCt);

        Assert.True(found.IsAmbiguous);
        Assert.Null(found.ProviderEntityId);
    }

    /// <summary>The provider's search matches on a substring, so a near match answers nothing.</summary>
    [Fact]
    public async Task OnlyNearMatchesResolveToNothingOnStashDb()
    {
        var catalogue = StashDbOver(StashDb("performerDuplicate"));

        var found = await catalogue.LookUpByNameAsync(
            WhisparrEntityKind.Performer, "Jessic", [], TestCt);

        Assert.Null(found.ProviderEntityId);
        Assert.False(found.IsAmbiguous);
    }

    /// <summary>An alias is offered after the entity's own name, and a provider that matches on it answers.</summary>
    [Fact]
    public async Task AnAliasIsTriedAfterTheNameOnStashDb()
    {
        var (catalogue, handler) = StashDbRecording(
            StashDb("findStudioAbsent"), StashDb("findStudioExact"));

        var found = await catalogue.LookUpByNameAsync(
            WhisparrEntityKind.Studio, "ZZZ No Such Studio Here 12345", ["Brazzers"], TestCt);

        Assert.NotNull(found.ProviderEntityId);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task ASiteNamedExactlyResolvesToTheIdentifierCoveStoresOnThePornDb()
    {
        var (catalogue, _) = ThePornDbOver(ThePornDb("sitesExact"));

        var found = await catalogue.LookUpByNameAsync(
            WhisparrEntityKind.Studio, "Brazzers", [], TestCt);

        Assert.Equal("e3b61b3e-0c20-4bea-9441-b88430ed6317", found.ProviderEntityId);
    }

    [Fact]
    public async Task ASiteTheProviderDoesNotNameResolvesToNothingOnThePornDb()
    {
        var (catalogue, _) = ThePornDbOver(ThePornDb("sitesAbsent"));

        var found = await catalogue.LookUpByNameAsync(
            WhisparrEntityKind.Studio, "ZZZ No Such Site Here 12345", [], TestCt);

        Assert.Null(found.ProviderEntityId);
        Assert.False(found.IsAmbiguous);
    }

    /// <summary>
    /// A tag answers the numeric identifier, which is what the scene route reads. There is no route
    /// carrying a tag's stored identifier at all, so this lookup is the only way a tag is reached.
    /// </summary>
    [Fact]
    public async Task ATagResolvesToTheProvidersNumericIdentifierOnThePornDb()
    {
        var (catalogue, handler) = ThePornDbOver(ThePornDb("tagsExactOnFirstPage"));

        var found = await catalogue.LookUpByNameAsync(WhisparrEntityKind.Tag, "Anal", [], TestCt);

        Assert.Equal("70", found.ProviderEntityId);
        Assert.Single(handler.Targets);
    }

    /// <summary>
    /// The search is relevance-ordered and pages at thirty, so an exact name can fall past the first
    /// page and the walk has to continue to reach it.
    /// </summary>
    [Fact]
    public async Task AnExactNameOnTheSecondPageIsReachedOnThePornDb()
    {
        var (catalogue, handler) = ThePornDbOver(
            ThePornDb("tagsExactOnFirstPage"), ThePornDb("tagsSecondPage"));

        var found = await catalogue.LookUpByNameAsync(
            WhisparrEntityKind.Tag, "Double Anal Penetration (DAP)", [], TestCt);

        Assert.NotNull(found.ProviderEntityId);
        Assert.Equal(2, handler.Targets.Count);
        Assert.Contains("page=2", handler.Targets[1], StringComparison.Ordinal);
    }

    /// <summary>The walk stops where the provider says its own result ends.</summary>
    [Fact]
    public async Task TheWalkStopsAtTheProvidersLastPageOnThePornDb()
    {
        var (catalogue, handler) = ThePornDbOver(
            ThePornDb("tagsExactOnFirstPage"), ThePornDb("tagsSecondPage"));

        var found = await catalogue.LookUpByNameAsync(
            WhisparrEntityKind.Tag, "No Such Tag Here 12345", [], TestCt);

        Assert.Null(found.ProviderEntityId);
        Assert.Equal(2, handler.Targets.Count);
    }

    /// <summary>
    /// A long result is walked no further than the stated bound, so the cost of a lookup is bounded
    /// by the search rather than by how many entities carry a common word.
    /// </summary>
    [Fact]
    public async Task TheWalkStopsAtItsOwnBoundOnThePornDb()
    {
        var (catalogue, handler) = ThePornDbOver(ThePornDb("performersDuplicate"));

        var found = await catalogue.LookUpByNameAsync(
            WhisparrEntityKind.Performer, "Jessic", [], TestCt);

        Assert.Null(found.ProviderEntityId);
        Assert.Equal(ThePornDbCatalogue.MaxLookupPages, handler.Targets.Count);
    }

    [Fact]
    public async Task SeveralExactMatchesResolveToNoIdentifierOnThePornDb()
    {
        var (catalogue, _) = ThePornDbOver(ThePornDb("performersDuplicate"));

        var found = await catalogue.LookUpByNameAsync(
            WhisparrEntityKind.Performer, "Jessica", [], TestCt);

        Assert.True(found.IsAmbiguous);
        Assert.Null(found.ProviderEntityId);
    }

    [Fact]
    public async Task APerformerNamedExactlyOnceResolvesOnThePornDb()
    {
        var (catalogue, _) = ThePornDbOver(ThePornDb("performersExact"));

        var found = await catalogue.LookUpByNameAsync(
            WhisparrEntityKind.Performer, "Mia Malkova", [], TestCt);

        Assert.Equal("dcf62cfa-c156-481d-b176-1f061e61546b", found.ProviderEntityId);
    }

    /// <summary>
    /// A credential the source refuses is not an entity the source has no name for.
    /// </summary>
    /// <remarks>
    /// Read as an absence it becomes a settled fact about the library with no way to retry, and the
    /// reader is sent to check link chips a working key would have matched. The alias is supplied so
    /// the single request is the second half of the claim: a walk that carried on would send again
    /// into the same refusal and could answer the absence at the end of it.
    /// </remarks>
    [Fact]
    public async Task ARefusedCredentialIsNotAnUnnamedEntityOnStashDb()
    {
        var (catalogue, handler) = StashDbAnswering((HttpStatusCode.Unauthorized, "{}"));

        var found = await catalogue.LookUpByNameAsync(
            WhisparrEntityKind.Studio, "Brazzers", ["Brazzers Exxtra"], TestCt);

        Assert.False(found.WasReached);
        Assert.Null(found.ProviderEntityId);
        Assert.NotEqual(ProviderIdentityLookup.Unmatched, found);
        Assert.Single(handler.Requests);
    }

    /// <summary>
    /// The source states an expired key as an <c>errors</c> member inside a success status, and that
    /// is not an unnamed entity either.
    /// </summary>
    [Fact]
    public async Task ARefusalInsideASuccessStatusIsNotAnUnnamedEntityOnStashDb()
    {
        var (catalogue, handler) = StashDbAnswering(
            (HttpStatusCode.OK, """{"errors":[{"message":"not authorized"}]}"""));

        var found = await catalogue.LookUpByNameAsync(
            WhisparrEntityKind.Performer, "Mia Malkova", ["Mia"], TestCt);

        Assert.False(found.WasReached);
        Assert.Null(found.ProviderEntityId);
        Assert.NotEqual(ProviderIdentityLookup.Unmatched, found);
        Assert.Single(handler.Requests);
    }

    /// <summary>A credential this source refuses is not an entity it has no name for either.</summary>
    [Fact]
    public async Task ARefusedCredentialIsNotAnUnnamedEntityOnThePornDb()
    {
        var (catalogue, handler) = ThePornDbAnswering((HttpStatusCode.Unauthorized, "{}"));

        var found = await catalogue.LookUpByNameAsync(
            WhisparrEntityKind.Studio, "Brazzers", ["Brazzers Exxtra"], TestCt);

        Assert.False(found.WasReached);
        Assert.Null(found.ProviderEntityId);
        Assert.NotEqual(ProviderIdentityLookup.Unmatched, found);
        Assert.Single(handler.Targets);
    }

    /// <summary>
    /// This source states its own refusal as a <c>message</c> member, and that is not an unnamed
    /// entity either.
    /// </summary>
    [Fact]
    public async Task ARefusalStatedInTheBodyIsNotAnUnnamedEntityOnThePornDb()
    {
        var (catalogue, handler) = ThePornDbAnswering(
            (HttpStatusCode.OK, """{"message":"Unauthenticated."}"""));

        var found = await catalogue.LookUpByNameAsync(
            WhisparrEntityKind.Tag, "Anal", ["Anal Sex"], TestCt);

        Assert.False(found.WasReached);
        Assert.Null(found.ProviderEntityId);
        Assert.NotEqual(ProviderIdentityLookup.Unmatched, found);
        Assert.Single(handler.Targets);
    }

    /// <summary>An entity the source really has no name for is still an absence, on both sources.</summary>
    [Fact]
    public async Task AnEntityNeitherSourceNamesIsReachedAndUnmatched()
    {
        var stashDb = StashDbOver(StashDb("findStudioAbsent"));
        var (thePornDb, _) = ThePornDbOver(ThePornDb("sitesAbsent"));

        var onStashDb = await stashDb.LookUpByNameAsync(
            WhisparrEntityKind.Studio, "ZZZ No Such Studio Here 12345", [], TestCt);
        var onThePornDb = await thePornDb.LookUpByNameAsync(
            WhisparrEntityKind.Studio, "ZZZ No Such Site Here 12345", [], TestCt);

        Assert.True(onStashDb.WasReached);
        Assert.True(onThePornDb.WasReached);
    }

    private static JsonObject StashDbAnswer(string label)
        => JsonNode.Parse(ProbeFixtures.Read(StashDbFixture))!["cases"]![label]!["response"]!["data"]!
            .DeepClone()
            .AsObject();

    private static string StashDb(string label)
        => JsonNode.Parse(ProbeFixtures.Read(StashDbFixture))!["cases"]![label]!["response"]!
            .DeepClone()
            .ToJsonString();

    private static string ThePornDb(string label)
        => JsonNode.Parse(ProbeFixtures.Read(ThePornDbFixture))!["cases"]![label]!["response"]!
            .DeepClone()
            .ToJsonString();

    private static CoveConfiguration Configured(string endpoint)
    {
        var config = new CoveConfiguration();
        config.Scraping.MetadataServers.Add(
            new MetadataServerInstance
            {
                Endpoint = endpoint,
                ApiKey = SomeKey,
                Name = "provider",

                // Zero paces nothing, so a case settling a question about a composed request does
                // not wait on a limiter.
                MaxRequestsPerMinute = 0,
            });

        return config;
    }

    private static StashDbCatalogue StashDbOver(params string[] answers)
        => StashDbRecording(answers).Catalogue;

    private static (StashDbCatalogue Catalogue, BodyRecordingHandler Handler) StashDbRecording(
        params string[] answers)
        => StashDbAnswering([.. answers.Select(answer => (HttpStatusCode.OK, answer))]);

    private static (StashDbCatalogue Catalogue, BodyRecordingHandler Handler) StashDbAnswering(
        params (HttpStatusCode Status, string Answer)[] answers)
    {
        var handler = BodyRecordingHandler.AnsweringInTurn(answers);

        var catalogue = new StashDbCatalogue(
            new HttpClient(handler) { BaseAddress = new Uri(StashDbSpelling) },
            new ProviderEndpointPort(Configured(StashDbSpelling)),
            new OptionsStore(new FakeStore()),
            new ProviderPacer(),
            NullLogger.Instance);

        return (catalogue, handler);
    }

    private static (ThePornDbCatalogue Catalogue, BodyRecordingHandler Handler) ThePornDbOver(
        params string[] answers)
        => ThePornDbAnswering([.. answers.Select(answer => (HttpStatusCode.OK, answer))]);

    private static (ThePornDbCatalogue Catalogue, BodyRecordingHandler Handler) ThePornDbAnswering(
        params (HttpStatusCode Status, string Answer)[] answers)
    {
        var handler = BodyRecordingHandler.AnsweringInTurn(answers);

        var catalogue = new ThePornDbCatalogue(
            new HttpClient(handler),
            new ProviderEndpointPort(Configured(ThePornDbSpelling)),
            new OptionsStore(new FakeStore()),
            new ProviderPacer(),
            NullLogger.Instance);

        return (catalogue, handler);
    }
}
