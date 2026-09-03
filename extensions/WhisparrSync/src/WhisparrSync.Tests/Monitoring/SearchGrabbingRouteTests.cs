using System.Net;
using System.Net.Http.Json;
using Cove.Core.Auth;
using WhisparrSync.Contracts;
using WhisparrSync.Monitoring;
using WhisparrSync.Tests.Invariants;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Monitoring;

/// <summary>
/// The one gesture in this product that can make an instance download, over the path a user reaches.
/// </summary>
/// <remarks>
/// The guarantee this route is allowed to exist under is not that no grabbing verb is reachable. It
/// is that exactly one named gesture reaches one, and that nothing else can: the verb lives alone on
/// its own role, exactly one call site obtains that role by name, the route has its own path segment,
/// and no composed body and no bulk verb can express it. Each of those is asserted here rather than
/// read off the source.
/// <para>
/// Driven through the shipped registration rather than by calling the handler. A handler called
/// directly agrees with a route mounted at the wrong pattern, bound to a body the browser cannot
/// send, or reachable by a caller the declaration excludes.
/// </para>
/// <para>
/// No search is issued against a real instance here or anywhere else in this suite. What is asserted
/// is that the command left the seam once and carried the instance's own identifier; what the
/// instance then does with it is deliberately unmeasured.
/// </para>
/// </remarks>
public sealed class SearchGrabbingRouteTests
{
    /// <summary>The route segment the one grabbing gesture is served at.</summary>
    private const string SearchRoute = "search-all-monitored";

    /// <summary>A studio the instance holds and already monitors.</summary>
    private const string HeldAndMonitored =
        """{"id":9,"foreignId":"44e8ac11-9ed4-42e5-a9f4-bc2c138a5a6e","monitored":true}""";

    /// <summary>A studio the instance holds and does not yet monitor.</summary>
    private const string HeldNotMonitored =
        """{"id":9,"foreignId":"44e8ac11-9ed4-42e5-a9f4-bc2c138a5a6e","monitored":false}""";

    private const string LinksIntoPlace = """{"copyUsingHardlinks":true}""";

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    /// <summary>
    /// The route issues the search once for a studio the instance holds, naming the instance's own
    /// identifier.
    /// </summary>
    /// <remarks>
    /// The ARGUMENTS rather than a count: what makes this the right request is that the entity id it
    /// carries came from the instance's own record of the entity, not from the caller's route segment.
    /// </remarks>
    [Fact]
    public async Task AHeldStudioIsSearchedOnceWithTheInstancesOwnIdentifier()
    {
        await using var host = await HoldingHost(HeldAndMonitored);
        var studioId = await SeededStudio(host);

        var answered = await SearchAsync(host, "studio", studioId);
        var view = (await answered.Content.ReadFromJsonAsync<EntityMonitoringView>(TestCt))!;

        Assert.Equal(HttpStatusCode.OK, answered.StatusCode);
        Assert.Equal(MonitorRefusalKind.None, view.Refusal);

        var search = Assert.Single(
            host.Client.Acting,
            call => call.Verb == nameof(IWhisparrSearchGrabbing.SearchMonitoredAsync));
        Assert.Equal(WhisparrGeneration.V3, search.Generation);
        Assert.Equal(WhisparrEntityKind.Studio, search.Kind);
        Assert.Equal(9, search.EntityId);
        Assert.NotEqual(studioId, search.EntityId);
    }

    /// <summary>
    /// An entity the instance does not hold is refused, and no search leaves.
    /// </summary>
    /// <remarks>
    /// A search for an entity the instance has never heard of is not a request to make: it monitors
    /// nothing there, so the command would name a row that does not exist.
    /// </remarks>
    [Fact]
    public async Task AnEntityTheInstanceDoesNotHoldIsRefusedAndNoSearchLeaves()
    {
        await using var host = await MonitorHost.CreateAsync();
        var studioId = await SeededStudio(host);

        var view = await SearchViewAsync(host, "studio", studioId);

        Assert.NotEqual(MonitorRefusalKind.None, view.Refusal);
        Assert.False(view.Monitored);
        Assert.DoesNotContain(
            nameof(IWhisparrSearchGrabbing.SearchMonitoredAsync), host.Client.Verbs);
    }

