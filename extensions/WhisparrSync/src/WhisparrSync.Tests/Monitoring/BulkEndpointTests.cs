using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Cove.Core.Auth;
using Cove.Extensions.Shared;
using WhisparrSync.Contracts;
using WhisparrSync.Monitoring;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.Monitoring;

/// <summary>
/// What the selection bar has to send for the buttons to appear at all, what the bulk route refuses
/// before anything is enqueued, and what this extension's own status route will and will not confirm.
/// </summary>
/// <remarks>
/// The registration half is a pin rather than an inspection: the entity type the host matches on is a
/// literal list membership check, so a singular spelling makes the button simply not appear with no
/// error anywhere. The literals here are hand-written; a list read out of the registration would
/// agree with it whatever it says.
/// </remarks>
public sealed class BulkEndpointTests
{
    private const string Studios = "studios";
    private const string Performers = "performers";

    /// <summary>Renamer's own bound, and the one this route copies.</summary>
    private const int Cap = 1000;

    [Fact]
    public void TheBulkActionsAreRegisteredUnderTheRawPluralTheSelectionBarPasses()
    {
        var bulk = BulkActions();

        Assert.Equal(
            [[Performers], [Studios]],
            bulk.Select(action => action.EntityTypes).OrderBy(types => types[0], StringComparer.Ordinal));
    }

    /// <summary>
    /// The singular spellings are the trap. The bar normalizes only the two media plurals, so a
    /// studio or performer action declaring one is filtered out of every selection.
    /// </summary>
    [Fact]
    public void NoBulkActionDeclaresASingularEntityType()
    {
        var declared = BulkActions().SelectMany(action => action.EntityTypes).ToList();

        Assert.DoesNotContain("studio", declared);
        Assert.DoesNotContain("performer", declared);
    }

    [Fact]
    public void EachBulkActionDispatchesAHandlerRatherThanPostingDirectly()
    {
        foreach (var action in BulkActions())
        {
            Assert.Null(action.ApiEndpoint);
            Assert.Equal("whisparrMonitorSelected", action.HandlerName);
            Assert.Equal(Permissions.ExtensionsConfigure, action.RequiredPermission);
            Assert.True(action.SuppressSuccessAlert);
        }
    }

    /// <summary>That action type has no renderer at all, so it would contribute nothing.</summary>
    [Fact]
    public void NoActionIsRegisteredAsAContextMenuOne()
        => Assert.DoesNotContain(
            WhisparrSyncFixture.Create().GetUIManifest().Actions,
            action => action.ActionType == "context-menu");

    [Fact]
    public async Task AnIdArrayOneOverTheCapIsRefusedNamingTheCapAndNothingIsEnqueued()
    {
        await using var host = await MonitorHost.CreateAsync();

        var answered = await host.PostBulkAsync(BodyFor(Studios, "monitor", Cap + 1));

        Assert.Equal(HttpStatusCode.BadRequest, answered.StatusCode);
        var refusal = await answered.Content.ReadFromJsonAsync<ErrorCode>(TestCt);
        Assert.Equal("TOO_MANY_IDS", refusal!.Code);
        Assert.Equal(Cap, refusal.Max);
        Assert.Empty(host.Jobs.Enqueued);
        Assert.Empty(host.Client.Verbs);
    }

    /// <summary>
    /// The control the refusal above needs: without it a refused enqueue could equally mean the
    /// route is broken for every size.
    /// </summary>
    [Fact]
    public async Task AnIdArrayAtTheCapIsEnqueued()
    {
        await using var host = await MonitorHost.CreateAsync();

        var answered = await host.PostBulkAsync(BodyFor(Studios, "monitor", Cap));

        Assert.Equal(HttpStatusCode.Accepted, answered.StatusCode);
        var accepted = await answered.Content.ReadFromJsonAsync<JobEnqueued>(TestCt);
        Assert.False(string.IsNullOrWhiteSpace(accepted!.JobId));
        Assert.Single(host.Jobs.Enqueued);
    }

    /// <summary>An empty run in the Job Drawer reads as work that happened.</summary>
    [Fact]
    public async Task AnEmptySelectionIsRefusedAndNothingIsEnqueued()
    {
        await using var host = await MonitorHost.CreateAsync();

        var answered = await host.PostBulkAsync(BodyFor(Studios, "monitor", 0));

        Assert.Equal(HttpStatusCode.BadRequest, answered.StatusCode);
        var refusal = await answered.Content.ReadFromJsonAsync<ErrorCode>(TestCt);
        Assert.Equal("NOTHING_SELECTED", refusal!.Code);
        Assert.Empty(host.Jobs.Enqueued);
    }

