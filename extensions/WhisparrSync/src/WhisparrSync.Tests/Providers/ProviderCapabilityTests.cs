using System.Net;
using System.Reflection;
using Cove.Core.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using WhisparrSync.Contracts;
using WhisparrSync.Options;
using WhisparrSync.Providers;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.Providers;

public sealed class ProviderCapabilityTests
{
    private const string SomeKey = "0e2e0e2e0e2e0e2e0e2e0e2e0e2e0e2e";

    // ThePornDB's ordering vocabulary declares no title value and refuses one it does not declare.
    [Fact]
    public void ThePornDbOrdersByNoTitle()
    {
        var capabilities = ThePornDb().Capabilities;

        Assert.DoesNotContain(ProviderCapability.SortByTitle, capabilities.Held);
        Assert.DoesNotContain(
            ThePornDb().Sorts,
            sort => sort.Label.Contains("Title", StringComparison.OrdinalIgnoreCase));
    }

    // A year is two bounds. StashDB's scene query carries one date criterion with no inclusive
    // modifier, so a year is not expressible on it.
    [Fact]
    public void StashDbFiltersByNoYear()
    {
        Assert.DoesNotContain(ProviderCapability.FilterByYear, StashDb().Capabilities.Held);
    }

    [Fact]
    public void ThePornDbFiltersByYearAndStashDbOrdersByTitle()
    {
        Assert.Contains(ProviderCapability.FilterByYear, ThePornDb().Capabilities.Held);
        Assert.Contains(ProviderCapability.SortByTitle, StashDb().Capabilities.Held);
        Assert.Contains(
            StashDb().Sorts,
            sort => sort.Label.Contains("Title", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ARoleTheProviderDoesNotHoldIsARefusalAndNotAThrow()
    {
        var refusedOnThePornDb = ThePornDb()
            .Capabilities.Obtain<ISortsByTitle>()
            .Match<ProviderCapabilityRefusal?>(_ => null, refusal => refusal);
        var refusedOnStashDb = StashDb()
            .Capabilities.Obtain<IFiltersByYear>()
            .Match<ProviderCapabilityRefusal?>(_ => null, refusal => refusal);

        Assert.Equal(ProviderCapability.SortByTitle, refusedOnThePornDb!.Capability);
        Assert.Equal("ThePornDB", refusedOnThePornDb.Provider);
        Assert.Equal(ProviderCapability.FilterByYear, refusedOnStashDb!.Capability);
        Assert.Equal("StashDB", refusedOnStashDb.Provider);
    }

    [Fact]
    public void ARoleTheProviderHoldsIsObtained()
    {
        Assert.NotNull(ThePornDb().Capabilities.Obtain<IFiltersByYear>().Match<object?>(role => role, _ => null));
        Assert.NotNull(StashDb().Capabilities.Obtain<ISortsByTitle>().Match<object?>(role => role, _ => null));
    }

    [Fact]
    public void ARoleThisProductDoesNotExpressThrows()
    {
        Assert.Throws<InvalidOperationException>(
            () => StashDb().Capabilities.Obtain<IDisposable>());
    }

    // ThePornDB scene rows carry an _id beside the uuid Cove stores. StashDB names a scene by its
    // uuid alone and issues no number to resolve to.
    [Fact]
    public void OnlyThePornDbResolvesASceneToANumber()
    {
        Assert.Contains(
            ProviderCapability.ResolveNumericSceneId, ThePornDb().Capabilities.Held);
        Assert.DoesNotContain(
            ProviderCapability.ResolveNumericSceneId, StashDb().Capabilities.Held);

        var refused = StashDb()
            .Capabilities.Obtain<IResolvesNumericSceneId>()
            .Match<ProviderCapabilityRefusal?>(_ => null, refusal => refusal);

        Assert.Equal(ProviderCapability.ResolveNumericSceneId, refused!.Capability);
        Assert.Equal("StashDB", refused.Provider);
        Assert.NotNull(
            ThePornDb()
                .Capabilities.Obtain<IResolvesNumericSceneId>()
                .Match<object?>(role => role, _ => null));
    }

    // ThePornDB's site route answers an id beside the uuid Cove stores. StashDB names a site by its
    // uuid alone.
    [Fact]
    public void OnlyThePornDbResolvesASiteToANumber()
    {
        Assert.Contains(ProviderCapability.ResolveNumericSiteId, ThePornDb().Capabilities.Held);
        Assert.NotNull(
            ThePornDb()
                .Capabilities.Obtain<IResolvesNumericSiteId>()
                .Match<object?>(role => role, _ => null));
    }

    [Fact]
    public void StashDbRefusesTheSiteNumberRoleByName()
    {
        Assert.DoesNotContain(ProviderCapability.ResolveNumericSiteId, StashDb().Capabilities.Held);

        var refused = StashDb()
            .Capabilities.Obtain<IResolvesNumericSiteId>()
            .Match<ProviderCapabilityRefusal?>(_ => null, refusal => refusal);

        Assert.Equal(ProviderCapability.ResolveNumericSiteId, refused!.Capability);
        Assert.Equal("StashDB", refused.Provider);
    }

    [Fact]
    public async Task StashDbSendsNoRequestToResolveASceneToANumber()
    {
        var handler = BodyRecordingHandler.Answering(HttpStatusCode.OK, "{}");
        var catalogue = new StashDbCatalogue(
            new HttpClient(handler),
            new ProviderEndpointPort(Configured("https://stashdb.org/graphql")),
            new OptionsStore(new FakeStore()),
            new ProviderPacer(),
            NullLogger.Instance);

        var resolved = await catalogue.ResolveNumericSceneIdAsync(
            "2846feb8-f7da-4312-a3a7-a32d32d3b865", TestContext.Current.CancellationToken);

        Assert.Null(resolved);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(typeof(StashDbCatalogue))]
    [InlineData(typeof(ThePornDbCatalogue))]
    public void NeitherCatalogueDeclaresASupportsMember(Type catalogue)
    {
        Assert.DoesNotContain(
            catalogue.GetMembers(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
                    | BindingFlags.Static | BindingFlags.DeclaredOnly),
            member => member.Name.StartsWith("Supports", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(typeof(ISortsByTitle))]
    [InlineData(typeof(ISortsByDate))]
    [InlineData(typeof(ISortsByDuration))]
    [InlineData(typeof(IFiltersByYear))]
    [InlineData(typeof(IListsPerformerFacet))]
    [InlineData(typeof(IListsTagFacet))]
    [InlineData(typeof(IListsSubStudioFacet))]
    [InlineData(typeof(ISearchesTitles))]
    [InlineData(typeof(ILooksUpByName))]
    [InlineData(typeof(IResolvesNumericSceneId))]
    [InlineData(typeof(IResolvesNumericSiteId))]
    public void EveryDeclaredRoleIsAnsweredByBothProviders(Type role)
    {
        foreach (var catalogue in (IProviderCatalogue[])[StashDb(), ThePornDb()])
        {
            var obtain = typeof(ProviderCapabilitySet)
                .GetMethod(nameof(ProviderCapabilitySet.Obtain))!
                .MakeGenericMethod(role);

            Assert.NotNull(obtain.Invoke(catalogue.Capabilities, null));
        }
    }

    [Theory]
    [InlineData(WhisparrGeneration.V3, "StashDB")]
    [InlineData(WhisparrGeneration.V2, "ThePornDB")]
    public async Task TheSelectedCatalogueNamesTheSourceItsGenerationReadsFrom(
        WhisparrGeneration generation, string named)
    {
        var options = new OptionsStore(new FakeStore());
        await options.SaveAsync(
            new WhisparrSyncOptions { SelectedGeneration = generation },
            TestContext.Current.CancellationToken);

        var selected = new ProviderCatalogueSelector(options, StashDb(), ThePornDb());

        Assert.Equal(named, selected.Capabilities.Provider);
    }

    // The site-scene monitor pass runs on v2 alone and addresses a row by its scene number.
    [Theory]
    [InlineData(WhisparrGeneration.V2, true)]
    [InlineData(WhisparrGeneration.V3, false)]
    public async Task TheCatalogueAGenerationReadsThroughIssuesASceneNumberOnlyOnTheOlderOne(
        WhisparrGeneration generation, bool issuesANumber)
    {
        var options = new OptionsStore(new FakeStore());
        await options.SaveAsync(
            new WhisparrSyncOptions { SelectedGeneration = generation },
            TestContext.Current.CancellationToken);

        var selected = new ProviderCatalogueSelector(options, StashDb(), ThePornDb());

        Assert.Equal(
            issuesANumber,
            selected.Capabilities.Obtain<IResolvesNumericSceneId>().Match(_ => true, _ => false));
    }

    private static CoveConfiguration Configured(string endpoint)
    {
        var config = new CoveConfiguration();
        config.Scraping.MetadataServers.Add(
            new MetadataServerInstance
            {
                Endpoint = endpoint,
                ApiKey = SomeKey,
                Name = "provider",
                MaxRequestsPerMinute = 0,
            });

        return config;
    }

    private static StashDbCatalogue StashDb()
        => new(
            new HttpClient(BodyRecordingHandler.Answering(HttpStatusCode.OK, "{}")),
            new ProviderEndpointPort(Configured("https://stashdb.org/graphql")),
            new OptionsStore(new FakeStore()),
            new ProviderPacer(),
            NullLogger.Instance);

    private static ThePornDbCatalogue ThePornDb()
        => new(
            new HttpClient(BodyRecordingHandler.Answering(HttpStatusCode.OK, "{}")),
            new ProviderEndpointPort(Configured("https://theporndb.net/graphql")),
            new OptionsStore(new FakeStore()),
            new ProviderPacer(),
            NullLogger.Instance);
}
