using System.Net;
using System.Text.Json;
using Cove.Core.Auth;
using Cove.Core.Interfaces;
using Cove.Plugins;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using WhisparrSync.Client;
using WhisparrSync.Contracts;
using WhisparrSync.Tests.TestSupport;
using Ext = global::WhisparrSync.WhisparrSync;

namespace WhisparrSync.Tests.Safety;

// References Cove types (auth/principal, IStatefulExtension, FakeStore), so this file is in the csproj bare-CI
// Compile-Remove group (unlike the cove-free SceneFolderOverlapDetectorTests).
[Trait("Tier", "L2")]
public sealed class FolderOverlapEndpointTests
{
    private const string StoredBaseUrl = "http://stored.local:6969";
    private const string StoredKey = "STORED-KEY";

    private static Ext NewExtension(FakeStore store)
    {
        var ext = new Ext();
        ((IStatefulExtension)ext).SetStore(store);
        return ext;
    }

    // An extension whose Cove-side roots resolve, which the containment comparison needs. The host supplies them
    // as CoveConfiguration.CovePaths, so the fixture registers exactly that and lets InitializeAsync capture the
    // scope factory the handler reads them through — no test-only seam into the handler.
    private static async Task<Ext> NewExtensionWithCoveRoots(FakeStore store, params string[] coveRoots)
    {
        var ext = NewExtension(store);
        var services = new ServiceCollection();
        services.AddSingleton(new CoveConfiguration
        {
            CovePaths = [.. coveRoots.Select(p => new CovePath { Path = p })],
        });
        await ext.InitializeAsync(services.BuildServiceProvider());
        return ext;
    }

    private static async Task<FakeStore> StoreWith(string version, string baseUrl = StoredBaseUrl)
    {
        var store = new FakeStore();
        await store.SetAsync(
            "options",
            $"{{\"BaseUrl\":\"{baseUrl}\",\"ApiKey\":\"{StoredKey}\",\"SelectedVersion\":\"{version}\"}}");
        return store;
    }

    private static (WhisparrClient Client, FakeHttpMessageHandler Handler) ClientWith(params Func<HttpResponseMessage>[] responses)
    {
        var handler = FakeHttpMessageHandler.Sequence(responses);
        return (new WhisparrClient(new HttpClient(handler)), handler);
    }

    private static Func<HttpResponseMessage> Ok(string body)
        => FakeHttpMessageHandler.Respond(HttpStatusCode.OK, "application/json", body);

    private static Func<HttpResponseMessage> Unavailable()
        => FakeHttpMessageHandler.Respond(HttpStatusCode.ServiceUnavailable, "application/json", "{}");

    private static int StatusOf(IResult result)
        => Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode ?? 200;

    // Every answer, including every abstention, is a 200 — asserted positively rather than as "not 403", which is
    // a permission check that a regression to a 400 would sail past. The 200 is the whole reason this shape was
    // chosen over a refusal status: the settings page's read catch collapses a thrown status into the same false
    // all-clear the reason vocabulary exists to remove.
    private static void AssertOk(IResult result) => Assert.Equal(200, StatusOf(result));

    // Serialized through the product's own response options, so these substring assertions read the wire text the
    // client actually receives — the camelCase spelling included.
    private static string ResponseJson(IResult result)
        => JsonSerializer.Serialize(
            Assert.IsAssignableFrom<IValueHttpResult>(result).Value, WireSerializers.EnumStringResponseJsonOptions);

    private static string RootFolders(string path)
        => JsonSerializer.Serialize(new[] { new { id = 2, path, accessible = true, freeSpace = 1L } });

    private static string Naming(string sceneFolderFormat)
        => JsonSerializer.Serialize(new { renameMovies = true, sceneFolderFormat });

    private static FakePrincipalAccessor Configure()
        => FakePrincipalAccessor.WithPermissions(Permissions.ExtensionsConfigure);

