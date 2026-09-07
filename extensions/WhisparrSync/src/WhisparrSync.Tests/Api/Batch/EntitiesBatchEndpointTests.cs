using Cove.Plugins;
using Microsoft.AspNetCore.Http;
using WhisparrSync.Adapters;
using WhisparrSync.Client;
using WhisparrSync.Contracts;
using WhisparrSync.Library;
using WhisparrSync.Options;
using WhisparrSync.Tests.TestSupport;
using Ext = global::WhisparrSync.WhisparrSync;

namespace WhisparrSync.Tests.Api.Batch;

/// <summary>
/// The <c>/entities-batch</c> (studios/performers bulk) contract. The ROUTED tier proves the configure gate,
/// unknown-kind/op 400s, and the version+kind capability gate (a performer on v2 and add-all-missing on v2 are
/// refused up front with VERSION_UNSUPPORTED — the same split the per-entity menu enforces). The CORE tier drives
/// the extracted <see cref="Ext.EntitiesBatchCoreAsync"/> with a seeded <see cref="FakeCoveLibraryPort"/> + a
/// fake-HTTP client to prove: monitor/search resolve each entity's OWN identity id from its Cove id (v3 StashDB,
/// v2 TPDB) and SKIP — no wire call — when it has none; add-all-missing / reflect-owned dispatch by Cove id
/// WITHOUT resolving an identity; and the aggregate counts.
/// </summary>
[Trait("Tier", "L2")]
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

    // The stored quality profile defaults to the unset sentinel, which is the state the configuration guard names.
    private static async Task<FakeStore> StoreWith(string version, string baseUrl = BaseUrl)
    {
        var store = new FakeStore();
        await store.SetAsync(
            "options", $"{{\"BaseUrl\":\"{baseUrl}\",\"ApiKey\":\"{ApiKey}\",\"SelectedVersion\":\"{version}\"}}");
        return store;
    }

    private static WhisparrOptions Options(string version)
        => new() { BaseUrl = BaseUrl, ApiKey = ApiKey, SelectedVersion = version };

    private static int StatusOf(IResult result)
        => Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode ?? 0;

    private static string ResponseJson(IResult result)
        => System.Text.Json.JsonSerializer.Serialize(Assert.IsAssignableFrom<IValueHttpResult>(result).Value);

    [Fact]
    public async Task EntitiesBatch_UnknownOp_Returns400()
    {
        var (client, _) = ClientWithHandler("[]");
        var result = await NewExtension(await StoreWith("v3")).EntitiesBatchAsync(
            new EntitiesBatchRequest("studio", [1], "obliterate", "NewReleases"),
            client, default);
        Assert.Equal(400, StatusOf(result));
    }

    [Fact]
    public async Task EntitiesBatch_V2Performer_Monitor_ReturnsVersionUnsupported400()
    {
        // v2 has no performer entity → the whole op is refused up front (the per-entity menu wouldn't offer it).
        var (client, handler) = ClientWithHandler("[]");
        var result = await NewExtension(await StoreWith("v2")).EntitiesBatchAsync(
            new EntitiesBatchRequest("performer", [1], "monitor", "NewReleases"),
            client, default);
        Assert.Equal(400, StatusOf(result));
        Assert.Equal(0, handler.CallCount); // refused before any wire call
    }

    [Fact]
    public async Task EntitiesBatch_V2Studio_AddMissing_ReturnsVersionUnsupported400()
    {
        // Add-all-missing needs the per-scene add (v3-only), so it is refused on a v2 studio.
        var (client, _) = ClientWithHandler("[]");
        var result = await NewExtension(await StoreWith("v2")).EntitiesBatchAsync(
            new EntitiesBatchRequest("studio", [1], "addMissing", "NewReleases"),
            client, default);
        Assert.Equal(400, StatusOf(result));
    }

    // ---- an incomplete stored configuration is refused IN THE HANDLER, before enqueue ----

    [Theory]
    [InlineData("monitor")]
    [InlineData("unmonitor")]
    public async Task EntitiesBatch_WithBlankStoredAddress_Refuses400_OnEveryOp(string op)
    {
        var (client, handler) = ClientWithHandler("[]");

        var result = await NewExtension(await StoreWith("v3", baseUrl: "")).EntitiesBatchAsync(
            new EntitiesBatchRequest("studio", [1], op, "NewReleases"),
            client, default);

        Assert.Equal(400, StatusOf(result));
        var json = ResponseJson(result);
        Assert.Contains("CONFIG_INCOMPLETE", json, StringComparison.Ordinal);
        Assert.Contains("baseUrl", json, StringComparison.Ordinal);
        // Above BOTH the inline branch and the enqueue branch: no aggregate is computed and nothing goes out.
        Assert.Equal(0, handler.CallCount);
        Assert.DoesNotContain("\"Total\"", json, StringComparison.Ordinal);
    }

    // ---- CORE tier: per-entity dispatch ----

    [Fact]
    public async Task Core_Monitor_SkipsEntityWithNoIdentity_NoWireCall()
    {
        var (client, handler) = ClientWithHandler("[]");
        var library = new FakeCoveLibraryPort(); // no identity seeded for entity 1

        var result = await Ext.EntitiesBatchCoreAsync(
            EntityKind.Studio, EntityBatchOp.Monitor, MonitorScope.NewReleases, [1],
            client, library, Options("v3"), new WhisparrCapabilityPort(client), default);

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
            EntityKind.Studio, EntityBatchOp.Monitor, MonitorScope.NewReleases, [1],
            client, library, Options("v2"), new WhisparrCapabilityPort(client), default);

        Assert.Equal(0, result.Skipped);        // the TPDB id resolved
        Assert.True(handler.CallCount > 0);      // and the adapter actually called Whisparr
    }

    [Fact]
    public async Task Core_ReflectOwned_ResolvesTheEntitysOwnId_AndRefusesWhenCoveHoldsNone()
    {
        // Reflect-owned asks Whisparr for THIS entity's catalogue, which is addressed by the entity's own
        // remote id — so the identity seam is consulted, and an entity Cove holds no id for cannot be asked
        // about at all. It is refused rather than enumerated: the alternative is the whole-movie-set read this
        // path exists to stop making.
        // The fixture answers the API description like a real v3 instance; without it the run would be refused
        // at the capability gate and never reach the identity seam this test is about.
        var handler = FakeHttpMessageHandler.Json("[]").ServingCatalogue("[]");
        var client = new WhisparrClient(new HttpClient(handler));
        var library = new FakeCoveLibraryPort();

        var result = await Ext.EntitiesBatchCoreAsync(
            EntityKind.Studio, EntityBatchOp.ReflectOwned, MonitorScope.NewReleases, [7],
            client, library, Options("v3"), new WhisparrCapabilityPort(client), default);

        Assert.Equal(1, result.Total);
        Assert.Equal(0, result.Succeeded);
        Assert.Equal(1, library.LoadEntityIdentityCallCount);
        Assert.Equal(0, WhisparrRequestCounter.Classify(handler).WholeSetMovieReads);
    }
}
