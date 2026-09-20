using System.Net;
using Cove.Core.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using WhisparrSync.Contracts;
using WhisparrSync.Missing;
using WhisparrSync.Options;
using WhisparrSync.Providers;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.Missing;

public sealed class MissingAbstentionTests
{
    private const string SomeKey = "0e2e0e2e0e2e0e2e0e2e0e2e0e2e0e2e";
    private const string StashDb = "https://stashdb.org/graphql";

    private static Uri SomeInstance => new("http://whisparr.invalid:6969");

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

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

    [Fact]
    public async Task AProviderThatAnsweredNothingIsNeverReadAsAnEmptyCatalogue()
    {
        var view = await PlannerOver(ScenesNamed("one"), unreachableProvider: true)
            .PlanAsync(Request(), Context(), NullLogger.Instance, TestCt);

        Assert.Equal(MissingRefusalKind.ProviderUnreachable, view.Refusal);
        Assert.Empty(view.Cards);
        Assert.Equal(0, view.CatalogueSize);
    }

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
            new StubOwnedScenes())
            .PlanAsync(Request(), Context(), NullLogger.Instance, TestCt);

        Assert.Equal(MissingRefusalKind.ProviderUnreachable, view.Refusal);
        Assert.NotEqual(MissingRefusalKind.NoProviderIdForEntity, view.Refusal);
        Assert.Empty(view.Cards);
    }

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
            new StubOwnedScenes())
            .PlanAsync(Request(), Context(), NullLogger.Instance, TestCt);

        Assert.Equal(MissingRefusalKind.NoProviderIdForEntity, view.Refusal);
        Assert.Empty(view.Cards);
    }

    [Fact]
    public async Task NoPartOfTheDerivationWritesAPerSceneValue()
    {
        var catalogue = new StubProviderCatalogue(ScenesNamed("one", "two"));
        var owned = new StubOwnedScenes();

        var view = await new MissingPagePlanner(
            new MissingIdentityResolver(
                new StubEntityIdentities("an-entity"), catalogue, new StubEntityNames()),
            catalogue,
            owned)
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
            new StubOwnedScenes());
    }

    private static List<ProviderScene> ScenesNamed(params string[] ids)
        => [.. ids.Select(id => new ProviderScene(id, id, null, null, null, null, [], []))];

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