    /// <summary>
    /// An entity carrying no identifier in the connected namespace is refused with nothing sent.
    /// </summary>
    /// <remarks>
    /// The outbound identifier comes only from the stored identity row, so an entity holding none has
    /// nothing this route could name and the refusal costs no outbound request at all.
    /// </remarks>
    [Fact]
    public async Task AnEntityCarryingNoIdentityIsRefusedWithNothingSent()
    {
        await using var host = await HoldingHost(HeldAndMonitored);
        var studioId = await host.SeedStudioAsync(null, null);

        var view = await SearchViewAsync(host, "studio", studioId);

        Assert.Equal(MonitorRefusalKind.NoIdentityInThisNamespace, view.Refusal);
        Assert.Empty(host.Client.Verbs);
    }

    /// <summary>
    /// A caller who cannot configure the extension cannot reach the route.
    /// </summary>
    /// <remarks>
    /// The most consequential route in this extension: it aims the stored credential at a third party
    /// AND spends the reader's bandwidth and disk. The host's declaration filter is inert on a
    /// minimal-API endpoint, so the in-handler gate is the enforcing one and this is what reads it.
    /// </remarks>
    [Fact]
    public async Task ACallerHoldingOnlyReadIsRefused()
    {
        await using var host = await MonitorHost.CreateAsync(
            FakePrincipalAccessor.WithPermissions(Permissions.VideosRead));

        var answered = await SearchAsync(host, "studio", 1);

        Assert.Equal(HttpStatusCode.Forbidden, answered.StatusCode);
        Assert.Empty(host.Client.Verbs);
    }

    [Fact]
    public async Task AKindTheRouteCannotParseIsABadRequest()
    {
        await using var host = await HoldingHost(HeldAndMonitored);

        var answered = await SearchAsync(host, "gallery", 1);

        Assert.Equal(HttpStatusCode.BadRequest, answered.StatusCode);
        Assert.Empty(host.Client.Verbs);
    }

    /// <summary>
    /// Exactly one place in the shipped product obtains the grabbing role by name.
    /// </summary>
    /// <remarks>
    /// Read out of the shipped source rather than off a grep in a checklist, so the count is asserted
    /// by something that runs. The source directory is located by walking up from the test assembly
    /// and a directory that cannot be found throws, because an assertion over no files at all would
    /// report the guarantee as held whatever the product does.
    /// <para>
    /// The role is the boundary: a call site that never asks for it by name has no implementation to
    /// express the request through, whatever it intended. One call site is what makes that a
    /// guarantee rather than a habit.
    /// </para>
    /// </remarks>
    [Fact]
    public void ExactlyOneProductionCallSiteObtainsTheGrabbingRole()
    {
        var sites = ObtainingTheGrabbingRole();

        Assert.Single(sites);
        Assert.StartsWith("WhisparrSync.Api.cs:", sites[0], StringComparison.Ordinal);
    }

    /// <summary>
    /// Every other gesture this extension serves reaches no grabbing verb at any position.
    /// </summary>
    /// <remarks>
    /// The whole monitor gesture, the whole unmonitor gesture, a whole scope change and a whole
    /// reflect-owned run, each driven to completion on one host so the ordered verb log holds all four.
    /// Every index rather than the last: a grab issued BEFORE an act would be just as acquiring.
    /// <para>
    /// Paired with an assertion that the log holds an acting verb, so a set of gestures that reached
    /// the instance not at all cannot satisfy this.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task NoOtherMountedGestureReachesAGrabbingVerbAtAnyPosition()
    {
        await using var host = await HoldingHost(HeldNotMonitored);
        var studioId = await SeededStudio(host);
        await host.SeedStudioFileAsync(studioId, "/library/vixen/2026");

        Assert.Equal(MonitorRefusalKind.None, (await host.MonitorAsync(studioId)).Refusal);
        await host.ChangeScopeAsync("studio", studioId, "allScenes");
        await host.UnmonitorAsync("studio", studioId);
        Assert.NotNull((await host.ReflectOwnedViewAsync("studio", studioId)).JobId);
        await host.Jobs.RunLastAsync(new RecordingJobProgress(), TestCt);

        Assert.Contains(
            host.Client.Verbs,
            verb => OutboundSeam.VerbClassByMember[verb] == WhisparrVerbClass.Act);
        Assert.DoesNotContain(
            nameof(IWhisparrSearchGrabbing.SearchMonitoredAsync), host.Client.Verbs);
        Assert.All(
            host.Client.Verbs,
            verb => Assert.NotEqual(
                WhisparrVerbClass.Grab, OutboundSeam.VerbClassByMember[verb]));
    }

