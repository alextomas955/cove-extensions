using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cove.Core.Auth;
using WhisparrSync.Contracts;
using WhisparrSync.Monitoring;
using WhisparrSync.Providers;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Library;

/// <summary>
/// The card status route: what it refuses, what it answers, and what it never reaches.
/// </summary>
/// <remarks>
/// Driven through the shipped registration rather than by calling the handler. A handler called
/// directly agrees with a route mounted at the wrong pattern, bound to a body the browser cannot
/// send, or reachable by a caller the declaration excludes.
/// </remarks>
public sealed class LibraryStatusRouteTests
{
    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    private static string Asking(params int[] coveIds) => JsonSerializer.Serialize(new { coveIds });

    private static Task<int> StudioIn(MonitorHost host)
        => host.SeedStudioAsync(MonitorHost.StoredEndpoint, MonitorHost.StudioRemoteIdValue);

    private static async Task<LibraryStatusView> ReadAsync(HttpResponseMessage answered)
    {
        answered.EnsureSuccessStatusCode();
        return (await answered.Content.ReadFromJsonAsync<LibraryStatusView>(TestCt))!;
    }

    /// <summary>A body this route cannot express is refused before anything is read.</summary>
    /// <remarks>
    /// The cap is one rendered page. A body over it is one no page of this surface can produce, and
    /// an identifier below one names no Cove entity.
    /// </remarks>
    [Fact]
    public async Task ABodyThisRouteCannotExpressIsRefused()
    {
        await using var host = await MonitorHost.CreateAsync();

        string[] refused =
        [
            Asking(),
            Asking(0),
            Asking(-1),
            Asking([.. Enumerable.Range(1, 41)]),
        ];

        foreach (var body in refused)
        {
            var answered = await host.PostLibraryStatusAsync("studio", body);
            Assert.Equal(HttpStatusCode.BadRequest, answered.StatusCode);
        }

        Assert.Empty(host.Client.Acting);
    }

    /// <summary>A kind segment naming nothing this route answers for is a bad request.</summary>
    /// <remarks>
    /// A video is refused here rather than answered: it is a card kind this product expresses and no
    /// entity it monitors, so nothing answers for one yet and a state drawn from a guess would be a
    /// claim no instance made.
    /// </remarks>
    [Theory]
    [InlineData("video")]
    [InlineData("tag")]
    [InlineData("7")]
    [InlineData("studios")]
    public async Task AKindThisRouteDoesNotAnswerForIsARefusedRequest(string kind)
    {
        await using var host = await MonitorHost.CreateAsync();

        var answered = await host.PostLibraryStatusAsync(kind, Asking(1));

        Assert.Equal(HttpStatusCode.BadRequest, answered.StatusCode);
    }

    /// <summary>One row per requested identifier, in the order requested.</summary>
    /// <remarks>
    /// The row count is the caller's own. A route answering one row per entity the instance holds
    /// would grow with the library whatever the caller asked about.
    /// </remarks>
    [Fact]
    public async Task TheAnswerCarriesOneRowPerRequestedIdInTheOrderRequested()
    {
        await using var host = await MonitorHost.CreateAsync();
        var first = await StudioIn(host);
        var second = await host.SeedStudioAsync(MonitorHost.StoredEndpoint, null);

        var view = await ReadAsync(await host.PostLibraryStatusAsync("studio", Asking(second, first)));

        Assert.Equal([second, first], view.Rows.Select(row => row.CoveId));
    }

    /// <summary>A studio the library names no usable identifier for carries no reading at all.</summary>
    [Fact]
    public async Task AStudioWithNoUsableLinkCarriesNoReading()
    {
        await using var host = await MonitorHost.CreateAsync();
        var unlinked = await host.SeedStudioAsync(MonitorHost.StoredEndpoint, null);

        var view = await ReadAsync(await host.PostLibraryStatusAsync("studio", Asking(unlinked)));

        Assert.Null(Assert.Single(view.Rows).Reading);
    }

    /// <summary>Nothing configured is stated once for the page and carries no rows.</summary>
    [Fact]
    public async Task NothingConnectedIsStatedOnceForThePage()
    {
        await using var host = await MonitorHost.CreateAsync(apiKey: null);
        await StudioIn(host);

        var view = await ReadAsync(await host.PostLibraryStatusAsync("studio", Asking(1)));

        Assert.Empty(view.Rows);
        Assert.Equal(LibraryStatusRefusalKind.NoInstanceConnected, view.Refusal);
    }

    /// <summary>
    /// A generation registering no role for a kind refuses from the absent registration rather than
    /// throwing.
    /// </summary>
    /// <remarks>
    /// The capability table is the evidence. A handler asking which generation is connected would
    /// answer the same refusal and would go on answering it after the generation gained a role.
    /// </remarks>
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

    /// <summary>The route sits at the read tier, which is the tier a library viewer already holds.</summary>
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

    /// <summary>Reading a status reaches no metadata provider at all.</summary>
    /// <remarks>
    /// Driven in a container whose provider throws on every member, so a reach is a failure here
    /// rather than an answer nobody inspected. The identifier each card is named by is the library's
    /// own stored row, so there is no lookup to make.
    /// </remarks>
    [Fact]
    public async Task ReadingAStatusReachesNoMetadataProvider()
    {
        await using var host = await MonitorHost.CreateAsync(catalogue: new ThrowingCatalogue());
        var studioId = await StudioIn(host);

        var view = await ReadAsync(await host.PostLibraryStatusAsync("studio", Asking(studioId)));

        Assert.Single(view.Rows);
        Assert.Equal(LibraryStatusRefusalKind.None, view.Refusal);
    }

    /// <summary>A provider no route on this path may reach.</summary>
    private sealed class ThrowingCatalogue : IProviderCatalogue
    {
        public IReadOnlyList<ProviderSortOption> Sorts => throw Reached();

        public ProviderCapabilitySet Capabilities => throw Reached();

        public Task<ProviderCatalogueAnswer> ReadPageAsync(
            ProviderCatalogueRequest request, CancellationToken ct) => throw Reached();

        public Task<int?> ReadCatalogueSizeAsync(
            ProviderCatalogueRequest request, CancellationToken ct) => throw Reached();

        public Task<ProviderIdentityLookup> LookUpByNameAsync(
            WhisparrEntityKind kind, string name, IReadOnlyList<string> aliases, CancellationToken ct)
            => throw Reached();

        public Task<IReadOnlyList<ProviderFacetMenu>> ListFacetMenusAsync(
            WhisparrEntityKind kind, string providerEntityId, CancellationToken ct) => throw Reached();

        private static InvalidOperationException Reached()
            => new("A library card status read reached a metadata provider.");
    }
}
