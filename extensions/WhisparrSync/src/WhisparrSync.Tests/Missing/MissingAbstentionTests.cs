using System.Net;
using Cove.Core.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using WhisparrSync.Contracts;
using WhisparrSync.Missing;
using WhisparrSync.Options;
using WhisparrSync.Providers;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Missing;

// An absence has to say which absence it is. A catalogue that was never read, an entity the instance
// has not been told about and an entity the source names no identifier for send a reader somewhere
// different, so none of them may collapse into an empty page.
public sealed class MissingAbstentionTests
{
    private const string SomeKey = "0e2e0e2e0e2e0e2e0e2e0e2e0e2e0e2e";
    private const string StashDb = "https://stashdb.org/graphql";

    private static Uri SomeInstance => new("http://whisparr.invalid:6969");

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AnInstanceThatDidNotAnswerIsNeverReadAsAnEmptyCatalogue()
    {
        var view = await PlannerOver()
            .PlanAsync(
                Request(),
                Context(new StubCatalogueReading(WhisparrCatalogueRefusal.NotReached)),
                NullLogger.Instance,
                TestCt);

        Assert.Equal(MissingRefusalKind.WhisparrCatalogueNotRead, view.Refusal);
        Assert.Empty(view.Cards);
        Assert.Equal(0, view.CatalogueSize);
    }

    [Fact]
    public async Task AnEntityTheInstanceDoesNotHoldIsItsOwnAnswer()
    {
        var view = await PlannerOver()
            .PlanAsync(
                Request(),
                Context(new StubCatalogueReading(WhisparrCatalogueRefusal.EntityNotHeld)),
                NullLogger.Instance,
                TestCt);

        Assert.Equal(MissingRefusalKind.EntityNotInWhisparr, view.Refusal);
        Assert.Empty(view.Cards);
    }

    // A transport failure reaching the instance is contained here, so the tab states it and offers a
    // retry rather than the request failing outright.
    [Fact]
    public async Task AFailureReachingTheInstanceIsContained()
    {
        var view = await PlannerOver()
            .PlanAsync(
                Request(),
                Context(StubCatalogueReading.Failing()),
                NullLogger.Instance,
                TestCt);

        Assert.Equal(MissingRefusalKind.WhisparrCatalogueNotRead, view.Refusal);
        Assert.Empty(view.Cards);
    }

    [Fact]
    public async Task ASourceThatRefusedTheNameLookupIsNeverReadAsAnEntityItHasNoIdFor()
    {
        var catalogue = RefusingCatalogue();

        var view = await new MissingPagePlanner(
            new MissingIdentityResolver(
                new StubEntityIdentities(null),
                TestProviderCatalogues.Naming(catalogue),
                new StubEntityNames(new EntityName("Brazzers", []))),
            TestProviderCatalogues.Naming(catalogue),
            new StubOwnedScenes(),
            new InstanceCatalogueCache(TimeProvider.System))
            .PlanAsync(Request(), Context(Listing()), NullLogger.Instance, TestCt);

        Assert.Equal(MissingRefusalKind.ProviderUnreachable, view.Refusal);
        Assert.Empty(view.Cards);
    }

    [Fact]
    public async Task ASourceThatNamesNoSuchEntityIsStillTheEntityRefusal()
    {
        var catalogue = new StubProviderCatalogue([]);

        var view = await new MissingPagePlanner(
            new MissingIdentityResolver(
                new StubEntityIdentities(null),
                TestProviderCatalogues.Naming(catalogue),
                new StubEntityNames(new EntityName("Brazzers", []))),
            TestProviderCatalogues.Naming(catalogue),
            new StubOwnedScenes(),
            new InstanceCatalogueCache(TimeProvider.System))
            .PlanAsync(Request(), Context(Listing()), NullLogger.Instance, TestCt);

        Assert.Equal(MissingRefusalKind.NoProviderIdForEntity, view.Refusal);
        Assert.Empty(view.Cards);
    }