    /// <summary>
    /// The bulk route carries no verb that reaches the search, and cannot be told to.
    /// </summary>
    /// <remarks>
    /// A selection is where one gesture becomes thousands of requests, and this library holds
    /// thousands of performers. The verb is absent from the bulk request's own enum, so naming it is
    /// not a refusal the route decides on but a body that does not bind at all.
    /// </remarks>
    [Fact]
    public async Task TheBulkRouteDeclaresNoSearchVerbAndCannotBeToldToUseOne()
    {
        Assert.Equal(
            [MonitorBulkVerb.Monitor, MonitorBulkVerb.Unmonitor], Enum.GetValues<MonitorBulkVerb>());

        await using var host = await HoldingHost(HeldAndMonitored);
        var studioId = await SeededStudio(host);

        var answered = await host.PostBulkAsync(
            $$"""{"entityType":"studios","verb":"searchAllMonitored","entityIds":[{{studioId}}]}""");

        Assert.False(answered.IsSuccessStatusCode);
        Assert.Empty(host.Client.Verbs);
        Assert.Empty(host.Jobs.Enqueued);
    }

    /// <summary>A host whose instance answers a studio read with <paramref name="studio"/>.</summary>
    private static async Task<MonitorHost> HoldingHost(string studio)
    {
        var host = await MonitorHost.CreateAsync();
        host.Client
            .Answering(nameof(IWhisparrStudioActing.ReadStudioAsync), MonitorHost.Json(200, studio))
            .Answering(
                nameof(IWhisparrReflectOwnedActing.ReadHardlinkSettingAsync),
                MonitorHost.Json(200, LinksIntoPlace));
        return host;
    }

    private static Task<int> SeededStudio(MonitorHost host)
        => host.SeedStudioAsync(MonitorHost.StoredEndpoint, MonitorHost.StudioRemoteIdValue);

    /// <summary>The raw answer to one entity's search route, which takes no body at all.</summary>
    private static Task<HttpResponseMessage> SearchAsync(MonitorHost host, string kind, int coveId)
        => host.Http.PostAsync(host.RouteFor(kind, coveId, SearchRoute), content: null, TestCt);

    private static async Task<EntityMonitoringView> SearchViewAsync(
        MonitorHost host, string kind, int coveId)
    {
        var answered = await SearchAsync(host, kind, coveId);
        answered.EnsureSuccessStatusCode();
        return (await answered.Content.ReadFromJsonAsync<EntityMonitoringView>(TestCt))!;
    }

    /// <summary>Every shipped source line obtaining the grabbing role by name, as file and line.</summary>
    private static IReadOnlyList<string> ObtainingTheGrabbingRole()
    {
        var obtaining = "Obtain<" + nameof(IWhisparrSearchGrabbing) + ">";

        return
        [
            .. Directory
                .EnumerateFiles(ShippedSourceRoot(), "*.cs", SearchOption.AllDirectories)
                .OrderBy(file => file, StringComparer.Ordinal)
                .SelectMany(file => File.ReadLines(file)
                    .Select((text, index) => (File: file, Number: index + 1, Text: text))
                    .Where(line => line.Text.Contains(obtaining, StringComparison.Ordinal))
                    .Select(line => $"{Path.GetFileName(line.File)}:{line.Number}"))
        ];
    }

    /// <summary>Where the shipped extension's own source lives, above this test assembly.</summary>
    /// <exception cref="InvalidOperationException">
    /// It was not found. An enumeration over no files would report every source-level guarantee as
    /// held, so this throws rather than answering an empty list.
    /// </exception>
    private static string ShippedSourceRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var project = Path.Combine(directory.FullName, "WhisparrSync", "WhisparrSync.csproj");
            if (File.Exists(project))
            {
                return Path.GetDirectoryName(project)!;
            }
        }

        throw new InvalidOperationException(
            $"No WhisparrSync.csproj was found above {AppContext.BaseDirectory}, so a source-level "
                + "assertion here would hold over no files at all.");
    }
}
