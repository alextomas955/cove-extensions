using Cove.Core.Auth;
using Cove.Plugins;
using Microsoft.AspNetCore.Http;
using WhisparrSync.Adapters;
using WhisparrSync.Client;
using WhisparrSync.Library;
using WhisparrSync.Monitor;
using WhisparrSync.Tests.TestSupport;
using Ext = global::WhisparrSync.WhisparrSync;

namespace WhisparrSync.Tests.Api;

/// <summary>
/// The <c>/entity-status-batch</c> (studio/performer per-card status) contract. The ROUTED tier proves the
/// configure gate, the oversized-selection 400, and the v3-only refusal. The CORE tier drives the pure
/// <see cref="Ext.EntityStatusBatchCoreAsync"/> (which classifies via <see cref="V3Adapter.ClassifyEntityStatusBatch"/>)
/// over a fabricated studio set to prove each entity's monitored flag and scenesPresent/scenesTotal come from
/// Whisparr's OWN entity resource, that an unresolvable id is omitted, and an entity absent from Whisparr is
/// added:false. A final CORE test drives the library-wide <see cref="Ext.EntityLibrarySummaryCoreAsync"/>.
/// </summary>
public sealed class EntityStatusBatchEndpointTests
{
    private const string BaseUrl = "http://stored.local:6969";
    private const string ApiKey = "STORED-KEY";

    private static Ext NewExtension(FakeStore? store = null)
    {
        var ext = new Ext();
        ((IStatefulExtension)ext).SetStore(store ?? new FakeStore());
        return ext;
    }

    private static async Task<FakeStore> StoreWith(string version)
    {
        var store = new FakeStore();
        await store.SetAsync(
            "options", $"{{\"BaseUrl\":\"{BaseUrl}\",\"ApiKey\":\"{ApiKey}\",\"SelectedVersion\":\"{version}\"}}");
        return store;
    }

    private static WhisparrClient ClientJson(string json)
        => new(new HttpClient(FakeHttpMessageHandler.Json(json)));

    private static int StatusOf(IResult result)
        => Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode ?? 0;

    // ---- ROUTED tier ----

    [Fact]
    public async Task Batch_WithoutConfigure_Returns403()
    {
        var result = await NewExtension().EntityStatusBatchAsync(
            new Ext.EntityStatusBatchRequest("studio", [1]), ClientJson("[]"),
            FakePrincipalAccessor.WithPermissions(Permissions.ExtensionsRead), default);
        Assert.Equal(403, StatusOf(result));
    }

    [Fact]
    public async Task Batch_V2Performer_ReturnsVersionUnsupported400()
    {
        // v2 studios ARE supported (matched by TPDB against the site list); performers are not — v2 has no
        // performer entity. This is the server-side backstop for the performer refusal.
        var result = await NewExtension(await StoreWith("v2")).EntityStatusBatchAsync(
            new Ext.EntityStatusBatchRequest("performer", [1]), ClientJson("[]"),
            FakePrincipalAccessor.WithPermissions(Permissions.ExtensionsConfigure), default);
        Assert.Equal(400, StatusOf(result));
    }

    [Fact]
    public async Task Batch_OversizedIdList_Returns400()
    {
        var oversized = Enumerable.Range(1, 1001).ToArray();
        var result = await NewExtension(await StoreWith("v3")).EntityStatusBatchAsync(
            new Ext.EntityStatusBatchRequest("studio", oversized), ClientJson("[]"),
            FakePrincipalAccessor.WithPermissions(Permissions.ExtensionsConfigure), default);
        Assert.Equal(400, StatusOf(result));
    }

    // ---- CORE tier ----

    [Fact]
    public async Task Core_ResolvesMonitoredAndCounts_FromWhisparrEntityResource_OmitsUnresolvable()
    {
        // Studio 1's StashDB id matches monitored Whisparr studio "Aurora" (sceneCount 1 of totalSceneCount 147),
        // so the count is Whisparr's OWN present/catalog figure — no movie set involved. Id 2 has no seeded
        // identity → omitted (no badge). The core is pure over the pre-fetched studio list (handler-cached).
        WhisparrStudio[] studios =
        [
            new(10, "studio-uuid", "Aurora", Monitored: true, QualityProfileId: null, RootFolderPath: null,
                Tags: null, SceneCount: 1, TotalSceneCount: 147),
        ];
        var library = new FakeCoveLibraryPort();
        library.SeedEntityIdentity(EntityKind.Studio, 1, new CoveEntityIdentity(StashIds: ["studio-uuid"], TpdbIds: []));

        var states = await Ext.EntityStatusBatchCoreAsync(
            EntityKind.Studio, [1, 2], library, studios, [], default);

        Assert.True(states[1].Added);
        Assert.True(states[1].Monitored);
        Assert.Equal(1, states[1].ScenesPresent);
        Assert.Equal(147, states[1].ScenesTotal);
        Assert.False(states.ContainsKey(2)); // no identity → omitted, never a fabricated status
    }

