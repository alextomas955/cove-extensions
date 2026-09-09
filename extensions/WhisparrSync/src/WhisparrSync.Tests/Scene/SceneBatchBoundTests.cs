using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Cove.Core.Auth;
using Cove.Extensions.Shared;
using WhisparrSync.Contracts;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.Scene;

/// <summary>
/// What the scene batch route refuses before anything is encoded or enqueued, and which bound each
/// refusal names.
/// </summary>
/// <remarks>
/// The two bounds are the point. A single-bound implementation passes the thousand cases and fails
/// the search pair, and a shared code makes the lower bound undescribable to a reader.
/// <para>
/// Every refusal is asserted against the job service as well as the answer, so a route that refused
/// a caller and enqueued the run anyway is a failure here.
/// </para>
/// </remarks>
public sealed class SceneBatchBoundTests
{
    /// <summary>The spelling the host's selection bar passes for a video selection.</summary>
    private const string Videos = "video";

    /// <summary>The bound the four non-grabbing verbs take.</summary>
    private const int Bound = 1000;

    /// <summary>The bound the search verb takes, because its cost multiplies outside Cove.</summary>
    private const int SearchBound = 100;

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    /// <summary>
    /// A body naming no verb is refused before the size of the selection matters.
    /// </summary>
    /// <remarks>
    /// The selection here is over both bounds, so a route that read the size first would answer one
    /// of the two bound codes. The verb decides what the request is, and a caller told to split
    /// would send two halves each still naming no verb.
    /// </remarks>
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

    /// <summary>An empty run in the Job Drawer reads as work that happened.</summary>
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

    /// <summary>
    /// A selection one over the thousand bound is refused for each of the four non-grabbing verbs.
    /// </summary>
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

    /// <summary>
    /// A selection one over the search bound is refused under a code of the search verb's own.
    /// </summary>
    /// <remarks>
    /// The code is distinct from the thousand bound's, because the browser chooses its sentence on
    /// the code and never on the text: one code for both bounds makes the lower one undescribable.
    /// </remarks>
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

    /// <summary>
    /// The same size is accepted for a non-grabbing verb, because the lower bound is the search
    /// verb's alone.
    /// </summary>
    /// <remarks>
    /// The discriminating half of the pair. A route holding one bound for every verb passes the
    /// refusal above and fails here.
    /// </remarks>
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

    /// <summary>
    /// A search selection at its bound is enqueued, so the refusal above is about the size.
    /// </summary>
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

    /// <summary>
    /// A selection type other than the videos one is refused.
    /// </summary>
    /// <remarks>
    /// The plural is the trap: the bar normalizes the videos plural to the singular before it
    /// matches, so the route accepts the singular and the registration declares it.
    /// </remarks>
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

    /// <summary>
    /// The route refuses a caller holding the read tier and enqueues nothing.
    /// </summary>
    /// <remarks>
    /// The caller holds the tier the scene's own read sits at, so a pass here is about the configure
    /// gate and not about holding no permission at all. The gate is the handler's first statement,
    /// so the body is never read.
    /// </remarks>
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
