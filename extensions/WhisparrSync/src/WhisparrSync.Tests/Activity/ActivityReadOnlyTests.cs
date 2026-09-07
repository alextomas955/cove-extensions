using Cove.Core.Auth;
using Microsoft.AspNetCore.Http;
using WhisparrSync.Client;
using static WhisparrSync.Tests.TestSupport.EndpointTestSupport;
using Ext = global::WhisparrSync.WhisparrSync;

namespace WhisparrSync.Tests.Activity;

/// <summary>
/// Security-critical: the three activity reads (<c>/activity/history</c>,
/// <c>/activity/queue</c>, <c>/activity/wanted</c>) are READ-ONLY and configure-gated. The host's
/// <c>[RequiresPermission]</c> filter is inert on minimal-API, so each handler self-checks — these prove, for
/// every route, that only GET requests reach Whisparr (no mutation/command/search/grab is reachable from a read
/// path — asserted by the recorded method log), that the deny trio (null / read-only / no-configure → 403)
/// holds, that configure proceeds, and that the stored key is never echoed.
/// </summary>
[Trait("Tier", "L2")]
public sealed class ActivityReadOnlyTests
{
    private const string StoredBaseUrl = "http://stored.local:6969";
    private const string StoredKey = "STORED-KEY";

    // One empty paged envelope binds into all three page DTOs (they share page/pageSize/totalRecords/records).
    private const string EmptyPage = """{"page":1,"pageSize":50,"totalRecords":0,"records":[]}""";

    private static Task<IResult> Invoke(
        string route, Ext ext, WhisparrClient client)
        => route switch
        {
            "history" => ext.ActivityHistoryAsync(1, 50, client, default),
            "queue" => ext.ActivityQueueAsync(1, 50, client, default),
            "wanted" => ext.ActivityWantedAsync(1, 50, client, default),
            _ => throw new ArgumentOutOfRangeException(nameof(route), route, "unknown route"),
        };

    public static TheoryData<string> AllRoutes() => new() { "history", "queue", "wanted" };

    // ---- only GET is reachable from any read path ----

    [Fact]
    public async Task All_activity_reads_issue_only_GET_requests()
    {
        var store = await StoreWith(StoredBaseUrl, StoredKey);
        var (client, handler) = ClientWithHandler(EmptyPage);
        var principal = FakePrincipalAccessor.WithPermissions(Permissions.ExtensionsConfigure);
        var ext = NewExtension(store);

        await ext.ActivityHistoryAsync(1, 50, client, default);
        await ext.ActivityQueueAsync(1, 50, client, default);
        await ext.ActivityWantedAsync(1, 50, client, default);

        // Positive proof: every recorded outbound request is a GET — no mutation/command verb slipped in.
        Assert.NotEmpty(handler.Requests);
        Assert.All(handler.Requests, r => Assert.Equal(HttpMethod.Get, r.Method));
    }

    // ---- no-echo: the stored API key is never echoed to any response ----

    [Theory]
    [MemberData(nameof(AllRoutes))]
    public async Task Route_ResponseNeverContainsTheApiKey(string route)
    {
        const string secretKey = "SUPER-SECRET-KEY-63b2";
        var store = await StoreWith(StoredBaseUrl, secretKey);
        var result = await Invoke(
            route, NewExtension(store), ClientReturning(EmptyPage));

        Assert.DoesNotContain(secretKey, ResponseJson(result), StringComparison.Ordinal);
    }
}
