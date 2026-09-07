using Microsoft.Extensions.Logging.Abstractions;
using WhisparrSync.Contracts;
using WhisparrSync.Missing;
using WhisparrSync.Monitoring;
using WhisparrSync.Providers;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.Missing;

/// <summary>
/// The three ways this surface can fail to answer, and what each leaves on screen.
/// </summary>
/// <remarks>
/// Two of the three keep the whole catalogue and state one reason above it, because the provider
/// answered and its answer is still the truth. Only a provider that answered nothing replaces the
/// grid.
/// <para>
/// The distinction between the two status abstentions is the requirement: one is permanent and one
/// clears, and a reader offered a retry for the permanent one would be offered a gesture that
/// cannot change the answer.
/// </para>
/// </remarks>
public sealed class MissingAbstentionTests
{
    private const string SomeKey = "0e2e0e2e0e2e0e2e0e2e0e2e0e2e0e2e";
    private const string StashDb = "https://stashdb.org/graphql";

    private static Uri SomeInstance => new("http://whisparr.invalid:6969");

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    /// <summary>
    /// The older generation keeps no per-scene records, so the gap is permanent and the whole
    /// catalogue still renders.
    /// </summary>
    [Fact]
    public async Task AGenerationKeepingNoSceneRecordsIsAPermanentAbsenceOverAFullPage()
    {
        var view = await PlannerOver(ScenesNamed("one", "two", "three"))
            .PlanAsync(
                Request(),
                new MissingPageContext(
                    SomeInstance,
                    SomeKey,
                    WhisparrGeneration.V2,
                    new ResolvedProvider(StashDb, "a-key", 240),
                    StatusReading: null,
                    ExclusionReading: null),
                NullLogger.Instance,
                TestCt);

        Assert.Equal(3, view.Cards.Count);
        Assert.False(view.StatusWasRead);
        Assert.True(view.StatusIsPermanentlyAbsent);
        Assert.Equal(MissingRefusalKind.WhisparrKeepsNoSceneRecords, view.Refusal);
        Assert.All(
            view.Cards, card => Assert.Equal(MissingSceneState.StatusUnknown, card.State));
    }

    /// <summary>
    /// A connected instance that did not answer is a transient gap: the catalogue still renders and
    /// a retry may clear it.
    /// </summary>
    [Fact]
    public async Task AnInstanceThatDidNotAnswerIsATransientAbsenceOverAFullPage()
    {
        var view = await PlannerOver(ScenesNamed("one", "two", "three"))
            .PlanAsync(
                Request(),
                Context(new StubSceneStatusReading(unreachable: true)),
                NullLogger.Instance,
                TestCt);

        Assert.Equal(3, view.Cards.Count);
        Assert.False(view.StatusWasRead);
        Assert.False(view.StatusIsPermanentlyAbsent);
        Assert.Equal(MissingRefusalKind.WhisparrStatusNotRead, view.Refusal);
        Assert.All(
            view.Cards, card => Assert.Equal(MissingSceneState.StatusUnknown, card.State));
    }

    /// <summary>
    /// An instance answering nothing useful for any card is the same transient gap, without a
    /// failure to catch.
    /// </summary>
    [Fact]
    public async Task AnInstanceAnsweringNoUsableStatusIsAlsoTransient()
    {
        var view = await PlannerOver(ScenesNamed("one", "two"))
            .PlanAsync(
                Request(),
                Context(new StubSceneStatusReading(presence: 500)),
                NullLogger.Instance,
                TestCt);

        Assert.Equal(2, view.Cards.Count);
        Assert.False(view.StatusWasRead);
        Assert.False(view.StatusIsPermanentlyAbsent);
        Assert.Equal(MissingRefusalKind.WhisparrStatusNotRead, view.Refusal);
    }

    /// <summary>
    /// The two status abstentions are never the same value, because a reader acts on them
    /// differently.
    /// </summary>
    [Fact]
    public async Task ThePermanentAndTheTransientAbsenceAreDifferentAnswers()
    {
        var permanent = await PlannerOver(ScenesNamed("one"))
            .PlanAsync(
                Request(),
                new MissingPageContext(
                    SomeInstance,
                    SomeKey,
                    WhisparrGeneration.V2,
                    new ResolvedProvider(StashDb, "a-key", 240),
                    StatusReading: null,
                    ExclusionReading: null),
                NullLogger.Instance,
                TestCt);

        var transient = await PlannerOver(ScenesNamed("one"))
            .PlanAsync(
                Request(),
                Context(new StubSceneStatusReading(unreachable: true)),
                NullLogger.Instance,
                TestCt);

        Assert.NotEqual(permanent.Refusal, transient.Refusal);
        Assert.NotEqual(
            permanent.StatusIsPermanentlyAbsent, transient.StatusIsPermanentlyAbsent);
        Assert.Equal(permanent.Cards.Count, transient.Cards.Count);
    }

