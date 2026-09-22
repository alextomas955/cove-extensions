using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cove.Core.Auth;
using WhisparrSync.Contracts;
using WhisparrSync.Providers;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Library;

// Driven through the shipped registration rather than by calling the handler. A handler called
// directly agrees with a route mounted at the wrong pattern, bound to a body the browser cannot
// send, or reachable by a caller the declaration excludes.
public sealed class LibraryStatusRouteTests
{
    // An identifier a scene is stored under in v3's namespace.
    private const string FirstScene = "023bacff-8d1d-4f27-bac5-bdaf833f5616";

    // The per-scene route answers a list, of one row where the instance holds the scene.

    // The spelling this library holds v2's identity rows under.
    private const string V2Endpoint = "theporndb.net/graphql";

    private const string V2RemoteId = "5f7c1d90-2a3b-4c6d-8e91-0b2f4a6d8c13";

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    private static string Asking(params int[] coveIds) => JsonSerializer.Serialize(new { coveIds });

    private static Task<int> StudioIn(MonitorHost host)
        => host.SeedStudioAsync(MonitorHost.StoredEndpoint, MonitorHost.StudioRemoteIdValue);

    private static async Task<LibraryStatusView> ReadAsync(HttpResponseMessage answered)
    {
        answered.EnsureSuccessStatusCode();
        return (await answered.Content.ReadFromJsonAsync<LibraryStatusView>(TestCt))!;
    }

    // An identifier below one names no Cove entity, and an empty body asks nothing.
    [Fact]
    public async Task ABodyThisRouteCannotExpressIsRefused()
    {
        await using var host = await MonitorHost.CreateAsync();

        string[] refused = [Asking(), Asking(0), Asking(-1)];

        foreach (var body in refused)
        {
            var answered = await host.PostLibraryStatusAsync("studio", body);
            Assert.Equal(HttpStatusCode.BadRequest, answered.StatusCode);
        }

        Assert.Empty(host.Client.Acting);
    }

    // Both halves are asserted, because a remainder reported on its own also holds for a route
    // that answers no page whole.
    [Fact]
    public async Task ABodyOverOnePageIsAnsweredAsFarAsThePageReaches()
    {
        await using var host = await MonitorHost.CreateAsync();

        var whole = await ReadAsync(await host.PostLibraryStatusAsync(
            "studio", Asking([.. Enumerable.Range(1, 40)])));
        var overIt = await ReadAsync(await host.PostLibraryStatusAsync(
            "studio", Asking([.. Enumerable.Range(1, 41)])));

        Assert.False(whole.MoreNotAnswered);
        Assert.Equal(40, whole.Rows.Count);

        Assert.True(overIt.MoreNotAnswered);
        Assert.Equal([.. Enumerable.Range(1, 40)], overIt.Rows.Select(row => row.CoveId));
    }

    [Theory]
    [InlineData("studio", LibraryCardKind.Studio)]
    [InlineData("performer", LibraryCardKind.Performer)]
    [InlineData("video", LibraryCardKind.Video)]
    public async Task TheAnswerNamesTheKindTheRouteSegmentAsked(string segment, LibraryCardKind kind)
    {
        await using var host = await MonitorHost.CreateAsync();

        var answered = await ReadAsync(await host.PostLibraryStatusAsync(segment, Asking(1)));

        Assert.Equal(kind, answered.Kind);
    }

    [Theory]
    [InlineData("tag")]
    [InlineData("7")]
    [InlineData("studios")]
    public async Task AKindThisRouteDoesNotAnswerForIsARefusedRequest(string kind)
    {
        await using var host = await MonitorHost.CreateAsync();

        var answered = await host.PostLibraryStatusAsync(kind, Asking(1));

        Assert.Equal(HttpStatusCode.BadRequest, answered.StatusCode);
    }

    // The row count is the caller's own. A route answering one row per entity the instance holds
    // would grow with the library whatever the caller asked about.
    [Fact]
    public async Task TheAnswerCarriesOneRowPerRequestedIdInTheOrderRequested()
    {
        await using var host = await MonitorHost.CreateAsync();
        var first = await StudioIn(host);
        var second = await host.SeedStudioAsync(MonitorHost.StoredEndpoint, null);

        var view = await ReadAsync(await host.PostLibraryStatusAsync("studio", Asking(second, first)));

        Assert.Equal([second, first], view.Rows.Select(row => row.CoveId));
    }

    [Fact]
    public async Task AStudioWithNoUsableLinkCarriesNoReading()
    {
        await using var host = await MonitorHost.CreateAsync();
        var unlinked = await host.SeedStudioAsync(MonitorHost.StoredEndpoint, null);

        var view = await ReadAsync(await host.PostLibraryStatusAsync("studio", Asking(unlinked)));

        Assert.Null(Assert.Single(view.Rows).Reading);
    }

