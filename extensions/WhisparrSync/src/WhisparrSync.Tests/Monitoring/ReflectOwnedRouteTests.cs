using System.Net;
using System.Net.Http.Json;
using Cove.Core.Auth;
using WhisparrSync.Contracts;
using WhisparrSync.Jobs;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Monitoring;

// Driven through the shipped registration rather than by calling the handler. A handler called
// directly agrees with a route mounted at the wrong pattern, bound to a body the browser cannot
// send, or reachable by a caller the declaration excludes.
public sealed class ReflectOwnedRouteTests
{
    private const string LinksIntoPlace = """{"copyUsingHardlinks":true}""";

    private const string CopiesInstead = """{"copyUsingHardlinks":false}""";

    // A row missing either the quality or the languages member is excluded rather than filled in,
    // so a fixture without both would make every attach assertion below vacuous.
    private const string AttachableRow = """
        [{"path":"/library/vixen/2026/scene.mp4","folderName":"2026",
          "quality":{"quality":{"id":7}},"languages":[{"id":1}],"movie":{"id":31}}]
        """;

    private const string Earlier = "/library/vixen/2025";
    private const string Later = "/library/vixen/2026";

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AnInstanceThatLinksIntoPlaceAnswersAcceptedWithAJobIdAndNoSkip()
    {
        await using var host = await LinkingHost();
        var studioId = await SeededStudio(host);

        var answered = await host.ReflectOwnedAsync("studio", studioId);
        var enqueued = (await answered.Content.ReadFromJsonAsync<ReflectOwnedEnqueued>(TestCt))!;

        Assert.Equal(HttpStatusCode.Accepted, answered.StatusCode);
        Assert.Null(enqueued.Skipped);
        Assert.NotNull(enqueued.JobId);
        Assert.Equal(MonitorRefusalKind.None, enqueued.Refusal);
    }

    [Fact]
    public async Task TheHardLinkSettingBeingOffStatesTheReasonAndSendsNothingElse()
    {
        await using var host = await MonitorHost.CreateAsync();
        host.Client.Answering(
            nameof(IWhisparrReflectOwnedActing.ReadHardlinkSettingAsync),
            MonitorHost.Json(200, CopiesInstead));
        var studioId = await SeededStudio(host);
        await host.SeedStudioFileAsync(studioId, Later);

        var enqueued = await host.ReflectOwnedViewAsync("studio", studioId);

        Assert.Equal(ReflectOwnedSkipReason.HardLinksOff, enqueued.Skipped);
        Assert.Null(enqueued.JobId);
        Assert.DoesNotContain(
            nameof(IWhisparrReflectOwnedActing.ListImportableFilesAsync), host.Client.Verbs);
        Assert.DoesNotContain(
            nameof(IWhisparrReflectOwnedActing.AttachOwnedFilesAsync), host.Client.Verbs);
        Assert.Empty(host.Jobs.Enqueued);
    }

    // Stricter than the default both builds ship with. Acting on a setting nobody read copies every
    // matched file in full with no error.
    [Fact]
    public async Task AnUnreadableSettingSkipsUnderItsOwnReason()
    {
        await using var host = await MonitorHost.CreateAsync();
        host.Client.Answering(
            nameof(IWhisparrReflectOwnedActing.ReadHardlinkSettingAsync),
            MonitorHost.Json(500, "not json at all"));
        var studioId = await SeededStudio(host);

        var enqueued = await host.ReflectOwnedViewAsync("studio", studioId);

        Assert.Equal(ReflectOwnedSkipReason.HardLinkSettingUnreadable, enqueued.Skipped);
        Assert.Null(enqueued.JobId);
        Assert.Empty(host.Jobs.Enqueued);
    }

