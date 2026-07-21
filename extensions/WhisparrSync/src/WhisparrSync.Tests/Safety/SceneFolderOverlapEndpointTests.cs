using System.Net;
using System.Text.Json;
using Cove.Core.Auth;
using Cove.Plugins;
using Microsoft.AspNetCore.Http;
using WhisparrSync.Client;
using WhisparrSync.Tests.TestSupport;
using Ext = global::WhisparrSync.WhisparrSync;

namespace WhisparrSync.Tests.Safety;

// References Cove types (auth/principal, IStatefulExtension, FakeStore), so this file is in the csproj bare-CI
// Compile-Remove group (unlike the cove-free SceneFolderOverlapDetectorTests).
public sealed class SceneFolderOverlapEndpointTests
{
    private const string StoredBaseUrl = "http://stored.local:6969";
    private const string StoredKey = "STORED-KEY";

    private static Ext NewExtension(FakeStore store)
    {
        var ext = new Ext();
        ((IStatefulExtension)ext).SetStore(store);
        return ext;
    }

    private static async Task<FakeStore> StoreWith(string version)
    {
        var store = new FakeStore();
        await store.SetAsync(
            "options",
            $"{{\"BaseUrl\":\"{StoredBaseUrl}\",\"ApiKey\":\"{StoredKey}\",\"SelectedVersion\":\"{version}\"}}");
        return store;
    }

    private static (WhisparrClient Client, FakeHttpMessageHandler Handler) ClientWith(params Func<HttpResponseMessage>[] responses)
    {
        var handler = FakeHttpMessageHandler.Sequence(responses);
        return (new WhisparrClient(new HttpClient(handler)), handler);
    }

    private static Func<HttpResponseMessage> Ok(string body)
        => FakeHttpMessageHandler.Respond(HttpStatusCode.OK, "application/json", body);

    private static int StatusOf(IResult result)
        => Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode ?? 200;

    private static string ResponseJson(IResult result)
        => JsonSerializer.Serialize(Assert.IsAssignableFrom<IValueHttpResult>(result).Value);

    private static string RootFolders(string path)
        => JsonSerializer.Serialize(new[] { new { id = 2, path, accessible = true, freeSpace = 1L } });

    private static string Naming(string sceneFolderFormat)
        => JsonSerializer.Serialize(new { renameMovies = true, sceneFolderFormat });

    private static FakePrincipalAccessor Configure()
        => FakePrincipalAccessor.WithPermissions(Permissions.ExtensionsConfigure);

    [Fact]
    public async Task WithoutConfigure_Returns403_NoWireCall()
    {
        var store = await StoreWith("v3");
        var (client, handler) = ClientWith(); // any wire call would fail (no responses queued)

        var result = await NewExtension(store).SceneFolderOverlapAsync(client, FakePrincipalAccessor.None(), default);

        Assert.Equal(403, StatusOf(result));
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task V3Overlap_NamesRootAndSuggestsParent()
    {
        var store = await StoreWith("v3");
        var (client, _) = ClientWith(
            Ok(RootFolders("/data/media/scenes")),   // GET /rootfolder
            Ok(Naming("scenes/{Studio}")));           // GET /config/naming

        var result = await NewExtension(store).SceneFolderOverlapAsync(client, Configure(), default);

        Assert.NotEqual(403, StatusOf(result));
        var json = ResponseJson(result);
        Assert.Contains("\"root\":\"/data/media/scenes\"", json, StringComparison.Ordinal);
        Assert.Contains("\"prefix\":\"scenes\"", json, StringComparison.Ordinal);
        Assert.Contains("\"suggestedRoot\":\"/data/media\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task V3NoOverlap_ReturnsEmptySet()
    {
        var store = await StoreWith("v3");
        var (client, _) = ClientWith(
            Ok(RootFolders("/data/media")),
            Ok(Naming("scenes/{Studio}")));

        var result = await NewExtension(store).SceneFolderOverlapAsync(client, Configure(), default);

        Assert.NotEqual(403, StatusOf(result));
        Assert.Contains("\"overlaps\":[]", ResponseJson(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task V2_IsQuiet_EmptySet_NoWireCall()
    {
        var store = await StoreWith("v2");
        var (client, handler) = ClientWith(); // no responses queued — a v2 read would fail the test

        var result = await NewExtension(store).SceneFolderOverlapAsync(client, Configure(), default);

        Assert.NotEqual(403, StatusOf(result));
        Assert.Contains("\"overlaps\":[]", ResponseJson(result), StringComparison.Ordinal);
        Assert.Equal(0, handler.CallCount);
    }
}
