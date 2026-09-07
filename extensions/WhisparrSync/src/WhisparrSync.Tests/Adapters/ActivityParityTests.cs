using WhisparrSync.Activity;
using WhisparrSync.Adapters;
using WhisparrSync.Client;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.Adapters;

/// <summary>
/// Version uniformity: the queue + wanted activity reads present the SAME projected shape on v3 and v2. Both
/// adapters implement <see cref="IWhisparrActivitySource"/> (it rides the <see cref="IWhisparrAdapter"/>
/// aggregate, not a v3-only role), and both serve the reads on the same <c>/api/v3</c> path — so the same
/// upstream envelope projects byte-identically through the version-agnostic <see cref="ActivityProjector"/>.
/// Bare-safe (Tier L0 like the sibling <c>V2OutwardParityTests</c>): no cove type, no host, no live Whisparr —
/// the adapters run over a <see cref="FakeHttpMessageHandler"/>.
/// </summary>
[Trait("Tier", "L0")]
public sealed class ActivityParityTests
{
    private const string BaseUrl = "http://localhost:6970";
    private const string ApiKey = "test-api-key";

    private const string QueueJson = """
        {"page":1,"pageSize":50,"totalRecords":1,"records":[
          {"id":1,"title":"Release.Title","status":"downloading","trackedDownloadState":"downloading",
           "trackedDownloadStatus":"ok","size":1000,"sizeleft":250,"timeleft":"00:10:00"}]}
        """;

    private const string WantedJson = """
        {"page":1,"pageSize":50,"totalRecords":1,"records":[
          {"id":1,"title":"Wanted.Scene","monitored":true,"hasFile":false,"releaseDate":"2026-01-01",
           "added":"2026-06-01T00:00:00Z","studioTitle":"Vixen"}]}
        """;

    private static V3Adapter V3On(string json)
        => new(new WhisparrClient(new HttpClient(FakeHttpMessageHandler.Json(json))), TimeSpan.Zero);

    private static V2Adapter V2On(string json)
        => new(new WhisparrClient(new HttpClient(FakeHttpMessageHandler.Json(json))), TimeSpan.Zero);

    [Fact]
    public void Both_adapters_implement_the_activity_source_role()
    {
        Assert.True(typeof(IWhisparrActivitySource).IsAssignableFrom(typeof(V3Adapter)));
        Assert.True(typeof(IWhisparrActivitySource).IsAssignableFrom(typeof(V2Adapter)));
    }

    [Fact]
    public async Task Queue_projects_the_same_row_shape_on_v3_and_v2()
    {
        var v3 = await V3On(QueueJson).ListQueueAsync(BaseUrl, ApiKey, 1, 50, CancellationToken.None);
        var v2 = await V2On(QueueJson).ListQueueAsync(BaseUrl, ApiKey, 1, 50, CancellationToken.None);

        var v3Rows = ActivityProjector.QueuePage(v3.Value!).Records;
        var v2Rows = ActivityProjector.QueuePage(v2.Value!).Records;

        // Element-wise record equality: same normalized state, progress, eta, and display facts on both versions.
        Assert.Equal(v3Rows, v2Rows);
        Assert.Single(v3Rows);
    }

    [Fact]
    public async Task Wanted_projects_the_same_row_shape_on_v3_and_v2()
    {
        var v3 = await V3On(WantedJson).ListWantedAsync(BaseUrl, ApiKey, 1, 50, CancellationToken.None);
        var v2 = await V2On(WantedJson).ListWantedAsync(BaseUrl, ApiKey, 1, 50, CancellationToken.None);

        var v3Rows = ActivityProjector.WantedPage(v3.Value!).Records;
        var v2Rows = ActivityProjector.WantedPage(v2.Value!).Records;

        Assert.Equal(v3Rows, v2Rows);
        Assert.Single(v3Rows);
    }
}
