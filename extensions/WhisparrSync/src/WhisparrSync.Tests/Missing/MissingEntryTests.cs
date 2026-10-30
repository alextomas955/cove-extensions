using System.Net.Http.Json;
using WhisparrSync.Contracts;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Missing;

// The three modes are told apart by what leaves for the instance rather than by what the route
// answers back, because all three answer the same outcome when they take.
public sealed class MissingEntryTests
{
    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AStudioIsOfferedTheCatalogueAddAndBothMonitorScopes()
    {
        await using var host = await MonitorHost.CreateAsync();
        var studioId = await host.SeedStudioAsync(
            MonitorHost.StoredEndpoint, MonitorHost.StudioRemoteIdValue);

        var offer = await OfferAsync(host, "studio", studioId);

        Assert.Equal(
            [
                MissingEntryMode.TrackOnly,
                MissingEntryMode.MonitorNewReleases,
                MissingEntryMode.MonitorAllScenes,
            ],
            offer.Modes);
    }

    // v2 holds a site and nothing else, so it addresses no performer at all: neither the monitor
    // modes nor the catalogue add is offered there, rather than being offered and refused on press.
    [Theory]
    [InlineData(WhisparrGeneration.V3, true)]
    [InlineData(WhisparrGeneration.V2, false)]
    public async Task APerformerIsOfferedTheEntryModesOnlyWhereTheInstanceAddressesOne(
        WhisparrGeneration generation, bool addressed)
    {
        await using var host = await MonitorHost.CreateAsync(generation: generation);
        var performerId = await host.SeedPerformerAsync(
            MonitorHost.StoredEndpoint, MonitorHost.PerformerRemoteIdValue);

        var offer = await OfferAsync(host, "performer", performerId);

        MissingEntryMode[] expected = addressed
            ? [MissingEntryMode.TrackOnly, MissingEntryMode.MonitorAllScenes]
            : [];

        Assert.Equal(expected, offer.Modes);
    }

    // A tag is a kind the route admits and no instance holds a role for, so it is refused at the
    // offer on both generations.
    [Theory]
    [InlineData(WhisparrGeneration.V3)]
    [InlineData(WhisparrGeneration.V2)]
    public async Task ATagIsOfferedNoEntryModeOnEitherGeneration(WhisparrGeneration generation)
    {
        await using var host = await MonitorHost.CreateAsync(generation: generation);

        var offer = await OfferAsync(host, "tag", 1);

        Assert.Empty(offer.Modes);
    }

    // The offer and the act read the same decision, so a request put straight to the route is
    // refused where the control would have offered nothing.
    [Theory]
    [InlineData(WhisparrGeneration.V2, "performer")]
    [InlineData(WhisparrGeneration.V3, "tag")]
    [InlineData(WhisparrGeneration.V2, "tag")]
    public async Task ATrackRequestForAKindTheInstanceHoldsNoRoleForSendsNothing(
        WhisparrGeneration generation, string kind)
    {
        await using var host = await MonitorHost.CreateAsync(generation: generation);
        var coveId = kind == "performer"
            ? await host.SeedPerformerAsync(
                MonitorHost.StoredEndpoint, MonitorHost.PerformerRemoteIdValue)
            : 1;

        var result = await AddAsync(
            host,
            kind,
            coveId,
            JsonContent.Create(new MissingEntryRequest(MissingEntryMode.TrackOnly)));

        Assert.Equal(MissingTrackOutcome.GenerationCannotTrackThisKind, result.Outcome);
        Assert.Empty(host.Client.Acting);
    }

    // The dialog states what the wider scope costs from this, so an offer that settled it the wrong
    // way would drop the one-way-door sentence on the generation that needs it.
    [Theory]
    [InlineData(WhisparrGeneration.V3, false)]
    [InlineData(WhisparrGeneration.V2, true)]
    public async Task TheOfferStatesWhetherAScopeChangeCarriesBack(
        WhisparrGeneration generation, bool retroactive)
    {
        await using var host = await MonitorHost.CreateAsync(generation: generation);
        var studioId = await host.SeedStudioAsync(
            MonitorHost.StoredEndpoint, MonitorHost.StudioRemoteIdValue);

        var offer = await OfferAsync(host, "studio", studioId);

        Assert.Equal(retroactive, offer.ScopeChangeIsRetroactive);
    }

