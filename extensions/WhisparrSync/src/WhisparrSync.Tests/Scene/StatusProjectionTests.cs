using System.Net;
using System.Text.Json;
using WhisparrSync.Client;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.Scene;

/// <summary>
/// Host-free contract for the skeleton's single outbound round-trip: a 200/JSON status projects to
/// Ok with Version + InstanceName, a 401 classifies as BadKey, and the request targets
/// <c>/api/v3/system/status</c> with the <c>X-Api-Key</c> header attached.
/// </summary>
public sealed class StatusProjectionTests
{
    private const string BaseUrl = "http://localhost:6969";
    private const string ApiKey = "test-api-key";

    [Fact]
    public async Task Ok_json_status_projects_version_and_instance_name()
    {
        var body = JsonSerializer.Serialize(new
        {
            version = "3.3.4.808",
            appName = "Whisparr",
            instanceName = "My Whisparr",
            branch = "eros-develop",
        });
        var handler = FakeHttpMessageHandler.Json(body);
        var client = new WhisparrClient(new HttpClient(handler));

        var result = await client.GetStatusAsync(BaseUrl, ApiKey, CancellationToken.None);

        Assert.True(result.IsOk);
        Assert.Equal(WhisparrResultState.Ok, result.State);
        Assert.NotNull(result.Value);
        Assert.Equal("3.3.4.808", result.Value!.Version);
        Assert.Equal("My Whisparr", result.Value.InstanceName);
    }

    [Fact]
    public async Task Unauthorized_status_classifies_as_bad_key()
    {
        var handler = FakeHttpMessageHandler.Status(HttpStatusCode.Unauthorized);
        var client = new WhisparrClient(new HttpClient(handler));

        var result = await client.GetStatusAsync(BaseUrl, ApiKey, CancellationToken.None);

        Assert.False(result.IsOk);
        Assert.Equal(WhisparrResultState.BadKey, result.State);
    }

    [Fact]
    public async Task Request_targets_v3_status_and_attaches_api_key_header()
    {
        var handler = FakeHttpMessageHandler.Json("{\"version\":\"3.0.0.0\"}");
        var client = new WhisparrClient(new HttpClient(handler));

        await client.GetStatusAsync(BaseUrl, ApiKey, CancellationToken.None);

        Assert.NotNull(handler.LastRequest);
        Assert.Equal($"{BaseUrl}/api/v3/system/status", handler.LastRequest!.RequestUri!.ToString());
        Assert.True(handler.LastRequest.Headers.TryGetValues("X-Api-Key", out var values));
        Assert.Equal(ApiKey, Assert.Single(values!));
    }
}
