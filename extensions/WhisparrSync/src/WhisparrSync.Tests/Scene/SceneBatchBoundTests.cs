using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Cove.Core.Auth;
using Cove.Extensions.Shared;
using WhisparrSync.Contracts;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.Scene;

// Every refusal is asserted against the job service as well as the answer, so a route that refused
// a caller and enqueued the run anyway fails here.
public sealed class SceneBatchBoundTests
{
    // The spelling the host's selection bar passes for a video selection.
    private const string Videos = "video";

    private const int Bound = 1000;

    // The search verb takes a lower bound, because its cost multiplies outside Cove.
    private const int SearchBound = 100;

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    // The selection is over both bounds, so a route that read the size first would answer a bound
    // code instead. A caller told to split would send two halves each still naming no verb.
    [Fact]
    public async Task ABodyNamingNoVerbIsRefusedBeforeTheSizeOfTheSelectionMatters()
    {
        await using var host = await MonitorHost.CreateAsync();

        var answered = await host.PostSceneBatchAsync(
            string.Create(
                CultureInfo.InvariantCulture,
                $$"""{"entityType":"{{Videos}}","coveIds":{{Ids(Bound + 1)}}}"""));

        Assert.Equal(HttpStatusCode.BadRequest, answered.StatusCode);
        var refusal = await answered.Content.ReadFromJsonAsync<ErrorCode>(TestCt);
        Assert.Equal("MISSING_VERB", refusal!.Code);
        Assert.Empty(host.Jobs.Enqueued);
    }

    [Fact]
    public async Task ABodyNamingNoIdsAtAllIsRefused()
    {
        await using var host = await MonitorHost.CreateAsync();

        var answered = await host.PostSceneBatchAsync(
            $$"""{"entityType":"{{Videos}}","verb":"monitor"}""");

        Assert.Equal(HttpStatusCode.BadRequest, answered.StatusCode);
        var refusal = await answered.Content.ReadFromJsonAsync<ErrorCode>(TestCt);
        Assert.Equal("MISSING_ENTITY_IDS", refusal!.Code);
        Assert.Empty(host.Jobs.Enqueued);
    }

    // An empty run in the Job Drawer reads as work that happened.
    [Fact]
    public async Task ASelectionOfNoScenesIsRefusedAsNothingSelected()
    {
        await using var host = await MonitorHost.CreateAsync();

        var answered = await host.PostSceneBatchAsync(BodyFor("monitor", 0));

        Assert.Equal(HttpStatusCode.BadRequest, answered.StatusCode);
        var refusal = await answered.Content.ReadFromJsonAsync<ErrorCode>(TestCt);
        Assert.Equal("NOTHING_SELECTED", refusal!.Code);
        Assert.Empty(host.Jobs.Enqueued);
    }

    [Theory]
    [InlineData("add")]
    [InlineData("monitor")]
    [InlineData("unmonitor")]
    [InlineData("exclude")]
    public async Task ASelectionOverTheThousandBoundIsRefusedNamingItAndNothingIsSent(string verb)
    {
        await using var host = await MonitorHost.CreateAsync();

        var answered = await host.PostSceneBatchAsync(BodyFor(verb, Bound + 1));

        Assert.Equal(HttpStatusCode.BadRequest, answered.StatusCode);
        var refusal = await answered.Content.ReadFromJsonAsync<ErrorCode>(TestCt);
        Assert.Equal("TOO_MANY_IDS", refusal!.Code);
        Assert.Equal(Bound, refusal.Max);
        Assert.Empty(host.Jobs.Enqueued);
        Assert.Empty(host.Client.Verbs);
    }

