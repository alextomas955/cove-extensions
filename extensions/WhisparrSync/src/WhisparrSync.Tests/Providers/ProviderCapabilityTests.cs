using System.Net;
using Cove.Core.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using WhisparrSync.Contracts;
using WhisparrSync.Options;
using WhisparrSync.Providers;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.Providers;

// What each source can be asked for, read off the source rather than off a table beside it. A
// capability one holds and the other does not is a member that answers, or a role the type does not
// implement.
public sealed class ProviderCapabilityTests
{
    private const string SomeKey = "0e2e0e2e0e2e0e2e0e2e0e2e0e2e0e2e";

    // ThePornDB's ordering vocabulary declares no title value and refuses one it does not declare.
    // A year is two bounds, and StashDB's scene query carries one date criterion with no inclusive
    // modifier, so a year is not expressible on it.
    [Fact]
    public void EachSourceOffersTheOrderingsItsOwnVocabularyDeclares()
    {
        Assert.DoesNotContain(
            ThePornDb().Sorts,
            sort => sort.Label.Contains("Title", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            StashDb().Sorts,
            sort => sort.Label.Contains("Title", StringComparison.OrdinalIgnoreCase));
    }

    // ThePornDB's scene route carries a picture beside the row Cove stores. StashDB was measured for
    // no single-scene read, so it implements no cover role and a page leaves its cards as composed.
    [Fact]
    public void OnlyThePornDbReadsASceneCover()
    {
        Assert.IsAssignableFrom<IReadsSceneCover>(ThePornDb());
        Assert.IsNotAssignableFrom<IReadsSceneCover>(StashDb());
    }

    // ThePornDB scene rows carry an _id beside the uuid Cove stores. StashDB names a scene by its
    // uuid alone, so it answers no number and sends nothing to find one.
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

    [Fact]
    public async Task StashDbSendsNoRequestToResolveASiteToANumber()
    {
        var handler = BodyRecordingHandler.Answering(HttpStatusCode.OK, "{}");
        var catalogue = new StashDbCatalogue(
            new HttpClient(handler),
            new ProviderEndpointPort(Configured("https://stashdb.org/graphql")),
            new OptionsStore(new FakeStore()),
            new ProviderPacer(),
            NullLogger.Instance);

        var resolved = await catalogue.ResolveNumericSiteIdAsync(
            "2846feb8-f7da-4312-a3a7-a32d32d3b865", TestContext.Current.CancellationToken);

        Assert.Equal(ProviderSiteNumber.NamesNone, resolved);
        Assert.Empty(handler.Requests);
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

        var chosen = await new ProviderCatalogueChoice(options, StashDb(), ThePornDb())
            .ChooseAsync(TestContext.Current.CancellationToken);

        Assert.Equal(named, chosen.ProviderName);
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