    [Fact]
    public async Task Core_EntityAbsentFromWhisparr_IsAddedFalse()
    {
        // The studio resolves a StashDB id, but no Whisparr studio matches it → Added:false / 0-of-0 (the badge,
        // which renders only when monitored, shows nothing).
        var library = new FakeCoveLibraryPort();
        library.SeedEntityIdentity(EntityKind.Studio, 1, new CoveEntityIdentity(StashIds: ["missing"], TpdbIds: []));

        var states = await Ext.EntityStatusBatchCoreAsync(
            EntityKind.Studio, [1], library, [], [], default);

        Assert.False(states[1].Added);
        Assert.False(states[1].Monitored);
    }

    [Fact]
    public async Task Summary_CountsMonitoredOverWholeLibrary_IncludingUnmappable()
    {
        // Three Cove studios: two carry a StashDB id (one maps to a monitored Whisparr studio, one to an
        // unmonitored one), the third has no id at all. Total is all three (the library-wide denominator);
        // monitored is only the one matching a monitored Whisparr studio.
        WhisparrStudio[] studios =
        [
            new(10, "mon-uuid", "Monitored", Monitored: true, QualityProfileId: null, RootFolderPath: null, Tags: null),
            new(11, "unmon-uuid", "Unmonitored", Monitored: false, QualityProfileId: null, RootFolderPath: null, Tags: null),
        ];
        var library = new FakeCoveLibraryPort();
        library.SeedEntityIdentity(EntityKind.Studio, 1, new CoveEntityIdentity(StashIds: ["mon-uuid"], TpdbIds: []));
        library.SeedEntityIdentity(EntityKind.Studio, 2, new CoveEntityIdentity(StashIds: ["unmon-uuid"], TpdbIds: []));
        library.SeedEntityIdentity(EntityKind.Studio, 3, new CoveEntityIdentity(StashIds: [], TpdbIds: []));

        var summary = await Ext.EntityLibrarySummaryCoreAsync(EntityKind.Studio, library, studios, [], default);

        Assert.Equal(3, summary.Total);
        Assert.Equal(1, summary.Monitored);
    }

    // ---- v2 CORE tier (studio only — matched by ThePornDB against the site list) ----

    [Fact]
    public async Task V2Core_ResolvesStudioByTpdb_ReadsSeriesStatistics_OmitsUnresolvable()
    {
        // v2 studio 1's TPDB id matches a monitored site (tvdbId 3417); the count is the site's own statistics
        // off the list row (episodes-with-file 1 / full catalog 674) — no per-site episode fetch. Id 2 has no
        // seeded identity → omitted.
        WhisparrSeries[] series =
        [
            new(10, TvdbId: 3417, Title: "Tushy", TitleSlug: null, Path: null, Monitored: true,
                Statistics: new WhisparrSeriesStatistics(EpisodeFileCount: 1, EpisodeCount: 674, TotalEpisodeCount: 674)),
        ];
        var library = new FakeCoveLibraryPort();
        library.SeedEntityIdentity(EntityKind.Studio, 1, new CoveEntityIdentity(StashIds: [], TpdbIds: ["3417"]));

        var states = await Ext.V2EntityStatusBatchCoreAsync([1, 2], library, series, default);

        Assert.True(states[1].Added);
        Assert.True(states[1].Monitored);
        Assert.Equal(1, states[1].ScenesPresent);
        Assert.Equal(674, states[1].ScenesTotal);
        Assert.False(states.ContainsKey(2));
    }

    [Fact]
    public async Task V2Summary_CountsMonitoredStudiosByTpdb_IncludingUnmappable()
    {
        WhisparrSeries[] series =
        [
            new(10, TvdbId: 3417, Title: "Mon", TitleSlug: null, Path: null, Monitored: true),
            new(11, TvdbId: 9999, Title: "Unmon", TitleSlug: null, Path: null, Monitored: false),
        ];
        var library = new FakeCoveLibraryPort();
        library.SeedEntityIdentity(EntityKind.Studio, 1, new CoveEntityIdentity(StashIds: [], TpdbIds: ["3417"]));
        library.SeedEntityIdentity(EntityKind.Studio, 2, new CoveEntityIdentity(StashIds: [], TpdbIds: ["9999"]));
        library.SeedEntityIdentity(EntityKind.Studio, 3, new CoveEntityIdentity(StashIds: [], TpdbIds: []));

        var summary = await Ext.V2EntityLibrarySummaryCoreAsync(library, series, default);

        Assert.Equal(3, summary.Total);
        Assert.Equal(1, summary.Monitored);
    }
}
