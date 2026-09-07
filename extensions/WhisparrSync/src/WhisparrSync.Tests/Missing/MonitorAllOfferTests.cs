using Cove.Core.Auth;
using Cove.Core.Interfaces;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging.Abstractions;
using WhisparrSync.Contracts;
using WhisparrSync.Missing;
using WhisparrSync.Options;
using WhisparrSync.Providers;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Missing;

/// <summary>
/// Whether the answered page offers the whole-entity action, per entity kind and per generation.
/// </summary>
/// <remarks>
/// Driven through the route rather than the derivation. The flag is decided where the connected
/// generation's roles are obtained, so a derivation-level case would assert whatever value it was
/// handed and never see a field nothing sets.
/// </remarks>
public sealed class MonitorAllOfferTests
{
    private const string SomeKey = "0e2e0e2e0e2e0e2e0e2e0e2e0e2e0e2e";
    private const string StashDb = "https://stashdb.org/graphql";
    private const string ThePornDb = "https://theporndb.net/graphql";

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    private static MissingRefusalKind[] ReachedNoCatalogue =>
    [
        MissingRefusalKind.NoInstanceConnected,
        MissingRefusalKind.NoMetadataProviderConfigured,
        MissingRefusalKind.NoProviderIdForEntity,
        MissingRefusalKind.ProviderUnreachable,
    ];

    /// <summary>
    /// The action registers catalogue items, which needs both the scene add and an arm acting on the
    /// kind. A tag is not an entity an instance monitors and the older generation adds no catalogue
    /// item at all, so each is an absent control rather than one that answers a refusal.
    /// </summary>
    [Theory]
    [InlineData(WhisparrGeneration.V3, "studio", true)]
    [InlineData(WhisparrGeneration.V3, "performer", true)]
    [InlineData(WhisparrGeneration.V3, "tag", false)]
    [InlineData(WhisparrGeneration.V2, "studio", false)]
    [InlineData(WhisparrGeneration.V2, "performer", false)]
    [InlineData(WhisparrGeneration.V2, "tag", false)]
    public async Task ThePageOffersTheWholeEntityActionWhereTheGenerationCanRegisterScenes(
        WhisparrGeneration generation, string kind, bool offered)
    {
        var view = await ReadPageAsync(generation, kind);

        // A page that reached no catalogue offers nothing whatever the generation holds, so it would
        // agree with the expectation without the flag having been decided.
        Assert.DoesNotContain(view.Refusal, ReachedNoCatalogue);
        Assert.Equal(offered, view.MonitorAllIsOffered);
    }

    private static async Task<MissingPageView> ReadPageAsync(
        WhisparrGeneration generation, string kind)
    {
        var options = new OptionsStore(new FakeStore());
        await options.SaveAsync(
            new WhisparrSyncOptions { SelectedGeneration = generation }.WithConnectionFor(
                generation,
                new WhisparrSyncGenerationConnection { Address = "http://whisparr.invalid:6969" }),
            TestCt);

        var answered = await WhisparrSync.ReadMissingPageAsync(
            kind,
            7,
            1,
            40,
            null,
            null,
            null,
            true,
            FakePrincipalAccessor.WithPermissions(Permissions.VideosRead),
            options,
            new RecordingCredentialPort().Holding(generation, SomeKey),
            new RecordingWhisparrClient(new WhisparrResponse(200, "application/json", "[]")),
            new ProviderEndpointPort(Configured(generation)),
            PlannerOver(),
            NullLogger.Instance,
            TestCt);

        return Assert.IsType<Ok<MissingPageView>>(answered.Result).Value!;
    }

    private static MissingPagePlanner PlannerOver()
    {
        var catalogue = new StubProviderCatalogue([]);
        return new MissingPagePlanner(
            new MissingIdentityResolver(
                new StubEntityIdentities("an-entity"), catalogue, new StubEntityNames()),
            catalogue,
            new StubOwnedScenes(),
            new SceneStatusPort(),
            new SceneExclusionPort());
    }

    private static CoveConfiguration Configured(WhisparrGeneration generation)
    {
        var config = new CoveConfiguration();
        config.Scraping.MetadataServers.Add(
            new MetadataServerInstance
            {
                Endpoint = generation == WhisparrGeneration.V3 ? StashDb : ThePornDb,
                ApiKey = SomeKey,
                Name = "provider",
                MaxRequestsPerMinute = 0,
            });

        return config;
    }
}
