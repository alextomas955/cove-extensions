using System.Globalization;
using Cove.Core.Interfaces;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Jobs;

public sealed class BulkReflectOwnedTests
{
    private const string LinksIntoPlace = """{"copyUsingHardlinks":true}""";

    private const string CopiesInstead = """{"copyUsingHardlinks":false}""";

    private const string Attachable = """
        [{"path":"/library/one/scene.mp4","folderName":"one",
          "quality":{"quality":{"id":7}},"languages":[{"id":1}],"movie":{"id":31}}]
        """;

    [Fact]
    public async Task AThreeStudioSelectionLinksEachStudiosOwnFolders()
    {
        await using var host = await LinkingHost();
        host.Client.Answering(nameof(RecordingWhisparrClient.AttachOwnedFilesAsync), MonitorHost.Json(200, "{}"));
        var seeded = await SeedAsync(host, 3);
        var progress = new RecordingJobProgress();

        await host.PostBulkAsync(MonitorBody(seeded.Ids));
        await host.RunEnqueuedBatchAsync(progress);

        Assert.Equal(seeded.Folders, FoldersRead(host));
    }

    // Enqueuing per entity instead would make a thousand-entity selection a thousand runs.
    [Fact]
    public async Task AThreeStudioSelectionProducesExactlyOneEnqueue()
    {
        await using var host = await LinkingHost();
        host.Client.Answering(nameof(RecordingWhisparrClient.AttachOwnedFilesAsync), MonitorHost.Json(200, "{}"));
        var seeded = await SeedAsync(host, 3);

        await host.PostBulkAsync(MonitorBody(seeded.Ids));
        await host.RunEnqueuedBatchAsync(new RecordingJobProgress());

        Assert.Single(host.Jobs.Enqueued);
    }

    // The hard-link setting belongs to the Whisparr instance, not to an entity.
    [Fact]
    public async Task TheHardLinkSettingIsReadOnceForTheWholeSelection()
    {
        await using var host = await LinkingHost();
        host.Client.Answering(nameof(RecordingWhisparrClient.AttachOwnedFilesAsync), MonitorHost.Json(200, "{}"));
        var seeded = await SeedAsync(host, 3);

        await host.PostBulkAsync(MonitorBody(seeded.Ids));
        await host.RunEnqueuedBatchAsync(new RecordingJobProgress());

        Assert.Single(
            host.Client.Acting,
            call => call.Verb == nameof(IWhisparrReflectOwnedActing.ReadHardlinkSettingAsync));
    }

    // A skipped link step is a condition of a step the caller did not ask for, so the monitor the
    // caller did ask for still reports as applied.
    [Fact]
    public async Task WithTheSettingOffNothingIsLinkedAndEveryUnitStillReportsItsMonitor()
    {
        await using var host = await LinkingHost(CopiesInstead);
        var seeded = await SeedAsync(host, 3);
        var progress = new RecordingJobProgress();

        await host.PostBulkAsync(MonitorBody(seeded.Ids));
        await host.RunEnqueuedBatchAsync(progress);

        Assert.Empty(FoldersRead(host));
        Assert.DoesNotContain(
            host.Client.Verbs, verb => verb == nameof(IWhisparrReflectOwnedActing.AttachOwnedFilesAsync));
        Assert.Equal(
            [JobUnitOutcome.Succeeded, JobUnitOutcome.Succeeded, JobUnitOutcome.Succeeded],
            progress.Units.Select(unit => unit.Outcome));
    }

    [Fact]
    public async Task AnEntityWhoseMonitorWasRefusedHasNoLinkStepRunForIt()
    {
        await using var host = await LinkingHost();
        host.Client.Answering(nameof(RecordingWhisparrClient.AttachOwnedFilesAsync), MonitorHost.Json(200, "{}"));
        var seeded = await SeedAsync(host, 1);
        var unidentified = await host.SeedStudioAsync(null, null);
        await host.SeedStudioFileAsync(unidentified, "/library/unidentified");

        await host.PostBulkAsync(MonitorBody([.. seeded.Ids, unidentified]));
        await host.RunEnqueuedBatchAsync(new RecordingJobProgress());

        Assert.Equal(seeded.Folders, FoldersRead(host));
    }

    [Fact]
    public async Task AnUnmonitorSelectionRunsNoLinkStepOfAnyKind()
    {
        await using var host = await LinkingHost();
        host.Client.Answering(nameof(RecordingWhisparrClient.SetStudioMonitoredAsync), MonitorHost.Json(200, "{}"));
        var seeded = await SeedAsync(host, 2);

        await host.PostBulkAsync(BodyOf("unmonitor", seeded.Ids));
        await host.RunEnqueuedBatchAsync(new RecordingJobProgress());

        Assert.DoesNotContain(
            host.Client.Verbs,
            verb => verb
                is nameof(IWhisparrReflectOwnedActing.ReadHardlinkSettingAsync)
                or nameof(IWhisparrReflectOwnedActing.ListImportableFilesAsync)
                or nameof(IWhisparrReflectOwnedActing.AttachOwnedFilesAsync));
    }

