using WhisparrSync.Contracts;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Scene;

/// <summary>
/// Which refusal a scene verb answers when more than one applies, one case per step.
/// </summary>
/// <remarks>
/// The order is fixed: no connection, then no identity, then an absent capability, then no entry,
/// then not monitoring. Each case below sets up the step under test together with every step
/// beneath it, so a handler asking the questions in another order answers a refusal further down
/// the list and reddens the case rather than passing it.
/// <para>
/// The several-identities step is the one that cannot be reached. The identity source groups a
/// video's rows and keeps only a video whose rows agree on one identifier, so a video carrying
/// conflicting links is simply absent from its answer and the handler cannot tell that apart from a
/// video with no link at all. The case states what it does answer.
/// </para>
/// </remarks>
public sealed class SceneRefusalPrecedenceTests
{
    private const string SceneId = "3c0a6b21-9f7d-4c58-a3e2-71b0d4f5e8a9";

    private const string OtherSceneId = "8b1f7d40-2a63-4f19-95cd-0e7a6b3c2d15";

    private const string Search = "search";

    /// <summary>
    /// The source v2 identifies against, which v3 reads as another
    /// namespace.
    /// </summary>
    private const string OtherEndpoint = "theporndb.net/graphql";

    /// <summary>The step highest in the list wins, even with every step beneath it also true.</summary>
    [Fact]
    public async Task NoInstanceConnectedOutranksEveryOtherStep()
    {
        await using var host = await MonitorHost.CreateAsync(apiKey: null);
        var coveId = await SeedSceneAsync(host, endpoint: null, remoteId: null);
        host.Client.Answering(
            nameof(IWhisparrSceneStatusReading.ReadSceneByRemoteIdAsync),
            MonitorHost.Json(200, "[]"));

        var result = await host.SceneActionAsync(coveId, Search);

        Assert.Equal(SceneRefusalKind.NoInstanceConnected, result.Refusal);
        Assert.Empty(host.Client.SceneStatuses);
    }

    /// <summary>No identity outranks the absent capability beneath it.</summary>
    [Fact]
    public async Task NoIdentityOutranksAnAbsentCapability()
    {
        await using var host = await MonitorHost.CreateAsync(
            generation: WhisparrGeneration.V2);
        var coveId = await SeedSceneAsync(host, endpoint: null, remoteId: null);

        var result = await host.SceneActionAsync(coveId, Search);

        Assert.Equal(SceneRefusalKind.NoIdentityInThisNamespace, result.Refusal);
        Assert.Empty(host.Client.SceneStatuses);
    }

    /// <summary>
    /// Conflicting links answer no identity, which is the whole of what the resolution can say.
    /// </summary>
    /// <remarks>
    /// The vocabulary declares a value for conflicting links because the surface states a sentence
    /// for it, and nothing answers that value. This is the case that says so, so a resolution that
    /// starts telling the two apart is a change with a failing test rather than a silent one.
    /// </remarks>
    [Fact]
    public async Task SeveralConflictingLinksAnswerNoIdentityRatherThanAStepAboveIt()
    {
        await using var host = await MonitorHost.CreateAsync();
        var coveId = await SeedSceneAsync(host, MonitorHost.StoredEndpoint, SceneId);
        await host.AddSceneIdentityAsync(coveId, MonitorHost.StoredEndpoint, OtherSceneId);
        await host.AddSceneIdentityAsync(coveId, OtherEndpoint, OtherSceneId);

        var result = await host.SceneActionAsync(coveId, Search);

        Assert.Equal(SceneRefusalKind.NoIdentityInThisNamespace, result.Refusal);
        Assert.NotEqual(SceneRefusalKind.SeveralIdentitiesInThisNamespace, result.Refusal);
        Assert.Empty(host.Client.SceneStatuses);
    }

    /// <summary>An absent capability outranks the no-entry step beneath it.</summary>
    /// <remarks>
    /// Whisparr v2 keeps no scene records, so it registers neither the read nor the grab.
    /// The scene carries an identity in that generation's own namespace, so the step above this one
    /// does not apply.
    /// </remarks>
    [Fact]
    public async Task AnAbsentCapabilityOutranksNoEntry()
    {
        await using var host = await MonitorHost.CreateAsync(generation: WhisparrGeneration.V2);
        var coveId = await SeedSceneAsync(host, OtherEndpoint, SceneId);

        var result = await host.SceneActionAsync(coveId, Search);

        Assert.Equal(SceneRefusalKind.CapabilityAbsentOnThisGeneration, result.Refusal);
        Assert.Empty(host.Client.SceneStatuses);
        Assert.Empty(host.Client.Acting);
    }

    /// <summary>No entry outranks the not-monitoring step beneath it.</summary>
    /// <remarks>
    /// An instance holding no entry holds no flag either, so a handler reading the flag first would
    /// answer not monitoring for a scene the instance never named.
    /// </remarks>
    [Fact]
    public async Task NoEntryOutranksNotMonitoring()
    {
        await using var host = await MonitorHost.CreateAsync();
        var coveId = await SeedSceneAsync(host, MonitorHost.StoredEndpoint, SceneId);
        host.Client.Answering(
            nameof(IWhisparrSceneStatusReading.ReadSceneByRemoteIdAsync),
            MonitorHost.Json(200, "[]"));

        var result = await host.SceneActionAsync(coveId, Search);

        Assert.Equal(SceneRefusalKind.WhisparrHasNoEntryForScene, result.Refusal);
        Assert.Empty(host.Client.Acting);
    }

    /// <summary>The last step, reached with every step above it satisfied.</summary>
    [Fact]
    public async Task NotMonitoringIsTheLastStepAndStillSendsNothing()
    {
        await using var host = await MonitorHost.CreateAsync();
        var coveId = await SeedSceneAsync(host, MonitorHost.StoredEndpoint, SceneId);
        host.Client.Answering(
            nameof(IWhisparrSceneStatusReading.ReadSceneByRemoteIdAsync),
            MonitorHost.Json(200, """[{"id":812,"monitored":false}]"""));

        var result = await host.SceneActionAsync(coveId, Search);

        Assert.Equal(SceneRefusalKind.WhisparrIsNotMonitoringThisScene, result.Refusal);
        Assert.Single(host.Client.SceneStatuses);
        Assert.Empty(host.Client.Acting);
    }

    private static async Task<int> SeedSceneAsync(
        MonitorHost host, string? endpoint, string? remoteId)
    {
        var studioId = await host.SeedStudioAsync(
            MonitorHost.StoredEndpoint, MonitorHost.StudioRemoteIdValue);
        return await host.SeedStudioSceneAsync(studioId, endpoint, remoteId);
    }
}