    /// <summary>
    /// A provider that answered nothing is not turned into an empty catalogue by the derivation.
    /// </summary>
    /// <remarks>
    /// It reaches the route's own containment, which replaces the grid and states
    /// <see cref="MissingRefusalKind.ProviderUnreachable"/>. A derivation that swallowed it here
    /// would answer a page listing nothing, which reads as a catalogue with nothing missing.
    /// </remarks>
    [Fact]
    public async Task AProviderThatAnsweredNothingIsNeverReadAsAnEmptyCatalogue()
        => await Assert.ThrowsAsync<HttpRequestException>(
            async () => await PlannerOver(ScenesNamed("one"), unreachableProvider: true)
                .PlanAsync(Request(), Context(), NullLogger.Instance, TestCt));

    /// <summary>
    /// The status containment is narrower than the provider one: an instance failure keeps the
    /// grid, a provider failure does not.
    /// </summary>
    [Fact]
    public async Task AnInstanceFailureIsContainedAndAProviderFailureIsNot()
    {
        var kept = await PlannerOver(ScenesNamed("one", "two"))
            .PlanAsync(
                Request(),
                Context(new StubSceneStatusReading(unreachable: true)),
                NullLogger.Instance,
                TestCt);

        Assert.NotEmpty(kept.Cards);
        await Assert.ThrowsAsync<HttpRequestException>(
            async () => await PlannerOver(ScenesNamed("one"), unreachableProvider: true)
                .PlanAsync(Request(), Context(), NullLogger.Instance, TestCt));
    }

    /// <summary>Nothing under the derivation writes a per-scene value anywhere.</summary>
    /// <remarks>
    /// Permanent hiding is the instance's own exclusion and nothing else, so this product stores no
    /// hidden-scene list, no dismissed-scene key and no per-scene row of its own.
    /// </remarks>
    [Fact]
    public async Task NoPartOfTheDerivationWritesAPerSceneValue()
    {
        var catalogue = new StubProviderCatalogue(ScenesNamed("one", "two"));
        var owned = new StubOwnedScenes();

        var view = await new MissingPagePlanner(
            new MissingIdentityResolver(
                new StubEntityIdentities("an-entity"), catalogue, new StubEntityNames()),
            catalogue,
            owned,
            new SceneStatusPort(),
            new SceneExclusionPort())
            .PlanAsync(Request(), Context(), NullLogger.Instance, TestCt);

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

    private static MissingPageContext Context(StubSceneStatusReading? reading = null)
        => new(
            SomeInstance,
            SomeKey,
            WhisparrGeneration.V3,
            new ResolvedProvider(StashDb, "a-key", 240),
            reading ?? new StubSceneStatusReading(presence: 404),
            ExclusionReading: null);

    private static MissingPagePlanner PlannerOver(
        List<ProviderScene> scenes, bool unreachableProvider = false)
    {
        var catalogue = unreachableProvider
            ? new UnreachableProviderCatalogue()
            : (IProviderCatalogue)new StubProviderCatalogue(scenes);

        return new MissingPagePlanner(
            new MissingIdentityResolver(
                new StubEntityIdentities("an-entity"), catalogue, new StubEntityNames()),
            catalogue,
            new StubOwnedScenes(),
            new SceneStatusPort(),
            new SceneExclusionPort());
    }

    private static List<ProviderScene> ScenesNamed(params string[] ids)
        => [.. ids.Select(id => new ProviderScene(id, id, null, null, null, null, [], []))];

    /// <summary>A provider whose every read reports that nothing arrived.</summary>
    private sealed class UnreachableProviderCatalogue : IProviderCatalogue
    {
        public IReadOnlyList<ProviderSortOption> Sorts { get; } = [];

        public ProviderCapabilitySet Capabilities { get; } =
            ProviderCapabilities.ForStashDb(new object());

        public Task<ProviderCataloguePage> ReadPageAsync(
            ProviderCatalogueRequest request, CancellationToken ct)
            => throw new HttpRequestException("The provider was not reached.");

        public Task<int?> ReadCatalogueSizeAsync(
            ProviderCatalogueRequest request, CancellationToken ct)
            => throw new HttpRequestException("The provider was not reached.");

        public Task<ProviderIdentityLookup> LookUpByNameAsync(
            WhisparrEntityKind kind, string name, IReadOnlyList<string> aliases, CancellationToken ct)
            => throw new HttpRequestException("The provider was not reached.");

        public Task<IReadOnlyList<ProviderFacetMenu>> ListFacetMenusAsync(
            WhisparrEntityKind kind, string providerEntityId, CancellationToken ct)
            => throw new HttpRequestException("The provider was not reached.");
    }
}
