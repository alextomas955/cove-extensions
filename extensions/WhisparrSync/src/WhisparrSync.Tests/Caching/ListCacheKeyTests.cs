using System.Text.Json;
using WhisparrSync.Client;
using WhisparrSync.Contracts;
using WhisparrSync.Tests.TestSupport;
using static WhisparrSync.Tests.TestSupport.EndpointTestSupport;
using Ext = global::WhisparrSync.WhisparrSync;

namespace WhisparrSync.Tests.Caching;

/// <summary>
/// The connected version is part of every list-cache key.
/// </summary>
/// <remarks>
/// A within-connection v3↔v2 switch keeps the same URL and the same API key, and the connection-scoped clear
/// does not see it — so a version-less key would serve the prior generation's slice to the next reader.
/// <para>
/// This is asserted at the key function, not by driving a switch through a handler, and the reason is worth
/// stating plainly: every one of the five slots is read by callers of a SINGLE generation today (the movie and
/// exclusion memos are v3-typed; the series memo is the v2 arm and the studio/performer memos the v3 arm of the
/// same branch). So a drive-through would pass with or without the version in the key — it would be measuring
/// the branch, not the memo. The version dimension is defence at the composition point, matching the reason
/// <c>DiscoveryCacheKeys</c> already records, and it becomes load-bearing the moment one slot gains readers on
/// both generations.
/// </para>
/// </remarks>
[Trait("Tier", "L2")]
public sealed class ListCacheKeyTests
{
    private const string BaseUrl = "http://stored.local:6969";
    private const string ApiKey = "STORED-KEY";

    private static string Studios(params string[] titles)
        => JsonSerializer.Serialize(titles.Select((title, index) => new
        {
            id = index + 1,
            foreignId = $"studio-{index + 1}",
            title,
            monitored = true,
            sceneCount = 1,
            totalSceneCount = 1,
        }));

    [Theory]
    [InlineData("v3", "v2")]
    [InlineData("v2", "v3")]
    [InlineData("v3", "")]
    public void TwoKeysDifferingOnlyInTheConnectedVersion_AreDifferentStrings(string left, string right)
        => Assert.NotEqual(
            Ext.ListCacheKey(left, BaseUrl, ApiKey),
            Ext.ListCacheKey(right, BaseUrl, ApiKey));

    [Fact]
    public void TheKeyStillSeparatesAHostAndAKeyRotation()
    {
        Assert.NotEqual(Ext.ListCacheKey("v3", BaseUrl, ApiKey), Ext.ListCacheKey("v3", "http://other:6969", ApiKey));
        Assert.NotEqual(Ext.ListCacheKey("v3", BaseUrl, ApiKey), Ext.ListCacheKey("v3", BaseUrl, "ROTATED"));
    }

    [Fact]
    public async Task ASecondReadOnTheSameVersion_IsServedFromTheMemo()
    {
        // The other half: the key must still MEMOIZE. A key that included something per-request would drive the
        // read count to one-per-call and satisfy the test above for the wrong reason.
        var handler = FakeHttpMessageHandler.Json(Studios("Only Studio"));
        var client = new WhisparrClient(new HttpClient(handler));
        var ext = NewExtension(await StoreWith(BaseUrl, ApiKey));

        await ext.EntityLibrarySummaryAsync("studio", client, default);
        var afterFirst = handler.Requests.Count(r => r.Url.Contains("/api/v3/studio", StringComparison.Ordinal));
        await ext.EntityLibrarySummaryAsync("studio", client, default);

        Assert.Equal(afterFirst, handler.Requests.Count(r => r.Url.Contains("/api/v3/studio", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task SceneDetail_OnV2_RefusesBeforeTheTransport()
    {
        var (client, handler) = ClientWithHandler("[]");
        var ext = NewExtension(await StoreWith(BaseUrl, ApiKey, "v2"));

        var result = await ext.SceneDetailAsync(new SceneDetailRequest(5), client, default);

        Assert.Equal(400, StatusOf(result));
        Assert.Contains("VERSION_UNSUPPORTED", ResponseJson(result), StringComparison.Ordinal);
        // Counted, not inferred from the absence of a throw — and zero requests means no origin tag either.
        Assert.Empty(handler.Requests);
    }
}
