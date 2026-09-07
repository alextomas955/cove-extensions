using WhisparrSync.Contracts;
using static WhisparrSync.Tests.TestSupport.EndpointTestSupport;

namespace WhisparrSync.Tests.Api.Auth;

/// <summary>
/// Security-critical: the host's <c>[RequiresPermission]</c> filter is
/// inert on minimal-API extension endpoints, so the read-only scene-status handlers
/// (<c>/scene-status-summary</c>, <c>/scene-detail</c>) enforce
/// <c>extensions.configure</c> themselves and reach the stored credentials only. These prove the deny/allow
/// pair for both routes, that an outbound status read carries the STORED host + key (never a caller
/// value — the body carries none), that the API key is never echoed in the response, and that a scene with
/// no resolvable StashDB identity is the handled <c>NO_STASHDB_IDENTITY</c> outcome with NO outbound call.
/// Mirrors <see cref="MonitorEndpointAuthTests"/>.
/// </summary>
[Trait("Tier", "L2")]
public sealed class SceneEndpointAuthTests
{
    private const string StoredBaseUrl = "http://stored.local:6969";
    private const string StoredKey = "STORED-KEY";

    // ---- the summary's outbound reads use the STORED host + key, never a caller value ----

    [Fact]
    public async Task Summary_OutboundCall_UsesTheStoredHostAndKey()
    {
        // The request has no body, so the only creds available are the stored ones. The summary reads the
        // movie set + exclusion set (two outbound GETs); prove they target the stored host with the stored key.
        var store = await StoreWith(StoredBaseUrl, StoredKey);
        var (client, handler) = ClientWithHandler("[]");

        await NewExtension(store).SceneStatusSummaryAsync(
            client, default);

        Assert.Equal(2, handler.CallCount); // movie read + exclusion read
        Assert.All(handler.Requests, r => Assert.StartsWith(StoredBaseUrl + "/", r.Url));
        Assert.Equal(StoredKey, SentApiKey(handler));
    }

    // ---- the API key is never echoed to the response ----

    [Fact]
    public async Task Summary_ResponseNeverContainsTheApiKey()
    {
        const string secretKey = "SUPER-SECRET-KEY-9f3a";
        var store = await StoreWith(StoredBaseUrl, secretKey);
        var result = await NewExtension(store).SceneStatusSummaryAsync(
            ClientReturning("[]"), default);

        Assert.DoesNotContain(secretKey, ResponseJson(result), StringComparison.Ordinal);
    }

    // ---- server-side identity resolution: a scene with no StashDB id is handled, and makes NO outbound call ----

    [Fact]
    public async Task Detail_NoResolvableStashId_ReturnsNoIdentity_AndMakesNoOutboundCall()
    {
        // With no host DB scope the scene cannot be resolved (LoadVideoByIdSafeAsync degrades to null), so the
        // handler returns the handled NO_STASHDB_IDENTITY outcome BEFORE any Whisparr call — never a 500.
        var store = await StoreWith(StoredBaseUrl, StoredKey);
        var (client, handler) = ClientWithHandler("[]");

        var result = await NewExtension(store).SceneDetailAsync(
            new SceneDetailRequest(5), client, default);
        Assert.Contains("NO_STASHDB_IDENTITY", ResponseJson(result), StringComparison.Ordinal);
        Assert.Equal(0, handler.CallCount);
    }
}