    [Fact]
    public async Task ASelectionTypeThisProductDoesNotAddressIsRefusedAndNothingIsEnqueued()
    {
        await using var host = await MonitorHost.CreateAsync();

        var answered = await host.PostBulkAsync(BodyFor("tags", "monitor", 3));

        Assert.Equal(HttpStatusCode.BadRequest, answered.StatusCode);
        var refusal = await answered.Content.ReadFromJsonAsync<ErrorCode>(TestCt);
        Assert.Equal("UNSUPPORTED_ENTITY_TYPE", refusal!.Code);
        Assert.Empty(host.Jobs.Enqueued);
    }

    [Fact]
    public async Task TheBulkRouteRefusesACallerWithoutTheConfigureTierAndEnqueuesNothing()
    {
        await using var host = await MonitorHost.CreateAsync(
            FakePrincipalAccessor.WithPermissions(Permissions.VideosRead));

        var answered = await host.PostBulkAsync(BodyFor(Studios, "monitor", 2));

        Assert.Equal(HttpStatusCode.Forbidden, answered.StatusCode);
        Assert.Empty(host.Jobs.Enqueued);
        Assert.Empty(host.Client.Verbs);
    }

    /// <summary>
    /// A job outside this extension's own type prefix is NOT FOUND rather than forbidden.
    /// </summary>
    /// <remarks>
    /// Answering forbidden would confirm the id names a real job, which is the fact the host's own
    /// gate withholds. The job asked about here is a REAL one this service holds, so a not-found is
    /// about the prefix rather than about the id being unknown.
    /// </remarks>
    [Fact]
    public async Task AJobOutsideThisExtensionsOwnPrefixIsAnsweredNotFoundAndNeverForbidden()
    {
        await using var host = await MonitorHost.CreateAsync();
        host.Jobs.Holding("someone-elses-job", "ext:com.example.other:their-batch");

        var answered = await host.ReadJobStatusAsync("someone-elses-job");

        Assert.Equal(HttpStatusCode.NotFound, answered.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, answered.StatusCode);
    }

    [Fact]
    public async Task AJobIdNamingNothingIsAnsweredTheSameWayAsAForeignOne()
    {
        await using var host = await MonitorHost.CreateAsync();

        var answered = await host.ReadJobStatusAsync("no-such-job");

        Assert.Equal(HttpStatusCode.NotFound, answered.StatusCode);
    }

    [Fact]
    public async Task ThisExtensionsOwnJobIsReported()
    {
        await using var host = await MonitorHost.CreateAsync();
        var enqueued = await host.PostBulkAsync(BodyFor(Studios, "monitor", 2));
        var accepted = await enqueued.Content.ReadFromJsonAsync<JobEnqueued>(TestCt);

        var answered = await host.ReadJobStatusAsync(accepted!.JobId);

        Assert.Equal(HttpStatusCode.OK, answered.StatusCode);
        var status = await answered.Content.ReadFromJsonAsync<BulkJobStatus>(TestCt);
        Assert.Equal(accepted.JobId, status!.Id);
        Assert.Equal(BulkJobState.Pending, status.Status);
    }

    [Fact]
    public async Task TheStatusRouteRefusesACallerWithoutTheConfigureTier()
    {
        await using var host = await MonitorHost.CreateAsync(
            FakePrincipalAccessor.WithPermissions(Permissions.VideosRead));
        host.Jobs.Holding("a-job", "ext:" + host.ExtensionId + ":monitoring-bulk");

        var answered = await host.ReadJobStatusAsync("a-job");

        Assert.Equal(HttpStatusCode.Forbidden, answered.StatusCode);
    }

    [Fact]
    public async Task TheBatchIsEnqueuedExclusiveUnderThisExtensionsOwnJobTypePrefix()
    {
        await using var host = await MonitorHost.CreateAsync();

        await host.PostBulkAsync(BodyFor(Studios, "monitor", 2));

        var enqueued = Assert.Single(host.Jobs.Enqueued);
        Assert.True(enqueued.Exclusive);
        Assert.Equal("ext:" + host.ExtensionId + ":monitoring-bulk", enqueued.Type);
    }

