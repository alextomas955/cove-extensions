using WhisparrSync.Contracts;
using WhisparrSync.Scene;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.Scene;

/// <summary>
/// One control with two labels, driven through both of its routes against the instance's own
/// exclusion list.
/// </summary>
/// <remarks>
/// Both halves read the list before they act, and each case states what that read answered. The
/// removing route takes the exclusion row's own identifier rather than the scene's, so the read is
/// what makes the request addressable at all.
/// <para>
/// What the instance answers to an exclusion it already holds was never measured against a real
/// build, and that is why the excluding half asks the list first instead of sending and reading the
/// answer: a duplicate that answered a conflict and a duplicate that answered a success would
/// otherwise put two different labels on one control.
/// </para>
/// </remarks>
public sealed class SceneExclusionRouteTests
{
    /// <summary>A scene as the provider issues its identifier.</summary>
    private const string SceneId = "3c0a6b21-9f7d-4c58-a3e2-71b0d4f5e8a9";

    /// <summary>The exclusion row's own identifier, which is what the removing route names.</summary>
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

    /// <summary>
    /// A list read that produced no whole answer is neither half's own answer.
    /// </summary>
    /// <remarks>
    /// Held apart from a list naming no exclusion, which is the assertion the pair above makes.
    /// Reporting an unread list as naming no exclusion would tell a reader the instance stated an
    /// absence it never stated.
    /// </remarks>
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

    /// <summary>
    /// The read reports the exclusion the instance's own list names, in both directions.
    /// </summary>
    /// <remarks>
    /// This is the fact the tab's control set turns on: the state vocabulary tests exclusion ahead
    /// of everything else, and one control carries both the excluding and the removing label. The
    /// scene's own row carries no exclusion member, so the list is the only thing that establishes
    /// it.
    /// </remarks>
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

    /// <summary>
    /// A list read that produced no whole answer refuses the read rather than reporting no
    /// exclusion.
    /// </summary>
    /// <remarks>
    /// Reporting the scene as not excluded would let an excluded scene read as monitored, because
    /// the vocabulary tests exclusion first and would never reach the flag it was given.
    /// </remarks>
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

    /// <summary>Neither half claims anything about a search.</summary>
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
