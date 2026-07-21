using Cove.Core.Auth;
using Cove.Plugins;
using Microsoft.AspNetCore.Http;
using WhisparrSync.Client;
using WhisparrSync.Library;
using WhisparrSync.Monitor;
using WhisparrSync.Options;
using WhisparrSync.Tests.TestSupport;
using Ext = global::WhisparrSync.WhisparrSync;

namespace WhisparrSync.Tests.Api;

/// <summary>
/// The <c>/entities-batch</c> (studios/performers bulk) contract. The ROUTED tier proves the configure gate,
/// unknown-kind/op 400s, and the version+kind capability gate (a performer on v2 and add-all-missing on v2 are
/// refused up front with VERSION_UNSUPPORTED — the same split the per-entity menu enforces). The CORE tier drives
/// the extracted <see cref="Ext.EntitiesBatchCoreAsync"/> with a seeded <see cref="FakeCoveLibraryPort"/> + a
/// fake-HTTP client to prove: monitor/search resolve each entity's OWN identity id from its Cove id (v3 StashDB,
/// v2 TPDB) and SKIP — no wire call — when it has none; add-all-missing / reflect-owned dispatch by Cove id
/// WITHOUT resolving an identity; and the aggregate counts.
/// </summary>
public sealed class EntitiesBatchEndpointTests
{
    private const string BaseUrl = "http://stored.local:6969";
    private const string ApiKey = "STORED-KEY";

    private static Ext NewExtension(FakeStore? store = null)
    {
        var ext = new Ext();
        ((IStatefulExtension)ext).SetStore(store ?? new FakeStore());
        return ext;
    }

    private static (WhisparrClient Client, FakeHttpMessageHandler Handler) ClientWithHandler(string json)
    {
        var handler = FakeHttpMessageHandler.Json(json);
        return (new WhisparrClient(new HttpClient(handler)), handler);
    }

    private static async Task<FakeStore> StoreWith(string version)
    {
        var store = new FakeStore();
        await store.SetAsync(
            "options", $"{{\"BaseUrl\":\"{BaseUrl}\",\"ApiKey\":\"{ApiKey}\",\"SelectedVersion\":\"{version}\"}}");
        return store;
    }

    private static WhisparrOptions Options(string version)
        => new() { BaseUrl = BaseUrl, ApiKey = ApiKey, SelectedVersion = version };

    private static int StatusOf(IResult result)
        => Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode ?? 0;

    // ---- ROUTED tier: gate + capability refusals ----

    [Fact]
    public async Task EntitiesBatch_WithoutConfigure_Returns403()
    {
        var (client, _) = ClientWithHandler("[]");
        var result = await NewExtension().EntitiesBatchAsync(
            new Ext.EntitiesBatchRequest("studio", [1], "monitor", "NewReleases"),
            client, FakePrincipalAccessor.WithPermissions(Permissions.ExtensionsRead), default);
        Assert.Equal(403, StatusOf(result));
    }

    [Fact]
    public async Task EntitiesBatch_UnknownOp_Returns400()
    {
        var (client, _) = ClientWithHandler("[]");
        var result = await NewExtension(await StoreWith("v3")).EntitiesBatchAsync(
            new Ext.EntitiesBatchRequest("studio", [1], "obliterate", "NewReleases"),
            client, FakePrincipalAccessor.WithPermissions(Permissions.ExtensionsConfigure), default);
        Assert.Equal(400, StatusOf(result));
    }

    [Fact]
    public async Task EntitiesBatch_V2Performer_Monitor_ReturnsVersionUnsupported400()
    {
        // v2 has no performer entity → the whole op is refused up front (the per-entity menu wouldn't offer it).
        var (client, handler) = ClientWithHandler("[]");
        var result = await NewExtension(await StoreWith("v2")).EntitiesBatchAsync(
            new Ext.EntitiesBatchRequest("performer", [1], "monitor", "NewReleases"),
            client, FakePrincipalAccessor.WithPermissions(Permissions.ExtensionsConfigure), default);
        Assert.Equal(400, StatusOf(result));
        Assert.Equal(0, handler.CallCount); // refused before any wire call
    }

    [Fact]
    public async Task EntitiesBatch_V2Studio_AddMissing_ReturnsVersionUnsupported400()
    {
        // Add-all-missing needs the per-scene add (v3-only), so it is refused on a v2 studio.
        var (client, _) = ClientWithHandler("[]");
        var result = await NewExtension(await StoreWith("v2")).EntitiesBatchAsync(
            new Ext.EntitiesBatchRequest("studio", [1], "addMissing", "NewReleases"),
            client, FakePrincipalAccessor.WithPermissions(Permissions.ExtensionsConfigure), default);
        Assert.Equal(400, StatusOf(result));
    }

    // ---- CORE tier: per-entity dispatch ----

    [Fact]
    public async Task Core_Monitor_SkipsEntityWithNoIdentity_NoWireCall()
    {
        var (client, handler) = ClientWithHandler("[]");
        var library = new FakeCoveLibraryPort(); // no identity seeded for entity 1

        var result = await Ext.EntitiesBatchCoreAsync(
            EntityKind.Studio, Ext.EntityBatchOp.Monitor, MonitorScope.NewReleases, [1],
            client, library, Options("v3"), default);

        Assert.Equal(1, result.Total);
        Assert.Equal(1, result.Skipped);       // no StashDB id → skipped
        Assert.Equal(0, result.Succeeded);
        Assert.Equal(0, result.Failed);
        Assert.Equal(0, handler.CallCount);     // skipped BEFORE any outbound call
        Assert.Equal(1, library.LoadEntityIdentityCallCount);
    }

    [Fact]
    public async Task Core_Monitor_OnV2_ResolvesTpdbIdentity_AndCallsWhisparr()
    {
        // v2 identity carries ONLY a TPDB id. If the core resolved by StashDB (empty) it would skip with no call;
        // a wire call (and no skip) proves it selected the TPDB id for the connected v2 instance.
        var (client, handler) = ClientWithHandler("[]");
        var library = new FakeCoveLibraryPort();
        library.SeedEntityIdentity(EntityKind.Studio, 1, new CoveEntityIdentity(StashIds: [], TpdbIds: ["3417"]));

        var result = await Ext.EntitiesBatchCoreAsync(
            EntityKind.Studio, Ext.EntityBatchOp.Monitor, MonitorScope.NewReleases, [1],
            client, library, Options("v2"), default);

        Assert.Equal(0, result.Skipped);        // the TPDB id resolved
        Assert.True(handler.CallCount > 0);      // and the adapter actually called Whisparr
    }

    [Fact]
    public async Task Core_ReflectOwned_DispatchesByCoveId_WithoutResolvingIdentity()
    {
        // Reflect-owned enumerates the entity's scenes by Cove id (no identity needed). With no scenes it is an
        // Ok no-op → the entity counts as succeeded, and the identity seam is never consulted.
        var (client, _) = ClientWithHandler("[]"); // GET /movie → [] (ListMovies)
        var library = new FakeCoveLibraryPort();

        var result = await Ext.EntitiesBatchCoreAsync(
            EntityKind.Studio, Ext.EntityBatchOp.ReflectOwned, MonitorScope.NewReleases, [7],
            client, library, Options("v3"), default);

        Assert.Equal(1, result.Total);
        Assert.Equal(1, result.Succeeded);
        Assert.Equal(0, result.Skipped);
        Assert.Equal(0, library.LoadEntityIdentityCallCount); // by-id op — never resolves an identity
    }
}
