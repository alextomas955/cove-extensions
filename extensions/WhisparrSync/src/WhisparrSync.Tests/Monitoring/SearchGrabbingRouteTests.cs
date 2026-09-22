using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Cove.Core.Auth;
using Cove.Core.Interfaces;
using WhisparrSync.Contracts;
using WhisparrSync.Tests.Invariants;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Monitoring;

// The guarantee is not that no grabbing verb is reachable. It is that exactly one named gesture
// reaches one: the verb lives alone on its own role, exactly one call site obtains that role by
// name, the route has its own path segment, and no composed body can express it. Each is asserted
// here rather than read off the source.
//
// Driven through the shipped registration rather than by calling the handler. A handler called
// directly agrees with a route mounted at the wrong pattern, bound to a body the browser cannot
// send, or reachable by a caller the declaration excludes.
//
// No search is issued against a real instance. What is asserted is that the command left the seam
// once and carried the instance's own identifier.
public sealed class SearchGrabbingRouteTests
{
    private const string SearchRoute = "search-all-monitored";

    private const string HeldAndMonitored =
        """{"id":9,"foreignId":"44e8ac11-9ed4-42e5-a9f4-bc2c138a5a6e","monitored":true}""";

    private const string HeldNotMonitored =
        """{"id":9,"foreignId":"44e8ac11-9ed4-42e5-a9f4-bc2c138a5a6e","monitored":false}""";

    private const string SecondHeldAndMonitored =
        """{"id":11,"foreignId":"9f0d6f27-1f3a-4a5f-8b21-6b2d3a5f9c10","monitored":true}""";

    private const string LinksIntoPlace = """{"copyUsingHardlinks":true}""";

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    // The arguments rather than a count: what makes this the right request is that the entity id it
    // carries came from the instance's own record of the entity, not from the caller's route segment.
    [Fact]
    public async Task AHeldStudioIsSearchedOnceWithTheInstancesOwnIdentifier()
    {
        await using var host = await HoldingHost(HeldAndMonitored);
        host.Client.Answering(nameof(RecordingWhisparrCore.SearchMonitoredAsync), MonitorHost.Json(200, "{}"));
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

    // Read as a class over the whole ordered log rather than as a count of one member name, so a
    // second grabbing member added to the seam and issued from here fails this too. The verb's class
    // has no retry entry, so an attempt whose answer did not arrive is reported rather than re-issued.
    [Fact]
    public async Task OnePressIssuesOneGrabbingClassVerbAndNoSecond()
    {
        await using var host = await HoldingHost(HeldAndMonitored);
        host.Client.Answering(nameof(RecordingWhisparrCore.SearchMonitoredAsync), MonitorHost.Json(200, "{}"));
        var studioId = await SeededStudio(host);

        Assert.Equal(MonitorRefusalKind.None, (await SearchViewAsync(host, "studio", studioId)).Refusal);

        Assert.Single(
            host.Client.Verbs,
            verb => OutboundSeam.VerbClassByMember[verb] == WhisparrVerbClass.Grab);
        Assert.Contains(
            host.Client.Verbs,
            verb => OutboundSeam.VerbClassByMember[verb] == WhisparrVerbClass.Read);
    }

    // A search for an entity the instance has never heard of would name a row that does not exist.
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

    // The outbound identifier comes only from the stored identity row, so an entity holding none has
    // nothing this route could name.
    [Fact]
    public async Task AnEntityCarryingNoIdentityIsRefusedWithNothingSent()
    {
        await using var host = await HoldingHost(HeldAndMonitored);
        var studioId = await host.SeedStudioAsync(null, null);

        var view = await SearchViewAsync(host, "studio", studioId);

        Assert.Equal(MonitorRefusalKind.NoIdentityInThisNamespace, view.Refusal);
        Assert.Empty(host.Client.Verbs);
    }

    // The host's declaration filter is inert on a minimal-API endpoint, so the in-handler gate is
    // the enforcing one and this is what reads it.
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

    // Read out of the shipped source, so the count is asserted by something that runs. The role is
    // the boundary: a call site that never reaches for it by name has no implementation to express
    // the request through. A directory that cannot be found throws, because an assertion over no
    // files would report the guarantee as held whatever the product does.
    [Fact]
    public void ExactlyOneProductionCallSiteReachesForTheGrabbingRole()
    {
        var sites = ReachingForTheGrabbingRole();

        Assert.Single(sites);
        Assert.StartsWith(
            "WhisparrSync.MonitoringResolution.cs:",
            sites[0],
            StringComparison.Ordinal
        );
    }

    // The whole monitor gesture, the whole unmonitor gesture, a whole scope change and a whole
    // reflect-owned run, each driven to completion on one host so the ordered verb log holds all
    // four. Every index rather than the last: a grab issued before an act would be just as
    // acquiring. Paired with an assertion that the log holds an acting verb, so a set of gestures
    // that reached the instance not at all cannot satisfy this.
    [Fact]
    public async Task NoOtherMountedGestureReachesAGrabbingVerbAtAnyPosition()
    {
        await using var host = await HoldingHost(HeldNotMonitored, HeldAndMonitored);
        host.Client.Answering(nameof(RecordingWhisparrCore.ListImportableFilesAsync), MonitorHost.Json(200, "{}"));
        host.Client.Answering(nameof(RecordingWhisparrCore.SetStudioScopeAsync), MonitorHost.Json(200, "{}"));
        host.Client.Answering(nameof(RecordingWhisparrCore.SetStudioMonitoredAsync), MonitorHost.Json(200, "{}"));
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

    // The instance's own command takes an id array and iterates the whole of it, so the selection is
    // one call rather than one per entity. The ids are asserted, not a count: each one has to come
    // from the instance's own record of that entity rather than from the selection the browser sent.
    [Fact]
    public async Task ASelectionIsSearchedInOneCommandNamingEveryEntityTheInstanceHolds()
    {
        await using var host = await AnsweringStudioReads(
            Held(HeldAndMonitored), Held(SecondHeldAndMonitored));
        host.Client.Answering(nameof(RecordingWhisparrCore.SearchMonitoredAsync), MonitorHost.Json(200, "{}"));
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

    // One unheld id fails the whole command and the answer names only that one, so a selection sent
    // as supplied would cost every other entity its search.
    [Fact]
    public async Task AnEntityTheInstanceDoesNotHoldIsLeftOutAndTheRestAreStillSearched()
    {
        await using var host = await AnsweringStudioReads(NotHeld, Held(HeldAndMonitored));
        host.Client.Answering(nameof(RecordingWhisparrCore.SearchMonitoredAsync), MonitorHost.Json(200, "{}"));
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
                    nameof(MonitorRefusalKind.InstanceHoldsNoSuchEntity),
                    Disposed: true),
                new ReportedUnit(Unit(held), JobUnitOutcome.Succeeded, null, Disposed: true),
            ],
            progress.Units);
        Assert.Equal((1d, "1 applied, 1 refused."), Assert.Single(progress.Reports));
    }

