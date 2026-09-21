using WhisparrSync.Contracts;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Scene;

// The refusal order is fixed: no connection, then no identity, then an absent capability, then no
// entry, then not monitoring. Each case sets up the step under test together with every step
// beneath it, so a handler asking in another order answers a refusal further down and fails.
//
// The several-identities step cannot be reached. The identity source keeps only a video whose rows
// agree on one identifier, so a video with conflicting links is absent from its answer and reads
// the same as a video with no link.
public sealed class SceneRefusalPrecedenceTests
{
    private const string SceneId = "3c0a6b21-9f7d-4c58-a3e2-71b0d4f5e8a9";

    private const string OtherSceneId = "8b1f7d40-2a63-4f19-95cd-0e7a6b3c2d15";

    private const string Search = "search";

    // The source v2 identifies against, which v3 reads as another namespace.
    private const string OtherEndpoint = "theporndb.net/graphql";

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

    // The vocabulary declares a value for conflicting links and nothing answers it. A resolution
    // that starts telling the two apart fails here rather than changing the surface silently.
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

    // Whisparr v2 keeps no scene records, so it registers neither the read nor the grab. The scene
    // carries an identity in that generation's namespace, so the step above does not apply.
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

    // An instance holding no entry holds no flag either, so a handler reading the flag first
    // answers not monitoring for a scene the instance never named.
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
