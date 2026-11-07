using System.Globalization;
using Cove.Core.Auth;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using WhisparrSync.Contracts;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Jobs;

// The batch body runs elevated, so the only thing that can keep a scoped account off an entity it
// cannot see is the caller copied at the request.
public sealed class BulkCallerReachTests
{
    private const string Studios = "studios";

    private const string OtherStudioRemoteId = "1b9a5c33-2d47-4f61-8c05-7ae9d2f41b88";

    private const string HeldMonitored =
        """{"id":1,"foreignId":"44e8ac11-9ed4-42e5-a9f4-bc2c138a5a6e","monitored":true}""";

    [Fact]
    public async Task AnEntityTheCallerCannotReadIsRefusedAndTheOtherIsStillActedOn()
    {
        await using var host = await MonitorHost.CreateAsync();
        host.Client.Answering(
            nameof(RecordingWhisparrCore.ReadHardlinkSettingAsync), MonitorHost.Json(200, "{}"));
        var reachable = await host.SeedStudioAsync(
            MonitorHost.StoredEndpoint, MonitorHost.StudioRemoteIdValue);
        var hidden = await host.SeedStudioAsync(MonitorHost.StoredEndpoint, OtherStudioRemoteId);
        host.Authz.Denying(EntityKinds.Studio, hidden);
        var progress = new RecordingJobProgress();

        await host.PostBulkAsync(BodyOf(Studios, "monitor", [reachable, hidden]));
        await host.RunEnqueuedBatchAsync(progress);

        Assert.Equal(
            nameof(MonitorRefusalKind.CallerMayNotReachThisEntity),
            Assert.Single(progress.Units, unit => unit.UnitId == Named(hidden)).Message);
        Assert.Equal(
            JobUnitOutcome.Succeeded,
            Assert.Single(progress.Units, unit => unit.UnitId == Named(reachable)).Outcome);
        Assert.DoesNotContain(
            host.Client.Acting, call => call.ForeignId == OtherStudioRemoteId);
        Assert.Contains(
            host.Client.Acting, call => call.ForeignId == MonitorHost.StudioRemoteIdValue);
    }

    [Fact]
    public async Task ASelectionWhereNoEntityIsReadableNeverReachesTheInstance()
    {
        await using var host = await MonitorHost.CreateAsync();
        var first = await host.SeedStudioAsync(
            MonitorHost.StoredEndpoint, MonitorHost.StudioRemoteIdValue);
        var second = await host.SeedStudioAsync(MonitorHost.StoredEndpoint, OtherStudioRemoteId);
        host.Authz.Denying(EntityKinds.Studio, first).Denying(EntityKinds.Studio, second);
        var progress = new RecordingJobProgress();

        await host.PostBulkAsync(BodyOf(Studios, "monitor", [first, second]));
        await host.RunEnqueuedBatchAsync(progress);

        Assert.Equal(
            [nameof(MonitorRefusalKind.CallerMayNotReachThisEntity),
             nameof(MonitorRefusalKind.CallerMayNotReachThisEntity)],
            progress.Units.Select(unit => unit.Message));
        Assert.Equal((1d, "0 applied, 2 refused."), Assert.Single(progress.Reports));
        Assert.Empty(host.Client.Verbs);
    }

    // The read permission is the one that decides whether Cove hides the entity from the account.
    [Fact]
    public async Task TheSelectedEntitiesAreAuthorizedUnderTheKindsOwnReadPermission()
    {
        await using var host = await MonitorHost.CreateAsync();
        host.Client.Answering(
            nameof(RecordingWhisparrCore.ReadHardlinkSettingAsync), MonitorHost.Json(200, "{}"));
        var studio = await host.SeedStudioAsync(
            MonitorHost.StoredEndpoint, MonitorHost.StudioRemoteIdValue);

        await host.PostBulkAsync(BodyOf(Studios, "monitor", [studio]));
        await host.RunEnqueuedBatchAsync(new RecordingJobProgress());

        Assert.Equal(
            [(Permissions.StudiosRead, EntityKinds.Studio, studio)], host.Authz.Asked);
    }

    [Fact]
    public async Task ACallerHoldingEveryPermissionIsNeverAskedAboutAndIsStillActedOn()
    {
        await using var host = await MonitorHost.CreateAsync(
            principal: FakePrincipalAccessor.WithPermissions(Permissions.All));
        host.Client.Answering(
            nameof(RecordingWhisparrCore.ReadHardlinkSettingAsync), MonitorHost.Json(200, "{}"));
        var studio = await host.SeedStudioAsync(
            MonitorHost.StoredEndpoint, MonitorHost.StudioRemoteIdValue);
        var progress = new RecordingJobProgress();

        await host.PostBulkAsync(BodyOf(Studios, "monitor", [studio]));
        await host.RunEnqueuedBatchAsync(progress);

        Assert.Empty(host.Authz.Asked);
        Assert.Equal(JobUnitOutcome.Succeeded, Assert.Single(progress.Units).Outcome);
    }

    // The search verb sends one command naming every resolved id, so a denied entity riding that
    // array would be acted on by a call nobody refused.
    [Fact]
    public async Task ASearchNamesOnlyTheEntitiesTheCallerCanRead()
    {
        await using var host = await MonitorHost.CreateAsync();
        host.Client
            .Answering(
                nameof(RecordingWhisparrCore.SearchMonitoredAsync), MonitorHost.Json(200, "{}"))
            .Answering(
                nameof(IWhisparrStudioActing.ReadStudioAsync), MonitorHost.Json(200, HeldMonitored));
        var reachable = await host.SeedStudioAsync(
            MonitorHost.StoredEndpoint, MonitorHost.StudioRemoteIdValue);
        var hidden = await host.SeedStudioAsync(MonitorHost.StoredEndpoint, OtherStudioRemoteId);
        host.Authz.Denying(EntityKinds.Studio, hidden);

        await host.PostBulkAsync(BodyOf(Studios, "searchAllMonitored", [reachable, hidden]));
        await host.RunEnqueuedBatchAsync(new RecordingJobProgress());

        var searched = Assert.Single(
            host.Client.Acting,
            call => call.Verb == nameof(IWhisparrSearchGrabbing.SearchMonitoredAsync));
        Assert.Equal([1], searched.EntityIds);
        Assert.DoesNotContain(host.Client.Acting, call => call.ForeignId == OtherStudioRemoteId);
    }

    private static string Named(int coveId) => coveId.ToString(CultureInfo.InvariantCulture);

    private static string BodyOf(string entityType, string verb, IReadOnlyList<int> ids)
        => string.Create(
            CultureInfo.InvariantCulture,
            $$"""
            {"entityType":"{{entityType}}","verb":"{{verb}}","scope":"futureScenes","entityIds":[{{string.Join(',', ids)}}]}
            """);
}
