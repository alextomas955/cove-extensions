using Cove.Core.Auth;
using Cove.Plugins;
using Microsoft.AspNetCore.Http;
using WhisparrSync.Client;
using WhisparrSync.Library;
using WhisparrSync.Scene;
using WhisparrSync.Tests.TestSupport;
using Ext = global::WhisparrSync.WhisparrSync;

namespace WhisparrSync.Tests.Api;

/// <summary>
/// The <c>/scene-status-batch</c> (per-card status) contract. The ROUTED tier proves the configure gate, the
/// oversized-selection 400, and the v3-only refusal (v2 has no per-scene identity → VERSION_UNSUPPORTED). The
/// CORE tier drives the pure <see cref="Ext.SceneStatusBatchCoreAsync"/> over a fabricated movie set + a seeded
/// <see cref="FakeCoveLibraryPort"/> to prove each id projects to a SceneCardStatus (primary state + secondary
/// hasFile) with the SAME monitored-primary projector the toolbar summary + scene panel use (a downloaded and
/// monitored scene → Monitored + hasFile, a not-added scene, and an absent id omitted from the map).
/// </summary>
public sealed class SceneStatusBatchEndpointTests
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

    private static CoveVideo Video(int coveId, string stashId)
        => new(coveId, $"Scene {coveId}", new DateOnly(2021, 1, 1), [stashId], [], [], []);

    private static int StatusOf(IResult result)
        => Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode ?? 0;

    // ---- ROUTED tier ----

    [Fact]
    public async Task Batch_WithoutConfigure_Returns403()
    {
        var result = await NewExtension().SceneStatusBatchAsync(
            new Ext.SceneStatusBatchRequest([1, 2]), ClientJson("[]"),
            FakePrincipalAccessor.WithPermissions(Permissions.ExtensionsRead), default);
        Assert.Equal(403, StatusOf(result));
    }

    [Fact]
    public async Task Batch_V2_ReturnsVersionUnsupported400()
    {
        // v2 has no per-scene StashDB identity — the card slot is not even registered there; this is the
        // server-side backstop (VERSION_UNSUPPORTED before any per-id work).
        var result = await NewExtension(await StoreWith("v2")).SceneStatusBatchAsync(
            new Ext.SceneStatusBatchRequest([1]), ClientJson("[]"),
            FakePrincipalAccessor.WithPermissions(Permissions.ExtensionsConfigure), default);
        Assert.Equal(400, StatusOf(result));
    }

    [Fact]
    public async Task Batch_OversizedIdList_Returns400()
    {
        var oversized = Enumerable.Range(1, 1001).ToArray();
        var result = await NewExtension(await StoreWith("v3")).SceneStatusBatchAsync(
            new Ext.SceneStatusBatchRequest(oversized), ClientJson("[]"),
            FakePrincipalAccessor.WithPermissions(Permissions.ExtensionsConfigure), default);
        Assert.Equal(400, StatusOf(result));
    }

    // ---- CORE tier ----

    [Fact]
    public async Task Core_ProjectsEachId_MonitoredWithFile_NotAdded_AndOmitsAbsent()
    {
        // Movie 42 has scene 1's StashDB id, is monitored, and has a file → Monitored (primary) + hasFile
        // (secondary); scene 2's id matches no movie → NotAdded with no file; id 3 has no seeded video → absent
        // from the map (the card shows no badge). The core is pure over the pre-fetched movie/exclusion sets (the
        // handler supplies the cached lists) — no fetch, so it cannot grab.
        WhisparrMovie[] movies =
            [new(42, "Scene 1", 2021, "uuid-a", "uuid-a", "scene", Monitored: true, HasFile: true, MovieFile: null)];
        var library = new FakeCoveLibraryPort();
        library.Seed(Video(1, "uuid-a"), Video(2, "uuid-zzz"));

        var states = await Ext.SceneStatusBatchCoreAsync([1, 2, 3], movies, [], library, default);

        Assert.Equal(SceneWhisparrState.Monitored, states[1].State);
        Assert.True(states[1].HasFile);
        Assert.Equal(SceneWhisparrState.NotAdded, states[2].State);
        Assert.False(states[2].HasFile);
        Assert.False(states.ContainsKey(3)); // no video for id 3 → omitted, never a fabricated state
    }
}
