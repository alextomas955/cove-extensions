using Microsoft.Extensions.Logging.Abstractions;
using WhisparrSync.Contracts;
using WhisparrSync.Missing;
using WhisparrSync.Monitoring;
using WhisparrSync.Providers;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.Missing;

/// <summary>
/// Every entity kind reaches the same derivation, and each resolves its identifier the same way.
/// </summary>
/// <remarks>
/// Driven per kind rather than once with a kind argument, so a kind served by an arm nobody wrote
/// fails here rather than answering the refusal a studio with no identifier answers.
/// <para>
/// The instance publishes no tag entity, so the tag case additionally asserts that no entity probe
/// is spent. A read that widened into a catalogue enumeration to have something to ask would answer
/// the same states at a cost that grows with what the instance holds.
/// </para>
/// </remarks>
public sealed class MissingKindCoverageTests
{
    private const string SomeKey = "0e2e0e2e0e2e0e2e0e2e0e2e0e2e0e2e";
    private const string StashDb = "https://stashdb.org/graphql";

    private static Uri SomeInstance => new("http://whisparr.invalid:6969");

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    public static TheoryData<WhisparrEntityKind> EveryKind =>
        [WhisparrEntityKind.Studio, WhisparrEntityKind.Performer, WhisparrEntityKind.Tag];

    /// <summary>An entity the library already identifies reaches the catalogue and gets cards.</summary>
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

    /// <summary>
    /// An entity the library holds no identifier for reaches the provider's exact-name lookup, with
    /// the name and aliases the library holds.
    /// </summary>
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

    /// <summary>
    /// An entity neither step identifies says so and asks the provider for no catalogue at all.
    /// </summary>
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

    /// <summary>
    /// An ambiguous name lookup counts as no identifier, because choosing between two exact matches
    /// would leave the answer to match order.
    /// </summary>
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

    /// <summary>
    /// A studio and a performer are probed once for the whole page; a tag has no entity to probe.
    /// </summary>
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
            new MissingIdentityResolver(new StubEntityIdentities(identity), catalogue, names),
            catalogue,
            new StubOwnedScenes(),
            new SceneStatusPort(),
            new SceneExclusionPort());

    private static List<ProviderScene> ScenesNamed(params string[] ids)
        => [.. ids.Select(id => new ProviderScene(id, id, null, null, null, null, [], []))];
}