    // An abstention carries the reason and nothing else: no findings to mistake for "nothing found".
    private static void AssertAbstains(IResult result, string reason)
    {
        AssertOk(result);
        var json = ResponseJson(result);
        Assert.Contains("\"checked\":false", json, StringComparison.Ordinal);
        Assert.Contains($"\"reason\":\"{reason}\"", json, StringComparison.Ordinal);
        Assert.Contains("\"findings\":[]", json, StringComparison.Ordinal);
    }

    // ---- the four abstention causes, one case each ----

    [Fact]
    public async Task NotConfigured_Abstains_WithNoWireCall()
    {
        // The version selector defaults to v3, so a never-configured extension PASSES the version gate: without a
        // reason naming the blank host, this leg answered "your folders don't overlap" having never spoken to
        // Whisparr at all.
        var store = await StoreWith("v3", baseUrl: "");
        var ext = await NewExtensionWithCoveRoots(store, "/data/media/scenes");
        var (client, handler) = ClientWith();

        var result = await ext.FolderOverlapAsync(client, default);

        AssertAbstains(result, FolderOverlapReason.NotConfigured);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task ReadFailed_Abstains_WhenTheRootReadDoesNotAnswer()
    {
        var store = await StoreWith("v3");
        var ext = await NewExtensionWithCoveRoots(store, "/data/media/scenes");
        var (client, _) = ClientWith(Unavailable());

        var result = await ext.FolderOverlapAsync(client, default);

        AssertAbstains(result, FolderOverlapReason.ReadFailed);
    }

    [Fact]
    public async Task CoveRootsUnknown_Abstains_AfterASuccessfulRootRead()
    {
        // Reachable in principle rather than defensively: the branch fires when no scope factory was captured, or
        // when neither the host configuration nor a database context resolves from one. Unlikely in production —
        // the host registers the configuration object as a singleton — but a comparison against ZERO Cove roots is
        // never an all-clear, so it must say so.
        var store = await StoreWith("v3");
        var (client, handler) = ClientWith(Ok(RootFolders("/data/media")));

        var result = await NewExtension(store).FolderOverlapAsync(client, default);

        AssertAbstains(result, FolderOverlapReason.CoveRootsUnknown);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task UnsupportedVersion_Abstains_WithNoWireCall()
    {
        // The settings dropdown cannot produce this, but the options blob accepts any submitted version, so a
        // hand-crafted POST can persist one this build cannot manage.
        var store = await StoreWith("v9");
        var ext = await NewExtensionWithCoveRoots(store, "/data/media/scenes");
        var (client, handler) = ClientWith();

        var result = await ext.FolderOverlapAsync(client, default);

        AssertAbstains(result, FolderOverlapReason.UnsupportedVersion);
        Assert.Equal(0, handler.CallCount);
    }

    // ---- both generations answer ----

    [Fact]
    public async Task V3Overlap_NamesRootAndSuggestsParent()
    {
        var store = await StoreWith("v3");
        var ext = await NewExtensionWithCoveRoots(store, "/srv/library"); // disjoint: isolates the format finding
        var (client, handler) = ClientWith(
            Ok(RootFolders("/data/media/scenes")),   // GET /rootfolder
            Ok(Naming("scenes/{Studio}")));           // GET /config/naming

        var result = await ext.FolderOverlapAsync(client, default);

        AssertOk(result);
        var json = ResponseJson(result);
        Assert.Contains("\"checked\":true", json, StringComparison.Ordinal);
        Assert.Contains("\"kind\":\"sceneFolderFormat\"", json, StringComparison.Ordinal);
        Assert.Contains("\"root\":\"/data/media/scenes\"", json, StringComparison.Ordinal);
        Assert.Contains("\"prefix\":\"scenes\"", json, StringComparison.Ordinal);
        Assert.Contains("\"suggestedRoot\":\"/data/media\"", json, StringComparison.Ordinal);
        // v3 CAN answer that kind, so it is never listed as unanswerable.
        Assert.Contains("\"notApplicable\":[]", json, StringComparison.Ordinal);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task V3_ReportsRootContainment_AndAnswersTheFormatKind_TwoWireCalls()
    {
        var store = await StoreWith("v3");
        var ext = await NewExtensionWithCoveRoots(store, "/data/media/scenes");
        var (client, handler) = ClientWith(
            Ok(RootFolders("/data/media")),
            Ok(Naming("{Studio}/{Movie Title}")));    // token-leading: nothing for the format kind to find

        var result = await ext.FolderOverlapAsync(client, default);

        AssertOk(result);
        var json = ResponseJson(result);
        Assert.Contains("\"checked\":true", json, StringComparison.Ordinal);
        Assert.Contains("\"kind\":\"rootContainment\"", json, StringComparison.Ordinal);
        Assert.Contains("\"whisparrRoot\":\"/data/media\"", json, StringComparison.Ordinal);
        Assert.Contains("\"coveRoot\":\"/data/media/scenes\"", json, StringComparison.Ordinal);
        Assert.Contains("\"notApplicable\":[]", json, StringComparison.Ordinal);
        // Both reads run on Eros: the roots and the naming config.
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task V2_ReportsRootContainment_AndFormatKindNotApplicable_OneWireCall()
    {
        var store = await StoreWith("v2");
        // The live fixture's real pair, and the reason containment is tested in BOTH directions: the WHISPARR root
        // is the outer path here, so a one-direction check would report no overlap where one exists.
        var ext = await NewExtensionWithCoveRoots(store, "/data/media/scenes");
        var (client, handler) = ClientWith(Ok(RootFolders("/data/media")));

        var result = await ext.FolderOverlapAsync(client, default);

        AssertOk(result);
        var json = ResponseJson(result);
        Assert.Contains("\"checked\":true", json, StringComparison.Ordinal);
        Assert.Contains("\"kind\":\"rootContainment\"", json, StringComparison.Ordinal);
        Assert.Contains("\"whisparrRoot\":\"/data/media\"", json, StringComparison.Ordinal);
        Assert.Contains("\"coveRoot\":\"/data/media/scenes\"", json, StringComparison.Ordinal);
        // The Scene Folder Format does not exist on v2, so the kind is declared unanswerable rather than clear.
        Assert.Contains("\"notApplicable\":[\"sceneFolderFormat\"]", json, StringComparison.Ordinal);
        // Exactly ONE call: the roots read runs on v2, and the naming read is not issued at all.
        Assert.Equal(1, handler.CallCount);
    }

    // ---- a genuine all-clear, on each generation, distinguishable from every abstention above ----

    [Fact]
    public async Task V3NoOverlap_ReturnsEmptySet()
    {
        var store = await StoreWith("v3");
        var ext = await NewExtensionWithCoveRoots(store, "/srv/library");
        var (client, handler) = ClientWith(
            Ok(RootFolders("/data/media")),
            Ok(Naming("scenes/{Studio}")));

        var result = await ext.FolderOverlapAsync(client, default);

        AssertOk(result);
        var json = ResponseJson(result);
        // An empty finding set is only an all-clear BECAUSE checked is true; the pair together is the assertion.
        Assert.Contains("\"checked\":true", json, StringComparison.Ordinal);
        Assert.Contains("\"findings\":[]", json, StringComparison.Ordinal);
        Assert.Contains("\"reason\":null", json, StringComparison.Ordinal);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task V2NoOverlap_IsAGenuineAllClear_WithTheFormatKindStillNotApplicable()
    {
        var store = await StoreWith("v2");
        var ext = await NewExtensionWithCoveRoots(store, "/srv/library");
        var (client, handler) = ClientWith(Ok(RootFolders("/data/media")));

        var result = await ext.FolderOverlapAsync(client, default);

        AssertOk(result);
        var json = ResponseJson(result);
        Assert.Contains("\"checked\":true", json, StringComparison.Ordinal);
        Assert.Contains("\"findings\":[]", json, StringComparison.Ordinal);
        Assert.Contains("\"reason\":null", json, StringComparison.Ordinal);
        // Not applicable is not a finding and not an abstention: the kind stays declared unanswerable even when
        // the answer this generation CAN give is a clean one.
        Assert.Contains("\"notApplicable\":[\"sceneFolderFormat\"]", json, StringComparison.Ordinal);
        Assert.Equal(1, handler.CallCount);
    }
}
