using System.Net;
using Cove.Core.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using WhisparrSync.Contracts;
using WhisparrSync.Missing;
using WhisparrSync.Options;
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
    /// Whisparr v2 keeps no per-scene records, so the gap is permanent and the whole
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
    /// The grid is replaced and <see cref="MissingRefusalKind.ProviderUnreachable"/> is stated. A
    /// derivation that read the failure as a page would answer a page listing nothing, which reads
    /// as a catalogue with nothing missing.
    /// </remarks>
    [Fact]
    public async Task AProviderThatAnsweredNothingIsNeverReadAsAnEmptyCatalogue()
    {
        var view = await PlannerOver(ScenesNamed("one"), unreachableProvider: true)
            .PlanAsync(Request(), Context(), NullLogger.Instance, TestCt);

        Assert.Equal(MissingRefusalKind.ProviderUnreachable, view.Refusal);
        Assert.Empty(view.Cards);
        Assert.Equal(0, view.CatalogueSize);
    }

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

        var replaced = await PlannerOver(ScenesNamed("one"), unreachableProvider: true)
            .PlanAsync(Request(), Context(), NullLogger.Instance, TestCt);

        Assert.NotEmpty(kept.Cards);
        Assert.Empty(replaced.Cards);
        Assert.Equal(MissingRefusalKind.ProviderUnreachable, replaced.Refusal);
    }

    /// <summary>
    /// A source that refused the name lookup is not an entity the source has no id for.
    /// </summary>
    /// <remarks>
    /// The name lookup runs before any page is read, so it is the first place a refused credential
    /// can be turned into a settled fact about the library. The two refusals are asserted apart
    /// because only one of them offers a Refresh.
    /// </remarks>
    [Fact]
    public async Task ASourceThatRefusedTheNameLookupIsNeverReadAsAnEntityItHasNoIdFor()
    {
        var catalogue = RefusingCatalogue();

        var view = await new MissingPagePlanner(
            new MissingIdentityResolver(
                new StubEntityIdentities(null),
                catalogue,
                new StubEntityNames(new EntityName("Brazzers", []))),
            catalogue,
            new StubOwnedScenes(),
            new SceneStatusPort(),
            new SceneExclusionPort())
            .PlanAsync(Request(), Context(), NullLogger.Instance, TestCt);

        Assert.Equal(MissingRefusalKind.ProviderUnreachable, view.Refusal);
        Assert.NotEqual(MissingRefusalKind.NoProviderIdForEntity, view.Refusal);
        Assert.Empty(view.Cards);
    }

    /// <summary>
    /// A source that answered and names no such entity is still the entity refusal, so the two are
    /// told apart by what happened rather than by both landing on the same value.
    /// </summary>
    [Fact]
    public async Task ASourceThatNamesNoSuchEntityIsStillTheEntityRefusal()
    {
        var catalogue = new StubProviderCatalogue(ScenesNamed("one"));

        var view = await new MissingPagePlanner(
            new MissingIdentityResolver(
                new StubEntityIdentities(null),
                catalogue,
                new StubEntityNames(new EntityName("Brazzers", []))),
            catalogue,
            new StubOwnedScenes(),
            new SceneStatusPort(),
            new SceneExclusionPort())
            .PlanAsync(Request(), Context(), NullLogger.Instance, TestCt);

        Assert.Equal(MissingRefusalKind.NoProviderIdForEntity, view.Refusal);
        Assert.Empty(view.Cards);
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
            ? RefusingCatalogue()
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

    /// <summary>The shipped StashDB catalogue over a transport that refuses the credential.</summary>
    /// <remarks>
    /// The shipped type rather than a stub of it. A stub that reports a failure the shipped
    /// catalogue never reports would leave this whole file asserting the stub.
    /// </remarks>
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
