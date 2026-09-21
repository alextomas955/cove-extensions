using Microsoft.Extensions.Logging.Abstractions;
using WhisparrSync.Contracts;
using WhisparrSync.Missing;
using WhisparrSync.Providers;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.Missing;

public sealed class MissingKindCoverageTests
{
    private const string SomeKey = "0e2e0e2e0e2e0e2e0e2e0e2e0e2e0e2e";
    private const string StashDb = "https://stashdb.org/graphql";

    private static Uri SomeInstance => new("http://whisparr.invalid:6969");

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    public static TheoryData<WhisparrEntityKind> EveryKind =>
        [WhisparrEntityKind.Studio, WhisparrEntityKind.Performer, WhisparrEntityKind.Tag];

    [Theory]
    [MemberData(nameof(EveryKind))]
    public async Task AnIdentifiedEntityOfEveryKindAnswersWithCards(WhisparrEntityKind kind)
    {
        var catalogue = new StubProviderCatalogue(ScenesNamed("one", "two"));
        var names = new StubEntityNames();

        var view = await PlannerOver(catalogue, identity: "an-entity", names: names)
            .PlanAsync(Request(kind), Context(), NullLogger.Instance, TestCt);

        Assert.Equal(MissingRefusalKind.None, view.Refusal);
        Assert.Equal(2, view.Cards.Count);
        Assert.Equal(1, catalogue.PageReads);

        // The library already named it, so the name lookup is not the step that answered.
        Assert.Empty(names.Reads);
    }

    [Theory]
    [MemberData(nameof(EveryKind))]
    public async Task AnEntityWithNoStoredIdentifierReachesTheNameLookup(WhisparrEntityKind kind)
    {
        var catalogue = new StubProviderCatalogue(
            ScenesNamed("one"), ProviderIdentityLookup.Matched("looked-up"));
        var names = new StubEntityNames(new EntityName("Vixen", ["Vixen Media"]));

        var view = await PlannerOver(catalogue, identity: null, names: names)
            .PlanAsync(Request(kind), Context(), NullLogger.Instance, TestCt);

        Assert.Equal(MissingRefusalKind.None, view.Refusal);
        Assert.Single(view.Cards);

        var looked = Assert.Single(catalogue.Lookups);
        Assert.Equal(kind, looked.Kind);
        Assert.Equal("Vixen", looked.Name);
        Assert.Equal(["Vixen Media"], looked.Aliases);
    }

    [Theory]
    [MemberData(nameof(EveryKind))]
    public async Task AnUnresolvableEntityRefusesAndReadsNoCatalogue(WhisparrEntityKind kind)
    {
        var catalogue = new StubProviderCatalogue(ScenesNamed("one"));

        var view = await PlannerOver(catalogue, identity: null, names: new StubEntityNames())
            .PlanAsync(Request(kind), Context(), NullLogger.Instance, TestCt);

        Assert.Equal(MissingRefusalKind.NoProviderIdForEntity, view.Refusal);
        Assert.Empty(view.Cards);
        Assert.Equal(0, catalogue.PageReads);
    }

    [Theory]
    [MemberData(nameof(EveryKind))]
    public async Task TwoExactMatchesCountAsNoIdentifier(WhisparrEntityKind kind)
    {
        var catalogue = new StubProviderCatalogue(
            ScenesNamed("one"), ProviderIdentityLookup.Ambiguous);
        var names = new StubEntityNames(new EntityName("Vixen", []));

        var view = await PlannerOver(catalogue, identity: null, names: names)
            .PlanAsync(Request(kind), Context(), NullLogger.Instance, TestCt);

        Assert.Equal(MissingRefusalKind.NoProviderIdForEntity, view.Refusal);
        Assert.Equal(0, catalogue.PageReads);
    }

    // The instance publishes no tag entity, so a tag has nothing to probe.
    [Theory]
    [InlineData(WhisparrEntityKind.Studio, 1)]
    [InlineData(WhisparrEntityKind.Performer, 1)]
    [InlineData(WhisparrEntityKind.Tag, 0)]
    public async Task TheEntityProbeIsSpentOnlyWhereTheInstancePublishesAnEntity(
        WhisparrEntityKind kind, int expectedProbes)
    {
        var reading = new StubSceneStatusReading();
        var catalogue = new StubProviderCatalogue(ScenesNamed("one", "two", "three"));

        await PlannerOver(catalogue, identity: "an-entity", names: new StubEntityNames())
            .PlanAsync(Request(kind), Context(reading), NullLogger.Instance, TestCt);

        Assert.Equal(expectedProbes, reading.PresenceReads);
        Assert.Equal(3, reading.SceneReads);
    }

    private static MissingPageRequest Request(WhisparrEntityKind kind)
        => new(
            kind,
            7,
            Page: 1,
            PerPage: 40,
            Sort: null,
            TitleSearch: null,
            Filters: new Dictionary<string, string>(),
            MenusAlreadyHeld: true);

    private static MissingPageContext Context(StubSceneStatusReading? reading = null)
        => new(
            SomeInstance,
            SomeKey,
            WhisparrGeneration.V3,
            new ResolvedProvider(StashDb, "a-key", 240),
            reading ?? new StubSceneStatusReading(presence: 404),
            ExclusionReading: null);

    private static MissingPagePlanner PlannerOver(
        StubProviderCatalogue catalogue, string? identity, StubEntityNames names)
        => new(
            new MissingIdentityResolver(
                new StubEntityIdentities(identity),
                TestProviderCatalogues.Naming(catalogue),
                names),
            TestProviderCatalogues.Naming(catalogue),
            new StubOwnedScenes());

    private static List<ProviderScene> ScenesNamed(params string[] ids)
        => [.. ids.Select(id => new ProviderScene(id, id, null, null, null, null, [], []))];
}