    // The exclusion read is asked once for the whole set and the status read once per identified
    // scene, so an unidentified card costs nothing and carries no reading.
    [Fact]
    public async Task TheVideoKindAnswersOneRowPerRequestedIdAndSpeaksOnlyForTheIdentifiedOnes()
    {
        await using var host = await MonitorHost.CreateAsync();
        host.Client.HeldSceneCards[FirstScene] = new WhisparrHeldCard(true, null);
        var studioId = await host.SeedStudioAsync(null, null);
        var identified = await host.SeedStudioSceneAsync(
            studioId, MonitorHost.StoredEndpoint, FirstScene);
        var unidentified = await host.SeedStudioSceneAsync(studioId, null, null);

        var view = await ReadAsync(
            await host.PostLibraryStatusAsync("video", Asking(unidentified, identified)));

        Assert.Equal([unidentified, identified], view.Rows.Select(row => row.CoveId));
        Assert.Null(view.Rows[0].Reading);
        Assert.Equal(new LibraryCardReading(false, true, true), view.Rows[1].Reading);
        Assert.Equal([FirstScene], Assert.Single(host.Client.ExclusionReads));

        // The page costs one batch read naming only the identified scene, and no scene is read on
        // its own.
        Assert.Equal([FirstScene], Assert.Single(host.Client.SceneBatchReads));
        Assert.Empty(host.Client.SceneStatuses);
    }

    // The instance answers a not-found for the same scene, so this pins the order the two reads
    // are folded in.
    [Fact]
    public async Task AnExcludedSceneIsNotReportedAsAnAbsence()
    {
        await using var host = await MonitorHost.CreateAsync();
        host.Client.Excluded.Add(FirstScene);
        host.Client.Answering(
            nameof(IWhisparrSceneStatusReading.ReadSceneByRemoteIdAsync), MonitorHost.Json(404, ""));
        var videoId = await host.SeedStudioSceneAsync(
            await host.SeedStudioAsync(null, null), MonitorHost.StoredEndpoint, FirstScene);

        var view = await ReadAsync(await host.PostLibraryStatusAsync("video", Asking(videoId)));

        Assert.Equal(
            new LibraryCardReading(true, false, null, false), Assert.Single(view.Rows).Reading);
    }

    [Fact]
    public async Task AGenerationReadingNoSceneRecordRefusesRatherThanThrows()
    {
        Assert.DoesNotContain(
            WhisparrCapability.ReadSceneStatus,
            GenerationCapabilities.CapabilitiesOf(WhisparrGeneration.V2));

        await using var host = await MonitorHost.CreateAsync(generation: WhisparrGeneration.V2);
        var videoId = await host.SeedStudioSceneAsync(
            await host.SeedStudioAsync(null, null), MonitorHost.StoredEndpoint, FirstScene);

        var view = await ReadAsync(await host.PostLibraryStatusAsync("video", Asking(videoId)));

        Assert.Empty(view.Rows);
        Assert.Equal(LibraryStatusRefusalKind.WhisparrCannotAnswerForThisKind, view.Refusal);
    }

    [Fact]
    public async Task NothingConnectedIsStatedOnceForThePage()
    {
        await using var host = await MonitorHost.CreateAsync(apiKey: null);
        await StudioIn(host);

        var view = await ReadAsync(await host.PostLibraryStatusAsync("studio", Asking(1)));

        Assert.Empty(view.Rows);
        Assert.Equal(LibraryStatusRefusalKind.NoInstanceConnected, view.Refusal);
    }

    // The capability table is the evidence. A handler asking which generation is connected would
    // answer the same refusal and would go on answering it after the generation gained the role.
    [Fact]
    public async Task AGenerationRegisteringNoRoleForTheKindRefusesRatherThanThrows()
    {
        Assert.DoesNotContain(
            WhisparrCapability.MonitorPerformer,
            GenerationCapabilities.CapabilitiesOf(WhisparrGeneration.V2));

        await using var host = await MonitorHost.CreateAsync(generation: WhisparrGeneration.V2);
        var performer = await host.SeedPerformerAsync(
            MonitorHost.StoredEndpoint, MonitorHost.StudioRemoteIdValue);

        var view = await ReadAsync(await host.PostLibraryStatusAsync("performer", Asking(performer)));

        Assert.Empty(view.Rows);
        Assert.Equal(LibraryStatusRefusalKind.WhisparrCannotAnswerForThisKind, view.Refusal);
    }

    // Driven by the lookup answer through the shipped client over a byte-level stub, so each case
    // runs the real lookup and the real parse. A lookup naming no site the instance holds is the
    // instance answering that it holds none, which the card states rather than leaving unestablished.
    [Theory]
    [InlineData("[]")]
    [InlineData(
        """[{"tvdbId":3372,"title":"Vixen","titleSlug":"vixen"},{"tvdbId":3373,"title":"Vixen 2","titleSlug":"vixen-2"}]""")]
    public async Task AnIdentifierTheInstanceHoldsNoSiteUnderIsStatedAsHeldByNothing(string lookup)
    {
        await using var host = await MonitorHost.CreateAsync(
            generation: WhisparrGeneration.V2,
            bytes: BodyRecordingHandler.Answering(HttpStatusCode.OK, lookup));
        var studioId = await host.SeedStudioAsync(V2Endpoint, V2RemoteId);

        var view = await ReadAsync(await host.PostLibraryStatusAsync("studio", Asking(studioId)));

        Assert.Equal(new LibraryCardReading(false, false, null), Assert.Single(view.Rows).Reading);
        Assert.Equal(LibraryStatusRefusalKind.None, view.Refusal);
    }

