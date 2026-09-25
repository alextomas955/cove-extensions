using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Cove.Core.Auth;
using Cove.Core.Interfaces;
using Cove.Extensions.Shared;
using WhisparrSync.Contracts;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Monitoring;

// The host matches an action's entity type by literal list membership, so a wrong spelling makes
// the button not appear with no error anywhere. The literals here are hand-written: a list read out
// of the registration would agree with it whatever it says.
public sealed class BulkEndpointTests
{
    private const string Studios = "studios";
    private const string Performers = "performers";

    // The bar passes the singular spelling for a video selection.
    private const string Videos = "video";

    // The bound Renamer uses, which this route copies.
    private const int Cap = 1000;

    // One listing row: a file under the outer declared root, matched to a site the instance holds
    // under the inner one.
    private const string InboxRowMatchedToAnotherRoot = """
        [{"path":"/config/library/inbox/scene.mp4","folderName":"inbox","size":41,
          "movie":{"id":7,"title":"A scene","path":"/config/library/rootB/Tushy"},
          "movieFileId":0,"quality":{"quality":{"id":6}},"languages":[{"id":1}],"rejections":[]}]
        """;

    [Fact]
    public void TheBulkActionsAreRegisteredUnderTheRawPluralTheSelectionBarPasses()
    {
        var bulk = BulkActions();

        Assert.Equal(
            [[Performers], [Studios], [Videos]],
            bulk.Select(action => action.EntityTypes).OrderBy(types => types[0], StringComparer.Ordinal));
    }

    // The bar normalizes only the two media plurals, so a studio or performer action declaring a
    // singular type is filtered out of every selection.
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
            Assert.NotNull(action.HandlerName);
            Assert.Equal(Permissions.ExtensionsConfigure, action.RequiredPermission);
            Assert.True(action.SuppressSuccessAlert);
        }

        // Pinned per selection, because the host resolves a handler name to the bundle's own map by
        // exact string and dispatches nothing, with no error, when they differ.
        Assert.Equal(
            ["whisparrMonitorSelected", "whisparrMonitorSelected", "whisparrSceneBatch"],
            BulkActions()
                .OrderBy(action => action.EntityTypes[0], StringComparer.Ordinal)
                .Select(action => action.HandlerName));
    }

    // The host has no renderer for a context-menu action, so one registered would show nothing.
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

    // The control the refusal above needs: without it a refused enqueue could equally mean the
    // route is broken for every size.
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

    // An empty run in the host's Job Drawer reads as work that happened.
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

    // Answering forbidden would confirm the id names a real job. The job asked about here is a real
    // one this service holds, so the not-found is about the prefix, not about an unknown id.
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

    // Whisparr v2 addresses no performer, so it holds no role to act through. The button is a
    // manifest fact registered whatever the generation is, so availability is answered at run time.
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

    [Fact]
    public async Task ASelectionCarryingOneEntityTwiceActsOnItOnce()
    {
        await using var host = await MonitorHost.CreateAsync();
        host.Client.Answering(nameof(RecordingWhisparrCore.ReadHardlinkSettingAsync), MonitorHost.Json(200, "{}"));
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

    // The instance declares a root inside another one and holds the site under the inner one, while
    // the file sits under the outer. An import across the two copies the whole file.
    [Fact]
    public async Task ASelectionWhoseFileAndSiteSitUnderDifferentRootsLinksNothingAndSaysWhy()
    {
        await using var host = await MonitorHost.CreateAsync();
        host.Client
            .Answering(
                nameof(IWhisparrReflectOwnedActing.ReadHardlinkSettingAsync),
                MonitorHost.Json(200, """{"copyUsingHardlinks":true}"""))
            .Answering(
                nameof(IWhisparrClient.ReadRootFoldersAsync),
                MonitorHost.Json(
                    200,
                    """[{"id":1,"path":"/config/library"},{"id":2,"path":"/config/library/rootB"}]"""))
            .Answering(
                nameof(IWhisparrReflectOwnedActing.ListImportableFilesAsync),
                MonitorHost.Json(200, InboxRowMatchedToAnotherRoot));
        var studio = await host.SeedStudioAsync(
            MonitorHost.StoredEndpoint, MonitorHost.StudioRemoteIdValue);
        await host.SeedStudioFileAsync(studio, "/config/library/inbox", 41);
        var progress = new RecordingJobProgress();

        await host.PostBulkAsync(BodyOf(Studios, "monitor", [studio]));
        await host.RunEnqueuedBatchAsync(progress);

        Assert.Equal(
            (1d, "1 applied, 0 refused. Some files were not linked: Whisparr holds their site "
                + "under a different root from the files, and nothing was copied."),
            Assert.Single(progress.Reports));
        Assert.DoesNotContain(
            nameof(IWhisparrReflectOwnedActing.AttachOwnedFilesAsync), host.Client.Verbs);
    }

    // The instance declares one root and holds nothing under it, which is what a container with no
    // counterpart for a Cove path answers. Adding anyway would create an entry at a root holding
    // none of the entity's files, which can never link anything and which a later run cannot tell
    // from an entry a reader made.
    [Fact]
    public async Task ASelectionWhoseRootAgreedOnNothingAddsNothingAndLinksNothing()
    {
        const string coveRoot = "G:/Downloads/P";
        await using var host = await MonitorHost.CreateAsync(
            libraryConfig: new CoveConfiguration { CovePaths = [new CovePath { Path = coveRoot }] });
        host.Client
            .Answering(
                nameof(IWhisparrReflectOwnedActing.ReadHardlinkSettingAsync),
                MonitorHost.Json(200, """{"copyUsingHardlinks":true}"""))
            .Answering(
                nameof(IWhisparrInstanceFilesystemReading.ReadInstanceFolderAsync),
                MonitorHost.Json(200, """{"parent":"/config/library/","directories":[],"files":[]}"""));
        var studio = await host.SeedStudioAsync(
            MonitorHost.StoredEndpoint, MonitorHost.StudioRemoteIdValue);
        await host.SeedStudioFileAsync(studio, coveRoot + "/Blue Harbor", 41);
        var progress = new RecordingJobProgress();

        await host.PostBulkAsync(BodyOf(Studios, "monitor", [studio]));
        await host.RunEnqueuedBatchAsync(progress);

        Assert.Equal((1d, "0 applied, 1 refused."), Assert.Single(progress.Reports));
        Assert.DoesNotContain(
            host.Client.Verbs,
            verb => verb is nameof(IWhisparrStudioActing.AddMonitoredStudioAsync)
                or nameof(IWhisparrReflectOwnedActing.ListImportableFilesAsync));
    }

    [Fact]
    public async Task TheUnmonitorVerbReachesTheUnmonitorPathAndNotTheAddOne()
    {
        await using var host = await MonitorHost.CreateAsync();
        host.Client.Answering(nameof(RecordingWhisparrCore.SetStudioMonitoredAsync), MonitorHost.Json(200, "{}"));
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