    [Fact]
    public async Task NothingStoredLeavesTheEntityNoWayOfBeingAdded()
    {
        await using var host = await MonitorHost.CreateAsync(apiKey: null);
        var studioId = await host.SeedStudioAsync(
            MonitorHost.StoredEndpoint, MonitorHost.StudioRemoteIdValue);

        var offer = await OfferAsync(host, "studio", studioId);

        Assert.Empty(offer.Modes);
        Assert.Null(offer.ScopeChangeIsRetroactive);
    }

    [Theory]
    [InlineData(
        MissingEntryMode.TrackOnly,
        nameof(IWhisparrStudioCatalogueTracking.TrackStudioCatalogueAsync),
        null)]
    [InlineData(
        MissingEntryMode.MonitorNewReleases,
        nameof(IWhisparrStudioActing.AddMonitoredStudioAsync),
        MonitorScope.FutureScenes)]
    [InlineData(
        MissingEntryMode.MonitorAllScenes,
        nameof(IWhisparrStudioActing.AddMonitoredStudioAsync),
        MonitorScope.AllScenes)]
    public async Task EachModeReachesTheOperationThatCarriesItOut(
        MissingEntryMode mode, string verb, MonitorScope? scope)
    {
        await using var host = await MonitorHost.CreateAsync();
        host.Client.Answering(
            nameof(IWhisparrStudioCatalogueTracking.TrackStudioCatalogueAsync),
            MonitorHost.Json(201, MonitorHost.AddedStudio));
        var studioId = await host.SeedStudioAsync(
            MonitorHost.StoredEndpoint, MonitorHost.StudioRemoteIdValue);

        var result = await AddAsync(
            host, "studio", studioId, JsonContent.Create(new MissingEntryRequest(mode)));

        Assert.Equal(MissingTrackOutcome.Added, result.Outcome);

        var call = Assert.Single(host.Client.Acting, acting => acting.Verb == verb);
        Assert.Equal(MonitorHost.StudioRemoteIdValue, call.ForeignId);
        Assert.Equal(scope, call.Scope);
    }

    // The browser that only ever asked for the catalogue sends no body at all, and one that names no
    // mode is what a later body can be. Neither may monitor anything.
    [Theory]
    [InlineData("{}")]
    [InlineData(null)]
    public async Task ARequestNamingNoModeAddsTheEntityForItsCatalogueAlone(string? body)
    {
        await using var host = await MonitorHost.CreateAsync();
        host.Client.Answering(
            nameof(IWhisparrStudioCatalogueTracking.TrackStudioCatalogueAsync),
            MonitorHost.Json(201, MonitorHost.AddedStudio));
        var studioId = await host.SeedStudioAsync(
            MonitorHost.StoredEndpoint, MonitorHost.StudioRemoteIdValue);

        using var content = body is null
            ? null
            : new StringContent(body, System.Text.Encoding.UTF8, "application/json");

        var result = await AddAsync(host, "studio", studioId, content);

        Assert.Equal(MissingTrackOutcome.Added, result.Outcome);

        var call = Assert.Single(
            host.Client.Acting,
            acting => acting.Verb == nameof(IWhisparrStudioCatalogueTracking.TrackStudioCatalogueAsync));
        Assert.Null(call.Scope);
        Assert.DoesNotContain(
            host.Client.Acting,
            acting => acting.Verb == nameof(IWhisparrStudioActing.AddMonitoredStudioAsync));
    }

    private static async Task<MissingEntryOffer> OfferAsync(
        MonitorHost host, string kind, int coveId)
    {
        var answered = await host.Http.GetAsync(host.RouteFor(kind, coveId, "missing/entry"), TestCt);

        answered.EnsureSuccessStatusCode();
        return (await answered.Content.ReadFromJsonAsync<MissingEntryOffer>(TestCt))!;
    }

    private static async Task<MissingTrackResult> AddAsync(
        MonitorHost host, string kind, int coveId, HttpContent? content)
    {
        var answered = await host.Http.PostAsync(
            host.RouteFor(kind, coveId, "missing/track"), content, TestCt);

        answered.EnsureSuccessStatusCode();
        return (await answered.Content.ReadFromJsonAsync<MissingTrackResult>(TestCt))!;
    }
}