    // An answer that is not a list of sites establishes nothing. Read as an absence it would draw a
    // badge saying the instance holds no such site, on a body no instance spoke a site in.
    [Fact]
    public async Task AnAnswerThatIsNotAListOfSitesEstablishesNeitherMember()
    {
        await using var host = await MonitorHost.CreateAsync(
            generation: WhisparrGeneration.V2,
            bytes: BodyRecordingHandler.Answering(
                HttpStatusCode.OK, """{"message":"not a list"}"""));
        var studioId = await host.SeedStudioAsync(V2Endpoint, V2RemoteId);

        var view = await ReadAsync(await host.PostLibraryStatusAsync("studio", Asking(studioId)));

        Assert.Equal(new LibraryCardReading(false, null, null), Assert.Single(view.Rows).Reading);
    }

    // The instance answers its headers and then stops sending, so the read is contained rather
    // than answered. Without this case a rule that never stated a reason would also pass.
    [Fact]
    public async Task AReadThatNeverCameBackIsStatedAsTheUnreachableReason()
    {
        await using var host = await MonitorHost.CreateAsync(
            bytes: BodyRecordingHandler.AnsweringWithABodyThatStopsPartWay());
        var studioId = await StudioIn(host);

        var view = await ReadAsync(await host.PostLibraryStatusAsync("studio", Asking(studioId)));

        Assert.Equal(new LibraryCardReading(false, null, null), Assert.Single(view.Rows).Reading);
        Assert.Equal(LibraryStatusRefusalKind.InstanceUnreachable, view.Refusal);
    }

    // The route sits at the read tier, the tier a library viewer already holds.
    [Fact]
    public async Task AReadingCallerIsServedAndACallerHoldingNothingIsRefused()
    {
        await using (var reading = await MonitorHost.CreateAsync(
            FakePrincipalAccessor.WithPermissions(Permissions.VideosRead)))
        {
            var studioId = await StudioIn(reading);
            var answered = await reading.PostLibraryStatusAsync("studio", Asking(studioId));
            Assert.Equal(HttpStatusCode.OK, answered.StatusCode);
        }

        await using var nothing = await MonitorHost.CreateAsync(
            FakePrincipalAccessor.WithPermissions());
        var refused = await nothing.PostLibraryStatusAsync("studio", Asking(1));

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }

    // The provider throws on every member, so a reach fails here rather than answering unnoticed.
    // Each card is named by the library's own stored row, so there is no lookup to make.
    [Fact]
    public async Task ReadingAStatusReachesNoMetadataProvider()
    {
        await using var host = await MonitorHost.CreateAsync(catalogue: new ThrowingCatalogue());
        var studioId = await StudioIn(host);

        var view = await ReadAsync(await host.PostLibraryStatusAsync("studio", Asking(studioId)));

        Assert.Single(view.Rows);
        Assert.Equal(LibraryStatusRefusalKind.None, view.Refusal);
    }

    private sealed class ThrowingCatalogue : IProviderCatalogue
    {
        public IReadOnlyList<ProviderSortOption> Sorts => throw Reached();

        public string DefaultSort => throw Reached();

        public string? SceneAddress(string providerSceneId) => throw Reached();

        public ProviderCapabilitySet Capabilities => throw Reached();

        public Task<ProviderCatalogueAnswer> ReadPageAsync(
            ProviderCatalogueRequest request, CancellationToken ct) => throw Reached();

        public Task<int?> ReadCatalogueSizeAsync(
            ProviderCatalogueRequest request, CancellationToken ct) => throw Reached();

        public Task<ProviderIdentityLookup> LookUpByNameAsync(
            WhisparrEntityKind kind, string name, IReadOnlyList<string> aliases, CancellationToken ct)
            => throw Reached();

        public Task<int?> ResolveNumericSceneIdAsync(string providerSceneId, CancellationToken ct)
            => throw Reached();

        public Task<ProviderSiteNumber> ResolveNumericSiteIdAsync(
            string providerSiteId, CancellationToken ct) => throw Reached();

        public Task<IReadOnlyList<ProviderFacetMenu>> ListFacetMenusAsync(
            WhisparrEntityKind kind, string providerEntityId, CancellationToken ct) => throw Reached();

        public Task<ProviderFacetSearch> SearchFacetValuesAsync(
            WhisparrEntityKind kind,
            string providerEntityId,
            string facetKey,
            string fragment,
            CancellationToken ct) => throw Reached();

        private static InvalidOperationException Reached()
            => new("A library card status read reached a metadata provider.");
    }
}