    [Fact]
    public async Task AnEntityCarryingNoIdentityIsRefusedWithNothingSentOutbound()
    {
        await using var host = await LinkingHost();
        var studioId = await host.SeedStudioAsync(null, null);

        var enqueued = await host.ReflectOwnedViewAsync("studio", studioId);

        Assert.Equal(MonitorRefusalKind.NoIdentityInThisNamespace, enqueued.Refusal);
        Assert.Null(enqueued.JobId);
        Assert.Null(enqueued.Skipped);
        Assert.Empty(host.Client.Verbs);
        Assert.Empty(host.Jobs.Enqueued);
    }

    [Fact]
    public async Task AKindTheRouteCannotParseIsABadRequest()
    {
        await using var host = await LinkingHost();

        var answered = await host.ReflectOwnedAsync("gallery", 1);

        Assert.Equal(HttpStatusCode.BadRequest, answered.StatusCode);
        Assert.Empty(host.Client.Verbs);
    }

    // The route aims this extension's stored credential at a third party, so a caller who cannot
    // configure the extension is out of reach of it.
    [Fact]
    public async Task ACallerHoldingOnlyReadIsRefused()
    {
        await using var host = await MonitorHost.CreateAsync(
            FakePrincipalAccessor.WithPermissions(Permissions.VideosRead));

        var answered = await host.ReflectOwnedAsync("studio", 1);

        Assert.Equal(HttpStatusCode.Forbidden, answered.StatusCode);
        Assert.Empty(host.Client.Verbs);
    }

    [Fact]
    public async Task MonitoringAnEntityEnqueuesExactlyOneReflectOwnedRunAndReadsNoFolderInTheRequest()
    {
        await using var host = await LinkingHost();
        var studioId = await SeededStudio(host);
        await host.SeedStudioFileAsync(studioId, Later);

        Assert.Equal(MonitorRefusalKind.None, (await host.MonitorAsync(studioId)).Refusal);

        var enqueued = Assert.Single(host.Jobs.Enqueued);
        Assert.StartsWith("ext:" + host.ExtensionId + ":", enqueued.Type, StringComparison.Ordinal);
        Assert.EndsWith(ReflectOwnedJob.JobId, enqueued.Type, StringComparison.Ordinal);
        Assert.DoesNotContain(
            nameof(IWhisparrReflectOwnedActing.ListImportableFilesAsync), host.Client.Verbs);
    }

    [Fact]
    public async Task AMonitorTheInstanceRefusesEnqueuesNoRun()
    {
        await using var host = await LinkingHost();
        host.Client.Answering(
            nameof(IWhisparrStudioActing.AddMonitoredStudioAsync), MonitorHost.Json(400, ""));
        var studioId = await SeededStudio(host);

        Assert.NotEqual(MonitorRefusalKind.None, (await host.MonitorAsync(studioId)).Refusal);
        Assert.Empty(host.Jobs.Enqueued);
    }

