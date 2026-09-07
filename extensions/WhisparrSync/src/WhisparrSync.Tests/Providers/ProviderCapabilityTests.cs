using System.Net;
using System.Reflection;
using Cove.Core.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using WhisparrSync.Contracts;
using WhisparrSync.Options;
using WhisparrSync.Providers;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.Providers;

/// <summary>
/// Which roles each provider holds, and what happens when one it does not hold is asked for.
/// </summary>
/// <remarks>
/// The two providers share almost no filter vocabulary, so a capability difference is an absent
/// registration rather than a control the surface dims. The two absences that decide the toolbar are
/// asserted by name: ThePornDB orders by no title, and StashDB filters by no year.
/// </remarks>
public sealed class ProviderCapabilityTests
{
    private const string SomeKey = "0e2e0e2e0e2e0e2e0e2e0e2e0e2e0e2e";

    /// <summary>
    /// The provider's own ordering vocabulary declares no title value and refuses one it does not
    /// declare, so the option is absent from the menu rather than offered and rejected.
    /// </summary>
    [Fact]
    public void ThePornDbOrdersByNoTitle()
    {
        var capabilities = ThePornDb().Capabilities;

        Assert.DoesNotContain(ProviderCapability.SortByTitle, capabilities.Held);
        Assert.DoesNotContain(
            ThePornDb().Sorts,
            sort => sort.Label.Contains("Title", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A year is two bounds and the provider's scene query carries one date criterion with no
    /// inclusive modifier, so a year is not expressible on it at all.
    /// </summary>
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

    /// <summary>
    /// A role the provider does not hold answers as a refusal naming what was asked for, so a caller
    /// states the absence rather than catching it.
    /// </summary>
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

    /// <summary>
    /// A role the provider holds is obtained. A set built without a source for one it holds is a
    /// construction fault rather than a statement about the provider.
    /// </summary>
    [Fact]
    public void ARoleTheProviderHoldsIsObtained()
    {
        Assert.NotNull(ThePornDb().Capabilities.Obtain<IFiltersByYear>().Match<object?>(role => role, _ => null));
        Assert.NotNull(StashDb().Capabilities.Obtain<ISortsByTitle>().Match<object?>(role => role, _ => null));
    }

    /// <summary>
    /// A role this product does not express says nothing about a provider, so it is not answerable
    /// as a refusal.
    /// </summary>
    [Fact]
    public void ARoleThisProductDoesNotExpressThrows()
    {
        Assert.Throws<InvalidOperationException>(
            () => StashDb().Capabilities.Obtain<IDisposable>());
    }

    /// <summary>
    /// Absence is the whole mechanism. A member asking whether a provider supports something would
    /// be a control the surface can render and then refuse.
    /// </summary>
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

    /// <summary>
    /// Every role this product declares is obtainable from either provider without throwing, so a
    /// set claiming a capability its source cannot honour is reported here rather than at a request.
    /// </summary>
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

    /// <summary>
    /// The name a sentence uses is the name of the source that answered. Held on the surface it
    /// would say one provider whichever generation is connected.
    /// </summary>
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