    // There is no command to compose. The instance accepts one whose id array is empty and runs it
    // over nothing, which a reader cannot tell from a search that found nothing.
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

    private static Task<HttpResponseMessage> BulkSearchAsync(MonitorHost host, params int[] coveIds)
    {
        var ids = string.Join(",", coveIds.Select(
            coveId => coveId.ToString(CultureInfo.InvariantCulture)));

        return host.PostBulkAsync(
            $$"""{"entityType":"studios","verb":"searchAllMonitored","entityIds":[{{ids}}]}""");
    }

    private static string Unit(int coveId) => coveId.ToString(CultureInfo.InvariantCulture);

    // Several answers describe one instance acting between two reads, which is what the monitor
    // path's own read-back classifies from. The last answer repeats.
    private static Task<MonitorHost> HoldingHost(params string[] studio)
        => AnsweringStudioReads([.. studio.Select(Held)]);

    // Answers rather than bodies, so a case can describe an instance that holds one selected entity
    // and not another.
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

    private static WhisparrResponse Held(string studio) => MonitorHost.Json(200, studio);

    private static WhisparrResponse NotHeld => MonitorHost.Json(404, "");

    private static Task<int> SeededStudio(MonitorHost host)
        => host.SeedStudioAsync(MonitorHost.StoredEndpoint, MonitorHost.StudioRemoteIdValue);

    private static Task<HttpResponseMessage> SearchAsync(MonitorHost host, string kind, int coveId)
        => host.Http.PostAsync(host.RouteFor(kind, coveId, SearchRoute), content: null, TestCt);

    private static async Task<EntityMonitoringView> SearchViewAsync(
        MonitorHost host, string kind, int coveId)
    {
        var answered = await SearchAsync(host, kind, coveId);
        answered.EnsureSuccessStatusCode();
        return (await answered.Content.ReadFromJsonAsync<EntityMonitoringView>(TestCt))!;
    }

    // A role is reached by testing an instance for it or casting to it. A base list naming the
    // interface is a declaration, not a reach, and matches none of these.
    private static IReadOnlyList<string> ReachingForTheGrabbingRole()
    {
        string[] reaching =
        [
            " as " + nameof(IWhisparrSearchGrabbing),
            " is " + nameof(IWhisparrSearchGrabbing),
            "(" + nameof(IWhisparrSearchGrabbing) + ")",
        ];

        return
        [
            .. Directory
                .EnumerateFiles(ShippedSourceRoot(), "*.cs", SearchOption.AllDirectories)
                .OrderBy(file => file, StringComparer.Ordinal)
                .SelectMany(file => File.ReadLines(file)
                    .Select((text, index) => (File: file, Number: index + 1, Text: text))
                    .Where(line => reaching.Any(
                        token => line.Text.Contains(token, StringComparison.Ordinal)))
                    .Select(line => $"{Path.GetFileName(line.File)}:{line.Number}"))
        ];
    }

    // Throws when the source root is not found: an enumeration over no files would report every
    // source-level guarantee as held.
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