    [Fact]
    public async Task ABulkMonitorEnqueuesItsOwnBatchAndNoRunPerEntity()
    {
        await using var host = await LinkingHost();
        var studioId = await SeededStudio(host);

        var answered = await host.PostBulkAsync(
            $$"""{"entityType":"studios","verb":"monitor","entityIds":[{{studioId}}]}""");
        answered.EnsureSuccessStatusCode();
        await host.RunEnqueuedBatchAsync(new RecordingJobProgress());

        Assert.Single(host.Jobs.Enqueued);
        Assert.DoesNotContain(
            host.Jobs.Enqueued,
            job => job.Type.EndsWith(ReflectOwnedJob.JobId, StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheEnqueuedRunReadsEachFolderOnceAndAttachesWhatItParsed()
    {
        await using var host = await LinkingHost();
        host.Client.Answering(nameof(RecordingWhisparrCore.AttachOwnedFilesAsync), MonitorHost.Json(200, "{}"));
        host.Client.Answering(
            nameof(IWhisparrReflectOwnedActing.ListImportableFilesAsync),
            MonitorHost.Json(200, AttachableRow));
        var studioId = await SeededStudio(host);
        await host.SeedStudioFileAsync(studioId, Later);
        await host.SeedStudioFileAsync(studioId, Later);
        await host.SeedStudioFileAsync(studioId, Earlier);

        await host.ReflectOwnedViewAsync("studio", studioId);
        var progress = new RecordingJobProgress();
        await host.Jobs.RunLastAsync(progress, TestCt);

        Assert.Equal(
            [Earlier, Later],
            host.Client.Acting
                .Where(call => call.Verb == nameof(IWhisparrReflectOwnedActing.ListImportableFilesAsync))
                .Select(call => call.Folder));
        Assert.Equal(
            2,
            host.Client.Verbs.Count(
                verb => verb == nameof(IWhisparrReflectOwnedActing.AttachOwnedFilesAsync)));
        Assert.Contains(progress.Reports, report => report.SubTask is not null);
    }

    [Fact]
    public async Task AnEntityWithNoFilesRunsToCompletionAndAttachesNothing()
    {
        await using var host = await LinkingHost();
        var studioId = await SeededStudio(host);

        var enqueued = await host.ReflectOwnedViewAsync("studio", studioId);
        Assert.NotNull(enqueued.JobId);
        Assert.Equal(MonitorRefusalKind.None, enqueued.Refusal);

        var progress = new RecordingJobProgress();
        await host.Jobs.RunLastAsync(progress, TestCt);

        Assert.DoesNotContain(
            nameof(IWhisparrReflectOwnedActing.ListImportableFilesAsync), host.Client.Verbs);
        Assert.DoesNotContain(
            nameof(IWhisparrReflectOwnedActing.AttachOwnedFilesAsync), host.Client.Verbs);
        Assert.Contains(progress.Reports, report => report.SubTask is not null);
    }

    // Both answers reach the same place through the same seam. Counting the refused folder as
    // nothing importable would report a clean pass over a folder the run never read.
    [Fact]
    public async Task AFolderWhoseListingWasRefusedIsCountedAsRefusedRatherThanAsEmpty()
    {
        await using var host = await LinkingHost();
        host.Client.Answering(
            nameof(IWhisparrReflectOwnedActing.ListImportableFilesAsync),
            MonitorHost.Json(200, string.Empty)
                with
            { Refusal = MonitorRefusalKind.AnswerTooLargeToRead });
        var studioId = await SeededStudio(host);
        await host.SeedStudioFileAsync(studioId, Later);

        await host.ReflectOwnedViewAsync("studio", studioId);
        var progress = new RecordingJobProgress();
        await host.Jobs.RunLastAsync(progress, TestCt);

        Assert.Contains(progress.Reports, report => report.SubTask == "0 linked, 1 refused.");
        Assert.DoesNotContain(
            nameof(IWhisparrReflectOwnedActing.AttachOwnedFilesAsync), host.Client.Verbs);
    }

    // The control for the case above. Without it, counting every unattached folder as refused would
    // pass that assertion and report every empty folder as a failure.
    [Fact]
    public async Task AFolderHoldingNothingImportableIsCountedAsNeitherAttachedNorRefused()
    {
        await using var host = await LinkingHost();
        host.Client.Answering(
            nameof(IWhisparrReflectOwnedActing.ListImportableFilesAsync),
            MonitorHost.Json(200, "[]"));
        var studioId = await SeededStudio(host);
        await host.SeedStudioFileAsync(studioId, Later);

        await host.ReflectOwnedViewAsync("studio", studioId);
        var progress = new RecordingJobProgress();
        await host.Jobs.RunLastAsync(progress, TestCt);

        Assert.Contains(progress.Reports, report => report.SubTask == "0 linked, 0 refused.");
    }

    private static async Task<MonitorHost> LinkingHost()
    {
        var host = await MonitorHost.CreateAsync();
        host.Client.Answering(
            nameof(IWhisparrReflectOwnedActing.ReadHardlinkSettingAsync),
            MonitorHost.Json(200, LinksIntoPlace));
        return host;
    }

    private static Task<int> SeededStudio(MonitorHost host)
        => host.SeedStudioAsync(MonitorHost.StoredEndpoint, MonitorHost.StudioRemoteIdValue);
}
