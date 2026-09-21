using WhisparrSync.Contracts;
using WhisparrSync.Scene;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.Scene;

// Both halves read the instance's exclusion list before they act. What the instance answers to an
// exclusion it already holds was never measured against a real build, so the excluding half asks
// the list first instead of sending and reading the answer.
public sealed class SceneExclusionRouteTests
{
    private const string SceneId = "3c0a6b21-9f7d-4c58-a3e2-71b0d4f5e8a9";

    // The exclusion row's own identifier, which is what the removing route names.
    private const int ExclusionOnTheInstance = 57;

    private const string Exclude = "exclude";

    private const string RemoveExclusion = "remove-exclusion";

    [Fact]
    public async Task ExcludingASceneTheListAlreadyNamesIsTakenAndSendsNothing()
    {
        await using var host = await MonitorHost.CreateAsync();
        var coveId = await SeedSceneAsync(host);
        host.Client.ExclusionIdByScene[SceneId] = ExclusionOnTheInstance;

        var result = await host.SceneActionAsync(coveId, Exclude);

        Assert.Equal(SceneRefusalKind.None, result.Refusal);
        Assert.Equal([SceneId], host.Client.ExclusionLookups);
        Assert.Empty(host.Client.Acting);
    }

    [Fact]
    public async Task ExcludingASceneTheListDoesNotNameSendsTheSceneIdentifier()
    {
        await using var host = await MonitorHost.CreateAsync();
        host.Client.Answering(nameof(RecordingWhisparrClient.AddSceneExclusionAsync), MonitorHost.Json(200, "{}"));
        var coveId = await SeedSceneAsync(host);

        var result = await host.SceneActionAsync(coveId, Exclude);

        Assert.Equal(SceneRefusalKind.None, result.Refusal);
        var sent = Assert.Single(host.Client.Acting);
        Assert.Equal(nameof(IWhisparrSceneExclusionActing.AddSceneExclusionAsync), sent.Verb);
        Assert.Equal(SceneId, sent.ForeignId);
    }

    [Fact]
    public async Task RemovingAnExclusionTheListNamesSendsTheExclusionsOwnIdentifier()
    {
        await using var host = await MonitorHost.CreateAsync();
        host.Client.Answering(nameof(RecordingWhisparrClient.RemoveSceneExclusionAsync), MonitorHost.Json(200, "{}"));
        var coveId = await SeedSceneAsync(host);
        host.Client.ExclusionIdByScene[SceneId] = ExclusionOnTheInstance;

        var result = await host.SceneActionAsync(coveId, RemoveExclusion);

        Assert.Equal(SceneRefusalKind.None, result.Refusal);
        var sent = Assert.Single(host.Client.Acting);
        Assert.Equal(nameof(IWhisparrSceneExclusionActing.RemoveSceneExclusionAsync), sent.Verb);
        Assert.Equal(ExclusionOnTheInstance, sent.EntityId);
        Assert.Null(sent.ForeignId);
    }

    [Fact]
    public async Task RemovingAnExclusionTheListDoesNotNameIsNoEntryAndSendsNothing()
    {
        await using var host = await MonitorHost.CreateAsync();
        var coveId = await SeedSceneAsync(host);

        var result = await host.SceneActionAsync(coveId, RemoveExclusion);

        Assert.Equal(SceneRefusalKind.WhisparrHasNoEntryForScene, result.Refusal);
        Assert.Equal([SceneId], host.Client.ExclusionLookups);
        Assert.Empty(host.Client.Acting);
    }

    // A list read that produced no whole answer is not a list naming no exclusion. Reporting it as
    // such would state an absence the instance never stated.
    [Theory]
    [InlineData(Exclude)]
    [InlineData(RemoveExclusion)]
    public async Task AListReadThatDidNotCompleteRefusesEitherHalfAndSendsNothing(string verb)
    {
        await using var host = await MonitorHost.CreateAsync();
        var coveId = await SeedSceneAsync(host);
        host.Client.ExclusionReadCompletes = false;

        var result = await host.SceneActionAsync(coveId, verb);

        Assert.Equal(SceneRefusalKind.DidNotReachWhisparr, result.Refusal);
        Assert.Empty(host.Client.Acting);
    }

    // The scene's own row carries no exclusion member, so the list is the only thing that
    // establishes it. Both directions are asserted, because a read answering a constant would
    // agree with one.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheReadReportsWhetherTheInstancesListNamesTheScene(bool onTheList)
    {
        await using var host = await MonitorHost.CreateAsync();
        host.Client.Answering(nameof(RecordingWhisparrClient.ReadSceneByRemoteIdAsync), MonitorHost.Json(200, "{}"));
        var coveId = await SeedSceneAsync(host);
        if (onTheList)
        {
            host.Client.ExclusionIdByScene[SceneId] = ExclusionOnTheInstance;
        }

        var view = await host.SceneDetailAsync(coveId);

        Assert.Equal(SceneRefusalKind.None, view.Refusal);
        Assert.Equal(onTheList, view.Excluded);
        Assert.Equal([SceneId], host.Client.ExclusionLookups);
    }

    // Reporting the scene as not excluded would let an excluded scene read as monitored, because
    // the state vocabulary tests exclusion first.
    [Fact]
    public async Task AListReadThatDidNotCompleteRefusesTheRead()
    {
        await using var host = await MonitorHost.CreateAsync();
        host.Client.Answering(nameof(RecordingWhisparrClient.ReadSceneByRemoteIdAsync), MonitorHost.Json(200, "{}"));
        var coveId = await SeedSceneAsync(host);
        host.Client.ExclusionReadCompletes = false;

        var view = await host.SceneDetailAsync(coveId);

        Assert.Equal(SceneRefusalKind.DidNotReachWhisparr, view.Refusal);
        Assert.Null(view.Present);
        Assert.Null(view.Monitored);
    }

    [Theory]
    [InlineData(Exclude)]
    [InlineData(RemoveExclusion)]
    public async Task NeitherHalfReportsASearch(string verb)
    {
        await using var host = await MonitorHost.CreateAsync();
        host.Client.Answering(nameof(RecordingWhisparrClient.RemoveSceneExclusionAsync), MonitorHost.Json(200, "{}"));
        var coveId = await SeedSceneAsync(host);
        host.Client.ExclusionIdByScene[SceneId] = ExclusionOnTheInstance;

        var result = await host.SceneActionAsync(coveId, verb);

        Assert.False(result.SearchIsWithWhisparr);
    }

    private static async Task<int> SeedSceneAsync(MonitorHost host)
    {
        var studioId = await host.SeedStudioAsync(
            MonitorHost.StoredEndpoint, MonitorHost.StudioRemoteIdValue);
        return await host.SeedStudioSceneAsync(studioId, MonitorHost.StoredEndpoint, SceneId);
    }
}
