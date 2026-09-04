using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cove.Extensions.Shared;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.Jobs;

/// <summary>
/// What the bulk route does with a body that names no verb, and whether the emitted wire document
/// and the server agree about which members a caller must supply.
/// </summary>
/// <remarks>
/// The bodies are raw strings rather than serialized records, because the defect is about a member
/// that is ABSENT: a serialized record always carries every member it declares, so a case built from
/// one could never send the body a browser can.
/// </remarks>
public sealed class BulkVerbGuardTests
{
    private const string Studios = "studios";

    /// <summary>The bound the route declares, which an over-cap case has to exceed.</summary>
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

    /// <summary>
    /// A member spelled out as null is the same request as one left out, because both bind to null.
    /// </summary>
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

    /// <summary>
    /// The control the two refusals above need: without it a refused enqueue could equally mean the
    /// route stopped accepting anything.
    /// </summary>
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

    /// <summary>
    /// The verb decides what the request IS, so a body naming none is refused without the size of the
    /// selection mattering.
    /// </summary>
    /// <remarks>
    /// Answering the cap first would report the wrong problem: a caller told to split a selection
    /// would send two halves, each still naming no verb.
    /// </remarks>
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

    /// <summary>
    /// Every member the emitted document lists as mandatory that the server refuses the absence of,
    /// derived by leaving each one out in turn.
    /// </summary>
    /// <remarks>
    /// The emit lists every positional member of a request record, whatever its nullability, so the
    /// list is not by itself a statement about what the server enforces. What is asserted is the
    /// agreement per member: each member the server refuses the absence of is one the document names,
    /// and the one member the server accepts the absence of is named with the reason it is legal.
    /// </remarks>
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

    /// <summary>
    /// The one member the server accepts the absence of, and why: null means take the stored default
    /// rather than take an unnamed one, and a verb that expresses no scope names none.
    /// </summary>
    [Fact]
    public async Task AnAbsentScopeIsAcceptedBecauseNullNamesTheStoredDefault()
    {
        await using var host = await MonitorHost.CreateAsync();

        var answered = await host.PostBulkAsync(BodyWithout("scope"));

        Assert.Equal(HttpStatusCode.Accepted, answered.StatusCode);
        Assert.Contains("scope", MandatoryInDocument());
    }

    /// <summary>The members the committed document lists as mandatory for one bulk body.</summary>
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

    /// <summary>One whole bulk body with <paramref name="member"/> left out of it.</summary>
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