    [Fact]
    public async Task NoPartOfTheDerivationWritesAPerSceneValue()
    {
        var view = await PlannerOver()
            .PlanAsync(
                Request(), Context(Listing("one", "two")), NullLogger.Instance, TestCt);

        // The derivation is delegate-driven and performs no I/O of its own, so the only writes it
        // could make are through a port it was handed. None of the ports it takes can write.
        Assert.Equal(2, view.Cards.Count);
        Assert.All(
            typeof(MissingPagePlanner).GetConstructors().Single().GetParameters(),
            parameter => Assert.DoesNotContain(
                parameter.ParameterType.GetMethods(),
                method => method.Name.StartsWith("Write", StringComparison.Ordinal)
                    || method.Name.StartsWith("Save", StringComparison.Ordinal)
                    || method.Name.StartsWith("Store", StringComparison.Ordinal)));
    }

    private static MissingPageRequest Request()
        => new(
            WhisparrEntityKind.Studio,
            7,
            Page: 1,
            PerPage: 40,
            Sort: null,
            TitleSearch: null,
            Filters: new Dictionary<string, string>(),
            MenusAlreadyHeld: true);

    private static MissingPageContext Context(IWhisparrEntityCatalogueReading reading)
        => new(
            SomeInstance,
            SomeKey,
            WhisparrGeneration.V3,
            new ResolvedProvider(StashDb, "a-key", 240),
            ExclusionReading: null,
            reading);

    private static StubCatalogueReading Listing(params string[] ids)
        => new(
        [
            .. ids.Select(
                id => new WhisparrCatalogueScene(
                    id, id, null, null, null, null, [], [], Monitored: false, HasFile: false)),
        ]);

    private static MissingPagePlanner PlannerOver()
        => new(
            new MissingIdentityResolver(
                new StubEntityIdentities("an-entity"),
                TestProviderCatalogues.Naming(new StubProviderCatalogue([])),
                new StubEntityNames()),
            TestProviderCatalogues.Naming(new StubProviderCatalogue([])),
            new StubOwnedScenes(),
            new InstanceCatalogueCache(TimeProvider.System));

    private sealed class StubCatalogueReading : IWhisparrEntityCatalogueReading
    {
        private readonly WhisparrEntityCatalogue? _answer;

        internal StubCatalogueReading(IReadOnlyList<WhisparrCatalogueScene> scenes)
            => _answer = WhisparrEntityCatalogue.Listing(scenes);

        internal StubCatalogueReading(WhisparrCatalogueRefusal refusal)
            => _answer = WhisparrEntityCatalogue.Refused(refusal);

        private StubCatalogueReading() => _answer = null;

        /// <summary>Answers nothing at all, as a transport that dropped does.</summary>
        internal static StubCatalogueReading Failing() => new();

        public Task<WhisparrEntityCatalogue> ReadEntityCatalogueAsync(
            Uri baseAddress,
            string apiKey,
            WhisparrGeneration generation,
            WhisparrEntityKind kind,
            string foreignId,
            CancellationToken ct)
            => _answer is { } answer
                ? Task.FromResult(answer)
                : throw new HttpRequestException("nothing answered");
    }

    // The shipped catalogue over a transport that refuses the credential. A stub could report a
    // failure the shipped catalogue never reports, leaving this file asserting the stub.
    private static StashDbCatalogue RefusingCatalogue()
    {
        var config = new CoveConfiguration();
        config.Scraping.MetadataServers.Add(
            new MetadataServerInstance
            {
                Endpoint = StashDb,
                ApiKey = SomeKey,
                Name = "stashdb",

                // Zero paces nothing, so this does not wait on a limiter to answer a refusal.
                MaxRequestsPerMinute = 0,
            });

        return new StashDbCatalogue(
            new HttpClient(BodyRecordingHandler.Answering(HttpStatusCode.Unauthorized, "{}"))
            {
                BaseAddress = new Uri(StashDb),
            },
            new ProviderEndpointPort(config),
            new OptionsStore(new FakeStore()),
            new ProviderPacer(),
            NullLogger.Instance);
    }
}
