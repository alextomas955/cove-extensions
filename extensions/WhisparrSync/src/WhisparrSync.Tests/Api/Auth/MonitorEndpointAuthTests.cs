using WhisparrSync.Contracts;
using static WhisparrSync.Tests.TestSupport.EndpointTestSupport;

namespace WhisparrSync.Tests.Api.Auth;

/// <summary>
/// Security-critical: the host's <c>[RequiresPermission]</c> filter is inert on
/// minimal-API extension endpoints, so the <c>/monitor</c> + <c>/monitor-status</c> handlers enforce
/// <c>extensions.configure</c> themselves and reach the stored credentials only. These prove the deny/allow
/// pair for both routes, that the outbound call carries the STORED host + key (never a caller value — the body
/// carries none), that the API key is never echoed in the response, and that an entity with no matching
/// StashDB endpoint is rejected server-side without any outbound call. Mirrors <see cref="EndpointAuthTests"/>.
/// </summary>
[Trait("Tier", "L2")]
public sealed class MonitorEndpointAuthTests
{
    private const string StoredBaseUrl = "http://stored.local:6969";
    private const string StoredKey = "STORED-KEY";
    private const string StashDbEndpoint = "https://stashdb.org/graphql"; // the WhisparrOptions default
    private const string StashId = "157c9e0d-5f8e-446a-b1c5-dddf3cb5b2d1";

    // A remote-id set whose StashDB endpoint matches the stored default, so the handler resolves a stashId.
    private static RemoteIdInput[] MatchingRemotes() => [new(StashDbEndpoint, StashId)];

    // A remote-id set whose only endpoint is a DIFFERENT metadata server — no StashDB identity to resolve.
    private static RemoteIdInput[] NonMatchingRemotes() => [new("https://theporndb.net/graphql", "some-id")];

    private static MonitorRequest MonitorOff() => new("studio", MatchingRemotes(), Monitored: false);
    private static MonitorStatusRequest StatusReq() => new("studio", MatchingRemotes());

    // ---- the outbound call uses the STORED host + key, never a caller value ----

    [Fact]
    public async Task Monitor_OutboundCall_UsesTheStoredHostAndKey()
    {
        // The request body carries NO url/key, so the only creds available are the stored ones. Prove the
        // studio GET targets the stored host and sends the stored X-Api-Key (never blank, never a caller value).
        var store = await StoreWith(StoredBaseUrl, StoredKey);
        var (client, handler) = ClientWithHandler("[]");

        await NewExtension(store).MonitorAsync(
            MonitorOff(), client, default);

        Assert.NotNull(handler.LastRequest);
        Assert.StartsWith(StoredBaseUrl + "/", handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal(StoredKey, SentApiKey(handler));
    }

    // ---- the API key is never echoed to the response ----

    [Fact]
    public async Task MonitorStatus_ResponseNeverContainsTheApiKey()
    {
        const string secretKey = "SUPER-SECRET-KEY-9f3a";
        var store = await StoreWith(StoredBaseUrl, secretKey);
        var result = await NewExtension(store).MonitorStatusAsync(
            StatusReq(), ClientReturning("[]"), default);

        Assert.DoesNotContain(secretKey, ResponseJson(result), StringComparison.Ordinal);
    }

    // ---- server-side stashId resolution: no matching StashDB endpoint -> handled, and NO outbound call ----

    [Fact]
    public async Task Monitor_NoMatchingStashDbEndpoint_ReturnsNoIdentity_AndMakesNoOutboundCall()
    {
        var store = await StoreWith(StoredBaseUrl, StoredKey);
        var (client, handler) = ClientWithHandler("[]");

        var req = new MonitorRequest("studio", NonMatchingRemotes(), Monitored: true);
        var result = await NewExtension(store).MonitorAsync(
            req, client, default);
        Assert.Contains("NO_STASHDB_IDENTITY", ResponseJson(result), StringComparison.Ordinal);
        Assert.Equal(0, handler.CallCount); // rejected before any Whisparr call
    }

    [Fact]
    public async Task MonitorStatus_NoMatchingStashDbEndpoint_ReturnsNoIdentity_AndMakesNoOutboundCall()
    {
        var store = await StoreWith(StoredBaseUrl, StoredKey);
        var (client, handler) = ClientWithHandler("[]");

        var req = new MonitorStatusRequest("studio", NonMatchingRemotes());
        var result = await NewExtension(store).MonitorStatusAsync(
            req, client, default);

        Assert.Contains("NO_STASHDB_IDENTITY", ResponseJson(result), StringComparison.Ordinal);
        Assert.Equal(0, handler.CallCount);
    }

    // ---- an unknown kind is rejected with a 400 (never guessed) ----

    [Fact]
    public async Task Monitor_UnknownKind_Returns400()
    {
        var store = await StoreWith(StoredBaseUrl, StoredKey);
        var (client, handler) = ClientWithHandler("[]");

        var req = new MonitorRequest("franchise", MatchingRemotes(), Monitored: true);
        var result = await NewExtension(store).MonitorAsync(
            req, client, default);

        Assert.Equal(400, StatusOf(result));
        Assert.Equal(0, handler.CallCount);
    }
}
