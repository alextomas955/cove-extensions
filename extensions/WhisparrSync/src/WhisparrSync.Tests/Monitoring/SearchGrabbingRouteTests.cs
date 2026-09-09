using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Cove.Core.Auth;
using Cove.Core.Interfaces;
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
/// and no composed body can express it. Each of those is asserted here rather than read off the
/// source.
/// <para>
/// The selection bar offers that same gesture over a whole selection. It is the same named verb
/// reached through the same obtained role, so the guarantee is unchanged: a selection carries one
/// command over the entities the instance holds, and everything else the bar offers still reaches no
/// grabbing verb at all.
/// </para>
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

    /// <summary>A second studio the instance holds and monitors, under an identifier of its own.</summary>
    private const string SecondHeldAndMonitored =
        """{"id":11,"foreignId":"9f0d6f27-1f3a-4a5f-8b21-6b2d3a5f9c10","monitored":true}""";

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
        Assert.Equal([9], search.EntityIds);
        Assert.DoesNotContain(studioId, search.EntityIds!);
    }

    /// <summary>
    /// One press issues one grabbing-class verb, and no second one.
    /// </summary>
    /// <remarks>
    /// Read as a CLASS over the whole ordered log rather than as a count of one member name, so a
    /// second grabbing member added to the seam and issued from here fails this too. One press
    /// becoming two commands is two searches against a third party, and the verb's class has no
    /// retry entry precisely so an attempt whose answer did not arrive is reported rather than
    /// re-issued.
    /// </remarks>
    [Fact]
    public async Task OnePressIssuesOneGrabbingClassVerbAndNoSecond()
    {
        await using var host = await HoldingHost(HeldAndMonitored);
        var studioId = await SeededStudio(host);

        Assert.Equal(MonitorRefusalKind.None, (await SearchViewAsync(host, "studio", studioId)).Refusal);

        Assert.Single(
            host.Client.Verbs,
            verb => OutboundSeam.VerbClassByMember[verb] == WhisparrVerbClass.Grab);
        Assert.Contains(
            host.Client.Verbs,
            verb => OutboundSeam.VerbClassByMember[verb] == WhisparrVerbClass.Read);
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
        await using var host = await HoldingHost(HeldNotMonitored, HeldAndMonitored);
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
    /// A selection is searched in ONE command naming every entity the instance holds.
    /// </summary>
    /// <remarks>
    /// The instance's own command takes an id array and iterates the whole of it, so the selection is
    /// one call rather than one per entity. The ids are ASSERTED, not a count: what makes this the
    /// right request is that each one came from the instance's own record of that entity rather than
    /// from the selection the browser sent.
    /// </remarks>
    [Fact]
    public async Task ASelectionIsSearchedInOneCommandNamingEveryEntityTheInstanceHolds()
    {
        await using var host = await AnsweringStudioReads(
            Held(HeldAndMonitored), Held(SecondHeldAndMonitored));
        var first = await SeededStudio(host);
        var second = await SeededStudio(host);
        var progress = new RecordingJobProgress();

        await BulkSearchAsync(host, first, second);
        await host.RunEnqueuedBatchAsync(progress);

        var search = Assert.Single(
            host.Client.Acting,
            call => call.Verb == nameof(IWhisparrSearchGrabbing.SearchMonitoredAsync));
        Assert.Equal(WhisparrEntityKind.Studio, search.Kind);
        Assert.Equal([9, 11], search.EntityIds);
        Assert.Equal(
            [JobUnitOutcome.Succeeded, JobUnitOutcome.Succeeded],
            progress.Units.Select(unit => unit.Outcome));
        Assert.Equal((1d, "2 applied, 0 refused."), Assert.Single(progress.Reports));
    }

    /// <summary>
    /// An entity the instance does not hold is reported and left out, and the rest are still searched.
    /// </summary>
    /// <remarks>
    /// One unheld id fails the whole command and the answer names only that one, so a selection sent
    /// as supplied would cost every other entity its search and leave the reader an error naming one
    /// id out of a hundred.
    /// </remarks>
    [Fact]
    public async Task AnEntityTheInstanceDoesNotHoldIsLeftOutAndTheRestAreStillSearched()
    {
        await using var host = await AnsweringStudioReads(NotHeld, Held(HeldAndMonitored));
        var absent = await SeededStudio(host);
        var held = await SeededStudio(host);
        var progress = new RecordingJobProgress();

        await BulkSearchAsync(host, absent, held);
        await host.RunEnqueuedBatchAsync(progress);

        var search = Assert.Single(
            host.Client.Acting,
            call => call.Verb == nameof(IWhisparrSearchGrabbing.SearchMonitoredAsync));
        Assert.Equal([9], search.EntityIds);

        // In the order the ids were supplied, so a reader can match them against the selection.
        Assert.Equal(
            [
                new ReportedUnit(
                    Unit(absent),
                    JobUnitOutcome.Skipped,
                    nameof(MonitorRefusalKind.InstanceHoldsNoSuchEntity)),
                new ReportedUnit(Unit(held), JobUnitOutcome.Succeeded, null),
            ],
            progress.Units);
        Assert.Equal((1d, "1 applied, 1 refused."), Assert.Single(progress.Reports));
    }

    /// <summary>
    /// A selection the instance holds none of sends nothing at all.
    /// </summary>
    /// <remarks>
    /// There is no command to compose. The instance accepts one whose id array is empty and runs it
    /// over nothing, which a reader cannot tell from a search that found nothing.
    /// </remarks>
    [Fact]
    public async Task ASelectionTheInstanceHoldsNoneOfSendsNothing()
    {
        await using var host = await AnsweringStudioReads(NotHeld);
        var first = await SeededStudio(host);
        var second = await SeededStudio(host);
        var progress = new RecordingJobProgress();

        await BulkSearchAsync(host, first, second);
        await host.RunEnqueuedBatchAsync(progress);

        Assert.DoesNotContain(
            nameof(IWhisparrSearchGrabbing.SearchMonitoredAsync), host.Client.Verbs);
        Assert.All(progress.Units, unit => Assert.Equal(JobUnitOutcome.Skipped, unit.Outcome));
        Assert.Equal((1d, "0 applied, 2 refused."), Assert.Single(progress.Reports));
    }

    /// <summary>
    /// A caller who cannot configure the extension cannot reach the selection's route either.
    /// </summary>
    [Fact]
    public async Task ACallerHoldingOnlyReadCannotSearchASelection()
    {
        await using var host = await MonitorHost.CreateAsync(
            FakePrincipalAccessor.WithPermissions(Permissions.VideosRead));

        var answered = await BulkSearchAsync(host, 1);

        Assert.Equal(HttpStatusCode.Forbidden, answered.StatusCode);
        Assert.Empty(host.Client.Verbs);
        Assert.Empty(host.Jobs.Enqueued);
    }

    /// <summary>The selection route's answer for a search over <paramref name="coveIds"/>.</summary>
    private static Task<HttpResponseMessage> BulkSearchAsync(MonitorHost host, params int[] coveIds)
    {
        var ids = string.Join(",", coveIds.Select(
            coveId => coveId.ToString(CultureInfo.InvariantCulture)));

        return host.PostBulkAsync(
            $$"""{"entityType":"studios","verb":"searchAllMonitored","entityIds":[{{ids}}]}""");
    }

    /// <summary>The unit id one Cove entity's turn is reported under.</summary>
    private static string Unit(int coveId) => coveId.ToString(CultureInfo.InvariantCulture);

    /// <summary>A host whose instance answers a studio read with <paramref name="studio"/>.</summary>
    /// <summary>One host whose studio read answers <paramref name="studio"/> in turn.</summary>
    /// <remarks>
    /// Several answers describe one instance acting between two reads, which is what the monitor
    /// path's own read-back then classifies from. The last one repeats, so a case that names one
    /// answer describes an instance that never changes.
    /// </remarks>
    private static Task<MonitorHost> HoldingHost(params string[] studio)
        => AnsweringStudioReads([.. studio.Select(Held)]);

    /// <summary>An instance answering studio reads with <paramref name="answers"/> in turn.</summary>
    /// <remarks>
    /// The answers rather than their bodies, so a case can describe an instance that holds one
    /// selected entity and not another.
    /// </remarks>
    private static async Task<MonitorHost> AnsweringStudioReads(params WhisparrResponse[] answers)
    {
        var host = await MonitorHost.CreateAsync();
        host.Client
            .Answering(nameof(IWhisparrStudioActing.ReadStudioAsync), answers)
            .Answering(
                nameof(IWhisparrReflectOwnedActing.ReadHardlinkSettingAsync),
                MonitorHost.Json(200, LinksIntoPlace));
        return host;
    }

    /// <summary>How an instance answers a read of a studio it holds.</summary>
    private static WhisparrResponse Held(string studio) => MonitorHost.Json(200, studio);

    /// <summary>How an instance answers a read of a studio it does not hold.</summary>
    private static WhisparrResponse NotHeld => MonitorHost.Json(404, "");

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
