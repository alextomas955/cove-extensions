using System.Net;
using System.Text.Json;
using Cove.Plugins;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using WhisparrSync.Client;
using WhisparrSync.Discovery;
using Ext = global::WhisparrSync.WhisparrSync;

namespace WhisparrSync.Tests.TestSupport;

/// <summary>
/// Shared preamble for the minimal-API endpoint-permission tests: a stateful extension over a
/// <see cref="FakeStore"/>, a <see cref="WhisparrClient"/> over a faked transport, an options-blob seed,
/// and result-shape readers. The host's <c>[RequiresPermission]</c> filter is inert on minimal-API routes,
/// so every <c>Api/Auth</c> test needs the same fixture to prove the in-handler permission gate.
/// </summary>
internal static class EndpointTestSupport
{
    public static Ext NewExtension(FakeStore? store = null)
    {
        var ext = new Ext();
        ((IStatefulExtension)ext).SetStore(store ?? new FakeStore());
        return ext;
    }

    public static WhisparrClient ClientReturning(string json)
        => new(new HttpClient(V3Instance(json)));

    // The API description a real v3 instance serves, reduced to the two narrow per-entity catalogue routes it
    // declares (read live and committed as e2e/fixtures/wire/openapi-capability.json). A fixture standing in
    // for a v3 instance must answer it, or the capability gate refuses before the behaviour under test runs —
    // and the fixture would then be measuring its own silence. A test about the ABSENCE of these routes builds
    // its own document instead of taking this fixture.
    private static readonly string V3ApiDescription = JsonSerializer.Serialize(new
    {
        paths = new Dictionary<string, object>
        {
            ["/api/v3/movie/listbystudioforeignid"] = new { },
            ["/api/v3/movie/listbyperformerforeignid"] = new { },
        },
    });

    private static FakeHttpMessageHandler V3Instance(string json)
        => FakeHttpMessageHandler.Json(json).Also(request =>
            request.Url.EndsWith("/docs/v3/openapi.json", StringComparison.Ordinal)
                ? FakeHttpMessageHandler.Respond(HttpStatusCode.OK, "application/json", V3ApiDescription)()
                : null);

    // A StashDB GraphQL client over a faked transport. The discovery routing only reaches it for an UNMONITORED
    // studio with a stored StashDB key; the auth/gate matrices store no StashDB key, so any canned body suffices.
    public static StashDbGraphQlClient StashDbClientReturning(string json = "{}")
        => new(new HttpClient(FakeHttpMessageHandler.Json(json)));

    // A ThePornDB REST client over a faked transport — the v2 direct-discovery analogue of StashDbClientReturning.
    // The routing reaches it only for an UNMONITORED v2 site with a resolved TPDB credential; the auth/gate
    // matrices resolve none, so any canned body suffices.
    public static TpdbClient TpdbClientReturning(string json = "{}")
        => new(new HttpClient(FakeHttpMessageHandler.Json(json)));

    // Returns the handler alongside the client: the outbound URL + X-Api-Key header are only observable through it.
    public static (WhisparrClient Client, FakeHttpMessageHandler Handler) ClientWithHandler(string json)
    {
        var handler = V3Instance(json);
        return (new WhisparrClient(new HttpClient(handler)), handler);
    }

    // A stored host + key. version defaults to v3 (default StashDbEndpoint); pass "v2" for the deferral paths.
    public static async Task<FakeStore> StoreWith(string baseUrl, string apiKey, string version = "v3")
    {
        var store = new FakeStore();
        await store.SetAsync(
            "options",
            $"{{\"BaseUrl\":\"{baseUrl}\",\"ApiKey\":\"{apiKey}\",\"SelectedVersion\":\"{version}\"}}");
        return store;
    }

    public static string SentApiKey(FakeHttpMessageHandler handler)
        => handler.LastRequest!.Headers.TryGetValues("X-Api-Key", out var values)
            ? string.Concat(values!)
            : string.Empty;

    public static int StatusOf(IResult result)
        => Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode ?? 0;

    /// <remarks>
    /// Serializes through the result's OWN <see cref="JsonSerializerOptions"/> — the one the handler attached.
    /// A bare <c>JsonSerializer.Serialize</c> reports a wire no endpoint emits: it applies no naming policy, so
    /// it agrees with the real response only for a value whose members are already spelled camelCase, and
    /// silently disagrees for every other shape.
    /// </remarks>
    public static string ResponseJson(IResult result)
    {
        var value = Assert.IsAssignableFrom<IValueHttpResult>(result).Value;
        var options = result.GetType().GetProperty(nameof(JsonHttpResult<object>.JsonSerializerOptions))
            ?.GetValue(result) as JsonSerializerOptions;
        return JsonSerializer.Serialize(value, options);
    }
}
