using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cove.Extensions.Shared;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.Jobs;

// The bodies are raw strings rather than serialized records. A serialized record always carries
// every member it declares, so it cannot send a body that leaves one out.
public sealed class BulkVerbGuardTests
{
    private const string Studios = "studios";

    // Mirrors the selection cap the bulk route declares.
    private const int Cap = 1000;

    [Fact]
    public async Task ABodyNamingNoVerbIsRefusedAndNothingIsEnqueuedAndNothingIsSent()
    {
        await using var host = await MonitorHost.CreateAsync();

        var answered = await host.PostBulkAsync("""{"entityType":"studios","entityIds":[1,2,3]}""");

        Assert.Equal(HttpStatusCode.BadRequest, answered.StatusCode);
        var refusal = await answered.Content.ReadFromJsonAsync<ErrorCode>(TestCt);
        Assert.Equal("MISSING_VERB", refusal!.Code);
        Assert.Empty(host.Jobs.Enqueued);
        Assert.Empty(host.Client.Verbs);
    }

    // A member spelled out as null binds the same as one left out.
    [Fact]
    public async Task ABodyNamingANullVerbIsRefusedTheSameWay()
    {
        await using var host = await MonitorHost.CreateAsync();

        var answered = await host.PostBulkAsync(
            """{"entityType":"studios","verb":null,"entityIds":[1,2,3]}""");

        Assert.Equal(HttpStatusCode.BadRequest, answered.StatusCode);
        var refusal = await answered.Content.ReadFromJsonAsync<ErrorCode>(TestCt);
        Assert.Equal("MISSING_VERB", refusal!.Code);
        Assert.Empty(host.Jobs.Enqueued);
    }

    // The control for the two refusals above. Without it a refused enqueue could equally mean the
    // route stopped accepting anything.
    [Fact]
    public async Task ABodyNamingAVerbThisProductServesIsStillEnqueued()
    {
        await using var host = await MonitorHost.CreateAsync();

        var answered = await host.PostBulkAsync(
            """{"entityType":"studios","verb":"monitor","entityIds":[1,2,3]}""");

        Assert.Equal(HttpStatusCode.Accepted, answered.StatusCode);
        Assert.Single(host.Jobs.Enqueued);
    }

    [Fact]
    public async Task AVerbSpellingThisProductDoesNotServeIsRefusedAndNothingIsEnqueued()
    {
        await using var host = await MonitorHost.CreateAsync();

        var answered = await host.PostBulkAsync(
            """{"entityType":"studios","verb":"grab","entityIds":[1,2,3]}""");

        Assert.Equal(HttpStatusCode.BadRequest, answered.StatusCode);
        Assert.Empty(host.Jobs.Enqueued);
        Assert.Empty(host.Client.Verbs);
    }

    // The missing verb is answered before the cap. A caller told to split a selection would send
    // two halves, each still naming no verb.
    [Fact]
    public async Task AnOverCapBodyNamingNoVerbIsRefusedForTheVerbRatherThanForTheCap()
    {
        await using var host = await MonitorHost.CreateAsync();
        var ids = string.Join(',', Enumerable.Range(1, Cap + 1));

        var answered = await host.PostBulkAsync(
            $$"""{"entityType":"studios","entityIds":[{{ids}}]}""");

        Assert.Equal(HttpStatusCode.BadRequest, answered.StatusCode);
        var refusal = await answered.Content.ReadFromJsonAsync<ErrorCode>(TestCt);
        Assert.Equal("MISSING_VERB", refusal!.Code);
        Assert.Empty(host.Jobs.Enqueued);
    }

    // The emitted document lists every positional member of a request record whatever its
    // nullability, so the list alone says nothing about what the server enforces. What is asserted
    // is the agreement per member, member by member.
    [Fact]
    public async Task TheDocumentAndTheServerAgreeAboutWhichMembersMayNotBeLeftOut()
    {
        await using var host = await MonitorHost.CreateAsync();
        var refused = new List<string>();

        foreach (var member in MandatoryInDocument())
        {
            var answered = await host.PostBulkAsync(BodyWithout(member));
            if (answered.StatusCode == HttpStatusCode.BadRequest)
            {
                refused.Add(member);
            }
        }

        Assert.Equal(["entityType", "verb", "entityIds"], refused);
    }

    // Scope is the one member the server accepts the absence of. A null scope means the stored
    // default.
    [Fact]
    public async Task AnAbsentScopeIsAcceptedBecauseNullNamesTheStoredDefault()
    {
        await using var host = await MonitorHost.CreateAsync();

        var answered = await host.PostBulkAsync(BodyWithout("scope"));

        Assert.Equal(HttpStatusCode.Accepted, answered.StatusCode);
        Assert.Contains("scope", MandatoryInDocument());
    }

    private static IReadOnlyList<string> MandatoryInDocument()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(WireDocument.Path()));

        return
        [
            .. document.RootElement
                .GetProperty("components")
                .GetProperty("schemas")
                .GetProperty("MonitorBulkRequest")
                .GetProperty("required")
                .EnumerateArray()
                .Select(member => member.GetString()!)
        ];
    }

    private static string BodyWithout(string member)
    {
        var members = new List<string>
        {
            $"\"entityType\":\"{Studios}\"",
            "\"verb\":\"monitor\"",
            "\"scope\":\"futureScenes\"",
            "\"entityIds\":[1,2,3]",
        };

        members.RemoveAll(
            declared => declared.StartsWith($"\"{member}\":", StringComparison.Ordinal));

        return "{" + string.Join(',', members) + "}";
    }

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;
}