    // The browser chooses its sentence on the code and never on the text, so one code for both
    // bounds leaves the lower one undescribable.
    [Fact]
    public async Task ASearchSelectionOverItsOwnBoundIsRefusedUnderItsOwnCodeAndNothingIsSent()
    {
        await using var host = await MonitorHost.CreateAsync();

        var answered = await host.PostSceneBatchAsync(BodyFor("search", SearchBound + 1));

        Assert.Equal(HttpStatusCode.BadRequest, answered.StatusCode);
        var refusal = await answered.Content.ReadFromJsonAsync<ErrorCode>(TestCt);
        Assert.Equal("TOO_MANY_SEARCH_IDS", refusal!.Code);
        Assert.NotEqual("TOO_MANY_IDS", refusal.Code);
        Assert.Equal(SearchBound, refusal.Max);
        Assert.Empty(host.Jobs.Enqueued);
        Assert.Empty(host.Client.Verbs);
    }

    // A route holding one bound for every verb passes the refusal above and fails here.
    [Fact]
    public async Task ASelectionOverTheSearchBoundIsAcceptedForANonGrabbingVerb()
    {
        await using var host = await MonitorHost.CreateAsync();

        var answered = await host.PostSceneBatchAsync(BodyFor("add", SearchBound + 1));

        Assert.Equal(HttpStatusCode.Accepted, answered.StatusCode);
        var accepted = await answered.Content.ReadFromJsonAsync<JobEnqueued>(TestCt);
        Assert.False(string.IsNullOrWhiteSpace(accepted!.JobId));
        Assert.Single(host.Jobs.Enqueued);
    }

    // The refusal above is about the size, not about the verb.
    [Fact]
    public async Task ASearchSelectionAtItsOwnBoundIsEnqueued()
    {
        await using var host = await MonitorHost.CreateAsync();

        var answered = await host.PostSceneBatchAsync(BodyFor("search", SearchBound));

        Assert.Equal(HttpStatusCode.Accepted, answered.StatusCode);
        var enqueued = Assert.Single(host.Jobs.Enqueued);
        Assert.True(enqueued.Exclusive);
        Assert.Equal("ext:" + host.ExtensionId + ":scene-batch", enqueued.Type);
    }

    // The selection bar normalizes the videos plural to the singular before it matches, so the
    // route accepts the singular and the registration declares it.
    [Theory]
    [InlineData("videos")]
    [InlineData("studios")]
    [InlineData("performers")]
    public async Task ASelectionTypeThisRouteDoesNotAddressIsRefused(string entityType)
    {
        await using var host = await MonitorHost.CreateAsync();

        var answered = await host.PostSceneBatchAsync(
            string.Create(
                CultureInfo.InvariantCulture,
                $$"""{"entityType":"{{entityType}}","verb":"monitor","coveIds":[1,2]}"""));

        Assert.Equal(HttpStatusCode.BadRequest, answered.StatusCode);
        var refusal = await answered.Content.ReadFromJsonAsync<ErrorCode>(TestCt);
        Assert.Equal("UNSUPPORTED_ENTITY_TYPE", refusal!.Code);
        Assert.Empty(host.Jobs.Enqueued);
    }

    // The caller holds the tier the scene's own read sits at, so the refusal is about the configure
    // gate rather than about holding no permission at all.
    [Fact]
    public async Task TheRouteRefusesACallerWithoutTheConfigureTierAndEnqueuesNothing()
    {
        await using var host = await MonitorHost.CreateAsync(
            FakePrincipalAccessor.WithPermissions(Permissions.VideosRead));

        var answered = await host.PostSceneBatchAsync(BodyFor("monitor", 2));

        Assert.Equal(HttpStatusCode.Forbidden, answered.StatusCode);
        Assert.Empty(host.Jobs.Enqueued);
        Assert.Empty(host.Client.Verbs);
    }

    private static string BodyFor(string verb, int ids)
        => string.Create(
            CultureInfo.InvariantCulture,
            $$"""{"entityType":"{{Videos}}","verb":"{{verb}}","coveIds":{{Ids(ids)}}}""");

    private static string Ids(int count)
        => "[" + string.Join(',', Enumerable.Range(1, count)) + "]";
}
