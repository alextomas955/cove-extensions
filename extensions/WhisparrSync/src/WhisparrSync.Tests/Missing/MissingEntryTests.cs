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

    // v2 addresses no performer, so it holds no role one can be monitored through and the two
    // monitor modes are absent rather than present and refused on press. Both generations track a
    // catalogue, so the first mode stands on either.
    [Theory]
    [InlineData(WhisparrGeneration.V3, true)]
    [InlineData(WhisparrGeneration.V2, false)]
    public async Task APerformerIsOfferedOneMonitorModeOnlyWhereTheInstanceAddressesOne(
        WhisparrGeneration generation, bool monitored)
    {
        await using var host = await MonitorHost.CreateAsync(generation: generation);
        var performerId = await host.SeedPerformerAsync(
            MonitorHost.StoredEndpoint, MonitorHost.PerformerRemoteIdValue);

        var offer = await OfferAsync(host, "performer", performerId);

        MissingEntryMode[] expected = monitored
            ? [MissingEntryMode.TrackOnly, MissingEntryMode.MonitorAllScenes]
            : [MissingEntryMode.TrackOnly];

        Assert.Equal(expected, offer.Modes);
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
        nameof(IWhisparrEntityTrackingActing.TrackEntityAsync),
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
            nameof(IWhisparrEntityTrackingActing.TrackEntityAsync),
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
            nameof(IWhisparrEntityTrackingActing.TrackEntityAsync),
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
            acting => acting.Verb == nameof(IWhisparrEntityTrackingActing.TrackEntityAsync));
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