    /// <summary>
    /// The verb the connected generation cannot honour is refused PER ENTITY, and the batch still
    /// reaches the entities after it.
    /// </summary>
    /// <remarks>
    /// The older generation addresses no performer at all, so it holds no role to act through. The
    /// button is a manifest fact and is registered whatever the generation is; the availability is a
    /// runtime one, and this is where it is answered.
    /// </remarks>
    [Fact]
    public async Task AVerbTheConnectedGenerationCannotHonourIsRefusedPerEntityRatherThanFailingTheBatch()
    {
        await using var host = await MonitorHost.CreateAsync(generation: WhisparrGeneration.V2);
        var first = await host.SeedPerformerAsync(null, null);
        var second = await host.SeedPerformerAsync(null, null);
        var progress = new RecordingJobProgress();

        await host.PostBulkAsync(BodyOf(Performers, "monitor", [first, second]));
        await host.RunEnqueuedBatchAsync(progress);

        Assert.Equal(
            [Unit(first), Unit(second)], progress.Units.Select(unit => unit.UnitId));
        Assert.Equal(
            [nameof(MonitorRefusalKind.CapabilityAbsentOnThisGeneration)],
            progress.Units.Select(unit => unit.Message).Distinct());
        Assert.Empty(host.Client.Acting);
        Assert.Equal((1d, "0 applied, 2 refused."), Assert.Single(progress.Reports));
    }

    /// <summary>The batch acts once per distinct id, whatever the selection carried.</summary>
    [Fact]
    public async Task ASelectionCarryingOneEntityTwiceActsOnItOnce()
    {
        await using var host = await MonitorHost.CreateAsync();
        var studio = await host.SeedStudioAsync(MonitorHost.StoredEndpoint, MonitorHost.StudioRemoteIdValue);
        var progress = new RecordingJobProgress();

        await host.PostBulkAsync(BodyOf(Studios, "monitor", [studio, studio]));
        await host.RunEnqueuedBatchAsync(progress);

        Assert.Equal([Unit(studio)], progress.Units.Select(unit => unit.UnitId));
        Assert.Single(
            host.Client.Acting,
            call => call.Verb == nameof(IWhisparrStudioActing.AddMonitoredStudioAsync));
        Assert.Equal(
            (1d, "1 applied, 0 refused. No files were linked: Whisparr's hard-link setting could not be read."),
            Assert.Single(progress.Reports));
    }

    /// <summary>
    /// Each verb reaches the same statement of itself the single-entity route reaches, so a selection
    /// cannot behave differently from a click.
    /// </summary>
    [Fact]
    public async Task TheUnmonitorVerbReachesTheUnmonitorPathAndNotTheAddOne()
    {
        await using var host = await MonitorHost.CreateAsync();
        host.Client.Answering(
            nameof(IWhisparrStudioActing.ReadStudioAsync),
            MonitorHost.Json(200, MonitorHost.AddedStudio));
        var studio = await host.SeedStudioAsync(MonitorHost.StoredEndpoint, MonitorHost.StudioRemoteIdValue);

        await host.PostBulkAsync(BodyOf(Studios, "unmonitor", [studio]));
        await host.RunEnqueuedBatchAsync(new RecordingJobProgress());

        Assert.Contains(
            host.Client.Acting,
            call => call.Verb == nameof(IWhisparrStudioActing.SetStudioMonitoredAsync)
                && call.Monitored == false);
        Assert.DoesNotContain(
            host.Client.Acting,
            call => call.Verb == nameof(IWhisparrStudioActing.AddMonitoredStudioAsync));
    }

    private static string Unit(int coveId) => coveId.ToString(CultureInfo.InvariantCulture);

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    private static IReadOnlyList<Cove.Plugins.ExtensionAction> BulkActions()
        => [.. WhisparrSyncFixture.Create().GetUIManifest().Actions
            .Where(action => action.ActionType == "bulk")];

    private static string BodyFor(string entityType, string verb, int idCount)
        => BodyOf(entityType, verb, [.. Enumerable.Range(1, idCount)]);

    private static string BodyOf(string entityType, string verb, IReadOnlyList<int> ids)
        => $$"""
        {"EntityType":"{{entityType}}","Verb":"{{verb}}","Scope":"futureScenes","EntityIds":[{{string.Join(',', ids)}}]}
        """;
}