    // Files already linked are on the instance and there is nothing to undo.
    [Fact]
    public async Task AStoppedSelectionKeepsWhatItLinkedAndReachesNoLaterEntity()
    {
        await using var host = await LinkingHost();
        host.Client.Answering(nameof(RecordingWhisparrClient.AttachOwnedFilesAsync), MonitorHost.Json(200, "{}"));
        var seeded = await SeedAsync(host, 3);
        var kept = new RecordingJobProgress();
        using var stopping = new CancellationTokenSource();

        await host.PostBulkAsync(MonitorBody(seeded.Ids));

        // The run rethrows after writing its summary, so the host classifies it as cancelled rather
        // than completed while the reader is still told what it managed to do.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => host.Jobs.RunLastAsync(
                new StoppingWhenUnitStarts(kept, Unit(seeded.Ids[1]), stopping), stopping.Token));

        var read = FoldersRead(host);
        Assert.Contains(seeded.Folders[0], read);
        Assert.DoesNotContain(seeded.Folders[2], read);
        Assert.Contains(", then stopped", Assert.Single(kept.Reports).SubTask);
    }

    [Fact]
    public async Task TheRunsOwnSummaryReportsTheLinkWorkApartFromTheMonitorOutcomes()
    {
        await using var host = await LinkingHost();
        host.Client.Answering(nameof(RecordingWhisparrClient.AttachOwnedFilesAsync), MonitorHost.Json(200, "{}"));
        var seeded = await SeedAsync(host, 3);
        var progress = new RecordingJobProgress();

        await host.PostBulkAsync(MonitorBody(seeded.Ids));
        await host.RunEnqueuedBatchAsync(progress);

        Assert.Equal((1d, "3 applied, 0 refused. 3 linked, 0 refused."), Assert.Single(progress.Reports));
    }

    [Fact]
    public async Task ASkippedLinkStepIsNamedInTheSummaryRatherThanCountedAsARefusal()
    {
        await using var host = await LinkingHost(CopiesInstead);
        var seeded = await SeedAsync(host, 2);
        var progress = new RecordingJobProgress();

        await host.PostBulkAsync(MonitorBody(seeded.Ids));
        await host.RunEnqueuedBatchAsync(progress);

        Assert.Equal(
            (1d, "2 applied, 0 refused. No files were linked: Whisparr's hard-link setting is off."),
            Assert.Single(progress.Reports));
    }

    [Fact]
    public async Task AnUnmonitorSelectionsSummaryNamesNoLinkWorkAtAll()
    {
        await using var host = await LinkingHost();
        host.Client.Answering(nameof(RecordingWhisparrClient.SetStudioMonitoredAsync), MonitorHost.Json(200, "{}"));
        var seeded = await SeedAsync(host, 2);
        var progress = new RecordingJobProgress();

        await host.PostBulkAsync(BodyOf("unmonitor", seeded.Ids));
        await host.RunEnqueuedBatchAsync(progress);

        Assert.Equal((1d, "2 applied, 0 refused."), Assert.Single(progress.Reports));
    }

    private static async Task<MonitorHost> LinkingHost(string setting = LinksIntoPlace)
    {
        var host = await MonitorHost.CreateAsync();
        host.Client
            .Answering(
                nameof(IWhisparrReflectOwnedActing.ReadHardlinkSettingAsync),
                MonitorHost.Json(200, setting))
            .Answering(
                nameof(IWhisparrReflectOwnedActing.ListImportableFilesAsync),
                MonitorHost.Json(200, Attachable));
        return host;
    }

    private static async Task<(int[] Ids, string[] Folders)> SeedAsync(MonitorHost host, int count)
    {
        var ids = new List<int>(count);
        var folders = new List<string>(count);

        for (var seeded = 0; seeded < count; seeded++)
        {
            var studio = await host.SeedStudioAsync(
                MonitorHost.StoredEndpoint, MonitorHost.StudioRemoteIdValue);
            var folder = string.Create(CultureInfo.InvariantCulture, $"/library/studio-{seeded}");
            await host.SeedStudioFileAsync(studio, folder);
            ids.Add(studio);
            folders.Add(folder);
        }

        return ([.. ids], [.. folders]);
    }

    private static string[] FoldersRead(MonitorHost host)
        => [.. host.Client.Acting
            .Where(call => call.Verb == nameof(IWhisparrReflectOwnedActing.ListImportableFilesAsync))
            .Select(call => call.Folder)
            .OfType<string>()];

    private static string Unit(int coveId) => coveId.ToString(CultureInfo.InvariantCulture);

    private static string MonitorBody(IReadOnlyList<int> ids) => BodyOf("monitor", ids);

    private static string BodyOf(string verb, IReadOnlyList<int> ids)
        => $$"""
        {"entityType":"studios","verb":"{{verb}}","scope":"futureScenes","entityIds":[{{string.Join(',', ids)}}]}
        """;

    private sealed class StoppingWhenUnitStarts(
        RecordingJobProgress kept, string stopAt, CancellationTokenSource stopping) : IJobProgress
    {
        public void Report(double progress, string? subTask = null) => kept.Report(progress, subTask);

        public IJobUnit StartUnit(string unitId, string? label = null)
        {
            if (string.Equals(unitId, stopAt, StringComparison.Ordinal))
            {
                stopping.Cancel();
            }

            return kept.StartUnit(unitId, label);
        }
    }
}
